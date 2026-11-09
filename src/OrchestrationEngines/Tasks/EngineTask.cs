using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Helpers;
using OrchestrationEngines.Logger;
using OrchestrationEngines.Restarter;
using Stateless;
using Stateless.Reflection;

namespace OrchestrationEngines.Tasks;

/// <summary>
/// Represents one step in an engine's startup/maintenance pipeline (e.g. create engine, compute context, kick-start engine).
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="EngineTask"/> is a small unit of orchestration that can depend on other tasks. Dependencies are expressed as a DAG.
/// A task typically starts, waits for dependencies (if any), then executes <see cref="_workAsync"/> and reaches a terminal state.
/// </para>
///
/// <para>
/// This class uses a Stateless state machine (<see cref="StateMachine"/>) to model transitions such as
/// Created → WaitingForDependencies → Running → CompletedWithSuccess, or failure/cancellation paths.
/// </para>
///
/// <para>
/// The <see cref="InnerTask"/> is the "completion latch" for external awaiting: it completes when the task reaches a terminal
/// state (success or failure), and it is reset when the task is reset.
/// </para>
/// </remarks>
public partial class EngineTask
{
  // ---------------------------
  // State machine plumbing
  // ---------------------------

  /// <summary>
  /// Stateless state machine, created on first touch and replaced on each reset. Access via <see cref="StateMachine"/>.
  /// </summary>
  private StateMachine<EngineTaskState, EngineTaskTrigger> _machine;

  /// <summary>
  /// Serializes externally fired triggers for this task's state machine.
  /// </summary>
  private readonly SemaphoreSlim _triggerLock = new(1, 1);

  /// <summary>
  /// Guards the lazy creation of <see cref="_machine"/> and its replacement on a reset. The first touch of a pristine task is genuinely
  /// concurrent — an unlocked <c>State</c> read (TriggerStartAsync's snapshot, the engine's ComputeState,
  /// the monitor loop) can race a dependency's CompleteDependency callback firing into the same task from
  /// a pool thread. See <see cref="StateMachine"/>.
  /// </summary>
  private readonly object _machineInitLock = new();

  /// <summary>
  /// Parameterized triggers used to capture a human-readable failure reason.
  /// </summary>
  protected StateMachine<EngineTaskState, EngineTaskTrigger>.TriggerWithParameters<string> _failTriggerWithReason;

  /// <summary>
  /// Parameterized trigger used to capture a human-readable recovery failure reason.
  /// </summary>
  protected StateMachine<EngineTaskState, EngineTaskTrigger>.TriggerWithParameters<string> _failRecoveryTriggerWithReason;

  /// <summary>
  /// Suppresses dependency callbacks while this task is being reset.
  /// </summary>
  private bool _suppressDependencyCallbacks;

  /// <summary>
  /// Set by <see cref="EndRunAsync"/> until the task is reset: a dependency completing or failing meanwhile is ignored,
  /// since the task would otherwise start on the token that was cancelled. Read and written under the trigger lock.
  /// </summary>
  private bool _runEnded;

  /// <summary>
  /// Enables or disables dependency callback suppression.
  /// </summary>
  internal void SetDependencyCallbackSuppression(bool suppress)
  {
    _suppressDependencyCallbacks = suppress;
  }

  // ---------------------------
  // Cancellation and completion
  // ---------------------------

  /// <summary>
  /// Engine-wide token: cancelling this token should cancel all tasks in the engine/coordinator scope.
  /// </summary>
  private readonly CancellationToken _engineToken;

  /// <summary>
  /// The clock that times the task's time-out and runs, and that a recovery policy set with
  /// <see cref="SetRecovery(RestartPolicy)"/> uses.
  /// </summary>
  public TimeProvider TimeProvider { get; }

  /// <summary>
  /// The token used by this task instance for its current run. This is a linked token source, created from <see cref="_engineToken"/>.
  /// </summary>
  /// <remarks>
  /// This token is recreated during reset so that re-executed tasks get a fresh cancellation scope.
  /// </remarks>
  public CancellationToken CancellationToken;

  /// <summary>
  /// Linked CTS used to generate <see cref="CancellationToken"/> for the current run of this task.
  /// </summary>
  protected CancellationTokenSource _linkedCts;

  /// <summary>
  /// Completion latch for the task. This completes when the task reaches a terminal state.
  /// </summary>
  /// <remarks>
  /// This is not the same as the underlying work task. It is a durable signal to external callers that the EngineTask lifecycle
  /// has finished (success/failure/cancel). It is recreated on reset.
  /// </remarks>
  private TaskCompletionSource<bool> _completionTcs = new();

  // ---------------------------
  // Work and logging
  // ---------------------------

  /// <summary>
  /// Delegate executed when the task enters Running. Implementations should respect <see cref="CancellationToken"/>.
  /// </summary>
  protected Func<CancellationToken, Task> _workAsync;

  /// <summary>
  /// Logger used by this task.
  /// </summary>
  public ILogger Logger { get; }

  /// <summary>
  /// Free-form description of what this task is currently doing, for status/monitoring displays
  /// (e.g. a "process state" table). Deliberately independent of logging: set this only at
  /// meaningful state transitions, not from every log call.
  /// </summary>
  public string StateMessage { get; set; }

  // ---------------------------
  // Identity and behavior flags
  // ---------------------------

  /// <summary>
  /// Unique name for this task. Typically includes engine name + postfix indicating purpose (e.g. "{oo}", "{+-}").
  /// </summary>
  public string Name { get; }

  /// <summary>
  /// If true, this task is required for the application/engine to be considered "running".
  /// </summary>
  /// <remarks>
  /// Design invariant (comment-only at present): a RequiredForRunning task should not depend on a non-required task
  /// to avoid "optional" dependencies blocking a required startup path.
  /// </remarks>
  public bool RequiredForRunning { get; } = true;

  // ---------------------------
  // Observability / public status
  // ---------------------------

  /// <summary>
  /// Current state of the task state machine.
  /// </summary>
  public EngineTaskState State => StateMachine.State;

  /// <summary>
  /// A task representing completion of the EngineTask lifecycle.
  /// </summary>
  /// <remarks>
  /// This task completes when the EngineTask reaches a terminal state (CompletedWithSuccess, Failed, Cancelled, etc.).
  /// It is reset when the EngineTask is reset.
  /// </remarks>
  public Task InnerTask => _completionTcs.Task;

  /// <summary>
  /// Optional per-run timeout. If set and positive, the underlying work is raced against a delay task.
  /// </summary>
  public TimeSpan? Timeout { get; set; } = null;

  /// <summary>
  /// Fluent configuration helper for timeout.
  /// </summary>
  public EngineTask SetTimeOut(TimeSpan timeOutMilliseconds)
  {
    Timeout = timeOutMilliseconds;
    return this;
  }

  /// <summary>
  /// True once the task has transitioned out of Created (i.e. it has been started and is either waiting or running).
  /// </summary>
  public virtual bool IsStarted => State.IsStarted();

  /// <summary>
  /// True if the task is currently executing work or recovery work (i.e. a worker is running).
  /// </summary>
  public virtual bool IsRunning => State.IsRunning();

  /// <summary>
  /// True when <see cref="InnerTask"/> has completed (terminal lifecycle reached).
  /// </summary>
  public virtual bool IsCompleted => InnerTask.IsCompleted;

  /// <summary>
  /// True if the state machine is in a failure terminal state (including cancel).
  /// </summary>
  public virtual bool IsFailed => State.IsFailed();

  /// <summary>
  /// True if the task reached the successful terminal state.
  /// </summary>
  public virtual bool IsCompletedWithSuccess => State == EngineTaskState.CompletedWithSuccess;

  // ---------------------------
  // Dependencies
  // ---------------------------

  /// <summary>
  /// Internal list of tasks that must complete successfully before this task can run.
  /// </summary>
  private readonly List<EngineTask> _dependsOn = [];

  /// <summary>
  /// Read-only view of dependencies.
  /// </summary>
  public IEnumerable<EngineTask> DependsOn => _dependsOn;

  // ---------------------------
  // Timing / diagnostics
  // ---------------------------

  /// <summary>
  /// UTC timestamp when the current/last run started (entry into Running).
  /// </summary>
  public DateTime? RunStartedAtUtc { get; private set; }

