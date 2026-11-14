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
  /// Registry of instantiated engines, keyed by engine name.
  /// </summary>
  /// <remarks>
  /// Engines are created during execution of <see cref="EngineCreationTask"/>s
  /// and remain registered for the lifetime of the mechanic.
  /// </remarks>
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
  public void RegisterContext(string engineName, IEngineContext context)
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
  /// <param name="DependsOn">The depends on.</param>
  /// <remarks>
  /// This enables engines to access other engines they depend on
  /// without coupling them directly at construction time.
  /// </remarks>
  private IEngineContext ResolveContext(
      IEngineContext context,
      IEnumerable<string> DependsOn)
  {
    EngineContextWithDependencies contextWithDependencies = null;

    foreach (var dependsOn in DependsOn)
    {
      contextWithDependencies ??= new EngineContextWithDependencies(context);
      contextWithDependencies.AddUnderlyingEngine(dependsOn, _engines[dependsOn]);
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
  protected EngineCreationTask GetCreationTask(EngineDescriptor engineDescriptor)
  {
    var task = new EngineCreationTask(this, engineDescriptor, Logger, CancellationToken);
    AddTask(task.Name, task);
    return task;
  }

  /// <summary>
  /// Creates and registers the EngineContextTask for an engine.
  /// </summary>
  /// <param name="engineDescriptor">The engine descriptor.</param>
  protected EngineContextTask GetContextTask(EngineDescriptor engineDescriptor)
  {
    var task = new EngineContextTask(this, engineDescriptor, Logger, CancellationToken);
    AddTask(task.Name, task);
    return task;
  }

  /// <summary>
  /// Creates and registers the EngineKickStartTask for an engine.
  /// </summary>
  /// <param name="engineDescriptor">The engine descriptor.</param>
  protected EngineKickStartTask GetKickStartTask(EngineDescriptor engineDescriptor)
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
    && engine is not BaseEngine { IsCancellationRequested: true };

  /// <summary>
  /// Whether an engine of this name is known: created, or to be created by this mechanic's tasks (its kick-start task
  /// exists as soon as the mechanic's tasks are set up, while its creation task may not have run yet).
  /// </summary>
  private bool IsRegistered(string name) =>
    _engines.ContainsKey(name) || _tasks.ContainsKey(name + EngineKickStartTask.PostFix);

  /// <summary>
  /// Waits until the task <paramref name="taskName"/> of the engine <paramref name="engineName"/> has completed with
  /// success (see <see cref="IEngine.EnsureTaskReadyAsync"/>), after the engine has started: its tasks are set up when
  /// its kick-start starts it, and the kick-start ends once the engine is Ready, or has failed.
  /// </summary>
  /// <remarks>
  /// An engine that was created but never started is recognised as a <see cref="BaseEngine"/> without tasks; another
  /// <see cref="IEngine"/> answers for itself.
  /// </remarks>
  /// <exception cref="ArgumentException">No engine of that name is registered, or the engine has no such task.</exception>
  /// <exception cref="EngineTaskNotReadyException">
  /// The task ended without success; or the engine could not be created (reported by its creation task, which bears
  /// the engine's name) or was created but never started (reported by its kick-start task).
  /// </exception>
  public async Task EnsureEngineTaskReadyAsync(string engineName, string taskName, CancellationToken token = default)
  {
    var (engine, notStarted) = await WaitForKickStartAsync(engineName, token).ConfigureAwait(false);

    // Not created: the creation task says why.
    if (engine == null)
    {
      await EnsureTaskReadyAsync(engineName, token).ConfigureAwait(false);
      throw notStarted ?? new EngineTaskNotReadyException(engineName, EngineTaskState.Failed, "The engine was not created.");
    }

    // Created but never started (its context failed): it has no tasks, so report the kick-start. An engine that started
    // and then faulted has its tasks, and the task (or an unknown name) answers for itself.
    if (notStarted != null && engine is BaseEngine { Tasks.Count: 0 })
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
      await EnsureTaskReadyAsync(engineName + EngineKickStartTask.PostFix, token).ConfigureAwait(false);
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
  /// engine is Ready or its start has failed. So a task that hangs during the engine's first start can be reset only
  /// once the kick-start has given up (or through the engine itself). An engine whose tasks failed is reset like any
  /// other, and its tasks run again; the mechanic's own record of the failed start (its kick-start task) is not reset.
  /// </remarks>
  /// <param name="engineName">The name of the engine (compared ignoring case).</param>
  /// <param name="taskNames">The names of the tasks to reset (compared ignoring case).</param>
  /// <param name="resetDependentTasks">Whether to reset the tasks that depend on them too.</param>
  /// <param name="token">Cancels the wait for the kick-start; once the reset has begun, it is not cancelled.</param>
  /// <exception cref="ArgumentException">No engine of that name is registered, or it has no task of one of the names.</exception>
  /// <exception cref="InvalidOperationException">
  /// The engine was not created, never started, is stopped, or is not a <see cref="BaseEngine"/>.
  /// </exception>
  /// <exception cref="EngineTaskNotReadyException">
  /// The engine was stopped while its kick-start was waited for (its kick-start ended Cancelled), for example by a
  /// generation switch: the next generation has the live engine.
  /// </exception>
  public async Task ResetEngineTasksAsync(string engineName, IEnumerable<string> taskNames, bool resetDependentTasks,
    CancellationToken token = default)
  {
    // A failed start leaves an engine with tasks to reset, or one without, which is answered below.
    var (engine, notStarted) = await WaitForKickStartAsync(engineName, token).ConfigureAwait(false);
    // Stopped meanwhile (before or after the engine was created): report the cancelled kick-start, which a caller that
    // follows generation switches recognises.
    if (notStarted is { State: EngineTaskState.Cancelled } && engine is null or BaseEngine { IsCancellationRequested: true })
      throw notStarted;
    if (engine == null)
      throw new InvalidOperationException($"Engine '{engineName}' was not created, so it has no tasks to reset.");
    if (engine is not BaseEngine baseEngine)
      throw new InvalidOperationException($"Engine '{engineName}' is not a {nameof(BaseEngine)}, so its tasks cannot be reset.");
    if (baseEngine.Tasks.Count == 0)
      throw new InvalidOperationException($"Engine '{engineName}' has not started, so it has no tasks to reset.");
    await baseEngine.ResetTasksAsync(taskNames, resetDependentTasks).ConfigureAwait(false);
  }

  /// <summary>
  /// Asynchronously waits for an engine (and its dependencies) to reach Ready.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <param name="throwErrorIfNotFound">The throw error if not found.</param>
  /// <remarks>
  /// Returns null if the engine transitions to a terminal state beyond Ready, and its kick-start is not retrying it.
  /// </remarks>
  public async Task<IEngine> GetEngineWhenReadyAsync(string name, bool throwErrorIfNotFound = true)
  {
    if (!IsRegistered(name))
      if (throwErrorIfNotFound)
        throw new EvaluateException($"Engine {name} is not found. Has EngineCoordinator.StartAsync() been awaited?");
      else
        return null;

    var kickStartTaskName = name + EngineKickStartTask.PostFix;

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
    await EnsureReadyAsync(kickStartTaskName).ConfigureAwait(false);

    // An engine that is loading again (its tasks were reset after its first start, see BaseEngine.TriggerResetAsync) is
    // Starting while its kick-start completed long ago: wait for the engine itself. Its reset tasks have already been
    // started, so this wait fires no triggers. Stopping the engine ends the wait at once, also while a task's work has
    // not noticed the cancellation yet.
    if (_engines.TryGetValue(name, out engine) && engine.State == EngineState.Starting)
    {
      try
      {
        await EnsureEngineReadyAsync(engine).WaitAsync(engine.CancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (engine.CancellationToken.IsCancellationRequested)
      {
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

    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(dedicatedCt);
    var delay = Task.Delay(milliSecondsTimeOut, delayCts.Token);
    var ready = GetEngineWhenReadyAsync(name, throwErrorIfNotFound);

    var done = await Task.WhenAny(ready, delay).ConfigureAwait(false);
    if (done == ready)
    {
      // Cancel the time delay task.
      delayCts.Cancel();
      return await ready.ConfigureAwait(false);
    }
    dedicatedCt.ThrowIfCancellationRequested();

    return null;
  }

  // ---------------------------------------------------------------------
  // Engine lifecycle primitives
  // ---------------------------------------------------------------------

  /// <summary>
  /// Gets the engine async.
  /// </summary>
  /// <param name="engineName">The engine name.</param>
  /// <returns>The result.</returns>
  internal ValueTask<IEngine> GetEngineAsync(string engineName)
  {
    if (!_engines.TryGetValue(engineName, out var engine))
      throw new ArgumentException($"Unknown engine '{engineName}'", nameof(engineName));

    return new ValueTask<IEngine>(engine);
  }

  /// <summary>
  /// Starts an engine if it is in Created state.
  /// </summary>
  /// <param name="engineName">The engine name.</param>
  internal Task<IEngine> StartEngineAsync(string engineName)
  {
    if (!_engines.TryGetValue(engineName, out var engine))
      throw new ArgumentException($"Unknown engine '{engineName}'");

    return StartEngineAsync(engine);
  }

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
  /// Created must be waited on as well: StartAsync does not publish Starting, the engine state only
  /// leaves Created once a required task reaches a terminal state. Waiting on Starting alone let the
  /// kick-start task complete (and the mechanic report Ready) while the engine's tasks were still
  /// running, whenever StartAsync returned before its first required task had finished.
  /// </remarks>
  internal static async Task<IEngine> EnsureEngineReadyAsync(IEngine engine)
  {
    if (engine.State < EngineState.Ready)
      await engine.EnsureReadyAsync().ConfigureAwait(false);

    return engine;
  }

  /// <summary>
  /// Stops an engine if it has not yet fully stopped.
  /// </summary>
  /// <param name="engineName">The engine name.</param>
  internal Task StopEngineAsync(string engineName)
  {
    if (!_engines.TryGetValue(engineName, out var engine))
      throw new ArgumentException($"Unknown engine '{engineName}'");

    return engine.State <= EngineState.Ready
        ? engine.StopAsync()
        : Task.CompletedTask;
  }

  /// <summary>
  /// Ensures all managed engines reach Ready state.
  /// </summary>
  public virtual Task EnsureAllEnginesReadyAsync()
  {
    var completionTasks = _engines.Values
        .Where(e => e.State < EngineState.Ready)
        .Select(e => e.EnsureReadyAsync())
        .ToArray();

    LogWaitingForAllEngines(Logger);
    return Task.WhenAll(completionTasks);
  }

  // ---------------------------------------------------------------------
  // Shutdown and invalidation
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
  /// every engine still stops.
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
    // The stops no other stop waits for: awaiting these awaits them all.
    var last = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var first = new List<Task>();
    while (stops.Count < engines.Count)
    {
      var name = ready.Count > 0 ? ready.Dequeue() : graph.NextInACycle(stops);

      first.Clear();
      if (graph.Dependents.TryGetValue(name, out var dependents))
        foreach (var dependent in dependents)
          if (stops.TryGetValue(dependent, out var stop))
          {
            first.Add(stop);
            last.Remove(dependent);
          }
      stops[name] = graph.Stops(name)
        ? StopAfterAsync(name, engines[name], first.ToArray(), graph, failures)
        : first.Count == 0 ? Task.CompletedTask : Task.WhenAll(first);
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
    /// it from outside its cycle, directly or not, has a stop then.
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
    /// Every engine, the engines of a cycle together, with each cycle (or engine outside one) after the ones that
    /// depend on it: the order in which Tarjan's algorithm, following the edges to the dependents, completes the
    /// strongly connected components. Without recursion.
    /// </summary>
    private List<string> DependentsFirst()
    {
      var comparer = StringComparer.OrdinalIgnoreCase;
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
          string member;
          do
          {
            member = open.Pop();
            isOpen.Remove(member);
            order.Add(member);
          }
          while (!comparer.Equals(member, name));
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
    await Task.WhenAll(first).ConfigureAwait(false);

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

  /// <summary>
  /// Invalidates the context for an engine and all dependent engines.
  /// </summary>
  /// <param name="engineName">The engine name.</param>
  /// <remarks>
  /// <para>
  /// This forces affected EngineTasks (context and kick-start) back to
  /// their initial state so that context can be recomputed and engines
  /// restarted in dependency order.
  /// </para>
  /// </remarks>
  public Task InvalidateContextForEngineAsync(string engineName)
  {
    // 1. Find the context task. This is the one responsible for retrieving the context.
    var taskName = engineName + EngineContextTask.PostFix;
    if (!_tasks.TryGetValue(taskName, out var contextTask))
      throw new ArgumentException($"Cannot get task for engine '{engineName}'", nameof(engineName));

    // 2. Find all dependent tasks.
    var depTasks = GetThisAndAllDependents(contextTask);

    // 3. Note that the kick-start tasks will reset their engine.

    // 4. Reset all these tasks, so they will be executed again, starting 
    var resetTasks = depTasks
        .Select(t => t.FireAsync(EngineTaskTrigger.Reset))
        .ToArray();
    LogWaitingForResetOfEngines(Logger, engineName);
    // We go back to the initial state; however, the engine have been created by the creation tasks already.
    State = EngineState.Starting;
    return Task.WhenAll(resetTasks);
  }

  /// <summary>
  /// Performs one monitoring tick:
  /// - ensures all tasks that are eligible to start are started,
  /// - returns whether there is any unfinished mechanic task.
  /// </summary>
  internal async Task<bool> MonitorTickAsync()
  {
    // Ensure tasks that have been reset back to Created are re-started.
    await TriggerStartAsync().ConfigureAwait(false);

    // Monitoring should be active as long as at least one task is not finished.
    return _tasks.Values.Any(t => !t.IsCompleted);
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

  [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting on reset of all engines depending on and incl. {EngineName}.")]
  private static partial void LogWaitingForResetOfEngines(ILogger logger, string engineName);
}
