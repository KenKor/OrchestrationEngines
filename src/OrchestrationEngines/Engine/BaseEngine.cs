using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Helpers;
using OrchestrationEngines.Logger;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Engine;

/// <summary>
/// Base class for all runtime engines.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BaseEngine"/> provides a generic orchestration layer built on top of
/// <see cref="EngineTask"/> instances. Concrete engines define their behavior by
/// declaring tasks and dependencies in <see cref="SetUpTasks"/>.
/// </para>
///
/// <para>
/// The engine itself does not perform work directly; all execution is delegated
/// to EngineTasks, which together form a directed acyclic graph (DAG).
/// </para>
///
/// <para>
/// The engine lifecycle (<see cref="EngineState"/>) is derived from the collective
/// state of its tasks and is recomputed whenever a required task changes state.
/// </para>
/// </remarks>
public abstract partial class BaseEngine : IEngine
{
  /// <summary>
  /// <inheritdoc/>
  /// </summary>
  private readonly CancellationTokenSource _linkedCts;

  /// <summary>
  /// Makes cancelling <see cref="_linkedCts"/> and disposing it exclusive: a source is flagged cancelled before its
  /// callbacks run, so without it a stop could dispose the source while another thread's cancel is still notifying, and
  /// the tasks' sources linked to it would not be cancelled. It covers the cancels this engine makes (CancelAll,
  /// RequestCancellation), not one that comes through the parent token: that cascade runs without it, and a dispose
  /// meanwhile waits for the link's callback that is running, not for the callbacks it goes on to call.
  /// </summary>
  private readonly object _sourceGate = new();

  /// <summary>
  /// Indicates whether cancellation has been requested for this engine.
  /// </summary>
  public bool IsCancellationRequested => _token.IsCancellationRequested;

  /// <summary>
  /// Cancellation token to be observed by all tasks and engine activities.
  /// </summary>
  public CancellationToken CancellationToken => _token;

  /// <summary>
  /// The token of <see cref="_linkedCts"/>, kept apart because the source is disposed when the engine stops (see
  /// <see cref="StopAsync"/>), after which its token can still be read but the source's <c>Token</c> cannot.
  /// </summary>
  private readonly CancellationToken _token;

  /// <summary>
  /// All EngineTasks belonging to this engine, keyed by task name.
  /// </summary>
  /// <remarks>
  /// Tasks are created during <see cref="StartAsync"/> by invoking
  /// <see cref="SetUpTasks"/> and remain fixed for the lifetime of the run.
  /// <para>
  /// A snapshot that is never changed once published: a start builds the next set in a dictionary of its own and swaps
  /// it in at once, and a task added later copies the set. Readers on other threads (the states endpoint, state
  /// recomputes, queries, a monitor) may read or enumerate it while a kick-start retry rebuilds it; a dictionary changed
  /// in place made them fail with "collection was modified".
  /// </para>
  /// </remarks>
  protected IReadOnlyDictionary<string, EngineTask> _tasks => Volatile.Read(ref _taskSet);

  // Fix (NameCaseTest): task names were compared case-sensitively, so a lookup in another case did not find the task.
  // Every task set (this one, the one a set-up builds and the copy AddTask makes) now ignores case.
  private Dictionary<string, EngineTask> _taskSet = NewTaskSet();

  private static Dictionary<string, EngineTask> NewTaskSet() => new(StringComparer.OrdinalIgnoreCase);

  private static Dictionary<string, EngineTask> NewTaskSet(Dictionary<string, EngineTask> copyOf, string name, EngineTask task) =>
    new(copyOf, StringComparer.OrdinalIgnoreCase) { { name, task } };

  /// <summary>
  /// The set that <see cref="SetUpTasks"/> is building, which <see cref="AddTask"/> adds to; null outside a set-up.
  /// </summary>
  private Dictionary<string, EngineTask> _buildingTaskSet;

  /// <summary>
  /// What the last failed set-up threw, from that set-up until the next set is published: no task set was built, the
  /// engine is Faulted (see <see cref="ComputeState"/>), and this is the cause its Faulted change reports. Written under
  /// <see cref="_taskSetChange"/>.
  /// </summary>
  private volatile Exception _setUpException;

  /// <summary>
  /// True from a set-up that failed until the next set is published (see <see cref="_setUpException"/>).
  /// </summary>
  private bool SetUpFailed => _setUpException != null;

  private readonly Lock _taskSetChange = new();

  /// <summary>
  /// Read-only view of the engine's task set.
  /// </summary>
  public IReadOnlyDictionary<string, EngineTask> Tasks => _tasks;

  /// <summary>
  /// Indicates whether any task is currently running.
  /// </summary>
  public bool IsRunning() => _tasks.Values.Any(t => t.State == EngineTaskState.Running);

  /// <summary>
  /// Indicates whether any task has been cancelled.
  /// </summary>
  public bool IsTaskCancelled() => _tasks.Values.Any(t => t.State == EngineTaskState.Cancelled);

  /// <summary>
  /// Indicates whether all tasks have completed (success, failure, or cancellation).
  /// </summary>
  public bool IsCompleted => _tasks.Values.All(t => t.InnerTask.IsCompleted);

  /// <summary>
  /// Indicates whether all tasks required for running have completed.
  /// </summary>
  public bool IsRequiredCompleted =>
      _tasks.Values.Where(t => t.RequiredForRunning).All(t => t.InnerTask.IsCompleted);

  /// <summary>
  /// Indicates whether all tasks completed successfully.
  /// </summary>
  public bool IsCompletedWithSuccess() =>
      _tasks.Values.All(t => t.IsCompletedWithSuccess);

  /// <summary>
  /// Indicates whether all required tasks completed successfully.
  /// </summary>
  public bool IsRequiredCompletedWithSuccess() =>
      _tasks.Values.Where(t => t.RequiredForRunning).All(t => t.IsCompletedWithSuccess);

  /// <summary>
  /// Indicates whether any task has failed.
  /// </summary>
  public bool IsFailed() => _tasks.Values.Any(t => t.IsFailed);

  /// <summary>
  /// Logical name of the engine.
  /// </summary>
  /// <remarks>
  /// Defaults to the type name, but may be overridden via
  /// <see cref="DisplayNameAttribute"/>.
  /// </remarks>
  public virtual string EngineName =>
      GetType().GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
      ?? GetType().Name;

  private readonly ILogger _applicationLogger;

  /// <summary>
  /// Constructs a new BaseEngine objects
  /// It will have a local Cancellation token to cancel all its activities.
  /// </summary>
  /// <param name="logger">Logger class to send logging messages to.</param>
  public BaseEngine(ILogger logger)
  {
    // Link this task's CTS to the engine-wide token
    _linkedCts = new CancellationTokenSource();
    _token = _linkedCts.Token;
    _applicationLogger = new PrefixLogger(logger ?? NullLogger.Instance, () => LogTag);
  }

  /// <summary>
  /// Constructs a new BaseEngine objects.
  /// It will have a Cancellation token that listens to <paramref name="engineToken"/> as well,
  /// to cancel all its activities.
  /// </summary>
  /// <param name="logger">Logger class to send logging messages to.</param>
  /// <param name="engineToken">Parent token to derive a linked token from.</param>
  public BaseEngine(ILogger logger, CancellationToken engineToken)
  {
    // Link this task's CTS to the engine-wide token
    _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(engineToken);
    _token = _linkedCts.Token;
    _applicationLogger = new PrefixLogger(logger ?? NullLogger.Instance, () => LogTag);
  }

  /// <summary>
  /// <inheritdoc/>
  /// </summary>
  public ILogger Logger => _applicationLogger;

  /// <summary>
  /// What this engine's log lines, and those of the tasks it creates, start with: by default its name, so the lines of
  /// engines that load at the same time (for example, one engine per data set) can be told apart; none when <c>null</c>.
  /// </summary>
  /// <remarks>
  /// Read when a line is logged, not when the engine is constructed: a derived engine may only know its name then.
  /// </remarks>
  protected virtual string LogTag => $"{EngineName} ";

  /// <summary>
  /// The clock the engine's tasks use for their time-outs, run times and recovery backoff, and that the engine's own
  /// work can use for its waits; <see cref="TimeProvider.System"/> unless set. Tests set a clock they advance
  /// themselves.
  /// </summary>
  public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

  /*
   * ContextForNewEngine stuff.
   */

  private IEngineContext _context;

  /// <summary>
  /// <inheritdoc/>
  /// </summary>
  public IEngineContext Context
  {
    get { return _context; }
    set
    {
      var oldValue = _context;
      _context = value;
      // Fix (EngineContextChangedTest): the condition was "value != oldValue || ...", so any other instance counted as
      // a change and the context's Equals was never asked. Now a context equal to the current one is no change.
      if (!ReferenceEquals(value, oldValue) && !(value != null && value.Equals(oldValue)))
      {
        ContextChanged(oldValue, value);
      }
    }
  }

