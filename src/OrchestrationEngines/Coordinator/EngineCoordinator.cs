using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Helpers;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;

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
/// Its <see cref="State"/> is not stored but computed, from its own start and shutdown and from the state of the
/// required engines, as an engine's state is computed from its tasks.
/// </para>
/// </remarks>
public partial class EngineCoordinator : IDisposable
{
  // What the state is computed from, next to the engines (see State): the start (_mechanicStart), the shutdown, and
  // the context operations in flight.
  private volatile bool _shutdownStarted;
  private volatile bool _shutdownCompleted;

  // The state last logged, for the transition log line (see LogStateChange).
  private int _lastState = (int)EngineCoordinatorState.Created;

  // Synchronization object for short, synchronous critical sections.
  // Invariant: no awaits occur while holding this lock.
  private readonly Lock _sync = new();

  // A running generation switch, from before it starts the next generation until it hands over: its engines, those it
  // rebuilds from scratch (stopped early) among them, with the next generation once it has started (null if it did not
  // start); null when no switch runs. See NextGenerationAsync.
  private SwitchBeforeHandover _switchBeforeHandover;

  // The context operations (invalidations and refreshes) in flight. While one is, the coordinator is not Running: an
  // operation that has not reset its engine yet would otherwise find it Running until the reload starts.
  private volatile int _contextOperationsInFlight;

  private volatile bool _disposed;

  // Set on the next coordinator of a switch that failed before it handed over, before the switch shuts it down: a
  // lookup forwarded to it while the switch ran gives null, as one on this coordinator does (see
  // CleanUpNextGenerationAsync).
  private volatile bool _abandoned;

  /// <summary>
  /// Called with the next coordinator that a generation switch of this coordinator has just created, before it registers
  /// the engines on it. For tests: a switch that fails hands nothing back.
  /// </summary>
  internal Action<EngineCoordinator> NextGenerationCreated { get; set; }

  /// <summary>
  /// Whether <see cref="Dispose"/> has been called.
  /// </summary>
  internal bool IsDisposed => _disposed;

  // Declarative configuration describing all engines and their dependencies.
  private readonly MechanicContext _context = new();

  // The mechanic is the meta-engine that owns the engine task DAG.
  private MechanicEngine _mechanic;

  // Governs coordinator and mechanic lifetime. Not linked to the application token, which outlives every generation:
  // the shutdown disposes _applicationRegistration instead, so a shut-down coordinator's source is let go (#107). The
  // source itself is disposed only by Dispose: CancellationToken and the shut-down coordinator's work still read it.
  private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
  private readonly CancellationTokenRegistration _applicationRegistration;
  private readonly string _coordinatorName;

  // Logger for coordinator-level (application-level) logging.
  private readonly ILogger _applicationLogger;

  // Monitor wake-up signal. Released whenever new work may exist; it holds one wake-up at most (see PulseMonitor).
  private readonly SemaphoreSlim _monitorPulse = new SemaphoreSlim(0, 1);

  // 1 from the release of a wake-up until the monitor loop takes it: only the caller that sets it releases.
  private int _monitorPulsePending;

  // The wake-ups released that the monitor loop has not taken yet: 0 or 1 (tests).
  internal int PendingMonitorPulses => Volatile.Read(ref _monitorPulsePending);

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
  public bool IsCancellationRequested => _lifetimeCts.IsCancellationRequested;

  /// <summary>
  /// Cancellation token governing coordinator and mechanic lifetime.
  /// </summary>
  public CancellationToken CancellationToken => _lifetimeCts.Token;

  /// <summary>
  /// Current application-level lifecycle state.
  /// </summary>
  /// <remarks>
  /// Computed on every read, not stored, so it cannot differ from what it describes. Created until the start, Stopping
  /// and Stopped from the shutdown. In between it follows the engines registered as required for running:
  /// WaitingForEngines if one is Faulted or Stopped (and is not being started again); else Initializing while a
  /// context operation is in flight; else Running if all are Ready; else Initializing once one has begun to start,
  /// and WaitingForEngines before that. A read has no side effect: a change is logged where it can happen (see
  /// <see cref="LogStateChange"/>).
  /// </remarks>
  public EngineCoordinatorState State => ComputeState();

  /// <summary>
  /// Logs the state if it differs from the one logged last. Called where the state can change: the start, the
  /// shutdown, a context operation, and a state change of the mechanic or of one of its engines.
  /// </summary>
  private void LogStateChange()
  {
    var state = ComputeState();
    var previous = (EngineCoordinatorState)Interlocked.Exchange(ref _lastState, (int)state);
    if (previous != state)
      BestEffort.Log(() => LogTransition(_coordinatorName, previous, state));
  }

  private void OnEngineStatusChanged(object sender, EngineStatusEventArgs e) => LogStateChange();

  private EngineCoordinatorState ComputeState()
  {
    if (_shutdownCompleted)
      return EngineCoordinatorState.Stopped;
    if (_shutdownStarted)
      return EngineCoordinatorState.Stopping;
    if (Volatile.Read(ref _mechanicStart) == null)
      return EngineCoordinatorState.Created;

    // The descriptors do not change once the start is claimed (see TryRegisterEngineDescriptor).
    var engines = _context.EngineDescriptors.Where(d => d.RequiredForRunning).Select(d => StartStateOf(d.Name))
      .ToList();
    if (engines.Any(e => e > EngineState.Ready))
      return EngineCoordinatorState.WaitingForEngines;
    if (_contextOperationsInFlight > 0)
      return EngineCoordinatorState.Initializing;
    if (engines.All(e => e == EngineState.Ready))
      return EngineCoordinatorState.Running;
    return engines.Any(e => e != EngineState.Created)
      ? EngineCoordinatorState.Initializing
      : EngineCoordinatorState.WaitingForEngines;
  }