  /// <summary>
  /// UTC timestamp when the current/last run ended (exit from Running).
  /// </summary>
  public DateTime? RunEndedAtUtc { get; private set; }

  /// <summary>
  /// Duration of the last run.
  /// </summary>
  /// <remarks>
  /// - Zero if the task has never reached Running.
  /// - If currently running, duration is measured from start until now.
  /// </remarks>
  public TimeSpan? RunDuration =>
      (!RunStartedAtUtc.HasValue) ? TimeSpan.Zero : (RunEndedAtUtc ?? TimeProvider.GetUtcNow().UtcDateTime) - RunStartedAtUtc;

  /// <summary>
  /// Optional reason for failure, populated when failing via a trigger-with-reason or when the work delegate throws.
  /// </summary>
  public string FailReason { get; private set; }

  /// <summary>
  /// The exception the work or the recovery threw when the task last failed, or null (a time-out, a cancellation, a
  /// failed dependency). Cleared when the work or its recovery succeeds, and by a reset.
  /// </summary>
  /// <remarks>
  /// The task holds the exception, and what it refers to, until then: an exception that carries large objects or
  /// resources keeps them alive while the task stays failed.
  /// </remarks>
  public Exception FailException { get; private set; }

  /// <summary>
  /// Whether the task ended because it was cancelled (or because a task it depends on was), not because it failed.
  /// </summary>
  public bool WasCancelled { get; private set; }

  // ---------------------------
  // Recovery
  // ---------------------------

  /// <summary>
  /// Optional restart/recovery policy for this task.
  /// </summary>
  /// <remarks>
  /// If set, failures in Running can transition to Recovering, which invokes the enforcer.
  /// Dependency failures are not recovered here; the failing dependency task should carry its own recovery logic.
  /// </remarks>
  public IRestartPolicyEnforcer RecoveryHelper { get; set; } = null;

  /// <summary>
  /// Assigns the recovery policy enforcer used when this task can recover from running failures or cancellations.
  /// </summary>
  /// <param name="recoveryHelper">Recovery policy enforcer for this task.</param>
  public EngineTask SetRecovery(IRestartPolicyEnforcer recoveryHelper)
  {
    RecoveryHelper = recoveryHelper;
    return this;
  }

  /// <summary>
  /// Convenience overload that creates an enforcer from a policy specification.
  /// </summary>
  public EngineTask SetRecovery(RestartPolicy policy)
  {
    RecoveryHelper = policy.GetEnforcer(TimeProvider);
    return this;
  }

  // ---------------------------
  // Lifecycle events (dependency signaling)
  // ---------------------------

  /// <summary>
  /// Fired when this task reaches a successful terminal state.
  /// </summary>
  /// <remarks>
  /// Dependencies subscribe to this to detect progress and evaluate whether they can start running.
  /// </remarks>
  public event Action<EngineTask> CompletedStateReached;

  /// <summary>
  /// Fired when this task reaches a failed terminal state (including cancellation).
  /// </summary>
  /// <remarks>
  /// Dependencies subscribe to this so they can fail fast if a prerequisite fails.
  /// </remarks>
  public event Action<EngineTask> FailedStateReached;

  // ---------------------------
  // Constructors
  // ---------------------------

  /// <summary>
  /// Creates a task backed by an asynchronous delegate.
  /// </summary>
  /// <param name="name">Stable name used for identification and logging.</param>
  /// <param name="workAsync">Work to execute in Running state.</param>
  /// <param name="requiredForRunning">Whether this task is required for the engine/application to be "running".</param>
  /// <param name="logger">Logger sink. Wrapped (see <see cref="PrefixLogger"/>) so every message is tagged with <paramref name="name"/>.</param>
  /// <param name="engineToken">Engine/coordinator level cancellation token.</param>
  /// <param name="timeProvider">
  /// The clock for the time-out, the run times and the recovery backoff; <see cref="TimeProvider.System"/> when
  /// <c>null</c>. Tests pass a clock they advance themselves.
  /// </param>
  public EngineTask(
      string name,
      Func<CancellationToken, Task> workAsync,
      bool requiredForRunning,
      ILogger logger,
      CancellationToken engineToken,
      TimeProvider timeProvider = null)
  {
    Name = name ?? throw new ArgumentNullException(nameof(name));
    TimeProvider = timeProvider ?? TimeProvider.System;
    _workAsync = workAsync ?? throw new ArgumentNullException(nameof(workAsync));
    var taskPrefix = $"{Name}: ";
    Logger = new PrefixLogger(logger ?? NullLogger.Instance, () => taskPrefix);
    RequiredForRunning = requiredForRunning;

    // Link this task's CTS to the engine-wide token.
    _engineToken = engineToken;
    CreateToken();
  }

  /// <summary>
  /// Convenience constructor for synchronous work. The delegate is executed via <see cref="Task.Run(Action, CancellationToken)"/>.
  /// </summary>
  /// <remarks>
  /// Prefer the async constructor when possible. Use this for CPU-bound, synchronous work that needs to run on the thread pool.
  /// </remarks>
  public EngineTask(
      string name,
      Action<CancellationToken> workSync,
      bool requiredForRunning,
      ILogger logger,
      CancellationToken engineToken,
      TimeProvider timeProvider = null)
      : this(
          name,
          token => Task.Run(() => workSync(token), token),
           requiredForRunning,
           logger,
           engineToken,
           timeProvider)
  {
  }

  /// <summary>
  /// Requests cancellation of the current run of this task.
  /// </summary>
  /// <remarks>
  /// Cancelling the task does not guarantee the underlying work stops immediately; work delegates must respect the token.
  /// Until the work has stopped, the task's next run waits (see <see cref="StartRun"/>). A callback registered on the
  /// token that throws is logged, and the cancellation goes ahead.
  /// </remarks>
  protected void RequestCancellation()
  {
    // A cancelled source may have been replaced and disposed meanwhile (see CreateToken); CancelBestEffort leaves it
    // alone. Under the gate, so a dispose waits for a cancel that is still notifying (see _sourceGate).
    lock (_sourceGate)
      _linkedCts.CancelBestEffort(Logger);
  }

  /// <summary>
  /// Makes cancelling the task's source and disposing it exclusive: a source is flagged cancelled before its callbacks
  /// run, so a reset or release on another thread could otherwise dispose it while a cancel is still notifying. It
  /// covers the task's own cancels (RequestCancellation), not one that comes through the engine's token.
  /// </summary>
  private readonly object _sourceGate = new();

  /// <summary>
  /// Fix: counts the resets, so that the work of a run that was reset can tell it is no longer current.
  /// </summary>
  /// <remarks>
  /// Changed with <see cref="Interlocked.Increment(ref int)"/> and read with <c>Volatile.Read</c>
  /// (see <see cref="CurrentRun"/>), not a <c>volatile</c> field: the keyword does not make the increment atomic.
  /// Whether a run is current is decided under the trigger lock (see FireIfCurrentAsync); a read outside it is only
  /// an early way out.
  /// </remarks>
  private int _runGeneration;

  /// <summary>
  /// The generation of the current run.
  /// </summary>
  private int CurrentRun => Volatile.Read(ref _runGeneration);

  /// <summary>
  /// How long a run waits for the previous run's work to end before it warns that the previous run does not stop.
  /// </summary>
  public static readonly TimeSpan PreviousRunWarningAfter = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long a run that timed out waits for its cancelled work to stop before it gives up its recovery.
  /// </summary>
  /// <remarks>
  /// Only used when the task has a <see cref="RecoveryHelper"/>: the recovery waits for the timed-out work (see
  /// <see cref="StartRun"/>), so work that does not notice its cancellation would keep the task in Recovering for good.
  /// </remarks>
  internal TimeSpan TimedOutWorkGrace { get; set; } = PreviousRunWarningAfter;

  /// <summary>
  /// Set when the work of a run that timed out did not stop within <see cref="TimedOutWorkGrace"/>: the time-out then
  /// ends the task in TimedOut instead of recovering. Set and read under the trigger lock, cleared by a reset.
  /// </summary>
  private bool _timedOutWorkAbandoned;