  /// <summary>
  /// Hook invoked when the engine context changes.
  /// </summary>
  /// <param name="oldValue">The old value.</param>
  /// <param name="value">The value.</param>
  /// <remarks>
  /// Subclasses may override to react to context changes.
  /// </remarks>
  protected virtual void ContextChanged(IEngineContext oldValue, IEngineContext value)
  {
  }

  /// <summary>
  /// Requests cancellation of all engine activities.
  /// </summary>
  protected virtual void RequestCancellation() => CancelSource();

  /// <summary>
  /// Cancels the engine's source. Once cancelled, it may have been disposed by a stop (see StopAsync);
  /// CancelBestEffort leaves it alone. <see cref="CancelAll"/> and <see cref="StopAsync"/> call this, not the virtual
  /// <see cref="RequestCancellation"/>, so an override cannot keep a stop from cancelling.
  /// </summary>
  private void CancelSource()
  {
    lock (_sourceGate)
      _linkedCts.CancelBestEffort(Logger);
  }

  // ---------------------------------------------------------------------
  // Engine state handling
  // ---------------------------------------------------------------------

  /// <summary>
  /// Backing field for <see cref="State"/>.
  /// </summary>
  private EngineState _state = EngineState.Created;

  /// <summary>
  /// <inheritdoc/>
  /// </summary>
  public EngineState State
  {
    get { return _state; }
    // Fix (EngineStatusEventTest): through the gate, so that a direct write and a recompute on another
    // thread cannot publish out of order either.
    set { SetStateSynchronized(() => value); }
  }

  /// <inheritdoc/>
  /// <remarks>
  /// <para>
  /// On a change to <see cref="EngineState.Faulted"/>, <see cref="EngineStatusEventArgs.Error"/> is never null. It is
  /// an <see cref="EngineTaskNotReadyException"/> for the required task whose failure faulted the engine: its name, the
  /// state it ended in and its <see cref="EngineTask.FailReason"/>, with the exception its work or recovery threw as
  /// the inner exception (none for a time-out). Of several failed required tasks, it is the one whose run ended first
  /// (for a failed recovery, the run that went into recovery; runs that ended within one tick of the clock, in the
  /// set's order); a task that failed only because a dependency did comes after those that failed by themselves.
  /// </para>
  ///
  /// <para>
  /// An engine Faulted without a failed task carries what faulted it instead: the exception its task set-up threw, or
  /// an <see cref="InvalidOperationException"/> saying why it was not started again (see
  /// <see cref="AbandonRestart"/>). Otherwise it is an <see cref="InvalidOperationException"/> saying the engine is
  /// Faulted: the state was set directly (the <see cref="State"/> setter, a <see cref="ComputeState"/> override), or
  /// the failed task was reset between the state change and the lookup.
  /// </para>
  ///
  /// <para>
  /// The error is taken when the state changes, so it describes the tasks as they were then, even if they are reset
  /// before the event is delivered.
  /// </para>
  /// </remarks>
  public event EventHandler<EngineStatusEventArgs> EngineStatusChanged;

  /// <summary>
  /// The error of the change being published (see <see cref="SetStateSynchronized"/>), for
  /// <see cref="OnEngineStatusChanged"/>. Only the publishing thread writes it; an override that calls the base method
  /// from another thread, during a publication, would read that publication's error.
  /// </summary>
  private Exception _publishingError;

  /// <summary>
  /// Hook invoked after a state transition.
  /// </summary>
  /// <param name="oldValue">The old value.</param>
  /// <param name="value">The value.</param>
  /// <remarks>
  /// Each subscriber is called in turn; one that throws is logged and does not keep the others from the change. An
  /// override calls the base method synchronously, with the same arguments: the error of a change to Faulted is the one
  /// taken when the state changed (see <see cref="EngineStatusChanged"/>), and is looked up again otherwise.
  /// </remarks>
  protected virtual void OnEngineStatusChanged(EngineState oldValue, EngineState value)
  {
    var handlers = EngineStatusChanged;
    if (handlers == null)
      return;
    var args = new EngineStatusEventArgs(EngineName, value,
      value == EngineState.Faulted ? _publishingError ?? FaultCause() : null);
    handlers.InvokeEach(handler => handler(this, args), ex => LogStatusSubscriberFailed(ex, value));
  }

  /// <summary>
  /// Gate serializing the task-callback state recomputes. Task work runs detached on the thread pool
  /// (EngineTask's Running state enters via Task.Run), so two tasks' CompletedStateReached callbacks can
  /// interleave the unsynchronized 'State = ComputeState()': a callback that computed a stale Starting
  /// (its sibling had not completed yet at read time) can be preempted and WRITE that stale value after
  /// another thread already wrote Ready — leaving the engine permanently Starting while every required
  /// task is CompletedWithSuccess. GetEngineWhenReadyAsync then returns null (state != Ready after the
  /// kick-start wait) and the engine's graph silently goes missing.
  /// </summary>
  private readonly object _stateGate = new();

  /// <summary>
  /// Atomically recomputes the engine state from the CURRENT task states and publishes it. Because the
  /// compute happens inside the gate, the chronologically last write always reflects the newest task
  /// states — a stale precomputed value can no longer overwrite a fresher one. The status-changed event
  /// is raised OUTSIDE the gate: subscribers (e.g. the coordinator) take their own locks, and holding
  /// the gate across the callback would invert lock order.
  /// </summary>
  protected void RecomputeStateSynchronized()
  {
    // Not while a prepared restart ends the current set: the engine is Created until the next set runs.
    if (!RestartPending)
      SetStateSynchronized(ComputeState);
  }

  /// <summary>
  /// The state changes that are still to be published, in the order they were made.
  /// </summary>
  private readonly Queue<(EngineState oldValue, EngineState newValue, Exception error)> _pendingChanges = new();

  /// <summary>
  /// True while a thread is publishing the pending changes.
  /// </summary>
  private bool _publishing;

  /// <summary>
  /// Writes the state the <paramref name="compute"/> gives under the gate and publishes the change outside it, in
  /// the order the changes were made.
  /// </summary>
  /// <remarks>
  /// Fix (EngineStatusEventTest): the write was ordered by the gate, the publication was not. A thread
  /// that had computed Starting could be preempted between the gate and the event, and a thread that then computed
  /// Ready published first; the Starting that followed sent the coordinator back to Initializing for good. The
  /// change is now queued under the gate and the queue drained by one thread at a time, so subscribers see the
  /// changes in the order they were made, still without the gate held across their callbacks. A subscriber that
  /// changes the state from its callback has that change delivered after the current one, not nested.
  /// </remarks>
  /// <param name="compute">The new state.</param>
  /// <param name="faultCause">What faulted the engine, for a change to Faulted; if null, it is looked up (see
  /// <see cref="FaultCause"/>).</param>
  private void SetStateSynchronized(Func<EngineState> compute, Exception faultCause = null)
  {
    lock (_stateGate)
    {
      var oldValue = _state;
      var newValue = compute();
      _state = newValue;
      // The cause is taken now, under the gate: by the time the change is published the tasks may have been reset.
      if (newValue != oldValue)
        _pendingChanges.Enqueue((oldValue, newValue, newValue == EngineState.Faulted ? faultCause ?? FaultCause() : null));
    }

    while (true)
    {
      (EngineState oldValue, EngineState newValue, Exception error) change;
      lock (_stateGate)
      {
        // Another thread is publishing and will take this change along.
        if (_publishing || _pendingChanges.Count == 0)
          return;
        _publishing = true;
        change = _pendingChanges.Dequeue();
      }

      try
      {
        _publishingError = change.error;
        OnEngineStatusChanged(change.oldValue, change.newValue);
      }
      catch (Exception ex)
      {
        // Only an override gets here (the base method catches per subscriber). It must not hold back the changes queued
        // after this one, nor fail whoever set the state.
        LogStatusOverrideFailed(ex, change.newValue);
      }
      finally
      {
        _publishingError = null;
        lock (_stateGate)
          _publishing = false;
      }
    }
  }

  /// <summary>
  /// Whether a required task in <paramref name="state"/> faults the engine: it failed (also by a time-out, a failed
  /// recovery or a failed dependency). A cancelled task makes the engine Stopped instead. Shared by
  /// <see cref="ComputeState"/> and <see cref="FaultCause"/>, so the cause is looked up among the tasks that fault it.
  /// </summary>
  private static bool FaultsEngine(EngineTaskState state) => state.IsFailed() && state != EngineTaskState.Cancelled;