  /// <summary>
  /// The state of a required engine, for <see cref="ComputeState"/>: its own state, which the mechanic's kick-start of
  /// it completes where the engine cannot tell. A Faulted or Stopped engine counts as Starting while its kick-start
  /// has not given up (it starts the engine again, and a lookup waits for that). An engine that has not started (not
  /// created, or still Created) is Faulted if its kick-start gave up, and Starting once it exists or its kick-start
  /// has been fired.
  /// </summary>
  private EngineState StartStateOf(string name)
  {
    IEngine engine = null;
    EngineTask kickStart = null;
    _mechanic?.Engines.TryGetValue(name, out engine);
    _mechanic?.Tasks.TryGetValue(EngineKickStartTask.KickStartName(name), out kickStart);
    var state = engine?.State ?? EngineState.Created;
    if (kickStart == null || state is EngineState.Starting or EngineState.Ready)
      return state;
    if (state != EngineState.Created)
      return kickStart.IsCompleted ? state : EngineState.Starting;
    var kickStartState = kickStart.State;
    if (kickStartState.IsFailed())
      return EngineState.Faulted;
    return engine != null || kickStartState != EngineTaskState.Created ? EngineState.Starting : EngineState.Created;
  }

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
    /// The current coordinator. Written (by <see cref="TryHandOver"/>) and read with <see cref="Interlocked"/> (full
    /// fences), so that a lookup on a replaced coordinator does not see a stale "still current" just after a switch.
    /// </summary>
    public EngineCoordinator Current => Interlocked.CompareExchange(ref _current, null, null);

    /// <summary>
    /// Makes <paramref name="next"/> the current coordinator if <paramref name="replaced"/> still is, and says whether
    /// it did: of two switches on one coordinator, only the first to hand over replaces it.
    /// </summary>
    public bool TryHandOver(EngineCoordinator replaced, EngineCoordinator next)
      => ReferenceEquals(Interlocked.CompareExchange(ref _current, next, replaced), replaced);
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
    // Link coordinator cancellation to application cancellation. An application token that is cancelled already
    // cancels the source here, as a linked source would start cancelled.
    _applicationRegistration = applicationToken.UnsafeRegister(
      static source => ((CancellationTokenSource)source).Cancel(), _lifetimeCts);
    _applicationToken = applicationToken;
    _lineage = new Lineage(this);
    _coordinatorName = coordinatorName;
    _applicationLogger = logger ?? NullLogger.Instance;
    _useDispatcher = useDispatcher;

