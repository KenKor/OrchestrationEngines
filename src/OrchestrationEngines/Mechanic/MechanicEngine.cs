using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Helpers;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Mechanic;

/// <summary>
/// Central orchestration engine responsible for creating, configuring, starting,
/// stopping, and coordinating all runtime engines in the application.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="MechanicEngine"/> acts as a *meta-engine*: it does not perform
/// domain work itself, but manages the lifecycle of other engines via
/// <see cref="EngineTask"/> instances.
/// </para>
///
/// <para>
/// Engine lifecycle is decomposed into three explicit phases per engine:
/// <list type="number">
/// <item>Creation (<see cref="EngineCreationTask"/>)</item>
/// <item>Context resolution (<see cref="EngineContextTask"/>)</item>
/// <item>Kick-start / readiness (<see cref="EngineKickStartTask"/>)</item>
/// </list>
/// Each phase is modeled as an EngineTask and wired into a dependency DAG.
/// </para>
///
/// <para>
/// The mechanic itself is an <see cref="BaseEngine"/> and therefore participates
/// in the same lifecycle model as the engines it coordinates.
/// </para>
/// </remarks>
public partial class MechanicEngine : BaseEngine
{
  /// <summary>
  /// The engines behind <see cref="Engines"/>.
  /// </summary>
  private readonly ConcurrentDictionary<string, IEngine> _engines =
    new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// How long an engine may take to stop before a warning names it (only logged; the stop goes on waiting).
  /// </summary>
  private static readonly TimeSpan StopWarningDelay = TimeSpan.FromSeconds(30);

  /// <summary>
  /// 1 once <see cref="StopAsync"/> is about to stop the engines (see <see cref="RegisterEngine"/>).
  /// </summary>
  private int _stopping;

  /// <summary>
  /// Registry of instantiated engines, keyed by engine name.
  /// </summary>
  /// <remarks>
  /// Engines are created during execution of <see cref="EngineCreationTask"/>s
  /// and remain registered for the lifetime of the mechanic.
  /// </remarks>
  public IReadOnlyDictionary<string, IEngine> Engines => _engines;

  /// <summary>
  /// Constructs a new MechanicEngine with its own cancellation scope.
  /// </summary>
  /// <param name="logger">The logger.</param>
  public MechanicEngine(ILogger logger) : base(logger)
  {
  }

  /// <summary>
  /// Constructs a new MechanicEngine linked to a parent cancellation token.
  /// </summary>
  /// <param name="logger">The logger.</param>
  /// <param name="engineToken">The engine token.</param>
  /// <remarks>
  /// Cancelling <paramref name="engineToken"/> will cancel all activities
  /// of the mechanic and the engines it manages.
  /// </remarks>
  public MechanicEngine(ILogger logger, CancellationToken engineToken)
      : base(logger, engineToken)
  {
  }

  /// <inheritdoc/>
  /// <remarks>
  /// None: the mechanic's lines are not about one engine, and its tasks are named after the engine they create, set up
  /// or start (for example <c>[G]{+-}</c>).
  /// </remarks>
  protected override string LogTag => null;

  // ---------------------------------------------------------------------
  // Engine registration
  // ---------------------------------------------------------------------

  /// <summary>
  /// Registers a newly created engine instance.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <param name="engine">The engine.</param>
  /// <remarks>
  /// <para>
  /// This method is intended to be called only by <see cref="EngineCreationTask"/>.
  /// Re-registration replaces any existing instance with the same name.
  /// </para>
  /// <para>
  /// An engine registered after <see cref="StopAsync"/> began (its factory did not honour its token) is stopped here,
  /// without waiting for it, and this is logged; its stop may end after <see cref="StopAsync"/> has returned. A factory
  /// should honour its token.
  /// </para>
  /// </remarks>
  public void RegisterEngine(string name, IEngine engine)
  {
    _engines[name] = engine;
    // Added before the flag is read, with a full fence (see StopAsync), so no engine is missed.
    if (Interlocked.CompareExchange(ref _stopping, 0, 0) == 0)
      return;

    BestEffort.Log(() => LogEngineCreatedAfterStop(Logger, name));
    _ = StopAfterAsync(name, engine, [], graph: null, failures: null);
  }

  /// <summary>
  /// Assigns a context to an existing engine instance.
  /// </summary>
  /// <param name="engineName">The engine name.</param>
  /// <param name="context">The context.</param>
  /// <remarks>
  /// <para>
  /// This method is intended to be called only by <see cref="EngineContextTask"/>,
  /// after the corresponding engine has been created.
  /// </para>
  ///
  /// <para>
  /// If the engine depends on other engines, the supplied context is wrapped
  /// in an <see cref="EngineContextWithDependencies"/> so that dependency engines
  /// can be accessed by the engine at runtime.
  /// </para>
  /// </remarks>
  internal void RegisterContext(string engineName, IEngineContext context)
  {
    ArgumentNullException.ThrowIfNull(context);

    // Should only be called by EngineContextTask, which is dependent on the EngineCreationTask.
    if (!_engines.TryGetValue(engineName, out var engine))
      throw new ArgumentException($"Unknown engine '{engineName}'");

    // If this engine has dependencies, enrich the context with dependent engines
    if (Context is MechanicContext mc)
    {
      // The engine's dependencies, from its descriptor. Fix (ContextFanOutTest): the name was compared case-sensitively,
      // so a refresh with the name in another case lost the engine's underlying engines.
      context = ResolveContext(context, mc.EngineDescriptors
          .FirstOrDefault(ed => String.Equals(ed.Name, engineName, StringComparison.OrdinalIgnoreCase))?.DependsOn
          ?? Enumerable.Empty<string>());
    }
    // We can start the engine now with a context.
    engine.Context = context;
  }

