using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Helpers;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Stateless;

namespace OrchestrationEngines.Coordinator;

/// <summary>
/// Coordinates application-level lifecycle based on the state of managed engines.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="EngineCoordinator"/> is responsible for:
/// <list type="bullet">
/// <item>collecting engine descriptors before startup,</item>
/// <item>creating and starting the <see cref="MechanicEngine"/>,</item>
/// <item>tracking aggregate engine readiness,</item>
/// <item>exposing application-level lifecycle state.</item>
/// </list>
/// </para>
///
/// <para>
/// It uses an application-level state machine to translate low-level
/// engine lifecycle events into coarse-grained application states
/// such as Initializing, Running, and Stopped.
/// </para>
/// </remarks>
public partial class EngineCoordinator : IDisposable
{
  // App-level state machine
  private StateMachine<EngineCoordinatorState, EngineCoordinatorTrigger> _appMachine;

  // Synchronization object for short, synchronous critical sections.
  // Invariant: no awaits occur while holding this lock.
  private readonly Lock _sync = new();

  // The engines a running generation switch stopped early, as it rebuilds them from scratch, with the next generation
  // once it has started (null if it did not start); null when no such switch runs. See GetEngineWhenReadyAsync.
  private RebuildingSwitch _rebuildingSwitch;

  // Declarative configuration describing all engines and their dependencies.
  private readonly MechanicContext _context = new();

  // The mechanic is the meta-engine that owns the engine task DAG.
  private MechanicEngine _mechanic;

  // Cancellation token source governing coordinator and mechanic lifetime.
  private readonly CancellationTokenSource _linkedCts;
  private readonly string _coordinatorName;

  // Logger for coordinator-level (application-level) logging.
  private readonly ILogger _applicationLogger;

  // Monitor wake-up signal. Released whenever new work may exist.
  private readonly SemaphoreSlim _monitorPulse = new SemaphoreSlim(0, int.MaxValue);

  // Dispatcher to take in commands, that we'll then execute on a monitoring thread (that will not be aborted).
  private readonly MechanicDispatcher _mechanicDispatcher;
  private readonly bool _useDispatcher;

  // Monitoring task that will (re)fire on tasks that are Created or Reset.
  private EngineTask _monitorTask;

  // The one start of the mechanic (set up and fire its tasks). Claimed under _sync by the first StartAsync caller;
  // every other caller awaits it, so no caller fires or enumerates the mechanic's tasks while they are being created.
  private Task _mechanicStart;

  /// <summary>
  /// Indicates whether cancellation has been requested for the coordinator.
  /// </summary>
  public bool IsCancellationRequested => _linkedCts.IsCancellationRequested;

  /// <summary>
  /// Cancellation token governing coordinator and mechanic lifetime.
  /// </summary>
  public CancellationToken CancellationToken => _linkedCts.Token;

  /// <summary>
  /// Current application-level lifecycle state.
  /// </summary>
  /// <remarks>
  /// If the state machine has not yet been initialized, this defaults to <see cref="EngineCoordinatorState.Created"/>.
  /// </remarks>
  public EngineCoordinatorState State => AppMachine?.State ?? EngineCoordinatorState.Created;