    // Create a dispatcher, e.g. if we are on .NET48/.NET481 where threads can be Aborted.
    if (useDispatcher)
      _mechanicDispatcher = new MechanicDispatcher(MechanicDispatcher.DefaultThreadName, _applicationLogger, _lifetimeCts.Token);
  }

  /// <summary>
  /// The clock of the mechanic, which times the lookups' time-outs; <see cref="TimeProvider.System"/> unless set. The
  /// next generation of a switch takes it over. Tests set a clock they advance themselves.
  /// </summary>
  internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

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
    var mechanic = new MechanicEngine(_applicationLogger, _lifetimeCts.Token) { TimeProvider = TimeProvider };

    // The coordinator's state follows the engines and their kick-starts (the mechanic's tasks): log it when they
    // change.
    mechanic.EngineStatusChanged += OnEngineStatusChanged;
    mechanic.EngineRegistered += engine => engine.EngineStatusChanged += OnEngineStatusChanged;

    // Attach engine descriptors before starting.
    mechanic.Context = _context;
    return mechanic;
  }

  /// <summary>
  /// Registers a new engine with the coordinator.
  /// </summary>
  /// <param name="name">Logical engine name.</param>
  /// <param name="engineFactory">
  /// Factory used to create engine instances. A null result fails the engine's creation, as a factory that throws does.
  /// </param>
  /// <param name="requiredForRunning">
  /// Whether this engine must be Ready for the application to be considered running.
  /// </param>
  /// <param name="version">
  /// Integer definition version used for generation-diff restart decisions.
  /// </param>
  /// <returns>The created engine descriptor.</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if registration is attempted after startup has begun, or after a shutdown.
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
        $"Engine '{descriptor.Name}' cannot be registered after the coordinator has started or been shut down; " +
        "a running coordinator takes new engines through RestartWithDefinitionsAsync.");

  /// <summary>
  /// Registers a fully configured descriptor with the coordinator, if it has not started yet.
  /// </summary>
  /// <param name="descriptor">The descriptor.</param>
  /// <returns>
  /// False if the coordinator has started (also when it started just now, on another thread): add the engine with a
  /// generation switch (<see cref="RestartWithDefinitionsAsync"/>) instead. Also false once it has been shut down,
  /// started or not.
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
  /// <para>
  /// The engines rebuilt from scratch are stopped dependents first, as a shutdown stops them (see
  /// <see cref="MechanicEngine.StopAsync"/>), with the order running through the engines handed over: with B → H → A,
  /// where A and B are rebuilt and H is handed over, B stops before A. A handed-over engine is not stopped then, so one
  /// that depends on a rebuilt engine keeps running, and may see that dependency stop, until the old generation shuts
  /// down.
  /// </para>
  /// <para>
  /// A failure stopping the engines rebuilt from scratch counts as stopped; the first is thrown once all have stopped,
  /// and the next generation does not start. A failure
  /// shutting down the previous generation, once the new coordinator has taken over, is not: it is logged as a warning
  /// (the engine's own failure is logged as an error), the previous coordinator still ends
  /// <see cref="EngineCoordinatorState.Stopped"/>, and the new coordinator is returned.
  /// </para>
  /// <para>
  /// A switch that fails before the new coordinator takes over (a failed stop of the engines rebuilt from scratch, a
  /// failed start of the new coordinator, or a ready-wait that fails or is cancelled) cleans the new coordinator up: it
  /// is shut down if it was started, its engines and dispatcher stopping, and disposed otherwise. The switch then throws
  /// its own failure; a failure of the clean-up is logged as a warning. This coordinator stays current, and its engines
  /// keep running, except those rebuilt from scratch that were stopped already.
  /// </para>
  /// <para>
  /// A switch asked of a coordinator that another switch replaced, or one that another switch on this coordinator
  /// beat to the hand-over (without the dispatcher, switches are not queued), switches the current generation of the
  /// lineage instead (see <see cref="Successor"/>). The coordinator returned can then be more than one generation past
  /// this one.
  /// </para>
  /// </remarks>
  public async Task<EngineCoordinator> RestartWithDefinitionsAsync(IEnumerable<EngineDescriptor> candidateDescriptors,
    bool waitUntilReady = true, CancellationToken token = default)
  {
    ArgumentNullException.ThrowIfNull(candidateDescriptors);
    var candidateList = candidateDescriptors.Where(d => d != null).ToArray();
    var candidateByName = EngineDefinitionDiff.ToDictionaryByName(candidateList, nameof(candidateDescriptors));

    // Fix (GenerationSwitchEdgeCaseTest): a replaced coordinator built a generation from its own stopped engines next
    // to the one running, or, with the dispatcher on, threw a cancellation. It goes to the current generation now.
    EngineCoordinator next = null;
    if (Successor == null)
    {
      try
      {
        next = await DispatchAsync(
            () => RestartWithDefinitionsAsyncInt(candidateList, candidateByName, waitUntilReady, token), token)
          .ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (!token.IsCancellationRequested && Successor != null)
      {
        // Queued behind a switch that replaced this coordinator: its shutdown cancelled the items still queued.
      }
    }

    // Replaced, queued behind the switch that replaced it, or its switch lost the hand-over (null): the current
    // generation switches.
    return next ?? await Current.RestartWithDefinitionsAsync(candidateList, waitUntilReady, token)
      .ConfigureAwait(false);
  }

  /// <returns>The next coordinator, or null when another switch on this coordinator handed over first.</returns>
  private async Task<EngineCoordinator> RestartWithDefinitionsAsyncInt(EngineDescriptor[] candidateList,
    Dictionary<string, EngineDescriptor> candidateByName, bool waitUntilReady, CancellationToken token)
  {
    Dictionary<string, EngineDescriptor> currentDescriptorByName;
    Dictionary<string, IEngine> currentEngineByName;
    MechanicEngine mechanic;

    lock (_sync)
    {
      currentDescriptorByName = EngineDefinitionDiff.ToDictionaryByName(_context.EngineDescriptors,
        nameof(_context.EngineDescriptors));

      currentEngineByName = Engines
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
      mechanic = _mechanic;
    }

    var diff = EngineDefinitionDiff.Create(currentDescriptorByName, candidateByName);
    var versionChanged = new HashSet<string>(diff.VersionChangedEngineNames, StringComparer.OrdinalIgnoreCase);
    var restartRequired = new HashSet<string>(diff.RestartRequiredEngineNames, StringComparer.OrdinalIgnoreCase);
    var handovers = new List<(string Name, Handover Handover)>();
    var rebuiltFromScratch = new List<string>();
    var runtimeDescriptors = new List<EngineDescriptor>(candidateList.Length);

    // Copy every candidate before the next coordinator is created: a candidate that cannot be copied (a null engine
    // factory, which its setter does not check) throws here, and then there is no next coordinator to dispose (#59, 7a).
    foreach (var candidate in candidateList)
    {
      var name = candidate.Name;
      currentEngineByName.TryGetValue(name, out var previousEngine);
      currentDescriptorByName.TryGetValue(name, out var previousDescriptor);

      EngineDescriptor runtimeDescriptor;
      // Fix (RebuiltEngineSwitchTest): without a restart factory the next engine is built from scratch and cannot take
      // over anything, yet the previous engine was handed over and kept running until the next generation had started,
      // so two copies of its data could be loaded at the same time. It is now not handed over, and stopped before the
      // next generation starts.
      if (previousEngine == null || candidate.RestartFromPrevious == null)
      {
        runtimeDescriptor = candidate.Clone();
        if (previousEngine != null)
          rebuiltFromScratch.Add(name);
      }
      else
      {
        var definitionChanged = versionChanged.Contains(name);
        var dependencyChanged = restartRequired.Contains(name) && !definitionChanged;
        var handover = new Handover(previousEngine, previousDescriptor);
        handovers.Add((name, handover));

        runtimeDescriptor = candidate.Clone(
          ct => CreateReplacementEngineFromPreviousAsync(
            candidate,
            handover,
            definitionChanged,
            dependencyChanged,
            ct));
      }

      runtimeDescriptors.Add(runtimeDescriptor);
    }

    var nextCoordinator = new EngineCoordinator(
      _coordinatorName + ">", _applicationLogger, _useDispatcher, _applicationToken)
    {
      TimeProvider = TimeProvider,
    };
    NextGenerationCreated?.Invoke(nextCoordinator);

    // The previous engines are in the handovers now. Drop these references: they live on in this async method's state,
    // which the returned task keeps for as long as anyone holds it.
    currentEngineByName = null;
    currentDescriptorByName = null;

    // Until it hands over, the switch owns the next coordinator: a failure before then cleans it up, so that a switch
    // that fails (and one retried after it) leaves no coordinator behind, nor a next generation running next to this
    // one (#100). This generation stays current.
    var pending = new SwitchBeforeHandover(rebuiltFromScratch, handovers.Select(h => h.Name));
    try
    {
      foreach (var runtimeDescriptor in runtimeDescriptors)
        nextCoordinator.RegisterEngineDescriptor(runtimeDescriptor);
      await StartNextGenerationAsync(nextCoordinator, mechanic, pending, rebuiltFromScratch, handovers, waitUntilReady,
        token).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      await CleanUpNextGenerationAsync(nextCoordinator, pending, ex.GetType().Name).ConfigureAwait(false);
      throw;
    }

    // Hand over to the next generation and shut this one down. Lookups on this coordinator, also those that are
    // waiting now, are answered by the current generation (see Successor). Shutting down stops every engine of this generation, whatever its
    // state (a Faulted engine's kick-start would otherwise go on recovering and load it again next to its successor),
    // and cancels this generation's mechanic, so its kick-starts and their recovery end at once.
    nextCoordinator._lineage = _lineage;
    if (!_lineage.TryHandOver(this, nextCoordinator))
    {
      // Another switch on this coordinator handed over first (without the dispatcher, switches are not queued): one
      // generation runs in a lineage, so this one is cleaned up, and the switch goes to the current generation.
      nextCoordinator._lineage = new Lineage(nextCoordinator);
      await CleanUpNextGenerationAsync(nextCoordinator, pending, "another switch handed over first")
        .ConfigureAwait(false);
      return null;
    }
    try
    {
      await ShutdownAsyncInt().ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      // The next generation is live: the switch has succeeded, and its caller needs the new coordinator. The mechanic
      // logged the failed stop with the engine named; say that it was the replaced generation's. A logging provider
      // that throws must not make the switch fail after all.
      BestEffort.Log(() => LogReplacedGenerationStopFailed(ex, _coordinatorName));
    }

    return nextCoordinator;
  }

  /// <summary>
  /// The part of a generation switch that starts the next generation, until it hands over: stops the engines rebuilt
  /// from scratch, starts the next coordinator, and waits until it is ready if asked to.
  /// </summary>
  private async Task StartNextGenerationAsync(EngineCoordinator nextCoordinator, MechanicEngine mechanic,
    SwitchBeforeHandover pending, List<string> rebuiltFromScratch, List<(string Name, Handover Handover)> handovers,
    bool waitUntilReady, CancellationToken token)
  {
    // Fix (#145): mark every switch, not only one that rebuilds engines, so that a context operation on an engine
    // handed over to its restart factory also waits for the next generation and goes there (see ForEngineAsync). On
    // this generation it was lost: the next engine is made from the previous one, maybe before the reload ended, and
    // the previous one is shut down at the hand-over.
    Volatile.Write(ref _switchBeforeHandover, pending);
    try
    {
      // Fix (RebuiltEngineSwitchTest): stop the engines that are rebuilt from scratch first, so that their data is
      // released before their replacements load it again. Whatever their state, as the shutdown below does: a Faulted
      // engine's kick-start would otherwise go on recovering it. A lookup that sees one of them stop waits for the next
      // generation to start and asks it (see GetEngineWhenReadyAsync), as the lookups do once the switch hands over.
      if (rebuiltFromScratch.Count > 0)
      {
        // Dependents first, through the engines handed over too (see MechanicEngine.StopEnginesAsync). A failure counts
        // as stopped and the first is rethrown once all have stopped: the next generation does not start. The engines
        // are looked up by name when they are stopped, so one that a kick-start re-created since the copy above is the
        // one stopped (the one it replaced has ended already).
        await mechanic.StopEnginesAsync(rebuiltFromScratch).ConfigureAwait(false);
      }

      await nextCoordinator.StartAsync().ConfigureAwait(false);
      pending.Next.TrySetResult(nextCoordinator);
    }
    finally
    {
      // The next generation did not start: the waiting calls give null, and this generation answers them.
      pending.Next.TrySetResult(null);
    }
    // Not awaited: it ends when the next generation's engines have started or given up, long after a switch that does not
    // wait until they are ready.
    _ = nextCoordinator.ReleaseHandoversWhenSettledAsync(handovers);
    if (waitUntilReady)
      await nextCoordinator.EnsureReadyAsync(token).ConfigureAwait(false);
  }

  /// <summary>
  /// Cleans up the next coordinator of a generation switch that failed before it handed over, or that another switch
  /// beat to the hand-over: shuts it down if the switch started it (its engines and its dispatcher stop), and disposes
  /// it otherwise. A failure of the clean-up is logged, so that it does not change what the switch returns or throws.
  /// </summary>
  /// <remarks>
  /// Calls for an engine of the switch are no longer sent to the next generation (see
  /// NextGenerationAsync), and those sent to it already give null: the engine stopped, and nothing replaced it.
  /// </remarks>
  private async Task CleanUpNextGenerationAsync(EngineCoordinator nextCoordinator, SwitchBeforeHandover pending,
    string reason)
  {
    // The record this switch set: no other generation answers for its engines now. Only its own: a switch that
    // started since (without the dispatcher, switches are not queued) keeps its record.
    Interlocked.CompareExchange(ref _switchBeforeHandover, null, pending);
    nextCoordinator._abandoned = true;
    try
    {
      // Started: StartAsync left Created, also when it failed after that.
      // Through its dispatcher, as a host's shutdown is (#107): its mechanic is stopped on the thread its monitor ticks
      // run on. The next coordinator has not been handed over, so this shuts down no other generation.
      if (nextCoordinator.State != EngineCoordinatorState.Created)
        await nextCoordinator.ShutdownAsync().ConfigureAwait(false);
      else
        nextCoordinator.Dispose();
    }
    catch (Exception ex)
    {
      BestEffort.Log(() => LogNextGenerationCleanUpFailed(ex, nextCoordinator._coordinatorName, reason));
    }
  }

  /// <summary>
  /// A generation switch before it hands over: the engines it rebuilds from scratch (stopped before the next generation
  /// started), those it hands over to their restart factories, and that next generation once it has started (null if
  /// it did not start).
  /// </summary>
  private sealed class SwitchBeforeHandover(IEnumerable<string> rebuilt, IEnumerable<string> handedOver)
  {
    private readonly HashSet<string> _rebuilt = new(rebuilt, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _handedOver = new(handedOver, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the next generation answers for <paramref name="name"/>: an engine the switch rebuilds from scratch, or,
    /// with <paramref name="handedOverToo"/>, one it hands over to its restart factory.
    /// </summary>
    public bool Answers(string name, bool handedOverToo) =>
      _rebuilt.Contains(name) || (handedOverToo && _handedOver.Contains(name));

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
    private readonly Lock _sync = new();

    public bool TryTake(out IEngine previous, out EngineDescriptor previousDescriptor)
    {
      lock (_sync)
      {
        previous = _previousEngine;
        previousDescriptor = _previousDescriptor;
        return previous != null;
      }
    }

    public void Release()
    {
      lock (_sync)
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
      // The kick-start depends on the engine's creation and context tasks; a retry keeps it from completing, and a
      // reset of it is followed to its next run.
      await Mechanic.EnsureTaskReadyAsync(EngineKickStartTask.KickStartName(name)).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      // The kick-start gave up, or the generation did not start: nothing will start the engine either.
      LogHandoverWaitFailed(ex, name);
    }

    handover.Release();
    if (_mechanic is { } mechanic && mechanic.Engines.TryGetValue(name, out var engine) && engine is IReplacesPreviousEngine replacing)
      replacing.ReleasePreviousEngine();
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

    // Checked before the handover is released, so a retry is offered the previous engine again, as after a throw.
    if (next == null)
      throw new InvalidOperationException(candidate.RestartFromPrevious == null
        ? $"The factory of engine '{candidate.Name}' returned null."
        : $"The restart factory of engine '{candidate.Name}' returned null.");

    // A generation switch must produce a fresh runtime instance with a fresh token chain.
    // Not retried (an EngineTaskException): the factory would return the same instance again.
    if (ReferenceEquals(next, previousEngine))
      throw new EngineReturnedPreviousInstanceException(candidate.Name);

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
    // Fix (MonitorPulse_ComesAfterTheWorkIsHandedOver): the pulse came before the work was queued, so the tick it
    // started could run before that work, and with one wake-up held for all calls no later tick was due for it.
    try
    {
      if (_mechanicDispatcher == null)
        return action();
      return _mechanicDispatcher.InvokeAsync(action, token);
    }
    finally
    {
      PulseMonitor();
    }
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
    // Pulses once the work is handed over, as the non-generic overload does.
    try
    {
      if (_mechanicDispatcher == null)
        return action();
      return _mechanicDispatcher.InvokeAsync<T>(action, token);
    }
    finally
    {
      PulseMonitor();
    }
  }

  /// <summary>
  /// Wakes the monitoring loop to re-check mechanic task state.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is a best-effort signal. The monitor uses <see cref="SemaphoreSlim"/> as a wake-up mechanism;
  /// releasing after disposal or after cancellation is benign, so exceptions are intentionally ignored.
  /// </para>
  /// <para>
  /// The signal holds one wake-up at most: one tick of the parked monitor sees all the work the calls before it
  /// scheduled, so calls made while the monitor polls leave one tick for later, not one each. Callers pulse after
  /// their work is queued, and the loop clears <see cref="_monitorPulsePending"/> before it queues its tick, so a
  /// caller that finds a wake-up pending has its work queued before that tick.
  /// </para>
  /// </remarks>
  private void PulseMonitor()
  {
    // Fix (MonitorPulse_ManyCalls_LeaveOneWakeUp): every call released the semaphore, so calls made while the
    // monitor polled each left one more tick for later. Only the caller that sets the flag releases, so no caller
    // releases a full semaphore.
    if (Interlocked.CompareExchange(ref _monitorPulsePending, 1, 0) != 0) return;
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
            engineToken: _lifetimeCts.Token,
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
      // Taken: the next call releases a new wake-up. Cleared before the tick is queued (see PulseMonitor).
      Volatile.Write(ref _monitorPulsePending, 0);
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
        startClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mechanicStart = startClaimed.Task;
      }
      mechanicStart = _mechanicStart;
    }
    LogStateChange();

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
    await Mechanic.EnsureReadyAsync().WaitObservedAsync(token).ConfigureAwait(false);
  }

  /// <summary>
  /// Waits until the task <paramref name="taskName"/> of the engine <paramref name="engineName"/> has completed with
  /// success, starting the coordinator if it has not been started yet.
  /// </summary>
  /// <remarks>
  /// It first waits for the engine to start (its kick-start, which ends once the engine is Ready or has failed), since
  /// an engine's tasks are set up when it starts: a task that completes early is reported once the engine is Ready.
  /// A generation switch during the wait is answered by the next generation; one that no longer has the engine
  /// reports it as unknown (<see cref="ArgumentException"/>). With the dispatcher on, a call during a switch does not
  /// wait for the switch to end: the next generation answers once it has started, so an engine of that generation can
  /// make the call from its own tasks. If the switch fails after that (during its wait until ready), such a call
  /// ends with an <see cref="EngineTaskNotReadyException"/> in state Cancelled: the failed switch stopped the engine
  /// it waited for (#154). One that reaches the next generation only after the failure shut it down is answered by
  /// this generation.
  /// </remarks>
  /// <param name="engineName">The name of the engine (compared ignoring case).</param>
  /// <param name="taskName">The name of the engine's task (compared ignoring case).</param>
  /// <param name="token">Cancels the caller's wait; the engine and its tasks go on.</param>
  /// <exception cref="ArgumentException">No engine of that name is registered, or the engine has no such task.</exception>
  /// <exception cref="EngineTaskNotReadyException">
  /// The task ended without success; or the engine could not be created or started, and then the exception names the
  /// mechanic's task that failed (its creation task, named after the engine, or its kick-start task) instead.
  /// </exception>
  public async Task EnsureEngineTaskReadyAsync(string engineName, string taskName, CancellationToken token = default)
  {
    ArgumentNullException.ThrowIfNull(engineName);
    ArgumentNullException.ThrowIfNull(taskName);
    await ForEngineAsync(engineName,
      generation => generation.EnsureEngineTaskReadyAsync(engineName, taskName, token),
      () => Mechanic.EnsureEngineTaskReadyAsync(engineName, taskName, token),
      token, handedOverToo: false).ConfigureAwait(false);
  }

  /// <summary>
  /// Runs <paramref name="onMechanic"/> for the engine <paramref name="engineName"/> on the generation that has it:
  /// <paramref name="onGeneration"/> on the next generation once this one was replaced, or once a switch that rebuilds
  /// the engine from scratch (or, with <paramref name="handedOverToo"/>, hands it over) has started it, else on this
  /// one, started if needed and <paramref name="start"/> is set. A
  /// generation switch during the call that ends it with a cancellation or a Cancelled task (its engine stopped) is
  /// answered by the next generation, if it has the engine; a call that completes on this generation before the switch
  /// hands over is not repeated there.
  /// </summary>
  /// <remarks>
  /// <paramref name="handedOverToo"/> is set for the operations that change the engine (an invalidation, a refresh, a
  /// task reset), which would be lost on the previous engine (#145); not for a wait, which the previous engine, still
  /// running, answers. With the dispatcher on, the next generation answers every call during a switch (see
  /// AnsweredByNextGeneration).
  /// </remarks>
  private async Task ForEngineAsync(string engineName, Func<EngineCoordinator, Task> onGeneration, Func<Task> onMechanic,
    CancellationToken token, bool start = true, bool handedOverToo = true)
  {
    // A coordinator replaced by a generation switch has stopped its engines: answer from the next generation. Fix
    // (ContextOperationGenerationTest): so does a switch that rebuilds this engine from scratch, which stops it before it
    // hands over. A call on this generation would then throw, be lost on the stopped engine, or start it again: wait for
    // the next generation and ask it, as the catch below does for a call the stop interrupts. With handedOverToo, also
    // for an engine the switch hands over (#145): the call would be lost on the previous engine, which the hand-over
    // shuts down. If the next generation did not start, this generation answers.
    if (await NextGenerationAsync(engineName, handedOverToo, Timeout.Infinite, token).ConfigureAwait(false) is
        { } nextGeneration
        && await AnsweredOnNextGenerationAsync(nextGeneration, onGeneration, token).ConfigureAwait(false))
      return;

    try
    {
      if (start)
        await DispatchAsync(StartAsync, token).ConfigureAwait(false);
      await onMechanic().ConfigureAwait(false);
    }
    // Pre-check the switch in the filter, so an exception with no switch behind it is not caught; NextGenerationAsync
    // confirms it.
    catch (Exception ex) when (ex is OperationCanceledException or EngineTaskNotReadyException { State: EngineTaskState.Cancelled }
      && !token.IsCancellationRequested && (Successor != null || Volatile.Read(ref _switchBeforeHandover) != null))
    {
      // A generation switch while waiting (it stopped this generation's engines, or has this engine): ask the next one,
      // if it has the engine.
      var next = await NextGenerationAsync(engineName, handedOverToo, Timeout.Infinite, token).ConfigureAwait(false);
      if (next == null || !await AnsweredOnNextGenerationAsync(next, onGeneration, token).ConfigureAwait(false))
        throw;
    }
  }

  /// <summary>
  /// Runs <paramref name="onGeneration"/> on <paramref name="next"/>: false if a switch that failed abandoned that
  /// generation and shut it down before the call got there (a cancellation the caller did not ask for), so that this
  /// generation, current again, answers it, as it answers a call made after the failure (#154).
  /// </summary>
  private static async Task<bool> AnsweredOnNextGenerationAsync(EngineCoordinator next,
    Func<EngineCoordinator, Task> onGeneration, CancellationToken token)
  {
    try
    {
      await onGeneration(next).ConfigureAwait(false);
      return true;
    }
    catch (OperationCanceledException) when (next._abandoned && !token.IsCancellationRequested)
    {
      return false;
    }
  }

  /// <summary>
  /// Resets tasks of the engine <paramref name="engineName"/>, and with <paramref name="resetDependentTasks"/> the
  /// engine's tasks that depend on them, dependents first; the tasks then run again (see
  /// <see cref="BaseEngine.ResetTasksAsync"/>). Starts the coordinator if it has not been started yet, and waits for the
  /// engine to start.
  /// </summary>
  /// <param name="engineName">The name of the engine (compared ignoring case).</param>
  /// <param name="taskNames">The names of the engine's tasks to reset (compared ignoring case).</param>
  /// <param name="resetDependentTasks">Whether to reset the engine's tasks that depend on them too.</param>
  /// <param name="token">Cancels the caller's wait for the engine to start.</param>
  /// <exception cref="ArgumentException">No engine of that name is registered, or it has no task of one of the names.</exception>
  /// <remarks>
  /// A generation switch during the wait is answered by the next generation, which resets its engine, as with
  /// <see cref="EnsureEngineTaskReadyAsync"/>. A call that arrives during a switch that has the engine (rebuilt from
  /// scratch, or handed over to its restart factory), before it hands over, waits until the switch has started the next
  /// generation and goes there, as with <see cref="InvalidateContextForEngineAsync(string, CancellationToken)"/>. The
  /// wait for the engine to start lasts until it is Ready or its start has failed (see
  /// <see cref="MechanicEngine.ResetEngineTasksAsync"/>). A task that hangs during the engine's first
  /// start can be reset without that wait through the engine itself (<see cref="GetEngineIfCreated"/>, cast to <see cref="BaseEngine"/>,
  /// then <see cref="BaseEngine.ResetTasksAsync"/>), or its run bounded with the task's <see cref="EngineTask.Timeout"/>.
  /// </remarks>
  /// <exception cref="InvalidOperationException">
  /// The engine was not created (its kick-start's exception is the <see cref="Exception.InnerException"/>), never
  /// started, or is not a <see cref="BaseEngine"/>.
  /// </exception>
  /// <exception cref="EngineTaskNotReadyException">
  /// The engine was stopped before or during the reset, and no next generation has it (its
  /// <see cref="EngineTaskNotReadyException.State"/> is Cancelled).
  /// </exception>
  public async Task ResetTasksForEngineAsync(string engineName, IEnumerable<string> taskNames, bool resetDependentTasks,
    CancellationToken token = default)
  {
    ArgumentNullException.ThrowIfNull(engineName);
    ArgumentNullException.ThrowIfNull(taskNames);
    await ForEngineAsync(engineName,
      generation => generation.ResetTasksForEngineAsync(engineName, taskNames, resetDependentTasks, token),
      () => Mechanic.ResetEngineTasksAsync(engineName, taskNames, resetDependentTasks, token),
      token).ConfigureAwait(false);
  }

  /// <summary>
  /// Whether the next generation of <paramref name="pending"/> answers for the engine <paramref name="name"/> once it
  /// has started.
  /// </summary>
  /// <remarks>
  /// Fix (RebuiltEngineSwitchTest): an engine the switch rebuilds from scratch, which it stopped before the next
  /// generation started. With <paramref name="handedOverToo"/>, also one it hands over (#145). Fix (#154): with the
  /// dispatcher, every engine. A call there used to queue behind the switch, which holds the dispatcher until it
  /// ends, and the next generation answered it. An engine of the next generation that made the call waited for itself.
  /// </remarks>
  private bool AnsweredByNextGeneration(SwitchBeforeHandover pending, string name, bool handedOverToo) =>
    QueuesBehindSwitch(pending) || pending.Answers(name, handedOverToo);

  /// <summary>
  /// Whether a call dispatched now would queue behind the switch <paramref name="pending"/>: with the dispatcher, a
  /// switch holds it until it ends (#154).
  /// </summary>
  private bool QueuesBehindSwitch(SwitchBeforeHandover pending) => _mechanicDispatcher != null && pending != null;

  /// <summary>
  /// The generation that answers for <paramref name="name"/> once this one was replaced: the one it handed over to, or
  /// the next one of a switch that answers for it (see <see cref="AnsweredByNextGeneration"/>), once it has started.
  /// Null if there is none, if that switch did not start it, or if it has not started within
  /// <paramref name="milliSecondsTimeOut"/>.
  /// </summary>
  private async Task<EngineCoordinator> NextGenerationAsync(
    string name, bool handedOverToo, int milliSecondsTimeOut, CancellationToken token)
  {
    if (Successor is { } successor)
      return successor;
    // A switch before it hands over (Successor) that answers for this engine: wait for the next generation to start,
    // and ask it.
    if (Volatile.Read(ref _switchBeforeHandover) is not { } pending
        || !AnsweredByNextGeneration(pending, name, handedOverToo))
      return null;
    if (milliSecondsTimeOut == Timeout.Infinite)
      return await pending.Next.Task.WaitObservedAsync(token).ConfigureAwait(false);

    // A timed lookup waits for it no longer than its time left (#156): stopping the rebuilt engines can take long.
    return await TimeProvider.WithinOrDefaultAsync(TimeSpan.FromMilliseconds(milliSecondsTimeOut), token,
      wait => pending.Next.Task.WaitObservedAsync(wait)).ConfigureAwait(false);
  }

  /// <summary>
  /// Initiates application shutdown and waits for completion.
  /// </summary>
  /// <remarks>
  /// <para>
  /// On a coordinator replaced by a generation switch this also shuts down the current generation, as lookups on it
  /// are answered by that generation (see <see cref="Successor"/>).
  /// </para>
  /// <para>
  /// An engine that fails to stop counts as stopped, so the engines it depends on still stop. The coordinator still
  /// ends <see cref="EngineCoordinatorState.Stopped"/>, and this rethrows the first failure once every engine has been
  /// stopped. A later call does not stop this coordinator's engines again.
  /// </para>
  /// </remarks>
  public async Task ShutdownAsync(CancellationToken token = default)
  {
    try
    {
      await DispatchAsync(ShutdownAsyncInt, token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!token.IsCancellationRequested && _lifetimeCts.IsCancellationRequested)
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
    // Shutting down, so the application token has nothing left to do here: take this coordinator's source off it
    // (#107). Not under _sync: if the application token is being cancelled on another thread, this waits for its
    // callback, which cancels the source, and a callback on the source may take _sync (an engine's state change).
    _applicationRegistration.Dispose();
    lock (_sync)
    {
      // Cancel any pending operations.
      _lifetimeCts.CancelBestEffort(_applicationLogger);
      if (_shutdownStarted) return;
      _shutdownStarted = true;
    }
    LogStateChange();

    ExceptionDispatchInfo stopFailure = null;
    try
    {
      if (_mechanic != null)
        await _mechanic.StopAsync().ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      stopFailure = ExceptionDispatchInfo.Capture(ex);
    }

    // A failed stop still ends the shutdown: nothing else would move the coordinator on from Stopping.
    _shutdownCompleted = true;
    LogStateChange();
    // An engine can outlive this coordinator (a generation switch hands it over): it must not keep it alive.
    foreach (var engine in _mechanic?.Engines.Values ?? [])
      engine.EngineStatusChanged -= OnEngineStatusChanged;
    stopFailure?.Throw();
  }

  /// <summary>
  /// Invalidates the context for a specific engine.
  /// </summary>
  /// <param name="name">Name of the engine whose context is invalidated.</param>
  /// <param name="token">Cancellation token controlling the dispatched invalidation request.</param>
  /// <remarks>
  /// This forces re-initialization of the affected engine and its dependents. It does not start a coordinator that has
  /// not started. On a coordinator that a generation switch has replaced (once it has handed over, see
  /// <see cref="Successor"/>), the newer generation invalidates its engine, as with
  /// <see cref="ResetTasksForEngineAsync"/>. A call that arrives during a switch that has the engine (rebuilt from
  /// scratch, or handed over to its restart factory), before it hands over, waits until the switch has started the next
  /// generation and goes there too.
  /// </remarks>
  /// <exception cref="ArgumentException">No engine is registered under <paramref name="name"/> (on the newer
  /// generation, once this coordinator was replaced).</exception>
  /// <exception cref="InvalidOperationException">The coordinator is shut down, so the engines would not start
  /// again, and it runs without a dispatcher (<c>useDispatcher: false</c>).</exception>
  /// <exception cref="OperationCanceledException">The coordinator is shut down and runs with a dispatcher (the
  /// default): its queue no longer takes work. Or <paramref name="token"/> was cancelled.</exception>
  public Task InvalidateContextForEngineAsync(string name, CancellationToken token = default)
    => ForEngineAsync(name,
      generation => generation.InvalidateContextForEngineAsync(name, token),
      () => DispatchAsync(() => InvalidateContextForEngineAsyncInt(name), token),
      token, start: false);

  /// <summary>
  /// Internal implementation of <see cref="InvalidateContextForEngineAsync(string, CancellationToken)"/>.
  /// </summary>
  /// <param name="name">Name of the engine whose context should be invalidated.</param>
  /// <remarks>
  /// Delegates to the mechanic to invalidate the engine context and dependent engines. The coordinator is not Running
  /// while the operation is in flight; after it, it is Running again once the required engines are Ready (at once
  /// after an optional engine's invalidation, or one the mechanic rejected).
  /// </remarks>
  public async Task InvalidateContextForEngineAsyncInt(string name)
  {
    BeginContextOperation();
    try
    {
      await Mechanic.InvalidateContextForEngineAsync(name).ConfigureAwait(false);
    }
    finally
    {
      EndContextOperation();
    }
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
  /// has already determined that a full engine restart is not required. It does not start a coordinator that has not
  /// started. On a coordinator that a generation switch has replaced (once it has handed over, see
  /// <see cref="Successor"/>), the context goes to the newer generation's engine, as with
  /// <see cref="ResetTasksForEngineAsync"/>. A call that arrives during a switch that has the engine (rebuilt from
  /// scratch, or handed over to its restart factory), before it hands over, waits until the switch has started the next
  /// generation and goes there too.
  /// </remarks>
  /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
  /// <exception cref="ArgumentException">No engine is registered under <paramref name="name"/> (on the newer
  /// generation, once this coordinator was replaced).</exception>
  public Task RefreshEngineContextAsync(string name, IEngineContext context, CancellationToken token = default)
    => ForEngineAsync(name,
      generation => generation.RefreshEngineContextAsync(name, context, token),
      () => DispatchAsync(() => RefreshEngineContextAsyncInt(name, context), token),
      token, start: false);

  /// <summary>
  /// Internal implementation of <see cref="RefreshEngineContextAsync(string, IEngineContext, CancellationToken)"/>.
  /// </summary>
  private Task RefreshEngineContextAsyncInt(string name, IEngineContext context)
  {
    ArgumentNullException.ThrowIfNull(context);

    BeginContextOperation();
    try
    {
      Mechanic.RegisterContext(name, context);
    }
    finally
    {
      EndContextOperation();
    }

    return Task.CompletedTask;
  }

  /// <summary>
  /// Begins a context operation (an invalidation or a refresh): until <see cref="EndContextOperation"/>, the
  /// coordinator is not Running (see <see cref="State"/>).
  /// </summary>
  private void BeginContextOperation()
  {
    lock (_sync)
      _contextOperationsInFlight++;
    LogStateChange();
  }

  /// <summary>
  /// Ends a context operation. After the last one in flight, the coordinator follows the required engines again.
  /// </summary>
  private void EndContextOperation()
  {
    lock (_sync)
      _contextOperationsInFlight--;
    LogStateChange();
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
  /// <remarks>
  /// On a coordinator replaced by a generation switch, the current generation answers: an engine that the switch left
  /// out gives null. With the dispatcher on, so does the next generation during a switch, once it has started, instead
  /// of the lookup waiting for the switch to end; an engine of the next generation can therefore make one from its own
  /// tasks. If the switch fails before its next generation has started, this generation answers; if it fails after,
  /// an engine already found there is one that the failure stops (#154); a lookup that reached it, or was still
  /// waiting there, when the failure shut it down is answered by this generation.
  /// </remarks>
  public Task<IEngine> GetEngineAsync(string name, bool throwErrorIfNotFound = true, CancellationToken token = default)
    => GetEngineWhenReadyAsync(name, throwErrorIfNotFound, Timeout.Infinite, token);

  /// <summary>
  /// Waits for an engine to become Ready or until cancelled.
  /// </summary>
  /// <param name="name">Engine name.</param>
  /// <param name="token">Cancellation token controlling the wait.</param>
  /// <remarks><inheritdoc cref="GetEngineAsync(string, bool, CancellationToken)" path="/remarks"/></remarks>
  public Task<IEngine> GetEngineAsync(string name, CancellationToken token) => GetEngineAsync(name, Timeout.Infinite, token);

  /// <summary>
  /// Waits for an engine to become Ready within a bounded time.
  /// </summary>
  /// <param name="name">Engine name.</param>
  /// <param name="milliSecondsTimeOut">Maximum wait time in milliseconds.</param>
  /// <param name="token">Cancellation token controlling the wait.</param>
  /// <remarks><inheritdoc cref="GetEngineAsync(string, bool, CancellationToken)" path="/remarks"/></remarks>
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
  /// <remarks><inheritdoc cref="GetEngineAsync(string, bool, CancellationToken)" path="/remarks"/></remarks>
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
    // A coordinator replaced by a generation switch has stopped its engines: answer from the next generation. Fix
    // (#154): with the dispatcher, so does a switch before it hands over, once its next generation has started (see
    // AnsweredByNextGeneration).
    var pending = Volatile.Read(ref _switchBeforeHandover);
    var startOnDispatcher = true;
    if (Successor != null || QueuesBehindSwitch(pending))
    {
      var switchWait = TimeProvider.GetTimestamp();
      var answering = await NextGenerationAsync(name, handedOverToo: false, milliSecondsTimeOut, token)
        .ConfigureAwait(false);
      if (answering == null)
      {
        // The time-out passed before the switch started its next generation: null. If it started it just after,
        // that generation answers, with an engine that is Ready already; if it did not, this generation does. So
        // does it once the switch has failed and cleaned up its next generation (the record is gone), or the
        // generation it handed over to.
        if (!pending.Next.Task.IsCompleted)
          return null;
        answering = Successor
          ?? (ReferenceEquals(Volatile.Read(ref _switchBeforeHandover), pending) ? pending.Next.Task.Result : null);
      }
      if (answering != null)
      {
        var found = await answering.GetEngineWhenReadyAsync(name, false, TimeLeft(milliSecondsTimeOut, switchWait),
          token).ConfigureAwait(false);
        // A failed switch abandoned that generation and shut it down before the lookup got there, or while it waited
        // there (null): this generation, current again, answers it, as it answers a call made after the failure (see
        // AnsweredOnNextGenerationAsync).
        if (found != null || !answering._abandoned)
          return found;
        // Not through the dispatcher if this generation has started: the failed switch holds it until its clean-up
        // has stopped that generation's engines, which no time-out bounds (#154).
        startOnDispatcher = Volatile.Read(ref _mechanicStart) is not { Status: TaskStatus.RanToCompletion };
      }
      milliSecondsTimeOut = TimeLeft(milliSecondsTimeOut, switchWait);
    }

    // The time-out counts from the end of the start, as the mechanic's own time-out does.
    var started = TimeProvider.GetTimestamp();
    IEngine engine;
    try
    {
      if (startOnDispatcher)
        await DispatchAsync(StartAsync, token).ConfigureAwait(false);
      started = TimeProvider.GetTimestamp();
      engine = milliSecondsTimeOut == Timeout.Infinite
        ? await Mechanic.GetEngineWhenReadyAsync(name, throwErrorIfNotFound, token).ConfigureAwait(false)
        : await Mechanic.GetEngineWhenReadyAsync(name, throwErrorIfNotFound, milliSecondsTimeOut, token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when ((Successor != null || _abandoned) && !token.IsCancellationRequested)
    {
      // The switch shut this generation down (its dispatcher or mechanic was cancelled) while the call was queued or
      // waiting, after handing over or, for a next generation it abandoned, after failing; the caller did not cancel.
      engine = null;
    }

    // Replaced by a generation switch while waiting: this generation stopped its engines, ask the next one (see
    // NextGenerationAsync). An engine that the switch released is not found there, and gives null.
    if (engine != null)
      return engine;
    var next = await NextGenerationAsync(name, handedOverToo: false, TimeLeft(milliSecondsTimeOut, started), token)
      .ConfigureAwait(false);
    return next == null
      ? null
      : await next.GetEngineWhenReadyAsync(name, false, TimeLeft(milliSecondsTimeOut, started), token)
        .ConfigureAwait(false);
  }

  /// <summary>
  /// What is left of a lookup's time-out that started at <paramref name="started"/> (a timestamp of
  /// <see cref="TimeProvider"/>), so a lookup passed on to the next generation keeps its deadline (#156). Nothing left
  /// is 0: the next generation then answers only with an engine that is Ready already.
  /// </summary>
  private int TimeLeft(int milliSecondsTimeOut, long started)
  {
    if (milliSecondsTimeOut == Timeout.Infinite)
      return Timeout.Infinite;
    var left = milliSecondsTimeOut - TimeProvider.GetElapsedTime(started).TotalMilliseconds;
    return left > 0 ? (int)left : 0;
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
    _disposed = true;
    _monitorPulse.Dispose();
    // The registration first: an application token cancelled after this must not cancel a disposed source.
    _applicationRegistration.Dispose();
    _lifetimeCts.Dispose();
    _mechanicDispatcher?.Dispose();
  }

  // ---------------------------------------------------------------------
  // Logging methods
  // ---------------------------------------------------------------------

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "'{CoordinatorName}': Coordinator transition {Source} --> {Destination}")]
  private partial void LogTransition(string coordinatorName, EngineCoordinatorState source,
    EngineCoordinatorState destination);

  [LoggerMessage(Level = LogLevel.Debug, Message = "The kick-start of engine '{EngineName}' did not complete with success, or could not be waited for; letting go of the engine it replaces.")]
  private partial void LogHandoverWaitFailed(Exception exception, string engineName);

  [LoggerMessage(Level = LogLevel.Warning, Message = "'{CoordinatorName}': Cleaning up the next generation of a switch "
    + "that did not hand over ({Reason}) failed; what the switch returns or throws is not changed by it.")]
  private partial void LogNextGenerationCleanUpFailed(Exception exception, string coordinatorName, string reason);

  [LoggerMessage(Level = LogLevel.Warning, Message = "'{CoordinatorName}': Stopping the generation this switch replaced failed; the switch has succeeded.")]
  private partial void LogReplacedGenerationStopFailed(Exception exception, string coordinatorName);
}