  /// <summary>
  /// Wraps a base context with references to dependency engines.
  /// </summary>
  /// <param name="context">The context.</param>
  /// <param name="dependsOn">The names of the engines the engine depends on.</param>
  /// <remarks>
  /// This enables engines to access other engines they depend on
  /// without coupling them directly at construction time.
  /// </remarks>
  private IEngineContext ResolveContext(
      IEngineContext context,
      IEnumerable<string> dependsOn)
  {
    EngineContextWithDependencies contextWithDependencies = null;

    foreach (var name in dependsOn)
    {
      contextWithDependencies ??= new EngineContextWithDependencies(context);
      contextWithDependencies.AddUnderlyingEngine(name, _engines[name]);
    }

    return contextWithDependencies ?? context;
  }

  // ---------------------------------------------------------------------
  // Task creation helpers
  // ---------------------------------------------------------------------

  /// <summary>
  /// Creates and registers the EngineCreationTask for an engine.
  /// </summary>
  /// <param name="engineDescriptor">The engine descriptor.</param>
  private EngineCreationTask GetCreationTask(EngineDescriptor engineDescriptor)
  {
    var task = new EngineCreationTask(this, engineDescriptor, Logger, CancellationToken);
    AddTask(task.Name, task);
    return task;
  }

  /// <summary>
  /// Creates and registers the EngineContextTask for an engine.
  /// </summary>
  /// <param name="engineDescriptor">The engine descriptor.</param>
  private EngineContextTask GetContextTask(EngineDescriptor engineDescriptor)
  {
    var task = new EngineContextTask(this, engineDescriptor, Logger, CancellationToken);
    AddTask(task.Name, task);
    return task;
  }

  /// <summary>
  /// Creates and registers the EngineKickStartTask for an engine.
  /// </summary>
  /// <param name="engineDescriptor">The engine descriptor.</param>
  private EngineKickStartTask GetKickStartTask(EngineDescriptor engineDescriptor)
  {
    var task = new EngineKickStartTask(this, engineDescriptor, Logger, CancellationToken);
    AddTask(task.Name, task);
    return task;
  }

  // ---------------------------------------------------------------------
  // Task graph construction
  // ---------------------------------------------------------------------

  /// <summary>
  /// Builds the complete EngineTask dependency graph for all engines.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method constructs a layered DAG:
  /// Creation → Context → KickStart,
  /// with inter-engine dependencies applied at the Context and KickStart layers.
  /// </para>
  ///
  /// <para>
  /// All tasks are created first, then wired explicitly to make dependency
  /// intent clear and prevent accidental mis-wiring.
  /// </para>
  /// </remarks>
  protected override void SetUpTasks()
  {
    if (Context is not MechanicContext mc)
      throw new InvalidCastException("Mechanic's context must be of type MechanicContext");

    // Fix (NameCaseTest): a dependency named in another case than its engine failed the set-up with a
    // KeyNotFoundException, as these were keyed case-sensitively.
    var creationTasks = new Dictionary<string, EngineCreationTask>(StringComparer.OrdinalIgnoreCase);
    var contextTasks = new Dictionary<string, EngineContextTask>(StringComparer.OrdinalIgnoreCase);
    var kickStartTasks = new Dictionary<string, EngineKickStartTask>(StringComparer.OrdinalIgnoreCase);

    // Per-engine lifecycle tasks
    foreach (var engineDescriptor in mc.EngineDescriptors)
    {
      var creationTask =
          creationTasks[engineDescriptor.Name] =
              GetCreationTask(engineDescriptor);

      var contextTask =
          contextTasks[engineDescriptor.Name] =
              GetContextTask(engineDescriptor);

      var kickStartTask =
          kickStartTasks[engineDescriptor.Name] =
              GetKickStartTask(engineDescriptor);

      // Intra-engine ordering
      contextTask.AddDependsOn(creationTask);
      kickStartTask.AddDependsOn(contextTask);
    }

    // Inter-engine dependency wiring
    foreach (var engineDescriptor in mc.EngineDescriptors)
    {
      var name = engineDescriptor.Name;

      foreach (var depName in engineDescriptor.DependsOn)
      {
        contextTasks[name].AddDependsOn(contextTasks[depName]);
        kickStartTasks[name].AddDependsOn(kickStartTasks[depName]);
      }
    }
  }

  // ---------------------------------------------------------------------
  // Engine access helpers
  // ---------------------------------------------------------------------

  /// <summary>
  /// Returns the engine instance if it is already in Ready state; otherwise null.
  /// </summary>
  /// <param name="name">The name.</param>
  public IEngine GetEngineIfReady(string name)
      => _engines.TryGetValue(name, out var engine)
         && engine.State == EngineState.Ready
          ? engine
          : null;

  /// <summary>
  /// Returns whether the engine's kick-start has not given up on it yet (it is between, or in, retries), and the engine
  /// was not stopped on purpose.
  /// </summary>
  private bool KickStartRetryPending(string kickStartTaskName, IEngine engine) =>
    _tasks.TryGetValue(kickStartTaskName, out var kickStart) && !kickStart.IsCompleted
    && !IsStopped(engine);

  /// <summary>
  /// The state of an engine as a wait for it sees it (see <see cref="GetEngineWhenReadyAsync(string, bool)"/>): its
  /// own state, which its kick-start completes where the engine cannot tell. A Faulted or Stopped engine counts as
  /// Starting while its kick-start is retrying it. An engine that has not started (not created, or still Created) is
  /// Faulted once its kick-start has given up, and Starting once it exists or its kick-start has started.
  /// </summary>
  /// <param name="name">The engine name.</param>
  internal EngineState StartStateOf(string name)
  {
    _engines.TryGetValue(name, out var engine);
    var state = engine?.State ?? EngineState.Created;
    var kickStartName = EngineKickStartTask.KickStartName(name);
    if (state is EngineState.Starting or EngineState.Ready || !_tasks.TryGetValue(kickStartName, out var kickStart))
      return state;
    if (state != EngineState.Created)
      return KickStartRetryPending(kickStartName, engine) ? EngineState.Starting : state;
    if (HasGivenUp(kickStartName))
      return EngineState.Faulted;
    return engine != null || kickStart.State.IsStarted() ? EngineState.Starting : EngineState.Created;
  }