  /// <summary>
  /// What faulted the engine, for the event (see <see cref="EngineStatusChanged"/>): the failed set-up's exception, or
  /// the required task whose run ended first, or, with neither, an <see cref="InvalidOperationException"/>.
  /// </summary>
  private Exception FaultCause()
  {
    if (_setUpException is { } setUpException)
      return setUpException;
    EngineTask cause = null;
    var causeState = default(EngineTaskState);
    var causeRank = (dependencyFailed: true, ended: DateTime.MaxValue);
    foreach (var task in _tasks.Values)
    {
      if (!task.RequiredForRunning)
        continue;
      // Read once: the task can change meanwhile.
      var state = task.State;
      if (!FaultsEngine(state))
        continue;
      // A task that failed only because a dependency did never ran; it comes after those that failed by themselves.
      var rank = (dependencyFailed: state == EngineTaskState.DependencyFailed, ended: task.RunEndedAtUtc ?? DateTime.MaxValue);
      if (cause == null || rank.CompareTo(causeRank) < 0)
      {
        cause = task;
        causeState = state;
        causeRank = rank;
      }
    }
    return cause != null
      ? EngineTaskNotReadyException.For(cause, causeState)
      : new InvalidOperationException($"Engine '{EngineName}' is Faulted.");
  }

  /// <summary>
  /// Computes the engine lifecycle state from required task states.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The engine is considered Ready only when all required tasks
  /// have completed successfully and a context is present.
  /// </para>
  ///
  /// <para>
  /// Failure or cancellation of any required task immediately
  /// transitions the engine to a terminal state. A failure is more severe than a
  /// cancellation: the engine is Faulted if any required task failed, and Stopped
  /// only if required tasks were cancelled and none failed.
  /// </para>
  /// <para>
  /// An engine whose last set-up failed has no tasks and is Faulted. An override that does not call this method must
  /// handle that case itself, or such an engine counts as Ready.
  /// </para>
  /// </remarks>
  protected virtual EngineState ComputeState()
  {
    // Fix (EngineStatusEventTest): a stopped engine is not started again, so once its token is cancelled it
    // is Stopped whatever its tasks say (a background task's late cancellation must not flip it back to Ready).
    if (IsCancellationRequested)
      return EngineState.Stopped;
    // No task set could be built (see SetUpAndStartAsync): with no tasks the engine would otherwise count as Ready.
    if (SetUpFailed)
      return EngineState.Faulted;

    var bestState = EngineState.Created;
    var allAreCompletedSuccessfully = true;
    var anyCancelled = false;
    // var allAreCompleted = true;
    foreach (var t in _tasks.Values.Where(t => t.RequiredForRunning))
    {
      switch (t.State)
      {
        case EngineTaskState.Created:
          break;
        case EngineTaskState.WaitingForDependencies:
          bestState = EngineState.Starting;
          break;
        case EngineTaskState.Running:
        case EngineTaskState.Recovering:
          bestState = EngineState.Starting;
          break;
        case EngineTaskState.CompletedWithSuccess:
          bestState = EngineState.Starting;
          // allAreCompleted &= t.InnerTask.IsCompleted;
          continue;
        case EngineTaskState.Cancelled:
          // Keep looking: a failed task elsewhere makes the engine Faulted, not Stopped.
          anyCancelled = true;
          break;
        case EngineTaskState.Resetting:
          // Between two runs, it counts as Faulted, though it has no cause to report.
          return EngineState.Faulted;
        case var failed when FaultsEngine(failed):
          return EngineState.Faulted;
      }

      allAreCompletedSuccessfully = false;
    }

    if (anyCancelled)
      return EngineState.Stopped;
    if (allAreCompletedSuccessfully)
      return (Context == null) ? EngineState.Stopped : EngineState.Ready;
    return bestState;
  }

  // ---------------------------------------------------------------------
  // Task graph definition
  // ---------------------------------------------------------------------

  /// <summary>
  /// Creates all tasks and declares their dependencies.
  /// </summary>
  /// <remarks>
  /// Subclasses must:
  /// <list type="bullet">
  /// <item>Create all tasks</item>
  /// <item>Add all dependency edges</item>
  /// <item>Not start tasks directly</item>
  /// </list>
  /// </remarks>
  protected abstract void SetUpTasks();

  // ---------------------------------------------------------------------
  // Engine lifecycle API
  // ---------------------------------------------------------------------

  /// <summary>
  /// Convenience method to run the engine start → ready → stop sequence.
  /// </summary>
  public async Task RunAsync()
  {
    await StartAsync().ConfigureAwait(false);
    await EnsureReadyAsync().ConfigureAwait(false);
    await StopAsync().ConfigureAwait(false);
  }

  /// <summary>
  /// Validates that no EngineTask depends on itself.
  /// </summary>
  /// <exception cref="CyclicEngineTaskDependencyException"></exception>
  public void CheckForCyclicDependecies() => EngineTask.CheckForCycles(_tasks.Values);

  /// <inheritdoc/>
  public virtual Task StartAsync()
  {
    // Stopping is final: a stopped engine is not started again. An engine cancelled other than by StopAsync (its parent
    // token, CancelAll) has not published Stopped yet: ComputeState says Stopped once the token is cancelled. A restart
    // prepared before the cancel (a reset) is not carried out: it is released first, or the recompute is skipped and
    // whoever waits for the restart waits until StopAsync.
    if (IsCancellationRequested)
    {
      CompleteRestart();
      RecomputeStateSynchronized();
      return Task.CompletedTask;
    }
    // Note that the context is stored in Context, at the time this is icalled.

    // A start replaces the task set. Tasks of the previous set that have not completed (a background monitor, which
    // keeps running while a required task failed; tasks someone started or waits on) would otherwise run on next to
    // their successors, since their token is the engine's, and their waiters would wait for a set that is thrown
    // away: end them first (see PrepareRestartAsync), which also releases those waiters. Ending cancels their work but
    // cannot stop it; the new set's tasks start once it has stopped (see SetUpAndStartAsync).
    var previous = _tasks.Values.Where(t => !t.State.IsCompleted()).ToArray();
    return previous.Length == 0 && !RestartPending ? SetUpAndStartAsync() : EndPreviousThenStartAsync(previous);
  }

  private async Task EndPreviousThenStartAsync(EngineTask[] previous)
  {
    ArmRestart();
    await Task.WhenAll(previous.Select(t => t.EndAsync())).ConfigureAwait(false);
    SetUnlessStopped(EngineState.Created);
    await SetUpAndStartAsync().ConfigureAwait(false);
  }

  /// <summary>
  /// Work outside this engine, or left by a failed set-up, that the next task set waits for (see
  /// <see cref="StartAfter"/>). Flat, so that retries after failed set-ups do not wrap it deeper each time. Guarded by
  /// <see cref="_taskSetChange"/>.
  /// </summary>
  private readonly HashSet<Task> _inheritedWork = [];

  /// <summary>
  /// Makes the tasks of the next task set run only once <paramref name="work"/> has ended, as they wait for the work of
  /// the set they replace: for work outside this engine that builds on the same data, such as that of a previous engine
  /// whose data this engine takes over. Used by the next start only. Adds to the work already waited for (such as the
  /// work a failed set-up handed on) rather than replacing it.
  /// </summary>
  /// <param name="work">The work to wait for. Null, or work that has ended, is ignored.</param>
  /// <remarks>Called before <see cref="StartAsync"/>.</remarks>
  protected void StartAfter(Task work) => InheritWork([work]);

  /// <summary>
  /// Adds <paramref name="work"/> to the work the next task set waits for (see <see cref="StartAfter"/>), skipping work
  /// that has ended and work that is there already, and dropping work there that has ended meanwhile.
  /// </summary>
  private void InheritWork(IEnumerable<Task> work)
  {
    lock (_taskSetChange)
    {
      _inheritedWork.RemoveWhere(static w => w.IsCompleted);
      foreach (var w in work)
      {
        if (w is { IsCompleted: false })
          _inheritedWork.Add(w);
      }
    }
  }

  /// <summary>
  /// The work of <paramref name="pending"/> as one task: a completed one for none, the piece itself for one, else one
  /// that ends when all have ended.
  /// </summary>
  private static Task WhenAllPending(IReadOnlyList<Task> pending) => pending.Count switch
  {
    0 => Task.CompletedTask,
    1 => pending[0],
    _ => Task.WhenAll(pending),
  };

