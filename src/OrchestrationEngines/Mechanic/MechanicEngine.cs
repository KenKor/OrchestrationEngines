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
  /// This method is intended to be called only by <see cref="EngineCreationTask"/>.
  /// Re-registration replaces any existing instance with the same name.
  /// </remarks>
  public void RegisterEngine(string name, IEngine engine)
  {
    _engines[name] = engine;
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

    // Not created: the creation task says why.
    if (!_engines.TryGetValue(engineName, out var engine))
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
  /// other are not ordered, so independent engines stop together and no engine waits for one it has nothing to do with.
  /// "Stopped" means the dependent's <see cref="IEngine.StopAsync"/> has returned: work that ignores its token may still
  /// be running then.
  /// </para>
  /// <para>
  /// An engine that throws while stopping is logged and counts as stopped, so the engines it depends on still stop;
  /// the first failure is rethrown once every engine has been stopped. An engine whose stop never returns keeps the
  /// engines it depends on from being stopped.
  /// </para>
  /// </remarks>
  public override async Task StopAsync()
  {
    await base.StopAsync().ConfigureAwait(false);
    State = EngineState.Stopped;

    LogWaitingForAllEngines(Logger);
    var failures = new ConcurrentQueue<Exception>();
    await Task.WhenAll(StopDependentsFirst(failures)).ConfigureAwait(false);
    if (failures.TryDequeue(out var firstFailure))
      ExceptionDispatchInfo.Capture(firstFailure).Throw();
  }

  /// <summary>
  /// Starts stopping every managed engine, each once the engines that depend on it have stopped (see
  /// <see cref="StopAsync"/>).
  /// </summary>
  /// <remarks>
  /// Dependencies come from the mechanic context's descriptors; an engine without one, or a dependency on an engine
  /// that is not registered, orders nothing. The descriptors are checked for cycles when the mechanic starts; should
  /// one remain anyway, the edge that closes it is not waited for, so every engine still stops.
  /// </remarks>
  /// <param name="failures">Receives each failure to stop, in the order they occur.</param>
  private Task[] StopDependentsFirst(ConcurrentQueue<Exception> failures)
  {
    var engines = new Dictionary<string, IEngine>(StringComparer.OrdinalIgnoreCase);
    foreach (var engine in _engines)
      engines[engine.Key] = engine.Value;
    // For each registered engine, the registered engines that depend on it.
    var dependents = engines.Keys.ToDictionary(name => name, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      StringComparer.OrdinalIgnoreCase);
    if (Context is MechanicContext mc)
      foreach (var descriptor in mc.EngineDescriptors.Where(d => d.Name != null && engines.ContainsKey(d.Name)))
        foreach (var parent in descriptor.DependsOn)
          if (dependents.TryGetValue(parent, out var ofParent))
            ofParent.Add(descriptor.Name);

    var stops = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
    var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Task StopAfterDependents(string name)
    {
      if (stops.TryGetValue(name, out var stop))
        return stop;
      visiting.Add(name);
      var first = dependents[name].Where(d => !visiting.Contains(d)).Select(StopAfterDependents).ToArray();
      visiting.Remove(name);
      stop = StopAfterAsync(name, engines[name], first, failures);
      stops[name] = stop;
      return stop;
    }

    return engines.Keys.Select(StopAfterDependents).ToArray();
  }

  /// <summary>
  /// Stops <paramref name="engine"/> once <paramref name="first"/> have ended; a failure is logged and queued, not
  /// thrown, so whatever waits for this stop goes on.
  /// </summary>
  private async Task StopAfterAsync(string name, IEngine engine, Task[] first, ConcurrentQueue<Exception> failures)
  {
    // These never fault: each catches its own failure.
    await Task.WhenAll(first).ConfigureAwait(false);
    try
    {
      await engine.StopAsync().ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      LogEngineFailedToStop(Logger, ex, name);
      failures.Enqueue(ex);
    }
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

  [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting on reset of all engines depending on and incl. {EngineName}.")]
  private static partial void LogWaitingForResetOfEngines(ILogger logger, string engineName);
}