  /// <summary>
  /// The latest run of the work or the recovery; it completes once that run's work has ended.
  /// </summary>
  /// <remarks>
  /// A reset, an end or a time-out cancels a run's token and drops its outcome, but cannot stop its work: the work stops
  /// when it notices the cancellation. Until then the next run waits (see <see cref="StartRun"/>), because both runs
  /// would build on the same data. Set under the trigger lock.
  /// </remarks>
  private Task _latestRun = Task.CompletedTask;

  /// <summary>
  /// Completes once the work of this task's latest run has ended, also when that run was reset, ended or timed out.
  /// </summary>
  internal Task LatestRunEnded => Volatile.Read(ref _latestRun);

  /// <summary>
  /// Makes the first run of this task wait until <paramref name="previousWork"/> has ended: the work of the task set that
  /// this task's set replaces (see <see cref="OrchestrationEngines.Engine.BaseEngine.StartAsync"/>).
  /// </summary>
  /// <param name="previousWork">The work to wait for.</param>
  /// <remarks>Called while the set is built, before anything can start this task.</remarks>
  internal void RunAfter(Task previousWork) => Volatile.Write(ref _latestRun, previousWork);

  private static readonly AsyncLocal<EngineTask> _running = new();

  /// <summary>
  /// The task whose work (or recovery) runs on the current asynchronous flow, or null outside a task's work.
  /// </summary>
  /// <remarks>
  /// Work that reports its progress through <see cref="StateMessage"/> uses this rather than looking its task up by name:
  /// once the engine has built a new task set, a lookup by name finds the successor, and a run that has not noticed its
  /// cancellation yet would report into it.
  /// </remarks>
  public static EngineTask Running => _running.Value;

  /// <summary>
  /// Starts a run of the work or the recovery once the previous run's work has ended. Called on entry into Running or
  /// Recovering, under the trigger lock.
  /// </summary>
  /// <param name="execute">The run.</param>
  private void StartRun(Func<Task> execute)
  {
    var previous = _latestRun;
    Volatile.Write(ref _latestRun, Task.Run(async () =>
    {
      _running.Value = this;
      await WaitForPreviousRunAsync(previous).ConfigureAwait(false);
      await execute().ConfigureAwait(false);
    }));
  }

  /// <summary>
  /// Waits until the work of the previous run has ended, and warns when that takes longer than
  /// <see cref="PreviousRunWarningAfter"/>: the previous run's work does not notice its cancellation.
  /// </summary>
  /// <param name="previous">The previous run.</param>
  private async Task WaitForPreviousRunAsync(Task previous)
  {
    if (previous.IsCompleted)
      return;

    // The task's message is only changed once the wait takes long: a recovery usually waits a moment for the run that
    // failed to finish, and its "Recovering after an error" message must not be replaced by that.
    using (var warningCts = new CancellationTokenSource())
    {
      var warning = Task.Delay(PreviousRunWarningAfter, TimeProvider, warningCts.Token);
      if (await Task.WhenAny(previous, warning).ConfigureAwait(false) == warning)
      {
        LogPreviousRunNotStopped(Logger, PreviousRunWarningAfter);
        StateMessage = $"Waiting until the previous run has stopped: it has not noticed its cancellation after {PreviousRunWarningAfter.TotalSeconds:0} s.";
      }
      warningCts.Cancel();
    }
    await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
  }

  /// <summary>
  /// Creates a new linked cancellation token source for this task run.
  /// </summary>
  /// <remarks>
  /// This is called once during construction and again during reset/recovery scenarios.
  /// <para>
  /// The source it replaces has been cancelled (by the reset, or before the recovery), so the work that holds its token
  /// has been told to stop, and nothing cancels it again (see <see cref="RequestCancellation"/>): it is disposed, which
  /// also takes its registration off the engine's token. Called under the trigger lock.
  /// </para>
  /// </remarks>
  private void CreateToken()
  {
    var previous = _linkedCts;
    var next = CancellationTokenSource.CreateLinkedTokenSource(_engineToken);
    lock (_sourceGate)
    {
      _linkedCts = next;
      CancellationToken = next.Token;
      previous?.Dispose();
    }
  }