  /// <summary>Returns whether the engine was stopped on purpose; an engine that is null was not.</summary>
  private static bool IsStopped(IEngine engine) => engine is BaseEngine { IsCancellationRequested: true };

  /// <summary>
  /// Whether an engine of this name is known: created, or to be created by this mechanic's tasks (its kick-start task
  /// exists as soon as the mechanic's tasks are set up, while its creation task may not have run yet).
  /// </summary>
  private bool IsRegistered(string name) =>
    _engines.ContainsKey(name) || _tasks.ContainsKey(EngineKickStartTask.KickStartName(name));

  /// <summary>
  /// Waits until the task <paramref name="taskName"/> of the engine <paramref name="engineName"/> has completed with
  /// success (see <see cref="IEngine.EnsureTaskReadyAsync"/>), after the engine has started: its tasks are set up when
  /// its kick-start starts it, and the kick-start ends once the engine is Ready, or has failed.
  /// </summary>
  /// <remarks>
  /// An engine that was created but never started is recognised as a <see cref="BaseEngine"/> that has not set up a task
  /// set (tasks it created in its constructor do not count); another <see cref="IEngine"/> answers for itself.
  /// </remarks>
  /// <exception cref="ArgumentException">No engine of that name is registered, or the engine has no such task.</exception>
  /// <exception cref="EngineTaskNotReadyException">
  /// The task ended without success; or the engine could not be created (reported by its creation task, which bears
  /// the engine's name) or was created but never started (reported by its kick-start task).
  /// </exception>
  internal async Task EnsureEngineTaskReadyAsync(string engineName, string taskName, CancellationToken token = default)
  {
    var (engine, notStarted) = await WaitForKickStartAsync(engineName, token).ConfigureAwait(false);

    // Not created: the creation task says why.
    if (engine == null)
    {
      await EnsureTaskReadyAsync(engineName, token).ConfigureAwait(false);
      throw notStarted ?? new EngineTaskNotReadyException(engineName, EngineTaskState.Failed, "The engine was not created.");
    }

    // Created but never started (its context failed): report the kick-start, and start none of its tasks (which would
    // run without a context). Also when it has no tasks: its last set-up failed (a restart), and the kick-start's
    // failure is the answer, not an unknown name. An engine that started and then faulted answers for its task.
    if (notStarted != null && engine is BaseEngine { HasTaskSet: false })
      throw notStarted;
    await engine.EnsureTaskReadyAsync(taskName, token).ConfigureAwait(false);
  }

  /// <summary>
  /// Waits for the kick-start of the engine <paramref name="engineName"/> to settle: until the engine is Ready, or its
  /// start has failed (or been cancelled). An engine's tasks are set up when its kick-start starts it.
  /// </summary>
  /// <returns>The engine, or null if it was not created; and the kick-start's exception if it did not succeed.</returns>
  /// <exception cref="ArgumentException">No engine of that name is registered.</exception>
  private async Task<(IEngine Engine, EngineTaskNotReadyException NotStarted)> WaitForKickStartAsync(string engineName,
    CancellationToken token)
  {
    if (!IsRegistered(engineName))
      throw new ArgumentException($"No engine '{engineName}' is registered.", nameof(engineName));
    EngineTaskNotReadyException notStarted = null;
    try
    {
      await EnsureTaskReadyAsync(EngineKickStartTask.KickStartName(engineName), token).ConfigureAwait(false);
    }
    catch (EngineTaskNotReadyException ex)
    {
      notStarted = ex;
    }
    return (_engines.TryGetValue(engineName, out var engine) ? engine : null, notStarted);
  }