  private async Task SetUpAndStartAsync()
  {
    try
    {
      // A stop that came in while the previous set was ending (see EndPreviousThenStartAsync): the engine is not set up
      // again, so a derived engine does not build tasks or load data after it has released its resources. The set it
      // would have replaced keeps its sources: they are cancelled through the engine's token, and go with the engine.
      if (IsCancellationRequested)
      {
        // Release the restart first: the recompute is skipped while one is pending.
        CompleteRestart();
        RecomputeStateSynchronized();
        return;
      }
      // Create all tasks and dependencies, in a set of their own that replaces the current one at once.
      LogSettingUpTasks();
      // The work of the set that is replaced can still be running: its tasks were ended (or timed out), which cancels
      // their work but cannot stop it. The new set's tasks start once it has ended, since they build on the same data.
      // So does work outside this engine that builds on it (see StartAfter).
      // Only work still running is waited for, and a failed set-up hands the pieces on as they are (see the catch), so
      // that retries do not wrap them deeper each time.
      var replacedTasks = _tasks.Values.ToArray();
      List<Task> pending;
      lock (_taskSetChange)
      {
        InheritWork(replacedTasks.Select(t => t.LatestRunEnded));
        pending = [.. _inheritedWork];
        _inheritedWork.Clear();
        _buildingTaskSet = NewTaskSet();
      }
      var replacedWork = WhenAllPending(pending);
      bool afterFailedSetUp;
      try
      {
        SetUpTasks();
        lock (_taskSetChange)
        {
          // Before the set is published: the tasks of a cyclic set would wait for each other for good, and so would
          // everyone waiting for them.
          EngineTask.CheckForCycles(_buildingTaskSet.Values);
          if (!replacedWork.IsCompleted)
          {
            foreach (var task in _buildingTaskSet.Values)
              task.RunAfter(replacedWork);
          }
          Volatile.Write(ref _taskSet, _buildingTaskSet);
          _buildingTaskSet = null;
          afterFailedSetUp = SetUpFailed;
          _setUpException = null;
        }
      }
      catch (Exception ex)
      {
        // The set could not be built: none of it is published, and the set it replaces is gone too (its tasks were
        // ended). The engine is Faulted until a start builds a set; a prepared restart can start it again (see
        // PrepareRestartAsync).
        Dictionary<string, EngineTask> unpublished;
        lock (_taskSetChange)
        {
          unpublished = _buildingTaskSet;
          _buildingTaskSet = null;
          // The work the next set must wait for is still running: hand it on to that set, before a start can see the
          // failure and build it.
          InheritWork(pending);
          Volatile.Write(ref _taskSet, NewTaskSet());
          _setUpException = ex;
        }
        // The tasks built before the failure never ran. (An overlapping set-up may have published the set already.)
        if (unpublished != null)
          ReleaseTokens(unpublished.Values);
        // A cancellation by a stop is part of the stop, not a failure. Any other failure is logged, also during a stop.
        if (!(IsCancellationRequested && ex is OperationCanceledException))
          LogSetUpFailed(ex);
        // A stop during the set-up (which may be why it threw), or one that comes in until the state is decided, leaves
        // the engine Stopped, as ComputeState would say: decided under the state gate, so it is not overwritten.
        SetUnlessStopped(EngineState.Faulted);
        throw;
      }
      finally
      {
        // Published over, or replaced by an empty set: the replaced set is gone either way.
        ReleaseTokens(replacedTasks);
      }

      // Fire the start trigger for those that we're started yet.
      // Fix (EngineStatusEventTest): publish Starting before the tasks run. The state was otherwise only
      // recomputed when a task ended, so an engine went from Created straight to Ready (or Faulted) and a host
      // watching EngineStatusChanged never saw it start. Publishing it after the triggers (from a recompute) is not
      // enough: a task that ends before the recompute makes it compute Ready at once, and Starting is skipped again.
      // So is a start after a failed set-up, which left the engine Faulted.
      // Decided under the state gate, so a stop that came in meanwhile is not overwritten with Starting.
      if (State == EngineState.Created || afterFailedSetUp)
        SetUnlessStopped(EngineState.Starting);
      await TriggerStartCoreAsync().ConfigureAwait(false);
    }
    finally
    {
      // The next set is in place (or could not be built): whoever waited for a prepared restart goes on with it.
      CompleteRestart();
    }
    RecomputeStateSynchronized();
  }

  /// <summary>
  /// Fires Start triggers on tasks that have not yet started.
  /// </summary>
  /// <param name="tasks">The tasks.</param>
  public Task TriggerStartAsync(IEnumerable<EngineTask> tasks = null)
  {
    // A set that a prepared restart replaces is not started again (see PrepareRestartAsync).
    return RestartPending ? Task.CompletedTask : TriggerStartCoreAsync(tasks);
  }

  private async Task TriggerStartCoreAsync(IEnumerable<EngineTask> tasks = null)
  {
    // Set default task list.
    tasks ??= _tasks.Values;
    // Fire Start on all tasks:
    // Tasks without dependencies will go straight to Running.
    // Tasks with dependencies will go to WaitingForDependencies.
    // Fix (StartTriggerTest): each task's state is read once. Read twice, a task that a dependency started in between
    // (Created to Running) was reset, and its work ran again. A task that leaves Created between this read and the
    // trigger ignores the Start, in whatever state it has reached.
    var startTasks = tasks
        .Where(t => t.State == EngineTaskState.Created)
        .Select(t => t.FireAsync(EngineTaskTrigger.Start))
        .ToArray();
    LogFiringStartTriggers();
    await Task.WhenAll(startTasks).ConfigureAwait(false);
  }

  /// <summary>
  /// Fires Reset triggers on the tasks (Start on those that never started) and, if the engine has started, starts the
  /// reset tasks again and recomputes the engine state.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The engine state is only recomputed when a task completes or fails, and the reset tasks of an engine that has
  /// started are started by nobody else (its kick-start completed long ago). Without this, a Ready engine would keep
  /// reporting Ready while its tasks are reset, and a lookup that waits for a Starting engine could find tasks still to
  /// start. The tasks are started before the state is recomputed, so such a lookup finds nothing left to start and
  /// fires no triggers of its own. An engine that has not started yet is only reset (its kick-start starts it), and a
  /// stopped engine is not started again.
  /// </para>
  ///
  /// <para>
  /// The set is reset in two passes (see <see cref="ResetInTwoPassesAsync"/>): first every task's current run is ended,
  /// dependents before the tasks they depend on; then the tasks go back to Created, dependencies first. So no dependent
  /// goes on running on a dependency that is being reset, and no started task of the set can start again (by a caller,
  /// or by a dependency's run completing) on a dependency of the set that is still to be reset. A task of the set that
  /// is still Created is only started after the passes.
  /// </para>
  ///
  /// <para>
  /// When a task's reset throws, the tasks are still started and the state recomputed as above, and then the reset's
  /// exception is rethrown. If starting them throws as well, that is logged, and the reset's exception is the one thrown.
  /// </para>
  /// </remarks>
  /// <param name="tasks">The tasks.</param>
  /// <exception cref="CyclicEngineTaskDependencyException">Tasks of the engine that depend on one of
  /// <paramref name="tasks"/> form a cycle (possible only for tasks added after the set-up); nothing is reset.</exception>
  public async Task TriggerResetAsync(IEnumerable<EngineTask> tasks = null)
  {
    // A set that a prepared restart replaces is not reset and started again (see PrepareRestartAsync).
    if (RestartPending)
      return;
    // Set default task list.
    tasks ??= _tasks.Values;
    // Decide before the reset changes the task states.
    var startAgain = State != EngineState.Created && !IsCancellationRequested;
    // Fire Reset on all tasks: a reset task goes back to Created. A Cancelled task is reset too while the engine is live:
    // it was cancelled by something else than a stop (an OperationCanceledException that is not ours, an
    // EngineTaskException), and skipping it left the tasks that depend on it waiting for good. Once the engine is
    // stopped, its cancelled tasks stay cancelled.
    var resetTasks = tasks
        .Where(t => !t.IsStarted || t.State != EngineTaskState.Cancelled || !IsCancellationRequested)
        .ToArray();
    // Reset the started tasks first, and only then fire Start on those that were still Created: a task started while a
    // dependency in this batch was still failed would otherwise fail at once (a task that starts with a failed
    // dependency ends DependencyFailed).
    var notStarted = resetTasks.Where(t => t.State == EngineTaskState.Created).ToArray();
    var toReset = resetTasks.Except(notStarted).ToArray();
    // Ordered before any reset, so a cycle throws with nothing reset.
    var levels = DependentsFirst(toReset);
    LogFiringResetTriggers();
    // A reset that throws (an OnResetEnteredAsync override) leaves the other tasks reset, in Created: they are started
    // and the state recomputed anyway, and only then is the reset's failure rethrown (#58, items 3 and 23).
    ExceptionDispatchInfo resetFailure = null;
    try
    {
      await ResetInTwoPassesAsync(levels).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      resetFailure = ExceptionDispatchInfo.Capture(ex);
    }

    try
    {
      await StartAfterResetAsync(notStarted, resetTasks, startAgain).ConfigureAwait(false);
    }
    catch (Exception ex) when (resetFailure != null)
    {
      // The reset's failure is the one the caller gets.
      LogStartAfterFailedResetFailed(ex);
    }
    resetFailure?.Throw();
  }