  /// <summary>
  /// Lets go of this task's token source once its latest run has ended, for a task whose set was replaced: disposing
  /// it takes its registration off the engine's token. The source is cancelled first, so a later cancel or reset of
  /// this task does not touch the disposed source (see <see cref="RequestCancellation"/> and <see cref="CreateToken"/>).
  /// </summary>
  internal async Task ReleaseTokenAsync()
  {
    await LatestRunEnded.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      RequestCancellation();
      lock (_sourceGate)
        _linkedCts.Dispose();
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  // ---------------------------
  // Dependency graph helpers
  // ---------------------------

  /// <summary>
  /// Checks whether this task (transitively) depends on itself and throws if it does.
  /// </summary>
  /// <remarks>
  /// This is a local safety check. A more complete validation can be done engine-wide by checking all tasks.
  /// </remarks>
  /// <exception cref="CyclicEngineTaskDependencyException"></exception>
  public void CheckForCyclicDependecies()
  {
    if (GetAllDependsOn().Contains(this))
      throw new CyclicEngineTaskDependencyException(this);
  }

  /// <summary>
  /// Returns a list of all tasks where this task is depending on.
  /// Computes the transitive closure of <see cref="DependsOn"/> (direct + indirect dependencies).
  /// </summary>
  public IReadOnlyCollection<EngineTask> GetAllDependsOn()
  {
    var visited = new HashSet<EngineTask>();
    CollectAllDependsOn(visited);
    return visited;
  }

  /// <summary>
  /// Computes the transitive closure of <see cref="DependsOn"/>, including this task itself.
  /// </summary>
  public IReadOnlyCollection<EngineTask> GetThisAndAllDependsOn()
  {
    var visited = new HashSet<EngineTask> { this };
    CollectAllDependsOn(visited);
    return visited;
  }

  /// <summary>
  /// DFS worker for dependency closure.
  /// </summary>
  /// <remarks>
  /// Uses a visited set to avoid duplicates (and to prevent infinite recursion if a cycle exists).
  /// </remarks>
  private void CollectAllDependsOn(HashSet<EngineTask> visited)
  {
    foreach (var dep in DependsOn)
    {
      // Add returns false if already present.
      if (!visited.Add(dep))
        continue;

      // Recurse only on first encounter.
      dep.CollectAllDependsOn(visited);
    }
  }

  // ---------------------------
  // State machine configuration
  // ---------------------------

  /// <summary>
  /// Lazy creation of the Stateless state machine.
  /// </summary>
  /// <remarks>
  /// Thread-safe double-checked creation. The previous unsynchronized <c>_machine ??= CreateStateMachine()</c>
  /// could run twice on a pristine task (an unlocked <c>State</c> read racing a fired trigger) and the
  /// loser's assignment then REPLACED a machine that had already transitioned to Running — reverting the
  /// observable state to Created, allowing a second Start to double-run the work delegate concurrently,
  /// and completing the task latch while the zombie duplicate still ran. Exactly-once creation also keeps
  /// CreateStateMachine's side effects (the parameterized-trigger fields) tied to the one live machine.
  /// </remarks>
  protected StateMachine<EngineTaskState, EngineTaskTrigger> StateMachine
  {
    get
    {
      var machine = Volatile.Read(ref _machine);
      if (machine != null)
        return machine;

      lock (_machineInitLock)
      {
        machine = _machine;
        if (machine == null)
        {
          machine = CreateStateMachine();
          Volatile.Write(ref _machine, machine);
        }

        return machine;
      }
    }
  }

  /// <summary>
  /// Replaces the machine on a reset, under the init lock and with a fence (see <see cref="CreateStateMachine"/>).
  /// </summary>
  private void ReplaceStateMachine()
  {
    lock (_machineInitLock)
      Volatile.Write(ref _machine, CreateStateMachine());
  }

  /// <summary>
  /// Adds a dependency edge: this task depends on <paramref name="parent"/>.
  /// </summary>
  /// <remarks>
  /// Subscribes to the parent's terminal events. When parent completes or fails, this task is signaled via triggers.
  /// This approach intentionally does not maintain a reverse "dependents list"; it uses events for reverse notification.
  ///
  /// Note: these event handlers are async lambdas. Since the event type is Action&lt;EngineTask&gt;, the async lambdas
  /// execute as "fire-and-forget" tasks (async void): an exception that escaped them would end the process, so they log
  /// it instead.
  /// </remarks>
  public virtual EngineTask AddDependsOn(EngineTask parent)
  {
    _dependsOn.Add(parent);

    // If the dependency completes, re-evaluate DependenciesReady and move to Running if possible.
    parent.CompletedStateReached += async _ =>
    {
      if (_suppressDependencyCallbacks)
        return;

      try
      {
        await FireDependencyTriggerAsync(EngineTaskTrigger.CompleteDependency).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        LogDependencyCallbackFailed(Logger, ex, EngineTaskTrigger.CompleteDependency, parent.Name);
      }
    };

    // If the dependency fails/cancels, fail fast.
    parent.FailedStateReached += async _ =>
    {
      if (_suppressDependencyCallbacks)
        return;

      try
      {
        // The reason is taken from the failed dependency on entry into DependencyFailed (see DependencyFailReason).
        await FireDependencyTriggerAsync(EngineTaskTrigger.FailDependency).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        LogDependencyCallbackFailed(Logger, ex, EngineTaskTrigger.FailDependency, parent.Name);
      }
    };

    return this;
  }

  /// <summary>
  /// Fires a dependency's outcome, unless <see cref="EndRunAsync"/> ended this task's run and it is not reset yet: it
  /// starts again from Created after the reset, which looks at its dependencies then.
  /// </summary>
  private async Task FireDependencyTriggerAsync(EngineTaskTrigger trigger)
  {
    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      if (_runEnded)
        return;
      await StateMachine.FireAsync(trigger).ConfigureAwait(false);
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  /// <summary>
  /// Convenience helper to add this task as the parent of one or more dependents.
  /// </summary>
  public virtual EngineTask AddDependent(EngineTask dependant, params EngineTask[] dependents)
  {
    dependant.AddDependsOn(this);

    if (dependents.Length > 0)
    {
      foreach (var otherDependent in dependents)
      {
        otherDependent.AddDependsOn(this);
      }
    }

    return this;
  }

  /// <summary>
  /// Builds the state machine definition for this task.
  /// </summary>
  /// <remarks>
  /// This defines lifecycle transitions and the behavior of dependency triggers. The machine is built on first touch
  /// and again on each reset, after the per-run state is cleared. Both run under the init lock, so the machine and
  /// this method's side effects (the parameterized-trigger fields) are replaced together, and the machine is published
  /// with a fence: a reader of <see cref="State"/> without a lock sees the old machine or the new one, fully built.
  /// </remarks>
  protected virtual StateMachine<EngineTaskState, EngineTaskTrigger> CreateStateMachine()
  {
    var machine = new StateMachine<EngineTaskState, EngineTaskTrigger>(EngineTaskState.Created);

    // Parameterized Fail triggers (with string reason).
    _failTriggerWithReason = machine.SetTriggerParameters<string>(EngineTaskTrigger.Fail);
    _failRecoveryTriggerWithReason = machine.SetTriggerParameters<string>(EngineTaskTrigger.FailRecovery);

    // ---------------------------
    // Created
    // ---------------------------
    // The transitions that depend on the dependencies are dynamic: one look at the dependencies decides the destination.
    // The dependencies change state under their own lock, so with a guard per destination each guard could see another
    // moment, and two could hold at once (a dependency that failed between them), which makes Stateless throw. A
    // dependency that changes after the look fires CompleteDependency or FailDependency itself, which the destination
    // handles.
    machine.Configure(EngineTaskState.Created)
        // Start at once without dependencies, or when they have completed; otherwise wait for them, unless one of them has
        // failed already: its failure was signalled before this task was (re)started, so waiting would never end. Fail at
        // once instead.
        .PermitDynamic(EngineTaskTrigger.Start, StartDestination, possibleDestinationStates: new DynamicStateInfos
        {
          { EngineTaskState.Running, "no dependencies, or all completed" },
          { EngineTaskState.WaitingForDependencies, "not all dependencies completed" },
          { EngineTaskState.DependencyFailed, "a dependency failed" },
        })

        // Dependency failure before we start implies this task cannot run.
        .Permit(EngineTaskTrigger.FailDependency, EngineTaskState.DependencyFailed)

        // A dependency completed signal may immediately allow us to run, or we remain waiting if not all dependencies
        // are done.
        .PermitDynamic(EngineTaskTrigger.CompleteDependency, CompleteDependencyDestination,
          possibleDestinationStates: CompleteDependencyDestinations())

        // Explicit cancellation.
        .Permit(EngineTaskTrigger.Cancel, EngineTaskState.Cancelled)

        // Allows re-execution under special conditions.
        .Permit(EngineTaskTrigger.Reset, EngineTaskState.Resetting);

    // ---------------------------
    // WaitingForDependencies
    // ---------------------------
    machine.Configure(EngineTaskState.WaitingForDependencies)
        .Ignore(EngineTaskTrigger.Start)
        .Permit(EngineTaskTrigger.FailDependency, EngineTaskState.DependencyFailed)
        // To Running, or back into WaitingForDependencies (a re-entry) while not all dependencies are done.
        .PermitDynamic(EngineTaskTrigger.CompleteDependency, CompleteDependencyDestination,
          possibleDestinationStates: CompleteDependencyDestinations())
        .Permit(EngineTaskTrigger.Cancel, EngineTaskState.Cancelled)
        .Ignore(EngineTaskTrigger.Reset);

    // ---------------------------
    // Running
    // ---------------------------
    machine.Configure(EngineTaskState.Running)
        // A start that found the task Created can fire Start after a dependency has started it (see
        // BaseEngine.TriggerStartCoreAsync): benign, like a Start on a failed task. Likewise in Recovering and
        // CompletedWithSuccess. Without these it was logged as an "Unhandled trigger" warning.
        .Ignore(EngineTaskTrigger.Start)
        .OnEntry(StartCallTimer)
        .OnExit(StopCallTimer)

        // The work delegate runs on entry into Running.
        //.OnEntryAsync(() => ExecuteWorkAsync())
        // The run and its token are taken on entry, under the trigger lock, so a reset before the detached work starts
        // still makes it stale (see FireIfCurrentAsync), and the work never reads the token of the run after it.
        .OnEntry(_ =>
        {
          var run = CurrentRun;
          var token = CancellationToken;
          StartRun(() => ExecuteWorkAsync(run, token));
        })

        .Permit(EngineTaskTrigger.Succeed, EngineTaskState.CompletedWithSuccess)

        // If RecoveryHelper is configured, failures can transition into Recovering.
        .PermitIf(EngineTaskTrigger.Fail, EngineTaskState.Failed, () => RecoveryHelper == null)
        .PermitIf(EngineTaskTrigger.Fail, EngineTaskState.Recovering, () => RecoveryHelper != null)

        // A recovery would wait for the timed-out work, so work that did not stop ends the task instead.
        .PermitIf(EngineTaskTrigger.TimeOut, EngineTaskState.TimedOut, () => RecoveryHelper == null || _timedOutWorkAbandoned)
        .PermitIf(EngineTaskTrigger.TimeOut, EngineTaskState.Recovering, () => RecoveryHelper != null && !_timedOutWorkAbandoned)

        // Cancellation behavior depends on whether this task token was cancelled and whether recovery is enabled.
        .PermitIf(
            EngineTaskTrigger.Cancel,
            EngineTaskState.Cancelled,
            () => CancellationToken.IsCancellationRequested)
        .PermitIf(
            EngineTaskTrigger.Cancel,
            EngineTaskState.Recovering,
            () => !CancellationToken.IsCancellationRequested && RecoveryHelper != null)
        .PermitIf(
            EngineTaskTrigger.Cancel,
            EngineTaskState.Cancelled,
            () => !CancellationToken.IsCancellationRequested && RecoveryHelper == null)

        // A task with several dependencies can already be Running when a later dependency completes and fires
        // CompleteDependency (via AddDependsOn's CompletedStateReached callback). A late dependency-completion
        // cannot un-satisfy an already-running task, so it is benign — ignore it (mirrors CompletedWithSuccess).
        // Without this, the OnUnhandledTrigger handler (see CreateStateMachine) logs it as a warning; before that handler
        // was registered in all builds, it crashed the whole engine drive.
        .Ignore(EngineTaskTrigger.CompleteDependency)

        .Permit(EngineTaskTrigger.Reset, EngineTaskState.Resetting);

    // ---------------------------
    // Recovering
    // ---------------------------
    machine.Configure(EngineTaskState.Recovering)
        .Ignore(EngineTaskTrigger.Start)
        // Tell why the task recovers: FailReason is only for a task that ended failed, so without this a recovering
        // task shows its last progress message and the error is only in the log.
        .OnEntryFrom(_failTriggerWithReason, reason => StateMessage = $"Recovering after an error: {reason}")
        .OnEntryFrom(EngineTaskTrigger.TimeOut, _ => StateMessage = $"Recovering after a time-out: {FailReason}")
        .OnEntryFrom(EngineTaskTrigger.Cancel, _ => StateMessage = "Recovering after a cancellation.")
        // Detached like the Running work path: recovery fires its completion triggers through
        // the serialized FireAsync, which must not be re-entered from within a transition.
        .OnEntry(_ =>
        {
          var run = CurrentRun;
          StartRun(() => ExecuteRecoveryAsync(run));
        })
        .OnExit(t => RecoveryCompleted(t.Destination))
        .Permit(EngineTaskTrigger.SucceedRecovery, EngineTaskState.Running)
        .Permit(EngineTaskTrigger.FailRecovery, EngineTaskState.RecoveryFailed)
        .Permit(EngineTaskTrigger.Cancel, EngineTaskState.Cancelled)
        // A dependency may complete while this task is recovering — benign, ignore it.
        .Ignore(EngineTaskTrigger.CompleteDependency)
        .Permit(EngineTaskTrigger.Reset, EngineTaskState.Resetting);

    // ---------------------------
    // Failed (terminal)
    // ---------------------------
    machine.Configure(EngineTaskState.Failed)
        .OnEntryFrom(_failTriggerWithReason, reason => OnFailedEntered(reason))
        .OnEntryFrom(_failRecoveryTriggerWithReason, reason => OnFailedEntered(reason))
        .OnEntryFrom(EngineTaskTrigger.FailDependency, _ => OnFailedEntered(DependencyFailReason(), DependenciesOnlyCancelled()))
        // Started while a dependency had already failed (see the Start transitions of Created).
        .OnEntryFrom(EngineTaskTrigger.Start, _ => OnFailedEntered(DependencyFailReason(), DependenciesOnlyCancelled()))
        // The superstate is entered before the Cancelled substate, so the reason must be known here: without it the
        // task was reported as failed for an 'Unknown failure.' (and its dependents with it) before Cancelled said why.
        .OnEntryFrom(EngineTaskTrigger.Cancel, _ => OnFailedEntered(FailReason ?? CancellationReason(), cancelled: true))
        .OnEntry(_ => OnFailedEntered())
        // A sibling dependency may complete after this task has already failed/cancelled/timed-out — benign,
        // ignore it. Inherited by the Failed substates (TimedOut/DependencyFailed/RecoveryFailed/Cancelled).
        .Ignore(EngineTaskTrigger.CompleteDependency)
        // Likewise a second dependency that fails (a task with several dependencies gets a FailDependency for each),
        // and a Start for a task whose dependency failed before it was started: the task has ended already, so both
        // are benign. Without these they were logged as "Unhandled trigger" warnings.
        .Ignore(EngineTaskTrigger.FailDependency)
        .Ignore(EngineTaskTrigger.Start)
        .Permit(EngineTaskTrigger.Reset, EngineTaskState.Resetting);

    // Failure substates (categorized reasons).
    machine.Configure(EngineTaskState.TimedOut).SubstateOf(EngineTaskState.Failed);
    machine.Configure(EngineTaskState.DependencyFailed).SubstateOf(EngineTaskState.Failed);
    machine.Configure(EngineTaskState.RecoveryFailed).SubstateOf(EngineTaskState.Failed);

    // ---------------------------
    // Cancelled (terminal substate of Failed)
    // ---------------------------
    machine.Configure(EngineTaskState.Cancelled)
        .SubstateOf(EngineTaskState.Failed)
        // The reason was set and logged on entry into Failed.
        .OnEntry(RequestCancellation);

    // ---------------------------
    // CompletedWithSuccess (terminal)
    // ---------------------------
    machine.Configure(EngineTaskState.CompletedWithSuccess)
        .Ignore(EngineTaskTrigger.Start)
        .OnEntry(() =>
        {
          LogEnteredCompletedWithSuccess(Logger);

          // If we succeeded after a recovery, reset recovery budget for future runs.
          RecoveryHelper?.Reset();

          // Notify dependents, then release awaiters waiting on lifecycle completion.
          RaiseTerminal(CompletedStateReached, nameof(CompletedStateReached));
        })
        .Ignore(EngineTaskTrigger.CompleteDependency)
        .Ignore(EngineTaskTrigger.Cancel)

        // Re-execution is allowed but requires the containing engine to reset dependents as needed.
        .Permit(EngineTaskTrigger.Reset, EngineTaskState.Resetting);

    // ---------------------------
    // Resetting
    // ---------------------------
    machine.Configure(EngineTaskState.Resetting)
        .OnEntryAsync(async () => await OnResetEnteredAsync())
        // A dependency may complete while this task is resetting — benign, ignore it.
        .Ignore(EngineTaskTrigger.CompleteDependency)
        .Permit(EngineTaskTrigger.Reset, EngineTaskState.Created);

    // Unhandled triggers are treated as warnings rather than exceptions: Stateless's default action THROWS, which would
    // crash the whole engine drive on a benign race such as a late CompleteDependency.
    machine.OnUnhandledTrigger((state, trigger) =>
    {
      LogUnhandledTrigger(Logger, trigger, state);
    });

    machine.OnTransitioned(t =>
    {
      LogTransition(Logger, t.Source, t.Trigger, t.Destination);
    });

    return machine;
  }

  // ---------------------------
  // Dependency readiness hooks
  // ---------------------------

  /// <summary>
  /// Returns true if this task has any dependencies.
  /// </summary>
  protected virtual bool HasDependecies() => DependsOn.Any();

  /// <summary>
  /// Returns true when all dependencies have reached CompletedWithSuccess.
  /// </summary>
  protected virtual bool DependenciesReady() => DependsOn.All(d => d.State == EngineTaskState.CompletedWithSuccess);

  /// <summary>
  /// Returns true when a dependency has ended failed (including cancelled), so this task cannot run.
  /// </summary>
  protected virtual bool AnyDependencyFailed() => DependsOn.Any(d => d.IsFailed);

  /// <summary>
  /// Returns where Start takes a task that has not started: Running when it has no dependencies or they have completed,
  /// DependencyFailed when one of them has failed, and WaitingForDependencies otherwise.
  /// </summary>
  private EngineTaskState StartDestination()
  {
    if (!HasDependecies() || DependenciesReady())
      return EngineTaskState.Running;
    return AnyDependencyFailed() ? EngineTaskState.DependencyFailed : EngineTaskState.WaitingForDependencies;
  }

  /// <summary>
  /// Returns where a completed dependency takes a task that has not started: Running when all dependencies have
  /// completed, and WaitingForDependencies otherwise (a failed one fires FailDependency itself).
  /// </summary>
  private EngineTaskState CompleteDependencyDestination() =>
    DependenciesReady() ? EngineTaskState.Running : EngineTaskState.WaitingForDependencies;

  /// <summary>
  /// The destinations of <see cref="CompleteDependencyDestination"/>, for the state machine's description.
  /// </summary>
  private static DynamicStateInfos CompleteDependencyDestinations() => new()
  {
    { EngineTaskState.Running, "all dependencies completed" },
    { EngineTaskState.WaitingForDependencies, "not all dependencies completed" },
  };

  /// <summary>
  /// Hook for subclasses to observe recovery completion and optionally act on the destination state.
  /// </summary>
  protected virtual void RecoveryCompleted(EngineTaskState nextState) { }

  // ---------------------------
  // Timing hooks
  // ---------------------------

  /// <summary>Starts measuring the current task run.</summary>
  protected virtual void StartCallTimer() => RunStartedAtUtc = TimeProvider.GetUtcNow().UtcDateTime;

  /// <summary>Stops measuring the current task run.</summary>
  /// <remarks>
  /// Runs as the exit action of Running, under the trigger lock, before the state changes: it must not throw or block,
  /// or the task could never leave Running.
  /// </remarks>
  protected virtual void StopCallTimer() => RunEndedAtUtc = TimeProvider.GetUtcNow().UtcDateTime;

  // ---------------------------
  // Terminal state handling
  // ---------------------------

  /// <summary>
  /// Called when entering a failed state, capturing and normalizing the failure reason.
  /// </summary>
  /// <param name="reasonFromTrigger">The reason, if the trigger gives one.</param>
  /// <param name="cancelled">
  /// Whether the task was cancelled (or a task it depends on was) rather than failed: that is expected when an engine
  /// stops or restarts, so it is logged as a warning, not as an error.
  /// </param>
  protected virtual void OnFailedEntered(string reasonFromTrigger = null, bool cancelled = false)
  {
    // Ensure we don't double-complete if this entry is hit multiple times (an OnEntryFrom, then the plain OnEntry).
    if (_completionTcs.Task.IsCompleted)
      return;

    if (!string.IsNullOrEmpty(reasonFromTrigger))
    {
      FailReason = reasonFromTrigger;
    }
    else if (string.IsNullOrEmpty(FailReason))
    {
      FailReason = "Unknown failure.";
    }

    WasCancelled = cancelled;
    if (cancelled)
      LogEnteredCancelled(Logger, FailReason);
    // The cause of the failure was logged as an error where it was caught (the work's or the recovery's exception, the
    // time-out); entering the state is Debug, so a failure is not an error twice. The state is the destination already:
    // Stateless sets it before it runs the entry actions.
    else if (State == EngineTaskState.DependencyFailed)
      LogEnteredDependencyFailed(Logger, FailReason);
    else
      LogEnteredFailed(Logger, FailReason);
    RaiseTerminal(FailedStateReached, nameof(FailedStateReached));
  }

  /// <summary>
  /// Calls each subscriber of a terminal-state event in turn, then completes this task's latch
  /// (<see cref="InnerTask"/>). A subscriber that throws is logged and does not keep the subscribers after it, among
  /// them the tasks that depend on this one, from being called (see <see cref="DelegateExtensions.InvokeEach{T}"/>).
  /// </summary>
  private void RaiseTerminal(Action<EngineTask> handlers, string eventName)
  {
    try
    {
      handlers.InvokeEach(handler => handler(this), ex => LogSubscriberFailed(Logger, ex, eventName));
    }
    finally
    {
      _completionTcs.TrySetResult(true);
    }
  }

  /// <summary>
  /// Returns why this task cannot run because of a task it depends on that failed or was cancelled.
  /// </summary>
  private string DependencyFailReason()
  {
    var failed = _dependsOn.FirstOrDefault(d => d.State.IsFailed());
    if (failed == null)
      return "Dependency failed.";
    return failed.WasCancelled
      ? $"Cancelled because '{failed.Name}', which it depends on, was cancelled."
      : $"Dependency '{failed.Name}' failed: {failed.FailReason}";
  }

  /// <summary>
  /// Whether every task this task depends on that has ended unsuccessfully was cancelled rather than failed.
  /// </summary>
  private bool DependenciesOnlyCancelled()
  {
    var failed = _dependsOn.Where(d => d.State.IsFailed()).ToArray();
    return failed.Length > 0 && failed.All(d => d.WasCancelled);
  }

  /// <summary>
  /// Returns why the task was cancelled when no one said why: its engine was stopped, or its own token was cancelled
  /// (an OperationCanceledException that is not the engine's, such as a time-out of a database command).
  /// </summary>
  private string CancellationReason() =>
    _engineToken.IsCancellationRequested
      ? "Cancelled because the engine was stopped."
      : "Cancelled because the work was cancelled (not by a stop of the engine).";

  /// <summary>
  /// Reset handler: clears per-run state before the state machine is recreated.
  /// </summary>
  /// <remarks>
  /// This method creates a new token and a new completion latch. The caller is responsible for
  /// replacing the state machine so the task returns to Created.
  /// </remarks>
  protected virtual Task OnResetEnteredAsync()
  {
    var suppressDependencyCallbacks = _suppressDependencyCallbacks;
    _suppressDependencyCallbacks = true;

    try
    {
      RequestCancellation();

      FailReason = null;
      FailException = null;
      RunStartedAtUtc = null;
      RunEndedAtUtc = null;
      LogReset(Logger);

      RecoveryHelper?.Reset();
      _timedOutWorkAbandoned = false;
      CreateToken();
      // Fix: the run that was reset must not report its outcome into the next run (see ExecuteWorkAsync).
      Interlocked.Increment(ref _runGeneration);

      // Recreate completion latch so new awaiters can observe a fresh lifecycle. Whoever waits on the previous latch
      // (a run that the reset ended before it completed) is released when the next run completes: replacing the latch
      // alone left those waiters waiting forever.
      var previousCompletion = _completionTcs;
      _completionTcs = new TaskCompletionSource<bool>();
      if (!previousCompletion.Task.IsCompleted)
      {
        _completionTcs.Task.ContinueWith(next => previousCompletion.TrySetResult(next.Result),
          CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      }
    }
    finally
    {
      _suppressDependencyCallbacks = suppressDependencyCallbacks;
    }

    return Task.CompletedTask;
  }

  // ---------------------------
  // Public API
  // ---------------------------

  /// <summary>
  /// Fires a trigger into the state machine.
  /// Only returns when the firing has completed or earlier if this task is not RequiredForRunning.
  /// </summary>
  /// <remarks>
  /// The current implementation forwards <paramref name="reason"/> to Stateless for all triggers.
  /// Only parameterized triggers actually consume this (Fail / FailRecovery). For other triggers, the parameter may be ignored.
  /// </remarks>
  /// <returns>Task that executes the trigger.</returns>
  public async Task FireAsync(EngineTaskTrigger trigger, string reason = "")
  {
    if (trigger == EngineTaskTrigger.Reset)
    {
      await ResetAsync().ConfigureAwait(false);
      return;
    }

    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      if (string.IsNullOrEmpty(reason))
        await StateMachine.FireAsync(trigger).ConfigureAwait(false);
      else
        await StateMachine.FireAsync(trigger, reason).ConfigureAwait(false);
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  /// <summary>
  /// Ends this task for good, because its engine replaces its task set: the current run (work or recovery) becomes
  /// stale, its token is cancelled, and the task goes to Cancelled, which releases whoever waits on it. A task that has
  /// completed (successfully or not) is left as it is.
  /// </summary>
  /// <remarks>
  /// Unlike a reset, the task does not go back to Created, so nothing can start it again: a reset task of a set that is
  /// about to be replaced could be started by a waiter or a monitor, and its waiters would then wait for a set that is
  /// thrown away.
  /// </remarks>
  internal Task EndAsync() => CancelAsync("Cancelled because the engine starts again with new tasks.");

  /// <summary>
  /// Cancels this task with <paramref name="reason"/>, as <see cref="EndAsync"/> does. A task that has completed
  /// (successfully or not) is left as it is.
  /// </summary>
  /// <param name="reason">Why the task is cancelled.</param>
  internal async Task CancelAsync(string reason)
  {
    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      if (State.IsCompleted())
        return;
      // Under the trigger lock: the run's outcome is dropped (see FireIfCurrentAsync), and with its token cancelled,
      // Cancel ends a running task in Cancelled instead of recovering it.
      Interlocked.Increment(ref _runGeneration);
      RequestCancellation();
      FailReason = reason;
      await StateMachine.FireAsync(EngineTaskTrigger.Cancel).ConfigureAwait(false);
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  /// <summary>
  /// Ends the current run (work or recovery) without changing the state, ahead of a reset: its outcome is dropped and
  /// its token cancelled, as a reset does, but the task keeps its state until it is reset. Until then it ignores its
  /// dependencies' outcomes, so a task waiting for its dependencies does not start on the cancelled token. See
  /// BaseEngine's two-pass reset.
  /// </summary>
  internal async Task EndRunAsync()
  {
    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      // Under the trigger lock: an outcome fired after this is not the current run's (see FireIfCurrentAsync).
      Interlocked.Increment(ref _runGeneration);
      RequestCancellation();
      _runEnded = true;
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  private async Task ResetAsync()
  {
    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      try
      {
        await OnResetEnteredAsync().ConfigureAwait(false);
      }
      catch (Exception ex) when (_runEnded)
      {
        // EndRunAsync dropped the run's outcome, so a task left in its state would never end, and whoever waits on it
        // would wait for good. End it Cancelled, as CancelAsync does; a later reset picks it up.
        if (!State.IsCompleted())
        {
          Interlocked.Increment(ref _runGeneration);
          RequestCancellation();
          FailReason = $"Cancelled because its reset failed: {ex.Message}";
          await StateMachine.FireAsync(EngineTaskTrigger.Cancel).ConfigureAwait(false);
        }
        throw;
      }
      ReplaceStateMachine();
      _runEnded = false;
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  /// <summary>
  /// Fires the outcome of a run of the work or the recovery, but only while that run is still the current one.
  /// </summary>
  /// <remarks>
  /// A reset cancels the running work or recovery and starts the task again with a new token and state machine. The
  /// old run then noticed the cancellation (or finished) and fired its outcome into the new run, which could end
  /// Cancelled before its own work had even run. The generation is compared under the trigger lock, which the reset
  /// also takes, so a reset cannot slip in between the check and the firing.
  /// </remarks>
  /// <param name="run">The generation the run started in, taken when it entered Running or Recovering.</param>
  /// <param name="trigger">The outcome to fire.</param>
  /// <param name="reason">The failure reason, for a parameterized trigger.</param>
  /// <param name="failReason">A failure reason to record first, for a trigger without one (a time-out).</param>
  /// <param name="cancelRun">
  /// Whether to cancel the run's token first (a time-out). Only for a current run: after a reset the token belongs to
  /// the next run.
  /// </param>
  /// <param name="exception">The exception the work or recovery threw, to record first (see <see cref="FailException"/>).</param>
  /// <returns>False if the run is stale and nothing was fired.</returns>
  private async Task<bool> FireIfCurrentAsync(int run, EngineTaskTrigger trigger, string reason = "", string failReason = null,
    bool cancelRun = false, Exception exception = null)
  {
    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      if (run != CurrentRun)
        return false;

      // The run's exception, or none once it (or its recovery) succeeded: a recovered task does not keep the exception.
      if (exception != null || trigger is EngineTaskTrigger.Succeed or EngineTaskTrigger.SucceedRecovery)
        FailException = exception;

      if (trigger == EngineTaskTrigger.Cancel)
        FailReason = failReason ?? CancellationReason();
      else if (failReason != null)
        FailReason = failReason;
      if (cancelRun)
        RequestCancellation();

      if (string.IsNullOrEmpty(reason))
        await StateMachine.FireAsync(trigger).ConfigureAwait(false);
      else
        await StateMachine.FireAsync(trigger, reason).ConfigureAwait(false);
      return true;
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  /// <summary>
  /// Cancels the run's work, but only while the run is still the current one.
  /// </summary>
  /// <param name="run">The generation the run started in.</param>
  private async Task CancelIfCurrentAsync(int run)
  {
    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      if (run == CurrentRun)
        RequestCancellation();
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  /// <summary>
  /// Fires TimeOut for a run whose work was cancelled after it timed out, while the run is still the current one and
  /// still running: a stop during the grace period has ended the task already.
  /// </summary>
  /// <param name="run">The generation the run started in.</param>
  /// <param name="workStopped">
  /// Whether the work stopped within <see cref="TimedOutWorkGrace"/>. If not, the task ends in TimedOut instead of
  /// recovering, since the recovery would wait for that work.
  /// </param>
  private async Task TimeOutIfCurrentAsync(int run, bool workStopped)
  {
    await _triggerLock.WaitAsync().ConfigureAwait(false);
    try
    {
      if (run != CurrentRun || State != EngineTaskState.Running)
        return;

      _timedOutWorkAbandoned = !workStopped;
      FailReason = workStopped
        ? $"Task timed out after {Timeout.Value}."
        : $"Task timed out after {Timeout.Value} and its work did not stop within {TimedOutWorkGrace}.";
      await StateMachine.FireAsync(EngineTaskTrigger.TimeOut).ConfigureAwait(false);
    }
    finally
    {
      _triggerLock.Release();
    }
  }

  /// <summary>
  /// Fires Cancel if the run's token was cancelled, as <see cref="CheckForCancellation"/> does, but only while the run
  /// is still the current one.
  /// </summary>
  /// <param name="run">The generation the run started in.</param>
  /// <param name="token">The run's token, taken with the generation.</param>
  /// <returns>True if the run must stop: it was cancelled, or it is stale.</returns>
  private async Task<bool> CheckForCancellationIfCurrentAsync(int run, CancellationToken token)
  {
    if (run != CurrentRun)
      return true;
    if (!token.IsCancellationRequested)
      return false;
    LogCancellationRequested(Logger);
    await FireIfCurrentAsync(run, EngineTaskTrigger.Cancel).ConfigureAwait(false);
    return true;
  }

  // ---------------------------
  // Work execution
  // ---------------------------

  /// <summary>
  /// Checks cancellation and, if requested, transitions to Cancelled.
  /// </summary>
  protected async Task<bool> CheckForCancellation()
  {
    if (CancellationToken.IsCancellationRequested)
    {
      LogCancellationRequested(Logger);
      await FireCancelAsync();
      return true;
    }

    return false;
  }

  /// <summary>
  /// Waits until the work of a run that timed out or was cancelled has ended, so this run (see <see cref="StartRun"/>)
  /// only ends with its work. The outcome was settled already; an exception is only logged.
  /// </summary>
  /// <param name="workTask">The work.</param>
  private async Task WorkEndedAsync(Task workTask)
  {
    await workTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    if (workTask.IsFaulted)
      LogWorkEndedWithException(Logger, workTask.Exception);
  }

  /// <summary>
  /// Executes the work delegate for this task, enforcing optional timeout and driving state transitions.
  /// </summary>
  /// <param name="run">The generation the run started in, taken when the task entered Running.</param>
  /// <param name="token">
  /// The run's token, taken with the generation. Not read from <see cref="CancellationToken"/>: a reset in between replaces
  /// that with the next run's token, and a stale run would then run its whole work with a token nobody cancels.
  /// </param>
  private async Task ExecuteWorkAsync(int run, CancellationToken token)
  {
    LogWorkStarted(Logger);

    // Fix (RuntimeRecoveryTest): a run fires its outcome only while it is still the current run. A reset
    // while the work ran cancels that work and starts the task again with a new token and state machine; the old
    // work then noticed the cancellation and fired Cancel (or, had it ignored the token and finished, Succeed) into
    // the new run, which ended Cancelled while its own work was still running. See FireIfCurrentAsync.
    if (await CheckForCancellationIfCurrentAsync(run, token).ConfigureAwait(false))
      return;

    try
    {
      Task workTask;
      if (Timeout.HasValue && Timeout.Value > TimeSpan.Zero)
      {
        // Note: this CTS is used only for the timeout delay task; the work itself is cancelled via RequestCancellation().
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timeoutTask = Task.Delay(Timeout.Value, TimeProvider, delayCts.Token);
        // The work runs on the thread pool: synchronous work (as task bodies often are) would otherwise run to its end
        // in the call itself, before the race below, and never time out.
        workTask = Task.Run(() => _workAsync(token));

        var completed = await Task.WhenAny(workTask, timeoutTask).ConfigureAwait(false);

        if (completed == timeoutTask)
        {
          if (timeoutTask.IsCanceled)
          {
            // The delay ended because the run's token was cancelled (a stop or a reset), not because time ran out: a
            // cancellation, also while synchronous work has not noticed it yet.
            await CheckForCancellationIfCurrentAsync(run, token).ConfigureAwait(false);
            await WorkEndedAsync(workTask).ConfigureAwait(false);
            return;
          }

          LogTimedOut(Logger, Timeout.Value);

          if (RecoveryHelper == null)
          {
            await FireIfCurrentAsync(run, EngineTaskTrigger.TimeOut, failReason: $"Task timed out after {Timeout.Value}.",
              cancelRun: true).ConfigureAwait(false);
          }
          else
          {
            // The recovery waits for this work, so first give the work a while to notice its cancellation.
            await CancelIfCurrentAsync(run).ConfigureAwait(false);
            using var graceCts = new CancellationTokenSource();
            var stopped = await Task.WhenAny(workTask, Task.Delay(TimedOutWorkGrace, TimeProvider, graceCts.Token))
              .ConfigureAwait(false) == workTask;
            graceCts.Cancel();
            await TimeOutIfCurrentAsync(run, stopped).ConfigureAwait(false);
          }
          await WorkEndedAsync(workTask).ConfigureAwait(false);
          return;
        }

        // Work completed first; stop the delay and propagate work exceptions (if any).
        delayCts.Cancel();
      }
      else
      {
        workTask = _workAsync(token);
      }
      await workTask.ConfigureAwait(false);

      if (await CheckForCancellationIfCurrentAsync(run, token).ConfigureAwait(false))
        return;

      LogWorkCompleted(Logger);
      await FireIfCurrentAsync(run, EngineTaskTrigger.Succeed).ConfigureAwait(false);
    }
    catch (ThreadAbortException)
    {
      LogThreadAbortCaught(Logger);
      throw;
    }
    catch (OperationCanceledException)
    {
      LogOperationCanceledCaught(Logger);
      await FireIfCurrentAsync(run, EngineTaskTrigger.Cancel).ConfigureAwait(false);
    }
    catch (EngineTaskException ex)
    {
      LogEngineTaskException(Logger, ex, ex.Message);

      // Design intent: EngineTaskException is treated as non-recoverable and maps to cancellation.
      // TODO: Reconsider "non-recoverable". Cancel from Running only ends in Cancelled when this task's token was
      // cancelled or no RecoveryHelper is set. An EngineTaskException thrown while the token is still live, by a task
      // with a RecoveryHelper (the engine lifecycle tasks take their descriptor's, and a host may set one on every
      // engine), goes to Recovering, so the work is retried. Keep the behaviour until it is decided whether such
      // exceptions should be retried.
      await FireIfCurrentAsync(run, EngineTaskTrigger.Cancel, failReason: $"Cancelled by the work: {ex.Message}")
        .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      LogWorkFailed(Logger, ex, ex.Message);

      // Fail with reason; may transition to Recovering depending on RecoveryHelper.
      await FireIfCurrentAsync(run, EngineTaskTrigger.Fail, ex.Message, exception: ex).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Executes recovery procedure when configured.
  /// </summary>
  /// <param name="run">The generation the recovery started in, taken when the task entered Recovering.</param>
  /// <remarks>
  /// Like the work, a recovery fires its outcome only while it is still the current run (see
  /// <see cref="FireIfCurrentAsync"/>). A reset during the recovery's backoff cancels the wait; the recovery used to
  /// fire Cancel into the next run, which then ended Cancelled before it had started.
  /// </remarks>
  protected virtual async Task ExecuteRecoveryAsync(int run)
  {
    if (RecoveryHelper == null)
      throw new ArgumentException(nameof(RecoveryHelper));

    try
    {
      // If only this task was cancelled (but engine is still alive), recreate token to allow recovery to proceed.
      // Under the trigger lock and only for the current run: after a reset the token belongs to the next run. The token
      // is taken there as well, so a reset after the lock does not hand this recovery the next run's live token.
      bool recreated;
      CancellationToken token;
      await _triggerLock.WaitAsync().ConfigureAwait(false);
      try
      {
        if (run != CurrentRun)
          return;
        recreated = CancellationToken.IsCancellationRequested && !_engineToken.IsCancellationRequested;
        if (recreated)
          CreateToken();
        token = CancellationToken;
      }
      finally
      {
        _triggerLock.Release();
      }

      if (recreated)
        LogRecoveryStartedAfterCancellation(Logger);
      else
        LogRecoveryStarted(Logger);

      await RecoveryHelper.Invoke(this, token).ConfigureAwait(false);

      if (await CheckForCancellationIfCurrentAsync(run, token).ConfigureAwait(false))
        return;

      LogRecoverySucceeded(Logger);
      await FireIfCurrentAsync(run, EngineTaskTrigger.SucceedRecovery).ConfigureAwait(false);
    }
    catch (ThreadAbortException)
    {
      LogThreadAbortCaught(Logger);
      throw;
    }
    catch (OperationCanceledException)
    {
      LogRecoveryCancelled(Logger);
      await FireIfCurrentAsync(run, EngineTaskTrigger.Cancel).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      LogRecoveryFailed(Logger, ex, ex.Message);
      await FireIfCurrentAsync(run, EngineTaskTrigger.FailRecovery, ex.Message, exception: ex).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Drives cancellation transition and normalizes <see cref="FailReason"/>.
  /// </summary>
  protected virtual async Task FireCancelAsync()
  {
    FailReason = CancellationReason();
    await FireAsync(EngineTaskTrigger.Cancel).ConfigureAwait(false);
  }

  /// <inheritdoc/>
  public override string ToString() =>
      $"<{Name}> [{State}] Duration: {RunDuration:g}; (FailReason: '{FailReason ?? "n/a"}') {StateMessage}";

  // ---------------------------------------------------------------------
  // Logging methods
  // ---------------------------------------------------------------------

  [LoggerMessage(Level = LogLevel.Warning, Message = "The previous run has not stopped {Waited} after it was cancelled; this run waits until it has.")]
  private static partial void LogPreviousRunNotStopped(ILogger logger, TimeSpan waited);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Cancelled. Reason: '{Reason}'.")]
  private static partial void LogEnteredCancelled(ILogger logger, string reason);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Entered CompletedWithSuccess state.")]
  private static partial void LogEnteredCompletedWithSuccess(ILogger logger);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Unhandled trigger {Trigger} in state {State}, ignoring.")]
  private static partial void LogUnhandledTrigger(ILogger logger, EngineTaskTrigger trigger, EngineTaskState state);

  [LoggerMessage(Level = LogLevel.Error, Message = "A subscriber to {EventName} threw; the other subscribers were still called.")]
  private static partial void LogSubscriberFailed(ILogger logger, Exception exception, string eventName);

  [LoggerMessage(Level = LogLevel.Error, Message = "Could not fire {Trigger} after dependency '{Dependency}' ended.")]
  private static partial void LogDependencyCallbackFailed(ILogger logger, Exception exception, EngineTaskTrigger trigger, string dependency);

  [LoggerMessage(Level = LogLevel.Trace, Message = "Transition {Source} --{Trigger}--> {Destination}")]
  private static partial void LogTransition(ILogger logger, EngineTaskState source, EngineTaskTrigger trigger, EngineTaskState destination);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Entered Failed state. Reason: '{Reason}'.")]
  private static partial void LogEnteredFailed(ILogger logger, string reason);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Entered Failed state because a task it depends on failed. Reason: '{Reason}'.")]
  private static partial void LogEnteredDependencyFailed(ILogger logger, string reason);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Reset.")]
  private static partial void LogReset(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Cancellation requested.")]
  private static partial void LogCancellationRequested(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "The work ended with an exception after its run had ended.")]
  private static partial void LogWorkEndedWithException(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Work started.")]
  private static partial void LogWorkStarted(ILogger logger);

  [LoggerMessage(Level = LogLevel.Error, Message = "Task timed out after {Timeout}.")]
  private static partial void LogTimedOut(ILogger logger, TimeSpan timeout);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Work completed without exception.")]
  private static partial void LogWorkCompleted(ILogger logger);

  [LoggerMessage(Level = LogLevel.Warning, Message = "ThreadAbortException caught.")]
  private static partial void LogThreadAbortCaught(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "OperationCanceledException caught.")]
  private static partial void LogOperationCanceledCaught(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Engine task exception: '{Reason}'.")]
  private static partial void LogEngineTaskException(ILogger logger, Exception exception, string reason);

  [LoggerMessage(Level = LogLevel.Error, Message = "Exception: '{Reason}'.")]
  private static partial void LogWorkFailed(ILogger logger, Exception exception, string reason);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Recovery start by resetting the requested cancellation.")]
  private static partial void LogRecoveryStartedAfterCancellation(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Recovery started.")]
  private static partial void LogRecoveryStarted(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Recovery succeeded.")]
  private static partial void LogRecoverySucceeded(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Recovery cancelled.")]
  private static partial void LogRecoveryCancelled(ILogger logger);

  [LoggerMessage(Level = LogLevel.Error, Message = "Exception during recovery: '{Reason}'.")]
  private static partial void LogRecoveryFailed(ILogger logger, Exception exception, string reason);
}