  /// <summary>
  /// Resets tasks of the engine <paramref name="engineName"/> once it has started (see
  /// <see cref="BaseEngine.ResetTasksAsync"/>).
  /// </summary>
  /// <remarks>
  /// It first waits for the engine's kick-start to settle, since an engine's tasks are set up when it starts: until the
  /// engine is Ready or its start has failed. So a task that hangs during the engine's first start is reset here only
  /// once the kick-start has given up. To reset it without waiting, reset it through the engine itself
  /// (<see cref="EngineCoordinator.GetEngineIfCreated"/>, cast to <see cref="BaseEngine"/>, then
  /// <see cref="BaseEngine.ResetTasksAsync"/>), or bound its
  /// run with the task's <see cref="EngineTask.Timeout"/>. An engine whose tasks failed is reset like any
  /// other, and its tasks run again. When its start was given up, its kick-start is reset too, with the kick-starts
  /// that depend on it, but the engine is not started again: the kick-start waits until the engine is Ready, and the
  /// engines that depend on it start, without their contexts being loaded again. If it fails again, its restart policy
  /// begins a fresh round. An exception from the reset of those kick-starts comes after the engine's own tasks were
  /// reset and run again.
  /// </remarks>
  /// <param name="engineName">The name of the engine (compared ignoring case).</param>
  /// <param name="taskNames">The names of the tasks to reset (compared ignoring case).</param>
  /// <param name="resetDependentTasks">Whether to reset the tasks that depend on them too.</param>
  /// <param name="token">Cancels the wait for the kick-start; once the reset has begun, it is not cancelled.</param>
  /// <exception cref="ArgumentException">No engine of that name is registered, or it has no task of one of the names.</exception>
  /// <exception cref="InvalidOperationException">
  /// The engine was not created (its kick-start's exception is the <see cref="Exception.InnerException"/>), never
  /// started, or is not a <see cref="BaseEngine"/>; or a restart of it was prepared and then given up, or its set-up
  /// failed (see <see cref="BaseEngine.ResetTasksAsync"/>).
  /// </exception>
  /// <exception cref="EngineTaskNotReadyException">
  /// The engine, or this mechanic, was stopped, before or during the reset, for example by a generation switch: the
  /// next generation has the live engine. Its <see cref="EngineTaskNotReadyException.State"/> is Cancelled.
  /// </exception>
  internal async Task ResetEngineTasksAsync(string engineName, IEnumerable<string> taskNames, bool resetDependentTasks,
    CancellationToken token = default)
  {
    // A failed start leaves an engine with tasks to reset, or one without, which is answered below.
    var (engine, notStarted) = await WaitForKickStartAsync(engineName, token).ConfigureAwait(false);
    // Stopped meanwhile (before or after the engine was created): report the cancelled kick-start, which a caller that
    // follows generation switches recognises. Only when something was stopped: an engine that was not created is null
    // whether or not anything was (#58 item 15).
    if (notStarted is { State: EngineTaskState.Cancelled }
        && (IsStopped(engine) || (engine == null && IsCancellationRequested)))
      throw notStarted;
    if (engine == null)
      throw new InvalidOperationException($"Engine '{engineName}' was not created, so it has no tasks to reset.",
        notStarted);
    if (engine is not BaseEngine baseEngine)
      throw new InvalidOperationException($"Engine '{engineName}' is not a {nameof(BaseEngine)}, so its tasks cannot be reset.");
    // Tasks it created in its constructor do not count: resetting them would start their work without a context.
    if (!baseEngine.HasTaskSet)
      throw new InvalidOperationException($"Engine '{engineName}' has not started, so it has no tasks to reset.");
    // Stopped during the reset: its tasks are not run again, whether the reset noticed (and threw) or not. Report it
    // with the one "stopped" signal, as above (#58 items 5 and 11).
    try
    {
      await baseEngine.ResetTasksAsync(taskNames, resetDependentTasks).ConfigureAwait(false);
    }
    catch (InvalidOperationException) when (baseEngine.IsCancellationRequested)
    {
      // Answered below.
    }
    if (baseEngine.IsCancellationRequested)
      throw new EngineTaskNotReadyException(EngineKickStartTask.KickStartName(engineName), EngineTaskState.Cancelled,
        "The engine was stopped during the reset.");
    // Its start was given up (Failed or one of its substates, such as RecoveryFailed once its restart policy gave up, or
    // Cancelled while the engine was not stopped, as above): its tasks run again, so clear the mechanic's record of the
    // failed start too, without restarting the engine, and the kick-starts that depend on it start their engines (#58
    // item 2).
    if (notStarted is null || !notStarted.State.IsFailed()
        || !_tasks.TryGetValue(EngineKickStartTask.KickStartName(engineName), out var task)
        || task is not EngineKickStartTask kickStart)
      return;
    try
    {
      await kickStart.ResetRecordOnlyAsync(this).ConfigureAwait(false);
    }
    catch (InvalidOperationException) when (IsCancellationRequested)
    {
      throw new EngineTaskNotReadyException(kickStart.Name, EngineTaskState.Cancelled,
        "The mechanic was stopped during the reset.");
    }
  }

  /// <summary>
  /// Asynchronously waits for an engine (and its dependencies) to reach Ready.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <param name="throwErrorIfNotFound">The throw error if not found.</param>
  /// <remarks>
  /// Returns null if the engine transitions to a terminal state beyond Ready, and its kick-start is not retrying it.
  /// </remarks>
  public Task<IEngine> GetEngineWhenReadyAsync(string name, bool throwErrorIfNotFound = true)
    => GetEngineWhenReadyAsync(name, throwErrorIfNotFound, CancellationToken.None);