  private async Task StartAfterResetAsync(EngineTask[] notStarted, EngineTask[] resetTasks, bool startAgain)
  {
    await TriggerStartCoreAsync(notStarted).ConfigureAwait(false);
    if (!startAgain)
      return;

    // Start what the reset left in Created: tasks without dependencies go straight to Running, the others to
    // WaitingForDependencies. Then publish the state (Starting while they run).
    // Fix (EngineStatusEventTest): Starting is published before the triggers, see StartAsync; a reset whose
    // tasks ended before the recompute went from Ready to Ready and was never announced. A reset of background tasks
    // only does not take the engine away from the host, so it is not announced.
    if (resetTasks.Any(t => t.RequiredForRunning))
      SetUnlessStopped(EngineState.Starting);
    await TriggerStartAsync(resetTasks).ConfigureAwait(false);
    RecomputeStateSynchronized();
  }

  /// <summary>
  /// Resets the tasks <paramref name="taskNames"/> and, with <paramref name="resetDependentTasks"/>, every task that
  /// depends on one of them, directly or not; then starts them again, as <see cref="TriggerResetAsync"/> does.
  /// </summary>
  /// <remarks>
  /// The runs of dependents are ended before those of the tasks they depend on, and no started task of the set starts
  /// again on a dependency of the set that is still to be reset (see <see cref="TriggerResetAsync"/>). A task named twice or
  /// reached twice is reset once. A reset ends the current run: its outcome is dropped and its token cancelled; it does
  /// not wait for the work to end, so work that ignores its token goes on until it checks. While a restart is prepared nothing is reset, as with
  /// <see cref="TriggerResetAsync"/>: the set is about to be replaced.
  /// </remarks>
  /// <param name="taskNames">The names of the tasks to reset (compared ignoring case).</param>
  /// <param name="resetDependentTasks">Whether to reset the tasks that depend on them too.</param>
  /// <exception cref="ArgumentException">The engine has no task of one of the names.</exception>
  /// <exception cref="InvalidOperationException">The engine is stopped, so its tasks would not run again.</exception>
  /// <exception cref="CyclicEngineTaskDependencyException">Tasks that depend on the tasks to reset form a cycle
  /// (possible only for tasks added after the set-up); nothing is reset.</exception>
  public async Task ResetTasksAsync(IEnumerable<string> taskNames, bool resetDependentTasks)
  {
    if (taskNames == null)
      throw new ArgumentNullException(nameof(taskNames));
    var tasks = _tasks;
    var named = new HashSet<EngineTask>();
    foreach (var name in taskNames)
    {
      if (name == null || !tasks.TryGetValue(name, out var task))
        throw new ArgumentException($"Engine '{EngineName}' has no task named '{name}'.", nameof(taskNames));
      named.Add(task);
    }
    // A stopped engine's reset tasks would sit in Created for good (TriggerResetAsync does not start them again).
    if (IsCancellationRequested)
      throw new InvalidOperationException($"Engine '{EngineName}' is stopped, so its tasks cannot be run again.");

    if (resetDependentTasks)
      AddAllDependents(named.ToArray(), named, DirectDependents(tasks.Values));
    await TriggerResetAsync(named.ToArray()).ConfigureAwait(false);
  }

  /// <summary>
  /// Each task's direct dependents among <paramref name="tasks"/>, for the tasks that have any.
  /// </summary>
  private static Dictionary<EngineTask, List<EngineTask>> DirectDependents(IEnumerable<EngineTask> tasks)
  {
    var dependents = new Dictionary<EngineTask, List<EngineTask>>();
    foreach (var task in tasks)
    {
      foreach (var dependency in task.DependsOn)
      {
        if (!dependents.TryGetValue(dependency, out var list))
          dependents[dependency] = list = new List<EngineTask>();
        list.Add(task);
      }
    }
    return dependents;
  }

  /// <summary>
  /// Adds to <paramref name="found"/> every task that depends on one of <paramref name="tasks"/>, directly or not, by
  /// the map of direct dependents <paramref name="dependents"/> (see <see cref="DirectDependents"/>).
  /// </summary>
  private static void AddAllDependents(IEnumerable<EngineTask> tasks, HashSet<EngineTask> found,
    Dictionary<EngineTask, List<EngineTask>> dependents)
  {
    var pending = new Stack<EngineTask>(tasks);
    while (pending.Count > 0)
    {
      if (!dependents.TryGetValue(pending.Pop(), out var direct))
        continue;
      foreach (var dependent in direct)
      {
        // Add returns false if already present: each task is walked once.
        if (found.Add(dependent))
          pending.Push(dependent);
      }
    }
  }