  /// <summary>
  /// Registry of instantiated engines, keyed by engine name.
  /// </summary>
  // Fix (NameCaseTest): the empty registry before start ignores case, like the mechanic's.
  public IReadOnlyDictionary<string, IEngine> Engines =>
    _mechanic?.Engines ?? new Dictionary<string, IEngine>(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// The application token this coordinator is linked to; the coordinator of a generation switch is linked to it too.
  /// </summary>
  private readonly CancellationToken _applicationToken;

  /// <summary>
  /// The generations of one coordinator: every coordinator a generation switch creates shares the lineage of the one
  /// it replaces, and the lineage knows the current one.
  /// </summary>
  /// <remarks>
  /// Shared rather than a link from each coordinator to the next: with links, whoever still held an early coordinator
  /// would keep every later generation alive through the chain. Now it keeps only the lineage and the current one.
  /// </remarks>
  private sealed class Lineage(EngineCoordinator first)
  {
    private EngineCoordinator _current = first;

    /// <summary>
    /// The current coordinator. Written and read with <see cref="Interlocked"/> (full fences), so that a lookup on a
    /// replaced coordinator does not see a stale "still current" just after a switch.
    /// </summary>
    public EngineCoordinator Current
    {
      get => Interlocked.CompareExchange(ref _current, null, null);
      set => Interlocked.Exchange(ref _current, value);
    }
  }

  private Lineage _lineage;

  /// <summary>
  /// The current coordinator once a generation switch (see <see cref="RestartWithDefinitionsAsync"/>) replaced this one,
  /// or null while this one is current. A lookup on a replaced coordinator is answered by the current one.
  /// </summary>
  public EngineCoordinator Successor => Current is var current && ReferenceEquals(current, this) ? null : current;

  /// <summary>
  /// The current coordinator of this coordinator's lineage: this one, until a generation switch replaces it. A switch
  /// hands over before it shuts the replaced coordinator down, so a reader that asks for the current one sees the
  /// next generation, not the one being stopped.
  /// </summary>
  public EngineCoordinator Current => _lineage.Current;

  /// <summary>
  /// Creates a new engine coordinator.
  /// </summary>
  /// <param name="coordinatorName">
  /// Logical coordinator name used in diagnostic messages.
  /// </param>
  /// <param name="logger">
  /// Logger used for application-level logging.
  /// </param>
  /// <param name="useDispatcher">
  /// Optional boolean that specifies if a dispatcher should be used.
  /// This is very much recommended if the Coordinator can be killed,
  /// e.g. if we are on .NET48/.NET481 where threads can be Aborted,
  /// or if we want to keep a self maintaining Coordinator.
  /// </param>
  /// <param name="applicationToken">
  /// Optional external cancellation token to which the coordinator is linked.
  /// </param>
  public EngineCoordinator(string coordinatorName, ILogger logger, bool useDispatcher = true, CancellationToken applicationToken = default)
  {
    // Link coordinator cancellation to application cancellation.
    _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
    _applicationToken = applicationToken;
    _lineage = new Lineage(this);
    _coordinatorName = coordinatorName;
    _applicationLogger = logger ?? NullLogger.Instance;
    _useDispatcher = useDispatcher;

    // Create a dispatcher, e.g. if we are on .NET48/.NET481 where threads can be Aborted.
    if (useDispatcher)
      _mechanicDispatcher = new MechanicDispatcher("EngineMechanicThread", _applicationLogger, _linkedCts.Token);
  }

  /// <summary>
  /// Lazily created mechanic engine.
  /// </summary>
  /// <remarks>
  /// The mechanic is created only after engine registration has completed
  /// and startup begins.
  /// </remarks>
  protected MechanicEngine Mechanic
  {
    get { return _mechanic ??= CreateMechanic(); }
  }

  /// <summary>
  /// Creates and initializes the mechanic engine.
  /// </summary>
  /// <remarks>
  /// This wires coordinator-level state tracking to mechanic engine events
  /// and assigns the collected <see cref="MechanicContext"/>.
  /// </remarks>
  protected virtual MechanicEngine CreateMechanic()
  {
    var mechanic = new MechanicEngine(_applicationLogger, _linkedCts.Token);

    // Listen to engine lifecycle events to drive coordinator state.
    mechanic.EngineStatusChanged += Mechanic_EngineStatusChanged;

    // Attach engine descriptors before starting.
    mechanic.Context = _context;
    return mechanic;
  }

  /// <summary>
  /// Lazily created application-level state machine.
  /// </summary>
  protected StateMachine<EngineCoordinatorState, EngineCoordinatorTrigger> AppMachine
  {
    get { return _appMachine ??= CreateStateMachine(); }
  }

  /// <summary>
  /// Creates the application-level state machine.
  /// </summary>
  /// <remarks>
  /// This machine represents coarse application intent and aggregates
  /// engine-level signals into high-level lifecycle transitions.
  /// </remarks>
  protected virtual StateMachine<EngineCoordinatorState, EngineCoordinatorTrigger> CreateStateMachine()
  {
    var machine =
        new StateMachine<EngineCoordinatorState, EngineCoordinatorTrigger>(EngineCoordinatorState.Created);

    machine.Configure(EngineCoordinatorState.Created)
        .Permit(EngineCoordinatorTrigger.Start, EngineCoordinatorState.WaitingForEngines);

    machine.Configure(EngineCoordinatorState.WaitingForEngines)
        .Ignore(EngineCoordinatorTrigger.Start)
        .Ignore(EngineCoordinatorTrigger.EngineReady)
        .Ignore(EngineCoordinatorTrigger.EngineFailed)
        // A context refresh of an engine that is (re)loading: the engine decides what to load again, and the coordinator
        // follows the engine as before.
        .Ignore(EngineCoordinatorTrigger.RefreshEngine)
        .Permit(EngineCoordinatorTrigger.EngineStarting, EngineCoordinatorState.Initializing)
        .Permit(EngineCoordinatorTrigger.StartShutdown, EngineCoordinatorState.Stopping);

    machine.Configure(EngineCoordinatorState.Initializing)
        .Ignore(EngineCoordinatorTrigger.Start)
        .Ignore(EngineCoordinatorTrigger.EngineStarting)
        .Ignore(EngineCoordinatorTrigger.RefreshEngine)
        .Permit(EngineCoordinatorTrigger.EngineReady, EngineCoordinatorState.Running)
        .Permit(EngineCoordinatorTrigger.EngineFailed, EngineCoordinatorState.WaitingForEngines)
        .Permit(EngineCoordinatorTrigger.StartShutdown, EngineCoordinatorState.Stopping);

    machine.Configure(EngineCoordinatorState.Running)
        .Ignore(EngineCoordinatorTrigger.Start)
        .Ignore(EngineCoordinatorTrigger.EngineStarting)
        .Ignore(EngineCoordinatorTrigger.EngineReady)
        .Permit(EngineCoordinatorTrigger.EngineFailed, EngineCoordinatorState.WaitingForEngines)
        .Permit(EngineCoordinatorTrigger.InvalidateContext, EngineCoordinatorState.Initializing)
        .Permit(EngineCoordinatorTrigger.RefreshEngine, EngineCoordinatorState.Initializing)
        .Permit(EngineCoordinatorTrigger.StartShutdown, EngineCoordinatorState.Stopping);

    machine.Configure(EngineCoordinatorState.Stopping)
        .Ignore(EngineCoordinatorTrigger.Start)
        .Ignore(EngineCoordinatorTrigger.EngineStarting)
        .Ignore(EngineCoordinatorTrigger.EngineReady)
        .Ignore(EngineCoordinatorTrigger.EngineFailed)
        .Ignore(EngineCoordinatorTrigger.StartShutdown)
        .Permit(EngineCoordinatorTrigger.CompleteShutdown, EngineCoordinatorState.Stopped);

    machine.Configure(EngineCoordinatorState.Stopped)
        .Ignore(EngineCoordinatorTrigger.Start)
        .Ignore(EngineCoordinatorTrigger.EngineStarting)
        .Ignore(EngineCoordinatorTrigger.EngineReady)
        .Ignore(EngineCoordinatorTrigger.EngineFailed)
        .Ignore(EngineCoordinatorTrigger.StartShutdown)
        .Ignore(EngineCoordinatorTrigger.CompleteShutdown);

    // Log unexpected triggers instead of throwing.
    machine.OnUnhandledTrigger((state, trigger) =>
    {
      LogUnhandledTrigger(_coordinatorName, trigger, state);
    });

    // Verbose transition logging for diagnostics.
    machine.OnTransitioned(t =>
    {
      LogTransition(_coordinatorName, t.Source, t.Trigger, t.Destination);
    });

    return machine;
  }

  /// <summary>
  /// Handles lifecycle events emitted by the mechanic engine.
  /// </summary>
  /// <param name="sender">The event source.</param>
  /// <param name="e">Engine lifecycle event data.</param>
  private void Mechanic_EngineStatusChanged(object sender, EngineStatusEventArgs e)
  {
    lock (_sync)
    {
      // Ignore engine events once shutdown has started.
      if (State is EngineCoordinatorState.Stopping || State is EngineCoordinatorState.Stopped)
        return;

      if (e.State == EngineState.Starting)
      {
        AppMachine.Fire(EngineCoordinatorTrigger.EngineStarting);
        return;
      }

      if (e.State == EngineState.Faulted)
      {
        AppMachine.Fire(EngineCoordinatorTrigger.EngineFailed);
        return;
      }

      if (e.State == EngineState.Ready)
      {
        // Only signal readiness when all required engines have succeeded.
        // Fix (OptionalEngineTest): required engines only. The mechanic reports Ready as soon as its required
        // tasks are done; checking every task here left the coordinator Initializing for good whenever an engine
        // registered with requiredForRunning: false was still loading (or had failed) at that moment.
        if (_mechanic.IsRequiredCompletedWithSuccess())
          AppMachine.Fire(EngineCoordinatorTrigger.EngineReady);
      }
    }
  }

  /// <summary>
  /// Registers a new engine with the coordinator.
  /// </summary>
  /// <param name="name">Logical engine name.</param>
  /// <param name="engineFactory">Factory used to create engine instances.</param>
  /// <param name="requiredForRunning">
  /// Whether this engine must be Ready for the application to be considered running.
  /// </param>
  /// <param name="version">
  /// Integer definition version used for generation-diff restart decisions.
  /// </param>
  /// <returns>The created engine descriptor.</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if registration is attempted after startup has begun.
  /// </exception>
  public EngineDescriptor RegisterEngine(
      string name,
      Func<CancellationToken, Task<IEngine>> engineFactory,
      bool requiredForRunning = true,
      int version = 1)
  {
    var descriptor = new EngineDescriptor(name, engineFactory, requiredForRunning, version);
    return RegisterEngineDescriptor(descriptor);
  }

  /// <summary>
  /// Registers a fully configured descriptor with the coordinator.
  /// </summary>
  /// <remarks>
  /// This API is used by generation-switch flows where descriptors are prepared externally.
  /// </remarks>
  public EngineDescriptor RegisterEngineDescriptor(EngineDescriptor descriptor) =>
    TryRegisterEngineDescriptor(descriptor)
      ? descriptor
      : throw new InvalidOperationException(
        $"Engine '{descriptor.Name}' cannot be registered after the coordinator has started; use RestartWithDefinitionsAsync.");

  /// <summary>
  /// Registers a fully configured descriptor with the coordinator, if it has not started yet.
  /// </summary>
  /// <param name="descriptor">The descriptor.</param>
  /// <returns>
  /// False if the coordinator has started (also when it started just now, on another thread): add the engine with a
  /// generation switch (<see cref="RestartWithDefinitionsAsync"/>) instead.
  /// </returns>
  /// <exception cref="ArgumentException">An engine with the same name is already registered.</exception>
  public bool TryRegisterEngineDescriptor(EngineDescriptor descriptor)
  {
    ArgumentNullException.ThrowIfNull(descriptor);

    lock (_sync)
    {
      // Only before the start: once the start is claimed, the mechanic builds its tasks from the descriptors (outside
      // this lock), so a later descriptor would never be loaded, or fail that enumeration. The state does not tell:
      // it stays WaitingForEngines while the mechanic builds its tasks, and after a failed first load. A running
      // coordinator takes new engines through a generation switch (RestartWithDefinitionsAsync).
      if (_mechanicStart != null || State != EngineCoordinatorState.Created)
        return false;

      if (!_context.EngineDescriptors.Add(descriptor))
        throw new ArgumentException($"Engine '{descriptor.Name}' is already registered.", nameof(descriptor));
    }

    return true;
  }

  /// <summary>
  /// Creates a version-oriented diff between currently registered and candidate definitions.
  /// </summary>
  /// <remarks>
  /// The diff marks restart roots by version change and expands that to all transitive dependents.
  /// </remarks>
  public EngineDefinitionDiff BuildDefinitionDiff(IEnumerable<EngineDescriptor> candidateDescriptors)
  {
    lock (_sync)
    {
      return EngineDefinitionDiff.Create(_context.EngineDescriptors, candidateDescriptors);
    }
  }

  /// <summary>
  /// Returns a snapshot of currently registered engine definitions.
  /// </summary>
  public IReadOnlyCollection<EngineDescriptor> GetRegisteredDefinitions()
  {
    lock (_sync)
    {
      return _context.EngineDescriptors.ToArray();
    }
  }

  /// <summary>
  /// Builds and starts a new coordinator generation from candidate definitions.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method uses <see cref="EngineDefinitionDiff"/> to annotate restart intent while
  /// creating a fully fresh coordinator generation.
  /// </para>
  /// <para>
  /// Generation-switch policy:
  /// <list type="bullet">
  /// <item>
  /// existing engines with a restart factory are recreated from previous generation input; those without one are
  /// stopped before the next generation starts, and rebuilt from their regular factory,
  /// </item>
  /// <item>
  /// restart metadata (definition-changed / dependency-changed / nothing-changed) is passed via
  /// <see cref="EngineRestartInput"/>.
  /// </item>
  /// <item>added engines: created from their regular factory.</item>
  /// </list>
  /// </para>
  /// <para>
  /// After the new coordinator is launched and optionally ready, all previous-generation engines
  /// are stopped to avoid duplicate runtime activity and token coupling to the old coordinator.
  /// </para>
  /// </remarks>
  public Task<EngineCoordinator> RestartWithDefinitionsAsync(IEnumerable<EngineDescriptor> candidateDescriptors, bool waitUntilReady = true, CancellationToken token = default)
    => DispatchAsync(() => RestartWithDefinitionsAsyncInt(candidateDescriptors, waitUntilReady, token), token);

  private async Task<EngineCoordinator> RestartWithDefinitionsAsyncInt(IEnumerable<EngineDescriptor> candidateDescriptors, bool waitUntilReady, CancellationToken token)
  {
    ArgumentNullException.ThrowIfNull(candidateDescriptors);

    var candidateList = candidateDescriptors.Where(d => d != null).ToArray();
    var diff = BuildDefinitionDiff(candidateList);

    Dictionary<string, EngineDescriptor> currentDescriptorByName;
    Dictionary<string, IEngine> currentEngineByName;

    lock (_sync)
    {
      currentDescriptorByName = _context.EngineDescriptors
        .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

      currentEngineByName = Engines
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    var versionChanged = new HashSet<string>(diff.VersionChangedEngineNames, StringComparer.OrdinalIgnoreCase);
    var restartRequired = new HashSet<string>(diff.RestartRequiredEngineNames, StringComparer.OrdinalIgnoreCase);
    var nextCoordinator = new EngineCoordinator(_coordinatorName + ">", _applicationLogger, _useDispatcher, _applicationToken);
    var handovers = new List<(string Name, Handover Handover)>();
    var rebuiltFromScratch = new List<(string Name, IEngine Engine)>();

    foreach (var candidate in candidateList)
    {
      var name = candidate.Name;
      currentEngineByName.TryGetValue(name, out var previousEngine);
      currentDescriptorByName.TryGetValue(name, out var previousDescriptor);

      EngineDescriptor runtimeDescriptor;
      // Fix (RebuiltEngineSwitchTest): without a restart factory the next engine is built from scratch and cannot take
      // over anything, yet the previous engine was handed over and kept running until the next generation had started,
      // so two copies of its data could be loaded at the same time. It is now not handed over, and stopped before the
      // next generation starts (as in the .NET Framework version).
      if (previousEngine == null || candidate.RestartFromPrevious == null)
      {
        runtimeDescriptor = CloneDescriptor(candidate);
        if (previousEngine != null)
          rebuiltFromScratch.Add((name, previousEngine));
      }
      else
      {
        var definitionChanged = versionChanged.Contains(name);
        var dependencyChanged = restartRequired.Contains(name) && !definitionChanged;
        var handover = new Handover(previousEngine, previousDescriptor);
        handovers.Add((name, handover));

        runtimeDescriptor = CloneDescriptor(
          candidate,
          ct => CreateReplacementEngineFromPreviousAsync(
            candidate,
            handover,
            definitionChanged,
            dependencyChanged,
            ct));
      }

      nextCoordinator.RegisterEngineDescriptor(runtimeDescriptor);
    }

    // The previous engines are in the handovers now. Drop these references: they live on in this async method's state,
    // which the returned task keeps for as long as anyone holds it.
    currentEngineByName = null;
    currentDescriptorByName = null;

    // Fix (RebuiltEngineSwitchTest): stop the engines that are rebuilt from scratch first, so that their data is
    // released before their replacements load it again. Whatever their state, as the shutdown below does: a Faulted
    // engine's kick-start would otherwise go on recovering it. A lookup that sees one of them stop waits for the next
    // generation to start and asks it (see GetEngineWhenReadyAsync), as the lookups do once the switch hands over.
    RebuildingSwitch rebuilding = null;
    try
    {
      if (rebuiltFromScratch.Count > 0)
      {
        rebuilding = new RebuildingSwitch(rebuiltFromScratch.Select(r => r.Name));
        Volatile.Write(ref _rebuildingSwitch, rebuilding);
        await Task.WhenAll(rebuiltFromScratch.Select(r => r.Engine.StopAsync())).ConfigureAwait(false);
      }
      rebuiltFromScratch = null;

      await nextCoordinator.StartAsync().ConfigureAwait(false);
      rebuilding?.Next.TrySetResult(nextCoordinator);
    }
    finally
    {
      // The next generation did not start: the waiting lookups give null.
      rebuilding?.Next.TrySetResult(null);
    }
    // Not awaited: it ends when the next generation's engines have started or given up, long after a switch that does not
    // wait until they are ready.
    _ = nextCoordinator.ReleaseHandoversWhenSettledAsync(handovers);
    if (waitUntilReady)
      await nextCoordinator.EnsureReadyAsync(token).ConfigureAwait(false);

    // Hand over to the next generation and shut this one down. Lookups on this coordinator, also those that are
    // waiting now, are answered by the current generation (see Successor). Shutting down stops every engine of this generation, whatever its
    // state (a Faulted engine's kick-start would otherwise go on recovering and load it again next to its successor),
    // and cancels this generation's mechanic, so its kick-starts and their recovery end at once.
    nextCoordinator._lineage = _lineage;
    _lineage.Current = nextCoordinator;
    await ShutdownAsyncInt().ConfigureAwait(false);

    return nextCoordinator;
  }

  /// <summary>
  /// The engines a generation switch stopped before the next generation started, as it rebuilds them from scratch, and
  /// that next generation once it has started (null if it did not start).
  /// </summary>
  private sealed class RebuildingSwitch(IEnumerable<string> names)
  {
    public HashSet<string> Names { get; } = new(names, StringComparer.OrdinalIgnoreCase);

    public TaskCompletionSource<EngineCoordinator> Next { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
  }

  /// <summary>
  /// The previous engine that a next generation's engine may take over, handed over once.
  /// </summary>
  /// <remarks>
  /// The handover is captured by the next generation's engine factory, which that generation keeps; holding the
  /// previous engine there would keep every earlier generation alive. It is released once the next engine has been
  /// created, after which a creation falls back to the registered factory.
  /// </remarks>
  private sealed class Handover(IEngine previousEngine, EngineDescriptor previousDescriptor)
  {
    private IEngine _previousEngine = previousEngine;
    private EngineDescriptor _previousDescriptor = previousDescriptor;

    public bool TryTake(out IEngine previous, out EngineDescriptor previousDescriptor)
    {
      lock (this)
      {
        previous = _previousEngine;
        previousDescriptor = _previousDescriptor;
        return previous != null;
      }
    }

    public void Release()
    {
      lock (this)
      {
        _previousEngine = null;
        _previousDescriptor = null;
      }
    }
  }

  /// <summary>
  /// Lets go of the previous engines handed to this (next) generation, each once its engine's kick-start has settled:
  /// the engine started (and took over what it could), or its kick-start gave up (its creation, context or a dependency
  /// failed, or this generation was shut down).
  /// </summary>
  /// <remarks>
  /// A handover is otherwise only released once the next engine has been created, and the engine itself (see
  /// <see cref="IReplacesPreviousEngine"/>) only lets go when it starts. An engine that never started kept the previous
  /// engine, and all it had loaded, for as long as this generation lived. Per engine, and not when this generation is
  /// ready: an engine that is not required for running is not waited for there, and would lose its takeover.
  /// </remarks>
  /// <param name="handovers">The handovers of this generation's engines, by engine name.</param>
  private Task ReleaseHandoversWhenSettledAsync(IReadOnlyList<(string Name, Handover Handover)> handovers) =>
    Task.WhenAll(handovers.Select(h => ReleaseHandoverWhenSettledAsync(h.Name, h.Handover)));

  private async Task ReleaseHandoverWhenSettledAsync(string name, Handover handover)
  {
    try
    {
      // The kick-start depends on the engine's creation and context tasks; a retry keeps it from completing.
      await Mechanic.EnsureReadyAsync(name + EngineKickStartTask.PostFix).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      // The generation did not start: nothing will start the engine either.
      LogHandoverWaitFailed(ex, name);
    }

    handover.Release();
    if (_mechanic is { } mechanic && mechanic.Engines.TryGetValue(name, out var engine) && engine is IReplacesPreviousEngine replacing)
      replacing.ReleasePreviousEngine();
  }

  private static EngineDescriptor CloneDescriptor(
    EngineDescriptor source,
    Func<CancellationToken, Task<IEngine>> createOverride = null)
  {
    ArgumentNullException.ThrowIfNull(source);

    var clone = new EngineDescriptor(
      source.Name,
      createOverride ?? source.RegisteredEngineFactory,
      source.RequiredForRunning,
      source.Version)
    {
      ContextFactory = source.ContextFactory,
      RecoveryHelper = source.RecoveryHelper,
      // After RecoveryHelper, whose setter clears it: the next generation's tasks get enforcers of their own.
      RecoveryPolicy = source.RecoveryPolicy,
      RestartPolicy = source.RestartPolicy,
      RestartFromPrevious = source.RestartFromPrevious,
      // Keep the registered factory, also when the engine factory is a handover (see Handover).
      RegisteredEngineFactory = source.RegisteredEngineFactory,
    };

    foreach (var dependencyName in source.DependsOn)
    {
      clone.AddDependsOn(dependencyName);
    }

    return clone;
  }

  private static async Task<IEngine> CreateReplacementEngineFromPreviousAsync(
    EngineDescriptor candidate,
    Handover handover,
    bool definitionChanged,
    bool dependencyChanged,
    CancellationToken token)
  {
    // Handed over once: a later creation (there normally is none) starts from the registered factory.
    if (!handover.TryTake(out var previousEngine, out var previousDescriptor))
      return await candidate.RegisteredEngineFactory(token).ConfigureAwait(false);

    var next = await candidate.CreateEngineFromPreviousAsync(
      previousEngine,
      previousDescriptor,
      definitionChanged,
      dependencyChanged,
      token).ConfigureAwait(false);

    // A generation switch must produce a fresh runtime instance with a fresh token chain.
    if (ReferenceEquals(next, previousEngine))
      throw new InvalidOperationException(
        $"Engine '{candidate.Name}' returned the previous instance during generation switch.");

    handover.Release();
    return next;
  }

  /// <summary>
  /// Executes a coordinator operation either on the mechanic dispatcher thread (if enabled)
  /// or directly on the calling thread.
  /// </summary>
  /// <param name="action">The operation to execute.</param>
  /// <param name="token">
  /// A cancellation token controlling the caller's wait for this operation. If a dispatcher
  /// is used, the token is passed through to the queued work item.
  /// </param>
  /// <returns>A task that completes when the operation completes.</returns>
  /// <remarks>
  /// <para>
  /// This method also pulses the monitor so that the monitoring loop can re-check task state
  /// promptly when new work is scheduled.
  /// </para>
  /// </remarks>
  protected Task DispatchAsync(Func<Task> action, CancellationToken token = default)
  {
    PulseMonitor();
    if (_mechanicDispatcher == null)
      return action();
    return _mechanicDispatcher.InvokeAsync(action, token);
  }

  /// <summary>
  /// Executes a coordinator operation either on the mechanic dispatcher thread (if enabled)
  /// or directly on the calling thread, returning a result.
  /// </summary>
  /// <typeparam name="T">The operation result type.</typeparam>
  /// <param name="action">The operation to execute.</param>
  /// <param name="token">
  /// A cancellation token controlling the caller's wait for this operation. If a dispatcher
  /// is used, the token is passed through to the queued work item.
  /// </param>
  /// <returns>A task that completes with the operation result.</returns>
  /// <remarks>
  /// <para>
  /// This method also pulses the monitor so that the monitoring loop can re-check task state
  /// promptly when new work is scheduled.
  /// </para>
  /// </remarks>
  protected Task<T> DispatchAsync<T>(Func<Task<T>> action, CancellationToken token = default)
  {
    PulseMonitor();
    if (_mechanicDispatcher == null)
      return action();
    return _mechanicDispatcher.InvokeAsync<T>(action, token);
  }

  /// <summary>
  /// Wakes the monitoring loop to re-check mechanic task state.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is a best-effort signal. The monitor uses <see cref="SemaphoreSlim"/> as a wake-up mechanism;
  /// releasing after disposal or after cancellation is benign, so exceptions are intentionally ignored.
  /// </para>
  /// </remarks>
  private void PulseMonitor()
  {
    // Best-effort: do not throw if released after disposal or cancellation.
    try { _monitorPulse.Release(); } catch { /* ignore */ }
  }

  /// <summary>
  /// Ensures the monitoring task is created and started.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Monitoring is implemented as an <see cref="EngineTask"/> to reuse the existing recovery mechanism.
  /// The recovery policy is set to <see cref="RestartPolicy.Unlimited"/> so monitoring is resilient to
  /// transient failures.
  /// </para>
  /// <para>
  /// The monitoring task is started in a fire-and-forget manner; internal recovery handles restarts.
  /// </para>
  /// </remarks>
  private void EnsureMonitorStarted()
  {
    if (_monitorTask != null) return;

    // Monitoring is implemented as an EngineTask so that we can reuse the existing recovery mechanism.
    _monitorTask = new EngineTask(
            name: "MechanicMonitoring",
            workAsync: MonitorLoopAsync,
            engineToken: _linkedCts.Token,
            logger: _applicationLogger,
            requiredForRunning: false)
        .SetRecovery(RestartPolicy.Unlimited);

    // Fire-and-forget start. If the monitor fails, EngineTask recovery will re-run it.
    _ = _monitorTask.FireAsync(EngineTaskTrigger.Start);
  }

  /// <summary>
  /// Background monitoring loop that periodically ticks the mechanic and re-triggers start for any
  /// tasks that are in a (re)startable state (e.g., Created/Reset).
  /// </summary>
  /// <param name="token">Cancellation token that ends the monitoring loop.</param>
  /// <remarks>
  /// <para>
  /// The loop has two modes:
  /// <list type="bullet">
  /// <item>
  /// Active polling: while the mechanic reports pending work, the loop ticks and sleeps for a short interval.
  /// </item>
  /// <item>
  /// Parked: once there is no pending work, the loop waits on <see cref="_monitorPulse"/> until
  /// an external call (e.g., GetEngineAsync / EnsureReadyAsync / InvalidateContext...) signals that work may exist.
  /// </item>
  /// </list>
  /// </para>
  /// <para>
  /// Mechanic operations are executed via the dispatcher to ensure all mechanic state mutation occurs
  /// on the dedicated mechanic thread.
  /// </para>
  /// </remarks>
  private async Task MonitorLoopAsync(CancellationToken token)
  {
    if (_mechanicDispatcher == null) return;
    // Ensure the dispatcher is up before monitoring begins.
    _mechanicDispatcher.Start();

    // Not necessary: Ensure we do at least one tick immediately after StartAsync is fired.
    // PulseMonitor();

    // Poll while there is unfinished work, otherwise park until pulsed.
    var pollInterval = TimeSpan.FromMilliseconds(200);

    while (!token.IsCancellationRequested)
    {
      bool hasPending = false;
      try
      {
        hasPending = await _mechanicDispatcher
            .InvokeAsync(() => Mechanic.MonitorTickAsync(), token)
            .ConfigureAwait(false);
      }
      catch (TaskCanceledException)
      {
        // Application is shutting down.
        return;
      }
      catch (OperationCanceledException) when (token.IsCancellationRequested)
      {
        return;
      }

      if (hasPending)
      {
        await Task.Delay(pollInterval, token).ConfigureAwait(false);
        continue;
      }

      // No pending tasks: wait until someone requests an engine / readiness / invalidation.
      await _monitorPulse.WaitAsync(token).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Starts engine orchestration.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method is idempotent; subsequent calls ensure start triggers are fired.
  /// </para>
  /// <para>
  /// A failed start is final for this coordinator: every later call (also through <see cref="EnsureReadyAsync"/> and
  /// <see cref="GetEngineAsync(string, CancellationToken)"/>) throws the same exception. Starting only fails while the task graph is built from the
  /// registered definitions (an engine that depends on one that is not registered, or a dependency cycle); loading
  /// engines fails into their tasks, which retry. The definitions cannot change once the start is claimed, so a retry would
  /// fail in the same way, and the graph that failed is never started half-wired. Recover with a generation switch
  /// (<see cref="RestartWithDefinitionsAsync"/>) with definitions that are complete.
  /// </para>
  /// </remarks>
  public async Task StartAsync()
  {
    EnsureMonitorStarted();

    // Claim the first start atomically. Checking State and then starting outside the lock let two concurrent first
    // callers both run Mechanic.StartAsync(), setting up the mechanic's task dictionary twice at the same time.
    TaskCompletionSource startClaimed = null;
    Task mechanicStart;
    MechanicEngine mechanic;
    lock (_sync)
    {
      mechanic = Mechanic;
      if (_mechanicStart == null)
      {
        AppMachine.Fire(EngineCoordinatorTrigger.Start);
        startClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mechanicStart = startClaimed.Task;
      }
      mechanicStart = _mechanicStart;
    }

    if (startClaimed != null)
    {
      // Run the start outside the lock (no awaits under _sync); later callers await its completion.
      try
      {
        await mechanic.StartAsync().ConfigureAwait(false);
        startClaimed.SetResult();
      }
      catch (Exception ex)
      {
        startClaimed.SetException(ex);
        throw;
      }
      return;
    }

    await mechanicStart.ConfigureAwait(false);

    // A _monitorTask is created on first StartAsync() and started fire-and-forget.
    // The monitor loop:
    // - ticks while there is pending work (poll interval 200ms)
    // - otherwise blocks on _monitorPulse (a SemaphoreSlim)
    await mechanic.TriggerStartAsync().ConfigureAwait(false);
  }

  /// <summary>
  /// Ensures that all required engines are Ready.
  /// </summary>
  /// <param name="token">Cancels the caller's wait; the engines go on starting.</param>
  /// <remarks>
  /// This implicitly starts the coordinator if it has not been started yet. Only the start runs on the dispatcher:
  /// waiting there would hold the dispatcher, and every call queued behind it, until all engines are Ready.
  /// </remarks>
  public async Task EnsureReadyAsync(CancellationToken token = default)
  {
    await DispatchAsync(StartAsync, token).ConfigureAwait(false);
    await Mechanic.EnsureReadyAsync().WaitAsync(token).ConfigureAwait(false);
  }

  /// <summary>
  /// Initiates application shutdown and waits for completion.
  /// </summary>
  /// <remarks>
  /// On a coordinator replaced by a generation switch this also shuts down the current generation, as lookups on it
  /// are answered by that generation (see <see cref="Successor"/>).
  /// </remarks>
  public async Task ShutdownAsync(CancellationToken token = default)
  {
    try
    {
      await DispatchAsync(ShutdownAsyncInt, token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!token.IsCancellationRequested && _linkedCts.IsCancellationRequested)
    {
      // Fix (GenerationSwitchEdgeCaseTest): a second shutdown with the dispatcher on waited forever for the stopped
      // dispatcher. The dispatcher no longer runs, so shut down here, as without a dispatcher. ShutdownAsyncInt is
      // safe to run next to a shutdown in progress: it cancels and changes state under the lock, and returns once the
      // coordinator is stopping. Checking the state instead of the token was not enough: a shutdown running at the same
      // time cancels the token just before it changes the state.
      await ShutdownAsyncInt().ConfigureAwait(false);
    }

    // Fix (GenerationSwitchEdgeCaseTest): a host that shut down the coordinator it started with left the current
    // generation running. The switch itself shuts the replaced generation down with ShutdownAsyncInt, not with this.
    if (Successor is { } current)
      await current.ShutdownAsync(token).ConfigureAwait(false);
  }

  /// <summary>
  /// Internal implementation of <see cref="ShutdownAsync"/> executed on the dispatcher thread.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Cancels the linked token source, transitions the coordinator into shutdown states, stops the mechanic,
  /// and completes the shutdown transition.
  /// </para>
  /// <para>
  /// This method is safe to call multiple times; subsequent calls will observe that shutdown is already in progress.
  /// </para>
  /// </remarks>
  private async Task ShutdownAsyncInt()
  {
    lock (_sync)
    {
      // Cancel any pending operations.
      _linkedCts.CancelBestEffort(_applicationLogger);
      // Fire shutdown if not already stopping.
      if (State >= EngineCoordinatorState.Stopping) return;
      AppMachine.Fire(EngineCoordinatorTrigger.StartShutdown);
    }

    if (_mechanic != null)
      await _mechanic.StopAsync().ConfigureAwait(false);

    lock (_sync)
    {
      AppMachine.Fire(EngineCoordinatorTrigger.CompleteShutdown);
    }
  }

  /// <summary>
  /// Invalidates the context for a specific engine.
  /// </summary>
  /// <param name="name">Name of the engine whose context is invalidated.</param>
  /// <param name="token">Cancellation token controlling the dispatched invalidation request.</param>
  /// <remarks>
  /// This forces re-initialization of the affected engine and its dependents.
  /// </remarks>
  public Task InvalidateContextForEngineAsync(string name, CancellationToken token = default)
    => DispatchAsync(() => InvalidateContextForEngineAsyncInt(name), token);

  /// <summary>
  /// Internal implementation of <see cref="InvalidateContextForEngineAsync(string, CancellationToken)"/>.
  /// </summary>
  /// <param name="name">Name of the engine whose context should be invalidated.</param>
  /// <remarks>
  /// Fires an application-level invalidate trigger and delegates to the mechanic to invalidate the
  /// engine context and dependent engines.
  /// </remarks>
  public Task InvalidateContextForEngineAsyncInt(string name)
  {
    lock (_sync) AppMachine.Fire(EngineCoordinatorTrigger.InvalidateContext);
    return Mechanic.InvalidateContextForEngineAsync(name);
  }

  /// <summary>
  /// Pushes an already-computed context into a specific, already-running engine instance,
  /// without resetting the engine's own tasks or recreating the engine.
  /// </summary>
  /// <param name="name">Name of the engine whose context should be refreshed.</param>
  /// <param name="context">
  /// The freshly computed context to assign. Callers must compute this themselves (e.g. by calling
  /// their own context-factory logic directly) rather than relying on this method to re-invoke the
  /// engine's registered <see cref="EngineDescriptor.ContextFactory"/> — that factory's closure may
  /// have been captured at the time of the *original* registration and can be stale by the time a
  /// caller decides a refresh (rather than a full restart) is appropriate.
  /// </param>
  /// <param name="token">Cancellation token controlling the dispatched request.</param>
  /// <remarks>
  /// Unlike <see cref="InvalidateContextForEngineAsync(string, CancellationToken)"/>, this does NOT
  /// reset the engine's kick-start task or any of its internal tasks — it only assigns the supplied
  /// <see cref="IEngineContext"/> to the live engine via its <c>Context</c> setter, which fires the
  /// engine's own <c>ContextChanged</c> hook. It is the engine's responsibility to decide, from the
  /// old vs. new context, what (if anything) needs to be invalidated/reset. Use this when the caller
  /// has already determined that a full engine restart is not required.
  /// </remarks>
  public Task RefreshEngineContextAsync(string name, IEngineContext context, CancellationToken token = default)
    => DispatchAsync(() => RefreshEngineContextAsyncInt(name, context), token);

  /// <summary>
  /// Internal implementation of <see cref="RefreshEngineContextAsync(string, IEngineContext, CancellationToken)"/>.
  /// </summary>
  private Task RefreshEngineContextAsyncInt(string name, IEngineContext context)
  {
    ArgumentNullException.ThrowIfNull(context);

    bool refreshing;
    lock (_sync)
    {
      refreshing = State == EngineCoordinatorState.Running;
      AppMachine.Fire(EngineCoordinatorTrigger.RefreshEngine);
    }

    try
    {
      Mechanic.RegisterContext(name, context);
    }
    finally
    {
      // The coordinator follows the mechanic, and a refresh leaves the mechanic's tasks completed (the engine itself
      // decides what to reload). No mechanic status change follows that would bring the coordinator back, so return
      // to Running here.
      lock (_sync)
      {
        // Fix (OptionalEngineTest): required engines only, as in Mechanic_EngineStatusChanged.
        if (refreshing && State == EngineCoordinatorState.Initializing && _mechanic.IsRequiredCompletedWithSuccess())
          AppMachine.Fire(EngineCoordinatorTrigger.EngineReady);
      }
    }

    return Task.CompletedTask;
  }

  /// <summary>
  /// Returns an engine if it is already Ready.
  /// </summary>
  /// <param name="name">Engine name.</param>
  public IEngine GetEngineIfReady(string name)
  {
    // This is a synchronous accessor; we keep it lightweight and avoid cross-thread waits.
    // Callers that need a consistent view should use GetEngineAsync / EnsureReadyAsync.
    // A coordinator replaced by a generation switch has stopped its engines: answer from the next generation.
    return Successor is { } replaced ? replaced.GetEngineIfReady(name) : _mechanic?.GetEngineIfReady(name);
  }

  /// <summary>
  /// Returns an engine in whatever state it is, or null if it has not been created (yet). Does not wait.
  /// </summary>
  /// <param name="name">Engine name.</param>
  public IEngine GetEngineIfCreated(string name)
  {
    // A coordinator replaced by a generation switch has stopped its engines: answer from the next generation.
    if (Successor is { } replaced)
      return replaced.GetEngineIfCreated(name);
    return _mechanic is { } mechanic && mechanic.Engines.TryGetValue(name, out var engine) ? engine : null;
  }

  /// <summary>
  /// Waits indefinitely for an engine to become Ready.
  /// </summary>
  /// <param name="name">Engine name.</param>
  /// <param name="throwErrorIfNotFound">
  /// If true, throws when the engine name is not registered.
  /// </param>
  /// <param name="token">Cancels the caller's wait; the engine goes on starting.</param>
  public Task<IEngine> GetEngineAsync(string name, bool throwErrorIfNotFound = true, CancellationToken token = default)
    => GetEngineWhenReadyAsync(name, throwErrorIfNotFound, Timeout.Infinite, token);

  /// <summary>
  /// Waits for an engine to become Ready or until cancelled.
  /// </summary>
  /// <param name="name">Engine name.</param>
  /// <param name="token">Cancellation token controlling the wait.</param>
  public Task<IEngine> GetEngineAsync(string name, CancellationToken token) => GetEngineAsync(name, Timeout.Infinite, token);

  /// <summary>
  /// Waits for an engine to become Ready within a bounded time.
  /// </summary>
  /// <param name="name">Engine name.</param>
  /// <param name="milliSecondsTimeOut">Maximum wait time in milliseconds.</param>
  /// <param name="token">Cancellation token controlling the wait.</param>
  public Task<IEngine> GetEngineAsync(string name, int milliSecondsTimeOut, CancellationToken token = default)
    => GetEngineWhenReadyAsync(name, false, milliSecondsTimeOut, token);

  /// <summary>
  /// Waits for an engine to become Ready within a bounded time.
  /// </summary>
  /// <param name="name">Engine name.</param>
  /// <param name="throwErrorIfNotFound">If true, throws when the engine name is not registered.</param>
  /// <param name="milliSecondsTimeOut">Maximum wait time in milliseconds.</param>
  /// <param name="token">Cancellation token controlling the wait.</param>
  /// <returns>The engine once Ready; null when it is not Ready within the time-out (it goes on starting).</returns>
  public Task<IEngine> GetEngineAsync(string name, bool throwErrorIfNotFound, int milliSecondsTimeOut, CancellationToken token = default)
    => GetEngineWhenReadyAsync(name, throwErrorIfNotFound, milliSecondsTimeOut, token);

  /// <summary>
  /// Starts the coordinator if needed, then waits until the engine is Ready, the time-out passes or the caller cancels.
  /// </summary>
  /// <param name="name">Engine name.</param>
  /// <param name="throwErrorIfNotFound">If true, throws when the engine name is not registered.</param>
  /// <param name="milliSecondsTimeOut">Maximum wait time in milliseconds, or <see cref="Timeout.Infinite"/>.</param>
  /// <param name="token">Cancellation token controlling the wait.</param>
  /// <returns>The engine once Ready; otherwise null.</returns>
  /// <remarks>
  /// Only the start runs on the dispatcher: waiting there would hold the dispatcher, and every call queued behind it
  /// (other lookups, a shutdown), until the engine is Ready. The wait itself runs as it does without a dispatcher.
  /// </remarks>
  private async Task<IEngine> GetEngineWhenReadyAsync(string name, bool throwErrorIfNotFound, int milliSecondsTimeOut, CancellationToken token)
  {
    // A coordinator replaced by a generation switch has stopped its engines: answer from the next generation.
    if (Successor is { } replaced)
      return await replaced.GetEngineWhenReadyAsync(name, false, milliSecondsTimeOut, token).ConfigureAwait(false);

    IEngine engine;
    try
    {
      await DispatchAsync(StartAsync, token).ConfigureAwait(false);
      engine = milliSecondsTimeOut == Timeout.Infinite
        ? await Mechanic.GetEngineWhenReadyAsync(name, throwErrorIfNotFound).WaitAsync(token).ConfigureAwait(false)
        : await Mechanic.GetEngineWhenReadyAsync(name, throwErrorIfNotFound, milliSecondsTimeOut, token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (Successor != null && !token.IsCancellationRequested)
    {
      // The switch shut this generation down (its dispatcher or mechanic was cancelled) while the call was queued or
      // waiting; the caller did not cancel.
      engine = null;
    }

    // Fix (RebuiltEngineSwitchTest): a switch that rebuilds this engine from scratch stopped it before the next
    // generation started, and before it hands over (Successor). Wait for the next generation to start, and ask it.
    if (engine == null && Successor == null && Volatile.Read(ref _rebuildingSwitch) is { } rebuilding &&
        rebuilding.Names.Contains(name))
    {
      var next = await rebuilding.Next.Task.WaitAsync(token).ConfigureAwait(false);
      return next == null ? null : await next.GetEngineWhenReadyAsync(name, false, milliSecondsTimeOut, token).ConfigureAwait(false);
    }

    // Replaced by a generation switch while waiting: this generation stopped its engines, ask the next one. An engine
    // that the switch released is not found there, and gives null.
    return engine ?? (Successor is { } successor
      ? await successor.GetEngineWhenReadyAsync(name, false, milliSecondsTimeOut, token).ConfigureAwait(false)
      : null);
  }

  /// <summary>
  /// Disposes coordinator-owned resources.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This releases monitor and cancellation resources and disposes the dispatcher if present.
  /// Callers should ensure shutdown has been initiated if they require an orderly stop.
  /// </para>
  /// </remarks>
  public void Dispose()
  {
    _monitorPulse.Dispose();
    _linkedCts.Dispose();
    _mechanicDispatcher?.Dispose();
  }

  // ---------------------------------------------------------------------
  // Logging methods
  // ---------------------------------------------------------------------

  [LoggerMessage(Level = LogLevel.Debug, Message = "'{CoordinatorName}': Unhandled coordinator trigger {Trigger} in state {State}, ignoring.")]
  private partial void LogUnhandledTrigger(string coordinatorName, EngineCoordinatorTrigger trigger, EngineCoordinatorState state);

  [LoggerMessage(Level = LogLevel.Debug, Message = "'{CoordinatorName}': Coordinator transition {Source} --{Trigger}--> {Destination}")]
  private partial void LogTransition(string coordinatorName, EngineCoordinatorState source, EngineCoordinatorTrigger trigger, EngineCoordinatorState destination);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Could not wait for the kick-start of engine '{EngineName}'; letting go of the engine it replaces.")]
  private partial void LogHandoverWaitFailed(Exception exception, string engineName);
}