  /// <summary>
  /// Waits for an engine (and its dependencies) to reach Ready, until <paramref name="token"/> is cancelled.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <param name="throwErrorIfNotFound">If true, throws when the engine name is not registered.</param>
  /// <param name="token">Ends the wait, and everything it waits on, not the engine's start.</param>
  /// <returns>
  /// As <see cref="GetEngineWhenReadyAsync(string, bool)"/>; a cancellation when the token is cancelled.
  /// </returns>
  internal async Task<IEngine> GetEngineWhenReadyAsync(string name, bool throwErrorIfNotFound, CancellationToken token)
  {
    if (!IsRegistered(name))
      if (throwErrorIfNotFound)
        throw new EvaluateException($"Engine {name} is not found. Has EngineCoordinator.StartAsync() been awaited?");
      else
        return null;

    var kickStartTaskName = EngineKickStartTask.KickStartName(name);

    if (_engines.TryGetValue(name, out var engine))
    {
      if (engine.State == EngineState.Ready)
        return engine;

      // Failed or stopped: no engine, unless its kick-start is retrying it (the descriptor's recovery waits before it
      // starts the engine again). Then wait for the retries below, as a lookup that arrives during an attempt does,
      // instead of failing a query that a second later would have found the engine Ready.
      if (engine.State > EngineState.Ready && !KickStartRetryPending(kickStartTaskName, engine))
        return null;
    }

    // The engine is created by its creation task, which may not have run yet on a freshly started mechanic:
    // StartAsync returns once the tasks have been fired, not once they have run.
    // We must await the engine to be created, get ready, kick-started and all, including for all its dependencies.
    // The kick-start task depends on the creation task, so this also waits for the engine to exist.
    // A kick-start that gave up, with no restart of the mechanic pending, needs no wait: the check below answers null
    // without an exception thrown on every lookup of an engine that failed.
    if (!HasGivenUp(kickStartTaskName))
    {
      try
      {
        await EnsureTaskReadyAsync(kickStartTaskName, token).ConfigureAwait(false);
      }
      catch (EngineTaskNotReadyException)
      {
        // The kick-start ended without success: the engine is not Ready, and the check below answers null. Its
        // ArgumentException for a task that was replaced is not caught: the mechanic's own sets always have the
        // kick-start.
      }
    }

    // An engine that is loading again (its tasks were reset after its first start, see BaseEngine.TriggerResetAsync) is
    // Starting while its kick-start completed long ago: wait for the engine itself. Its reset tasks have already been
    // started, so this wait fires no triggers. Stopping the engine ends the wait at once, also while a task's work has
    // not noticed the cancellation yet. The caller's cancellation ends only this wait: the engine's own one has no
    // token (IEngine.EnsureReadyAsync), and goes on until the engine settles.
    if (_engines.TryGetValue(name, out engine) && engine.State == EngineState.Starting)
    {
      using var stopOrCaller = CancellationTokenSource.CreateLinkedTokenSource(engine.CancellationToken, token);
      try
      {
        await EnsureEngineReadyAsync(engine).WaitObservedAsync(stopOrCaller.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (stopOrCaller.IsCancellationRequested)
      {
        // The caller's cancellation carries the caller's token; a stop of the engine answers null.
        token.ThrowIfCancellationRequested();
        return null;
      }
    }

    return _engines.TryGetValue(name, out engine) && engine.State == EngineState.Ready ? engine : null;
  }

  /// <summary>
  /// Asynchronously waits for an engine to reach Ready, with timeout support.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <param name="milliSecondsTimeOut">The milli seconds time out.</param>
  /// <param name="token">The token.</param>
  public Task<IEngine> GetEngineWhenReadyAsync(
      string name,
      int milliSecondsTimeOut,
      CancellationToken token = default)
    => GetEngineWhenReadyAsync(name, false, milliSecondsTimeOut, token);

  /// <summary>
  /// Asynchronously waits for an engine to reach Ready, with timeout support.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <param name="throwErrorIfNotFound">If true, throws when the engine name is not registered.</param>
  /// <param name="milliSecondsTimeOut">The milli seconds time out.</param>
  /// <param name="token">The token.</param>
  /// <returns>The engine once Ready; null when it is not Ready within the time-out (it goes on starting).</returns>
  public async Task<IEngine> GetEngineWhenReadyAsync(
      string name,
      bool throwErrorIfNotFound,
      int milliSecondsTimeOut,
      CancellationToken token = default)
  {
    var dedicatedCt = (token == default) ? CancellationToken : token;
    // A Ready engine needs no time-out source or timer; the inner lookup checks the same.
    if (_engines.TryGetValue(name, out var known) && known.State == EngineState.Ready)
      return known;

    // The time-out, or the caller, ends the wait inside the lookup too, so polling an engine that never settles does
    // not pile up waiters (#156). The time-out runs on the mechanic's clock and answers null; the caller's
    // cancellation carries the caller's token.
    return await TimeProvider.WithinOrDefaultAsync(TimeSpan.FromMilliseconds(milliSecondsTimeOut), dedicatedCt,
      lookup => GetEngineWhenReadyAsync(name, throwErrorIfNotFound, lookup)).ConfigureAwait(false);
  }

  // ---------------------------------------------------------------------
  // Engine lifecycle primitives
  // ---------------------------------------------------------------------

  /// <summary>
  /// Starts an engine if it is in Created state.
  /// </summary>
  /// <param name="engine">The engine.</param>
  internal static async Task<IEngine> StartEngineAsync(IEngine engine)
  {
    if (engine.State == EngineState.Created)
      await engine.StartAsync().ConfigureAwait(false);

    return engine;
  }

  /// <summary>
  /// Ensures an engine that is not yet Ready (Created or Starting) transitions to Ready.
  /// </summary>
  /// <param name="engine">The engine.</param>
  /// <remarks>
  /// Created must be waited on as well: an engine is Created until its start publishes Starting, which
  /// <see cref="BaseEngine"/> does just before its tasks run, and another <see cref="IEngine"/> may not do at all.
  /// Waiting on Starting alone let the kick-start task complete (and the mechanic report Ready) while the engine's
  /// tasks were still running, whenever StartAsync returned before its first required task had finished.
  /// </remarks>
  internal static async Task<IEngine> EnsureEngineReadyAsync(IEngine engine)
  {
    if (engine.State < EngineState.Ready)
      await engine.EnsureReadyAsync().ConfigureAwait(false);

    return engine;
  }

  // ---------------------------------------------------------------------
  // Shutdown
  // ---------------------------------------------------------------------

  /// <summary>
  /// Stops the mechanic and all managed engines, dependents first.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Each engine is stopped once every engine that depends on it (by its descriptor's
  /// <see cref="EngineDescriptor.DependsOn"/>) has been stopped, and not before; engines that do not depend on each
  /// other, directly or through other engines, are not ordered, so independent engines stop together and no engine
  /// waits for one it has nothing to do with. "Stopped" means the dependent's <see cref="IEngine.StopAsync"/> has
  /// returned: work that ignores its token may still be running then.
  /// </para>
  /// <para>
  /// An engine that throws while stopping is logged and counts as stopped, so the engines it depends on still stop;
  /// the first failure is rethrown once every engine has been stopped. An engine whose stop throws an
  /// <see cref="OperationCanceledException"/> (its cleanup was cut short) gets a warning instead, and that is not a
  /// failure: a stop override that uses the engine's own token sees it already cancelled, so cleanup that must finish
  /// should run without that token, or with its own time limit.
  /// </para>
  /// <para>
  /// An engine whose stop never returns keeps the engines it depends on from being stopped; after 30 seconds a warning
  /// names it and the engines waiting for it. Host code that can hang (an overridden stop, a custom
  /// <see cref="IEngine"/>, a blocking cancellation handler) should bound itself.
  /// </para>
  /// <para>
  /// An engine created after the stop began (its factory ignored its token) is stopped when it is registered (see
  /// <see cref="RegisterEngine"/>), possibly after this has returned.
  /// </para>
  /// </remarks>
  public override async Task StopAsync()
  {
    await base.StopAsync().ConfigureAwait(false);
    State = EngineState.Stopped;

    LogWaitingForAllEngines(Logger);
    // Before the walk copies _engines, with a full fence: an engine that RegisterEngine adds from now on is either in
    // the copy or sees the flag (or both, and is stopped twice, which an engine allows).
    Interlocked.Exchange(ref _stopping, 1);
    await StopDependentsFirstAsync(only: null).ConfigureAwait(false);
  }

  /// <summary>
  /// Stops the named engines dependents first, as <see cref="StopAsync"/> stops them all, and leaves the others
  /// running. The order follows the whole dependency graph: an engine that is not named still passes on the order, so
  /// with B → H → A, where only A and B are named, B stops before A.
  /// </summary>
  /// <param name="names">The engines to stop; names of engines that were not created are ignored.</param>
  /// <remarks>
  /// A failure is handled as in <see cref="StopAsync"/>: the first is rethrown once all have stopped.
  /// </remarks>
  internal Task StopEnginesAsync(IEnumerable<string> names) =>
    StopDependentsFirstAsync(new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));

  /// <summary>
  /// Stops the created engines (or only those in <paramref name="only"/>), each once the engines that depend on it
  /// have stopped (see <see cref="StopAsync"/>), and rethrows the first failure once all have stopped.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Dependencies come from the mechanic context's descriptors, between engines that were created: an engine without a
  /// descriptor has no dependencies of its own (it still waits for engines whose descriptors depend on it), and a
  /// dependency on an engine that was not created is ignored. So a registered engine whose creation failed breaks the
  /// order through it: with C → B → A and B not created, A and C stop together (C never started).
  /// </para>
  /// <para>
  /// The walk is Kahn's algorithm, dependents first, without recursion, so a long chain cannot overflow the stack
  /// (<see cref="BaseEngine"/>'s reset walk, <c>DependentsFirst</c>, is a Kahn walk over tasks in the same direction:
  /// a fix to one may apply to the other). Cycles are rejected
  /// when the mechanic's task set is set up (<see cref="EngineTask.CheckForCycles"/>); should one be made later
  /// (through <see cref="EngineDescriptor.AddDependsOn"/>), an engine in it is stopped without waiting for the
  /// dependents in its cycle, once every engine outside its cycle that depends on it, directly or not, has stopped, so
  /// every engine still stops. An engine that depends on an engine in a cycle waits for the whole cycle.
  /// </para>
  /// </remarks>
  /// <param name="only">The engines to stop, or <c>null</c> for all.</param>
  private async Task StopDependentsFirstAsync(ISet<string> only)
  {
    var engines = _engines.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
    var graph = new StopGraph(Context as MechanicContext, engines.Keys, only);
    var failures = new ConcurrentQueue<Exception>();

    // For each engine, how many of its dependents have no stop yet; an engine is ready when none is left.
    var pending = graph.Dependents.ToDictionary(d => d.Key, d => d.Value.Count, StringComparer.OrdinalIgnoreCase);
    var ready = new Queue<string>(engines.Keys.Where(name => !pending.ContainsKey(name)));
    var stops = new Dictionary<string, Task>(engines.Count, StringComparer.OrdinalIgnoreCase);
    // Holds every stop no other stop waits for: awaiting these awaits them all.
    var last = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var first = new List<Task>();
    while (stops.Count < engines.Count)
    {
      var breaksACycle = ready.Count == 0;
      var name = breaksACycle ? graph.NextInACycle(stops) : ready.Dequeue();

      first.Clear();
      if (graph.Dependents.TryGetValue(name, out var dependents))
        foreach (var dependent in dependents)
          if (stops.TryGetValue(dependent, out var stop))
          {
            if (graph.StopToWaitFor(name, dependent, stop) is { } wait)
              first.Add(wait);
            last.Remove(dependent);
          }
      if (breaksACycle)
        first.Add(graph.StopsAbove(name, stops));
      stops[name] = graph.Stops(name)
        ? StopAfterAsync(name, engines[name], first.ToArray(), graph, failures)
        : FromThreadPool(first);
      graph.Started(name, stops[name]);
      last.Add(name);

      if (graph.Dependencies.TryGetValue(name, out var dependencies))
        foreach (var dependency in dependencies)
          if (--pending[dependency] == 0 && !stops.ContainsKey(dependency))
            ready.Enqueue(dependency);
    }

    // These never fault.
    await Task.WhenAll(last.Select(name => stops[name])).ConfigureAwait(false);
    if (failures.TryDequeue(out var firstFailure))
      ExceptionDispatchInfo.Capture(firstFailure).Throw();
  }

  /// <summary>
  /// The dependency graph of one stop walk, in both directions, and which engines it stops.
  /// </summary>
  private sealed class StopGraph
  {
    private readonly ISet<string> _only;
    private readonly string[] _order;
    private List<string> _dependentsFirst;
    private int _next;
    // Found with _dependentsFirst, so null until a cycle is found: each engine's component (its cycle, or itself
    // alone).
    private Dictionary<string, Component> _componentOf;

    /// <param name="context">
    /// The mechanic's context, whose descriptors give the dependencies; <c>null</c> for none.
    /// </param>
    /// <param name="engines">The created engines.</param>
    /// <param name="only">The engines the walk stops, or <c>null</c> for all.</param>
    public StopGraph(MechanicContext context, ICollection<string> engines, ISet<string> only)
    {
      _only = only;
      _order = engines.ToArray();
      Dependents = context == null
        ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        : EngineDependents.Of(context.EngineDescriptors, engines.Contains, Dependencies);
    }

    /// <summary>For each engine, the engines that depend on it.</summary>
    public Dictionary<string, HashSet<string>> Dependents { get; }

    /// <summary>For each engine, the engines it depends on.</summary>
    public Dictionary<string, List<string>> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the walk stops <paramref name="name"/>, or only passes on the order through it.</summary>
    public bool Stops(string name) => _only == null || _only.Contains(name);

    /// <summary>
    /// When no engine is ready although some have no stop yet, so a cycle holds them: the next engine without a stop
    /// in an order where every cycle comes after the cycles and engines that depend on it. Every engine that depends on
    /// it from outside its cycle, directly or not, has a stop then; its stop waits for theirs through
    /// <see cref="StopsAbove"/>.
    /// </summary>
    /// <remarks>
    /// The order is found once per walk, on the first call; the calls then move one cursor through it, so a walk that
    /// runs into cycles pays linear time in all.
    /// </remarks>
    /// <param name="stops">The stops so far, by engine.</param>
    public string NextInACycle(Dictionary<string, Task> stops)
    {
      _dependentsFirst ??= DependentsFirst();
      while (stops.ContainsKey(_dependentsFirst[_next]))
        _next++;
      return _dependentsFirst[_next];
    }

    /// <summary>
    /// What the stop of <paramref name="name"/> waits for, for <paramref name="dependent"/>, an engine that depends on
    /// it and whose stop is <paramref name="stop"/>: that stop, or once a cycle was found and the dependent is in
    /// another cycle, the stop of that whole cycle. <c>null</c> when the stop of that cycle was already returned for
    /// <paramref name="name"/>.
    /// </summary>
    public Task StopToWaitFor(string name, string dependent, Task stop)
    {
      // No cycle found yet: no engine with a stop is in one.
      if (_componentOf == null)
        return stop;
      var cycle = _componentOf[dependent];
      if (cycle.Stop == null || cycle == _componentOf[name])
        return stop;
      if (ReferenceEquals(cycle.WaitedForBy, name))
        return null;
      cycle.WaitedForBy = name;
      return cycle.Stop;
    }

    /// <summary>
    /// Records the stop of <paramref name="name"/>, for the stop of its cycle (if it is in one) to wait for.
    /// </summary>
    public void Started(string name, Task stop) => _componentOf?[name].Started(stop);

    /// <summary>
    /// Where the walk breaks a cycle at <paramref name="name"/>: what its stop waits for besides the stops of its
    /// direct dependents, the stops of the engines outside the cycle that depend on any engine in it, each with its
    /// whole cycle. So a stop ends only after those of every engine outside its cycle that depends on it, directly or
    /// not. Found once per cycle, when the walk first breaks it, which is after all of them have a stop.
    /// </summary>
    /// <param name="name">The engine <see cref="NextInACycle"/> returned.</param>
    /// <param name="stops">The stops so far, by engine.</param>
    public Task StopsAbove(string name, Dictionary<string, Task> stops)
    {
      var component = _componentOf[name];
      if (component.Above != null)
        return component.Above;

      var waits = new List<Task>();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var seenCycles = new HashSet<Component>();
      foreach (var member in component.Members)
        foreach (var dependent in DependentsOf(member))
        {
          var other = _componentOf[dependent];
          if (other == component)
            continue;
          if (other.Stop != null)
          {
            if (seenCycles.Add(other))
              waits.Add(other.Stop);
          }
          else if (seen.Add(dependent))
            waits.Add(stops[dependent]);
        }
      component.Above = waits.Count == 0 ? Task.CompletedTask : Task.WhenAll(waits);
      return component.Above;
    }

    /// <summary>
    /// A strongly connected component of the graph: a cycle, or one engine outside every cycle.
    /// </summary>
    private sealed class Component
    {
      private readonly List<Task> _stops;
      private readonly TaskCompletionSource<Task> _all;

      /// <param name="members">The component's engines.</param>
      public Component(string[] members)
      {
        Members = members;
        if (members.Length < 2)
          return;
        _stops = new List<Task>(members.Length);
        _all = new TaskCompletionSource<Task>();
        Stop = _all.Task.Unwrap();
      }

      public string[] Members { get; }

      /// <summary>
      /// For a cycle, its stop: ends once the stops of all its engines have ended, also when it is awaited before
      /// each of them has a stop. <c>null</c> for one engine outside every cycle.
      /// </summary>
      public Task Stop { get; }

      /// <summary>What a break of this cycle waits for (<see cref="StopsAbove"/>); <c>null</c> until found.</summary>
      public Task Above { get; set; }

      /// <summary>
      /// The last engine whose stop was given <see cref="Stop"/> to wait for (<see cref="StopToWaitFor"/>), so an
      /// engine with several dependents in this cycle waits for it once, not once per edge.
      /// </summary>
      public string WaitedForBy { get; set; }

      public void Started(Task stop)
      {
        if (Stop == null)
          return;
        _stops.Add(stop);
        if (_stops.Count == Members.Length)
          _all.SetResult(Task.WhenAll(_stops));
      }
    }

    /// <summary>
    /// Every engine, the engines of a cycle together, with each cycle (or engine outside one) after the ones that
    /// depend on it: the order in which Tarjan's algorithm, following the edges to the dependents, completes the
    /// strongly connected components. Without recursion. Also records each engine's component.
    /// </summary>
    private List<string> DependentsFirst()
    {
      var comparer = StringComparer.OrdinalIgnoreCase;
      _componentOf = new Dictionary<string, Component>(comparer);
      var index = new Dictionary<string, int>(comparer);
      var low = new Dictionary<string, int>(comparer);
      var order = new List<string>(_order.Length);
      var open = new Stack<string>();
      var isOpen = new HashSet<string>(comparer);
      var work = new Stack<(string Name, IEnumerator<string> Dependents)>();

      void Visit(string name)
      {
        index[name] = low[name] = index.Count;
        open.Push(name);
        isOpen.Add(name);
        work.Push((name, DependentsOf(name).GetEnumerator()));
      }

      foreach (var root in _order)
      {
        if (index.ContainsKey(root))
          continue;
        Visit(root);
        while (work.Count > 0)
        {
          var (name, dependents) = work.Peek();
          if (dependents.MoveNext())
          {
            var dependent = dependents.Current;
            if (!index.ContainsKey(dependent))
              Visit(dependent);
            else if (isOpen.Contains(dependent))
              low[name] = Math.Min(low[name], index[dependent]);
            continue;
          }

          work.Pop();
          if (work.Count > 0)
          {
            var parent = work.Peek().Name;
            low[parent] = Math.Min(low[parent], low[name]);
          }
          if (low[name] != index[name])
            continue;
          // A component is complete: every component it reaches through dependents is in the order already.
          var start = order.Count;
          string member;
          do
          {
            member = open.Pop();
            isOpen.Remove(member);
            order.Add(member);
          }
          while (!comparer.Equals(member, name));
          var members = new string[order.Count - start];
          order.CopyTo(start, members, 0, members.Length);
          var component = new Component(members);
          foreach (var engine in component.Members)
            _componentOf[engine] = component;
        }
      }
      return order;
    }

    private IEnumerable<string> DependentsOf(string name) =>
      Dependents.TryGetValue(name, out var dependents) ? dependents : Enumerable.Empty<string>();

    /// <summary>
    /// The engines the walk stops that wait for <paramref name="name"/>'s stop: the engines it depends on, and through
    /// an engine the walk does not stop, the ones that engine depends on.
    /// </summary>
    public IReadOnlyList<string> WaitingFor(string name)
    {
      var waiting = new List<string>();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
      var queue = new Queue<string>();
      queue.Enqueue(name);
      while (queue.Count > 0)
      {
        if (!Dependencies.TryGetValue(queue.Dequeue(), out var dependencies))
          continue;
        foreach (var dependency in dependencies)
        {
          if (!seen.Add(dependency))
            continue;
          if (Stops(dependency))
            waiting.Add(dependency);
          else
            queue.Enqueue(dependency);
        }
      }
      return waiting;
    }
  }

  /// <summary>
  /// Stops <paramref name="engine"/> once <paramref name="first"/> have ended. A failure is logged and queued in
  /// <paramref name="failures"/> (if given), not thrown, so whatever waits for this stop goes on; a cancelled stop is
  /// only logged, as a warning.
  /// </summary>
  /// <param name="name">The engine's name.</param>
  /// <param name="engine">The engine to stop.</param>
  /// <param name="first">The stops to wait for first.</param>
  /// <param name="graph">
  /// The walk's graph, to name the engines waiting for a stop that takes long; <c>null</c> if none.
  /// </param>
  /// <param name="failures">Receives the failure to stop, if any; <c>null</c> to only log it.</param>
  private async Task StopAfterAsync(string name, IEngine engine, Task[] first, StopGraph graph,
    ConcurrentQueue<Exception> failures)
  {
    // These never fault.
    await FromThreadPool(first).ConfigureAwait(false);

    try
    {
      await WarnWhileStoppingAsync(name, engine.StopAsync(), graph).ConfigureAwait(false);
    }
    catch (OperationCanceledException ex)
    {
      BestEffort.Log(() => LogEngineStopCancelled(Logger, ex, name));
    }
    catch (Exception ex)
    {
      // Queued before it is logged, so a logging provider that throws cannot lose it.
      failures?.Enqueue(ex);
      BestEffort.Log(() => LogEngineFailedToStop(Logger, ex, name));
    }
  }

  /// <summary>
  /// A task that ends once <paramref name="task"/> has ended (however it ended), going on from the thread pool, not
  /// inline in whatever ended <paramref name="task"/>: in a long chain of stops that end at once, waits that go on inline
  /// run one inside the other, and whatever ended the first returns only once the whole chain has run. Already ended
  /// when <paramref name="task"/> has.
  /// </summary>
  private static Task FromThreadPool(Task task) =>
    task.IsCompleted
      ? Task.CompletedTask
      : task.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

  /// <summary>
  /// <see cref="FromThreadPool(Task)"/> for the end of all of <paramref name="tasks"/>, without a
  /// <see cref="Task.WhenAll(IEnumerable{Task})"/> when there is only one. Reads <paramref name="tasks"/> at once.
  /// </summary>
  private static Task FromThreadPool(IReadOnlyList<Task> tasks) =>
    FromThreadPool(tasks.Count == 1 ? tasks[0] : Task.WhenAll(tasks));

  /// <summary>
  /// Awaits <paramref name="stop"/>; if it has not finished after <see cref="StopWarningDelay"/>, logs a warning that
  /// names the engine and the engines waiting for it, and once it has returned, that it has.
  /// </summary>
  private async Task WarnWhileStoppingAsync(string name, Task stop, StopGraph graph)
  {
    if (await stop.EndsWithinAsync(StopWarningDelay, TimeProvider).ConfigureAwait(false))
    {
      await stop.ConfigureAwait(false);
      return;
    }

    BestEffort.Log(() =>
    {
      var waiting = graph?.WaitingFor(name) ?? [];
      LogEngineStillStopping(Logger, name, (int)StopWarningDelay.TotalSeconds,
        waiting.Count == 0 ? "none" : string.Join(", ", waiting));
    });
    await stop.ConfigureAwait(false);
    BestEffort.Log(() => LogEngineFinishedStopping(Logger, name));
  }

  // ---------------------------------------------------------------------
  // Logging methods
  // ---------------------------------------------------------------------

  [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting on completion of all engines..")]
  private static partial void LogWaitingForAllEngines(ILogger logger);

  [LoggerMessage(Level = LogLevel.Error, Message = "Engine {EngineName} failed to stop.")]
  private static partial void LogEngineFailedToStop(ILogger logger, Exception exception, string engineName);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Engine {EngineName}'s stop was cancelled; its cleanup may not have finished.")]
  private static partial void LogEngineStopCancelled(ILogger logger, Exception exception, string engineName);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Engine {EngineName} has not finished stopping after {Seconds} s; "
      + "engines waiting for it: {WaitingEngines}.")]
  private static partial void LogEngineStillStopping(ILogger logger, string engineName, int seconds,
    string waitingEngines);

  [LoggerMessage(Level = LogLevel.Information, Message = "Engine {EngineName} has finished stopping.")]
  private static partial void LogEngineFinishedStopping(ILogger logger, string engineName);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Engine {EngineName} was created after the stop began; stopping it.")]
  private static partial void LogEngineCreatedAfterStop(ILogger logger, string engineName);
}