  /// <summary>
  /// Puts <paramref name="tasks"/> in levels to reset one after the other: each task in a level after the tasks of
  /// <paramref name="tasks"/> that depend on it, directly or not (also through tasks outside it).
  /// </summary>
  /// <remarks>
  /// A task's level is the longest chain of dependents above it in the engine's tasks, so the tasks of one level do not
  /// depend on each other and are reset together. Linear in the tasks and their dependencies. The mechanic's stop walk
  /// over engines (<c>MechanicEngine.StopDependentsFirstAsync</c>) is a Kahn walk in the same direction: a fix to one
  /// may apply to the other.
  /// </remarks>
  /// <exception cref="CyclicEngineTaskDependencyException">Tasks that depend on one of <paramref name="tasks"/>, directly
  /// or not, form a cycle (possible only for tasks added after the set-up, which
  /// <see cref="CheckForCyclicDependecies()"/> did not see). The task in the exception is the one of
  /// <paramref name="tasks"/> that the cycle depends on, which need not be in the cycle itself.</exception>
  private List<List<EngineTask>> DependentsFirst(EngineTask[] tasks)
  {
    if (tasks.Length == 0)
      return new List<List<EngineTask>>();
    var all = _tasks.Values.Union(tasks).ToArray();
    var dependents = DirectDependents(all);
    // Kahn's algorithm over the reversed graph: a task is placed once all its direct dependents are.
    var remaining = all.ToDictionary(t => t, t => dependents.TryGetValue(t, out var d) ? d.Count : 0);
    var level = all.ToDictionary(t => t, _ => 0);
    var ready = new Queue<EngineTask>(all.Where(t => remaining[t] == 0));
    while (ready.Count > 0)
    {
      var task = ready.Dequeue();
      foreach (var dependency in task.DependsOn)
      {
        if (!remaining.ContainsKey(dependency))
          continue;
        level[dependency] = Math.Max(level[dependency], level[task] + 1);
        if (--remaining[dependency] == 0)
          ready.Enqueue(dependency);
      }
    }

    // A task never placed has a cycle among its dependents.
    if (tasks.FirstOrDefault(t => remaining[t] > 0) is { } cyclic)
      throw new CyclicEngineTaskDependencyException(cyclic);
    return tasks.GroupBy(t => level[t]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
  }

  /// <summary>
  /// Resets the tasks of <paramref name="levels"/> (see <see cref="DependentsFirst"/>) in two passes: the first ends each
  /// task's current run, a level after the other (dependents first); the second fires Reset, in the opposite order
  /// (dependencies first).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The first pass leaves the states as they are and drops each run's outcome. Until its reset, a task whose run was
  /// ended also ignores its dependencies' outcomes, so neither a dependency's old run nor one outside the set can start
  /// it (on its cancelled token) or fail it. A dependent whose run ended after its dependency's would go on running,
  /// until then, on a dependency that was reset under it.
  /// </para>
  ///
  /// <para>
  /// In the second pass a task goes back to Created only once the tasks of the set it depends on have. So whatever starts
  /// it (a caller, or a dependency's new run completing) waits for those dependencies' next run. A task whose reset
  /// throws is cancelled, so whoever waits on it is released; the failure is reported once every level has been reset:
  /// the other tasks are reset anyway.
  /// </para>
  /// </remarks>
  private static async Task ResetInTwoPassesAsync(List<List<EngineTask>> levels)
  {
    foreach (var level in levels)
      await Task.WhenAll(level.Select(t => t.EndRunAsync())).ConfigureAwait(false);

    var resets = new List<Task>();
    for (var i = levels.Count - 1; i >= 0; i--)
    {
      var resetLevel = levels[i].Select(t => t.FireAsync(EngineTaskTrigger.Reset)).ToArray();
      resets.AddRange(resetLevel);
      try
      {
        await Task.WhenAll(resetLevel).ConfigureAwait(false);
      }
      catch
      {
        // Reported below, once the next levels have been reset too.
      }
    }
    await Task.WhenAll(resets).ConfigureAwait(false);
  }

  /// <inheritdoc/>
  public virtual async Task EnsureReadyAsync()
  {
    // A prepared restart is waited for: the set it replaces is not the one to wait for.
    await WaitForRestartAsync().ConfigureAwait(false);
    // Check if all tasks have been fired to start, otherwise do it now.
    await TriggerStartAsync().ConfigureAwait(false);
    // Wait for all tasks (CompletedWithSuccess / Failed / Cancelled)
    var completionTasks = _tasks.Values
        .Where(t => t.RequiredForRunning && !t.IsCompleted)
        .Select(t => t.InnerTask).ToArray();
    LogWaitingForRequiredTasks();
    await Task.WhenAll(completionTasks).ConfigureAwait(false);
  }

  /// <inheritdoc/>
  public virtual void EnsureReadyWait() => EnsureReadyAsync().GetAwaiter().GetResult();

  /// <inheritdoc/>
  public virtual async Task EnsureAllReadyAsync()
  {
    await WaitForRestartAsync().ConfigureAwait(false);
    // Check if all tasks have been fired to start, otherwise do it now.
    await TriggerStartAsync().ConfigureAwait(false);
    // Wait for the required tasks (CompletedWithSuccess / Failed / Cancelled). Background tasks are not waited for: a
    // monitoring task runs until the engine stops, so waiting for it would never end.
    var completionTasks = _tasks.Values
        .Where(t => t.RequiredForRunning && !t.IsCompleted)
        .Select(t => t.InnerTask).ToArray();
    LogWaitingForRequiredTasks();
    await Task.WhenAll(completionTasks).ConfigureAwait(false);
  }

  /// <inheritdoc/>
  public virtual async Task<bool> EnsureTasksReadyAsync(string[] taskNames)
  {
    await WaitForRestartAsync().ConfigureAwait(false);
    // Check if tasks have been fired to start, otherwise do it now.
    await TriggerStartAsync().ConfigureAwait(false);
    // Fix (NameCaseTest): ordinal like every other name comparison, instead of culture-aware.
    var namesHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (string taskName in taskNames)
    {
      namesHash.Add(taskName);
    }
    // Fix (EngineReadinessApiTest): a task that had already completed was left out before the tasks were counted, so
    // asking for completed tasks reported them as not found (false). Count every task found, wait for those still running.
    var foundTasks = _tasks.Values.Where(t => namesHash.Contains(t.Name)).ToArray();
    // Wait for task (CompletedWithSuccess / Failed / Cancelled)
    var completionTasks = foundTasks
        .Where(t => !t.IsCompleted)
        .Select(t => t.InnerTask).ToArray();
    if (foundTasks.Length == 0)
    {
      if (taskNames.Length == 1)
      {
        LogTaskNotFound(taskNames[0]);
      }
      else
      {
        // Guarded despite the generated method's own check: joining the names builds a string.
        if (Logger.IsEnabled(LogLevel.Warning))
        {
          LogNoTasksFound(String.Join("', '", taskNames));
        }
      }
      return false; // None of the tasks were found.
    }
    if (foundTasks.Length != namesHash.Count)
    {
      // TODO: some tasks are optional and will not exist (depending on how the host configures the engine)
      // Guarded despite the generated method's own check: joining the names builds a string.
      if (Logger.IsEnabled(LogLevel.Trace))
      {
        LogSomeTasksNotFound(String.Join("', '", taskNames));
      }
      // TODO: specify which tasks are missing
    }
    // Guarded despite the generated method's own check: joining the names builds a string.
    if (Logger.IsEnabled(LogLevel.Debug))
    {
      LogEnsuringTasksReady(String.Join("', '", taskNames), completionTasks.Length);
    }
    await Task.WhenAll(completionTasks).ConfigureAwait(false);
    return true;
  }

  /// <inheritdoc/>
  public virtual async Task EnsureTaskReadyAsync(string taskName, CancellationToken token = default)
  {
    if (taskName == null)
      throw new ArgumentNullException(nameof(taskName));
    // The task an earlier pass waited for, which a restart or reset ended.
    EngineTask ended = null;
    while (true)
    {
      // Look the task up after a prepared restart, in the set that replaced the current one.
      await WaitForRestartAsync().WaitAsync(token).ConfigureAwait(false);
      if (!_tasks.TryGetValue(taskName, out var task))
      {
        // The restart's set has no such task (its set-up failed, or no longer creates it): the task that was waited
        // for ended, and that is the answer. Only a name never found is unknown.
        if (ended != null)
          throw EngineTaskNotReadyException.For(ended, ended.State);
        throw new ArgumentException($"The engine has no task '{taskName}'.", nameof(taskName));
      }
      await EnsureReadyAsync(task, token).ConfigureAwait(false);
      // Read once: the exception reports the state that was looked at.
      var state = task.State;
      if (state == EngineTaskState.CompletedWithSuccess)
        return;
      // A restart prepared meanwhile ended the task (Cancelled) and replaces it, or a reset made it run again: the
      // answer is the next run's, so look again.
      if (!state.IsCompleted() || RestartPending || !_tasks.TryGetValue(taskName, out var current) || current != task)
      {
        ended = task;
        continue;
      }
      throw EngineTaskNotReadyException.For(task, state);
    }
  }

  /// <summary>
  /// Waits for all task identified by <paramref name="taskName"/> and its dependencies to complete, including optional ones.
  /// </summary>
  /// <param name="taskName"></param>
  /// <returns></returns>
  public virtual async Task EnsureReadyAsync(string taskName)
  {
    // Look the task up after a prepared restart, in the set that replaced the current one.
    await WaitForRestartAsync().ConfigureAwait(false);
    await EnsureReadyAsync(_tasks[taskName]).ConfigureAwait(false);
  }

  /// <summary>
  /// Waits for all task identified by <paramref name="task"/> and its dependencies to complete, including optional ones.
  /// </summary>
  /// <param name="task">The task to wait for.</param>
  /// <param name="token">Ends the wait when cancelled (it then throws <see cref="OperationCanceledException"/>); the
  /// tasks run on.</param>
  public virtual async Task EnsureReadyAsync(EngineTask task, CancellationToken token = default)
  {
    // A wait that is cancelled already starts nothing.
    token.ThrowIfCancellationRequested();
    // After a prepared restart a task of the replaced set has ended; the caller looks the task up again.
    await WaitForRestartAsync().WaitAsync(token).ConfigureAwait(false);
    // A completed wait for the restart does not look at the token.
    token.ThrowIfCancellationRequested();
    var allTasks = task.GetThisAndAllDependsOn();
    // We should never await tasks that have not been fire to start yet.
    await TriggerStartAsync(allTasks).ConfigureAwait(false);
    // Wait for all tasks (CompletedWithSuccess / Failed / Cancelled)
    var completionTasks = allTasks
        // var completionTasks = task // We do not need to get its dependencies, since the tasks already have that stored in them.
        .Where(t => !t.IsCompleted)
        .Select(t => t.InnerTask).ToArray();
    if (completionTasks.Length == 0)
      return;
    LogWaitingForTask(task);
    // Cancelling the token ends the wait only; the tasks run on. Each task is waited for in turn (a task's latch never
    // faults), so a cancelled wait leaves at most the task it was waiting for with a continuation, and that only on
    // netstandard2.0 (see TaskExtensions.WaitAsync): a WhenAll would stay attached until every task completed, once per
    // cancelled wait. A token that cannot be cancelled awaits each task directly.
    foreach (var completion in completionTasks)
    {
      if (!completion.IsCompleted)
        await completion.WaitAsync(token).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Lets go of the token sources of tasks that no longer belong to the engine's set, each once its latest run has
  /// ended (see <see cref="EngineTask.ReleaseTokenAsync"/>). Without it every replaced task would stay registered on
  /// the engine's token for as long as the engine lives.
  /// </summary>
  private static void ReleaseTokens(IEnumerable<EngineTask> tasks)
  {
    foreach (var task in tasks)
      _ = task.ReleaseTokenAsync();
  }

  /// <inheritdoc/>
  public virtual async Task StopAsync()
  {
    CancelAll();
    // A prepared restart will not happen: release whoever waits for it.
    CompleteRestart();
    // Fix (EngineStatusEventTest): publish Stopped. The state is otherwise only recomputed when a task ends,
    // and cancelling the token does nothing to tasks that had already completed, so a Ready engine kept reporting
    // Ready after it was stopped. ComputeState reports Stopped once the engine's token is cancelled.
    RecomputeStateSynchronized();

    // Tasks that have not started their work (Created, or waiting for a dependency) do not observe the token, and a
    // waiting one only ends when its dependency does: one whose work ignores the token never does. Cancel them, so that
    // whoever waits for them is released.
    var notStarted = _tasks.Values
        .Where(t => t.State is EngineTaskState.Created or EngineTaskState.WaitingForDependencies)
        .ToArray();
    await Task.WhenAll(notStarted.Select(t => t.CancelAsync("Cancelled because the engine was stopped.")))
      .ConfigureAwait(false);

    // Cancelled, so nothing cancels it again (see CancelAll); disposing it takes it off the token it is linked to. Under
    // the gate, so it waits for another thread's cancel that is still notifying (see _sourceGate).
    lock (_sourceGate)
      _linkedCts.Dispose();
    ReleaseResources();
  }

  /// <summary>
  /// Called at the end of <see cref="StopAsync"/>, so that a derived engine can let go of the data it loaded.
  /// </summary>
  /// <remarks>
  /// Does nothing by default. When it is called, the engine's token has been cancelled and the tasks that had not
  /// started their work have been cancelled, but work that ignores its cancellation may still be running. It is called
  /// again when a stopped engine is stopped again.
  /// </remarks>
  protected virtual void ReleaseResources()
  {
  }

  // ---------------------------------------------------------------------
  // Task reset and recovery helpers
  // ---------------------------------------------------------------------

  /// <inheritdoc/>
  /// <remarks>
  /// The engine is started again with a new task set (see <see cref="StartAsync"/>), so the current set is not reset
  /// but ended (see <see cref="PrepareRestartAsync"/>).
  /// </remarks>
  public Task ResetAllTasksAsync() => PrepareRestartAsync(t => t.State >= EngineTaskState.Running);

  /// <inheritdoc/>
  /// <remarks>
  /// The engine is started again with a new task set (see <see cref="StartAsync"/>), so the failed tasks are not reset
  /// but the current set is ended (see <see cref="PrepareRestartAsync"/>).
  /// </remarks>
  public Task ResetFailedTasksAsync() => PrepareRestartAsync(t => t.State >= EngineTaskState.Failed);

  /// <summary>
  /// Set while a restart is prepared and the next task set has not been built yet (see <see cref="StartAsync"/>);
  /// completed when it has been built, or when the engine stops.
  /// </summary>
  private TaskCompletionSource _restart;

  private bool RestartPending => Volatile.Read(ref _restart) != null;

  /// <summary>
  /// Prepares the engine to be started again (as the kick-start's retry does), if a task matches
  /// <paramref name="needsRestart"/>: ends every task of the current set that has not completed, so nothing runs on and
  /// whoever waits on them is released, and sets the engine to Created.
  /// </summary>
  /// <remarks>
  /// Until <see cref="StartAsync"/> has built the next set, nothing starts the current one again (it would run next to
  /// its successor, and its waiters would wait for a set that is thrown away), and whoever waits until the engine is
  /// ready waits for the next set. The engine's state is not recomputed from the ending tasks meanwhile.
  /// </remarks>
  private async Task PrepareRestartAsync(Func<EngineTask, bool> needsRestart)
  {
    var tasks = _tasks.Values.ToArray();
    // An engine whose set-up failed has no tasks, but must be started again all the same.
    // Stopping is final: a stopped engine is not prepared for a restart.
    if (IsCancellationRequested || (!SetUpFailed && !tasks.Any(needsRestart)))
      return;
    ArmRestart();
    LogEndingTasksBeforeRestart();
    await Task.WhenAll(tasks.Select(t => t.EndAsync())).ConfigureAwait(false);
    SetUnlessStopped(EngineState.Created);
    // Cancelled meanwhile other than by StopAsync (a parent token, CancelAll): see ReleaseRestartIfCancelled. Checked
    // after the state decision, which it then redoes, so a cancel between the two is not missed.
    ReleaseRestartIfCancelled();
  }

  /// <summary>
  /// Marks a restart as prepared (see <see cref="RestartPending"/>). A stop that came first, and so found no restart to
  /// release, releases it here: whoever waits for the restart must not wait for one that a stopped engine never makes.
  /// </summary>
  private void ArmRestart()
  {
    Interlocked.CompareExchange(ref _restart, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);
    ReleaseRestartIfCancelled();
  }

  /// <summary>
  /// Releases the prepared restart if the engine is cancelled: stopping is final, so a cancelled engine has no restart
  /// to wait for, also when the cancel came from its parent token or <see cref="CancelAll"/> and not from
  /// <see cref="StopAsync"/>.
  /// </summary>
  private void ReleaseRestartIfCancelled()
  {
    if (!IsCancellationRequested)
      return;
    CompleteRestart();
    // Released first: the state is not recomputed while a restart is pending, and a cancel that came after the caller
    // decided the state would otherwise leave it as it was, since the tasks have ended and nothing recomputes it later.
    RecomputeStateSynchronized();
  }

  /// <summary>
  /// Sets the engine to <paramref name="state"/>, or to Stopped if it has been stopped meanwhile (the first rule of
  /// <see cref="ComputeState"/>). Decided under the state gate, so a concurrent stop's Stopped is not overwritten.
  /// </summary>
  private void SetUnlessStopped(EngineState state) =>
    SetStateSynchronized(() => IsCancellationRequested ? EngineState.Stopped : state);

  /// <summary>
  /// Waits until a restart that is prepared has built the next task set (see <see cref="PrepareRestartAsync"/>).
  /// </summary>
  private Task WaitForRestartAsync() => Volatile.Read(ref _restart)?.Task ?? Task.CompletedTask;

  /// <summary>
  /// Ends a prepared restart: the next task set has been built, or the engine stopped. Releases whoever waits for it.
  /// </summary>
  private void CompleteRestart() => Interlocked.Exchange(ref _restart, null)?.TrySetResult();

  /// <summary>
  /// Gives up a prepared restart that will not be carried out (see <see cref="PrepareRestartAsync"/>): the engine is
  /// Faulted, and whoever waits for the restart is released and finds the ended task set.
  /// </summary>
  /// <remarks>
  /// For the one that prepared the restart and then cannot start the engine again, such as a kick-start whose context
  /// task failed after it was reset. Without this, every wait until the engine is ready waited until the engine stopped.
  /// A cancelled engine is Stopped instead, without a fault cause, since stopping is final.
  /// </remarks>
  /// <param name="reason">Why the engine is not started again, for logging.</param>
  public void AbandonRestart(string reason)
  {
    if (!RestartPending)
      return;
    LogRestartAbandoned(reason);
    // Stopping is final: a cancelled engine is Stopped, not Faulted, and has no fault cause. Decided under the state
    // gate, so a cancel that comes meanwhile is not overwritten.
    SetStateSynchronized(() => IsCancellationRequested ? EngineState.Stopped : EngineState.Faulted,
      new InvalidOperationException($"Engine '{EngineName}' is not started again: {reason}"));
    CompleteRestart();
    // A cancel that came after the gate decided Faulted: stopping is final, so the engine is Stopped.
    ReleaseRestartIfCancelled();
  }

  /// <summary>
  /// Cancels all engine activity.
  /// </summary>
  public void CancelAll()
  {
    LogCancellingAllTasks();
    CancelSource();
  }

  /// <summary>
  /// Creates an Engine tasks with the specified parameters.
  /// It is stored as a task that this engine maintains.
  /// </summary>
  /// <param name="name">Name the task is identified by.</param>
  /// <param name="workAsync">
  /// Worker that defines the execution of the task. Its handling of its token's cancellation must not block (see
  /// <see cref="EngineTask(string, Func{CancellationToken, Task}, bool, ILogger, CancellationToken, TimeProvider)"/>).
  /// </param>
  /// <param name="requiredForRunning">True (default) if this task must be completed before the engine is considered ready.
  /// False for other tasks, e.g. a background or monitoring task.</param>
  /// <returns>The task.</returns>
  /// <exception cref="ArgumentException">The engine already has a task named <paramref name="name"/> (ignoring case);
  /// see <see cref="AddTask"/>.</exception>
  protected EngineTask CreateTask(string name, Func<CancellationToken, Task> workAsync,
      bool requiredForRunning = true)
  {
    CheckNameFree(name);
    var task = new EngineTask(name, workAsync, requiredForRunning, Logger, _token, TimeProvider);
    return AddTask(name, task);
  }

  /// <summary>
  /// Creates an Engine tasks with the specified parameters.
  /// It is stored as a task that this engine maintains.
  /// </summary>
  /// <param name="name">Name the task is identified by.</param>
  /// <param name="workAsync">
  /// Worker that defines the execution of the task. Its handling of its token's cancellation must not block (see
  /// <see cref="EngineTask(string, Func{CancellationToken, Task}, bool, ILogger, CancellationToken, TimeProvider)"/>).
  /// </param>
  /// <param name="requiredForRunning">True (default) if this task must be completed before the engine is considered ready.
  /// False for other tasks, e.g. a background or monitoring task.</param>
  /// <returns>The task.</returns>
  /// <exception cref="ArgumentException">The engine already has a task named <paramref name="name"/> (ignoring case);
  /// see <see cref="AddTask"/>.</exception>
  protected EngineTask CreateTask(string name, Action<CancellationToken> workAsync, bool requiredForRunning = true)
  {
    CheckNameFree(name);
    var task = new EngineTask(name, workAsync, requiredForRunning, Logger, _token, TimeProvider);
    return AddTask(name, task);
  }

  /// <summary>
  /// Throws if the engine already has a task named <paramref name="name"/> (ignoring case): in the set being built during
  /// a set-up, or in the current set after it. Called under <see cref="_taskSetChange"/>.
  /// </summary>
  private void ThrowIfNameInUse(string name)
  {
    if ((_buildingTaskSet ?? _taskSet).TryGetValue(name, out var existing))
      throw new ArgumentException($"Engine '{EngineName}' already has a task named '{existing.Name}', so '{name}' cannot be added (task names ignore case).", nameof(name));
  }

  /// <summary>
  /// Refuses a name already in use before a task is built for it, so a refused task holds no token registration.
  /// <see cref="AddTask"/> checks again, as the one that counts.
  /// </summary>
  private void CheckNameFree(string name)
  {
    lock (_taskSetChange)
    {
      ThrowIfNameInUse(name);
    }
  }

  /// <summary>
  /// Adds a task that this engine maintains.
  /// </summary>
  /// <param name="name">Name the task is identified by; names ignore case.</param>
  /// <param name="task">The task.</param>
  /// <returns>The task.</returns>
  /// <exception cref="ArgumentException">The engine already has a task named <paramref name="name"/> (ignoring case),
  /// in the set being built during a set-up, or in the current set after it. The set is left unchanged: putting the
  /// task in place of the existing one would leave that one wired as a dependency but never started.</exception>
  protected EngineTask AddTask(string name, EngineTask task)
  {
    lock (_taskSetChange)
    {
      ThrowIfNameInUse(name);
      if (_buildingTaskSet != null)
      {
        _buildingTaskSet.Add(name, task);
      }
      else
      {
        // Outside a set-up the published set is not changed: copy it.
        Volatile.Write(ref _taskSet, NewTaskSet(_taskSet, name, task));
      }
    }
    if (task.RequiredForRunning)
    {
      // Set call back to be informed about the progress. These callbacks fire concurrently from the
      // thread-pool threads the task work runs on; the synchronized recompute prevents a stale state
      // from overwriting a fresher one (see _stateGate). For a failed/cancelled task ComputeState
      // yields the same Faulted/Stopped result the previous direct assignment did.
      task.CompletedStateReached += _ => RecomputeStateSynchronized();
      task.FailedStateReached += _ => RecomputeStateSynchronized();
    }
    return task;
  }

  /// <summary>
  /// Complete list of tasks that this tasks depends on,
  /// directly or indirectly.
  /// </summary>
  /// <param name="task">The task.</param>
  public IReadOnlyCollection<EngineTask> GetAllDependents(EngineTask task)
  {
    var visited = new HashSet<EngineTask>();
    AddAllDependents([task], visited, DirectDependents(_tasks.Values));
    return visited;
  }

  /// <summary>
  /// Complete list of tasks that this tasks depends on,
  /// directly or indirectly, including this task.
  /// </summary>
  /// <param name="task">The task.</param>
  public IReadOnlyCollection<EngineTask> GetThisAndAllDependents(EngineTask task)
  {
    var visited = new HashSet<EngineTask>
    {
      task
    };
    AddAllDependents([task], visited, DirectDependents(_tasks.Values));
    return visited;
  }

  /// <summary>
  /// Collection of tasks, in order as defined by the engine descriptor. 
  /// </summary>
  /// <param name="task">Task's who's dependents (tasks that are depedent on) we want to retrieve.</param>
  /// <returns></returns>
  protected IEnumerable<EngineTask> GetDependents(EngineTask task) => _tasks.Values
      .Where(t => t.DependsOn.Contains(task));

  /// <summary>
  /// Collection of tasks, ordered on name. 
  /// </summary>
  /// <param name="task">Task's who's dependents (tasks that are depedent on) we want to retrieve.</param>
  /// <returns></returns>
  protected IEnumerable<EngineTask> GetDependentsOrdered(EngineTask task) => GetDependents(task)
      .OrderBy(t => t.Name);

  // ---------------------------------------------------------------------
  // Reporting helpers (diagnostics only)
  // ---------------------------------------------------------------------

  /// <summary>
  /// Returns a textual summary of engine state and tasks.
  /// </summary>
  public override string ToString() =>
      $"{EngineName}, State: {State}"; //"Running: {IsRunning}; Cancellation requested: {IsCancellationRequested}; Task cancelled: {IsTaskCancelled}; IsCompleted: {IsCompleted}; Successful: {IsCompletedWithSuccess}; Failed: {IsFailed}.";

  /// <summary>
  /// Produces a human-readable report of all tasks and their states.
  /// </summary>
  public virtual string ReportTasks()
  {
    var sb = new StringBuilder();
    sb.AppendLine(ToString());
    foreach (var t in _tasks.Values
                 .OrderBy(t => t.RunStartedAtUtc ?? DateTime.MaxValue)
                 .ThenBy(t => t.Name))
    {
      sb.Append("   ");
      sb.AppendLine(t.ToString());
    }

    return sb.ToString();
  }

  /// <summary>
  /// Produces a textual dependency tree of all tasks.
  /// </summary>
  public virtual string ReportDependencies()
  {
    var sb = new StringBuilder();
    sb.AppendLine(ToString());
    // Find roots = tasks with no dependencies
    var roots = _tasks.Values
        .Where(t => !t.DependsOn.Any())
        .OrderBy(t => t.Name)
        .ToList();
    // If no task is a root, everything depends on something → treat all as roots
    if (roots.Count == 0)
    {
      roots = [.. _tasks.Values.OrderBy(t => t.Name)];
    }
    // Tracks tasks whose subtrees were already printed
    var expanded = new HashSet<EngineTask>();
    foreach (var root in roots)
    {
      WriteDependencyTree(root, sb, expanded);
    }

    return sb.ToString();
  }

  private void WriteDependencyTree(
      EngineTask task,
      StringBuilder sb,
      HashSet<EngineTask> expanded,
      string indent = null)
  {
    // First time printing this task
    if (!expanded.Contains(task))
    {
      sb.AppendLine($"{indent}{task.Name} [{task.State}]");
      expanded.Add(task);
      var childIndent = string.IsNullOrEmpty(indent) ? " -> " : "   " + indent;
      foreach (var dep in GetDependentsOrdered(task))
      {
        WriteDependencyTree(dep, sb, expanded, childIndent);
      }
    }
    else
    {
      // Already expanded above -> print pointer
      sb.AppendLine($"{indent}{task.Name} -> ...");
    }
  }

  // ---------------------------------------------------------------------
  // Logging methods
  // ---------------------------------------------------------------------

  [LoggerMessage(Level = LogLevel.Debug, Message = "Setting up tasks and dependencies...")]
  private partial void LogSettingUpTasks();

  [LoggerMessage(Level = LogLevel.Error, Message = "Setting up tasks and dependencies failed.")]
  private partial void LogSetUpFailed(Exception exception);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Firing start triggers. Now wait for all tasks to reach a ready state...")]
  private partial void LogFiringStartTriggers();

  [LoggerMessage(Level = LogLevel.Debug, Message = "Firing reset triggers. Now wait for all tasks to reach a ready state...")]
  private partial void LogFiringResetTriggers();

  [LoggerMessage(Level = LogLevel.Error, Message = "Starting the tasks after a failed reset failed too; the reset's failure is rethrown.")]
  private partial void LogStartAfterFailedResetFailed(Exception exception);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting on ensuring ready of all states that are required for running.")]
  private partial void LogWaitingForRequiredTasks();

  [LoggerMessage(Level = LogLevel.Warning, Message = "Task '{TaskName}' not found in this engine.")]
  private partial void LogTaskNotFound(string taskName);

  [LoggerMessage(Level = LogLevel.Warning, Message = "None of the task(s) '{TaskNames}' found in this engine.")]
  private partial void LogNoTasksFound(string taskNames);

  [LoggerMessage(Level = LogLevel.Trace, Message = "Some of the task(s) '{TaskNames}' not found in this engine.")]
  private partial void LogSomeTasksNotFound(string taskNames);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Ensuring readiness of task(s) '{TaskNames}', based on {Length} inner tasks.")]
  private partial void LogEnsuringTasksReady(string taskNames, int length);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting on ensuring ready of all states for task {Task}.")]
  private partial void LogWaitingForTask(EngineTask task);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Ending the current tasks before the engine starts again...")]
  private partial void LogEndingTasksBeforeRestart();

  [LoggerMessage(Level = LogLevel.Warning, Message = "The engine is not started again: {Reason}")]
  private partial void LogRestartAbandoned(string reason);

  [LoggerMessage(Level = LogLevel.Error, Message = "A subscriber to EngineStatusChanged threw on the change to {State}; the other subscribers were still called.")]
  private partial void LogStatusSubscriberFailed(Exception exception, EngineState state);

  [LoggerMessage(Level = LogLevel.Error, Message = "OnEngineStatusChanged threw on the change to {State}; the change stands and later changes are still published.")]
  private partial void LogStatusOverrideFailed(Exception exception, EngineState state);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Cancelling all tasks via global token.")]
  private partial void LogCancellingAllTasks();
}
