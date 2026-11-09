using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
  /// Indicates whether cancellation has been requested for this engine.
  /// </summary>
  public bool IsCancellationRequested => _linkedCts.IsCancellationRequested;

  /// <summary>
  /// Cancellation token to be observed by all tasks and engine activities.
  /// </summary>
  public CancellationToken CancellationToken => _linkedCts.Token;

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
  /// True from a set-up that failed until the next set is published: no task set was built, and the engine is Faulted
  /// (see <see cref="ComputeState"/>). Written under <see cref="_taskSetChange"/>.
  /// </summary>
  private volatile bool _setUpFailed;

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
  protected virtual void RequestCancellation()
  {
    _linkedCts.Cancel();
  }

  // ---------------------------------------------------------------------
  // Engine state handling
  // ---------------------------------------------------------------------

  /// <summary>
  /// Backing field for <see cref="State"/>.
  /// </summary>
  public EngineState _state = EngineState.Created;

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

  /// <summary>
  /// Event raised when the engine lifecycle state changes.
  /// </summary>
  public event EventHandler<EngineStatusEventArgs> EngineStatusChanged;

  /// <summary>
  /// Hook invoked after a state transition.
  /// </summary>
  /// <param name="oldValue">The old value.</param>
  /// <param name="value">The value.</param>
  protected virtual void OnEngineStatusChanged(EngineState oldValue, EngineState value)
  {
    EngineStatusChanged?.Invoke(this, new EngineStatusEventArgs(EngineName, value, null));
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
  private readonly Queue<(EngineState oldValue, EngineState newValue)> _pendingChanges = new();

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
  private void SetStateSynchronized(Func<EngineState> compute)
  {
    lock (_stateGate)
    {
      var oldValue = _state;
      var newValue = compute();
      _state = newValue;
      if (newValue != oldValue)
        _pendingChanges.Enqueue((oldValue, newValue));
    }

    while (true)
    {
      (EngineState oldValue, EngineState newValue) change;
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
        OnEngineStatusChanged(change.oldValue, change.newValue);
      }
      finally
      {
        lock (_stateGate)
          _publishing = false;
      }
    }
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
    if (_setUpFailed)
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
        // case >= EngineTaskState.Failed: C#10
        default:
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
  public void CheckForCyclicDependecies() => CheckForCyclicDependecies(_tasks.Values);

  private static void CheckForCyclicDependecies(IEnumerable<EngineTask> tasks)
  {
    foreach (var t in tasks)
    {
      t.CheckForCyclicDependecies();
    }
  }

  /// <inheritdoc/>
  public virtual Task StartAsync()
  {
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
    Interlocked.CompareExchange(ref _restart, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);
    await Task.WhenAll(previous.Select(t => t.EndAsync())).ConfigureAwait(false);
    State = EngineState.Created;
    await SetUpAndStartAsync().ConfigureAwait(false);
  }

  /// <summary>
  /// Work outside this engine that the next task set waits for (see <see cref="StartAfter"/>), or null.
  /// </summary>
  private Task _inheritedWork;

  /// <summary>
  /// Makes the tasks of the next task set run only once <paramref name="work"/> has ended, as they wait for the work of
  /// the set they replace: for work outside this engine that builds on the same data, such as that of a previous engine
  /// whose data this engine takes over. Used by the next start only. Adds to the work already waited for (such as the
  /// work a failed set-up handed on) rather than replacing it.
  /// </summary>
  /// <param name="work">The work to wait for.</param>
  /// <remarks>Called before <see cref="StartAsync"/>.</remarks>
  protected void StartAfter(Task work) => InheritWork(work);

  /// <summary>
  /// Adds <paramref name="work"/> to the work the next task set waits for (see <see cref="StartAfter"/>).
  /// </summary>
  private void InheritWork(Task work)
  {
    Task current;
    do
    {
      current = Volatile.Read(ref _inheritedWork);
    }
    while (Interlocked.CompareExchange(ref _inheritedWork, current == null ? work : Task.WhenAll(current, work), current) != current);
  }

  private async Task SetUpAndStartAsync()
  {
    try
    {
      // Create all tasks and dependencies, in a set of their own that replaces the current one at once.
      LogSettingUpTasks();
      // The work of the set that is replaced can still be running: its tasks were ended (or timed out), which cancels
      // their work but cannot stop it. The new set's tasks start once it has ended, since they build on the same data.
      // So does work outside this engine that builds on it (see StartAfter).
      // Only work still running is waited for, and a single piece is passed on as is, so that retries after failed
      // set-ups do not wrap it deeper each time.
      var pending = _tasks.Values.Select(t => t.LatestRunEnded)
        .Append(Interlocked.Exchange(ref _inheritedWork, null))
        .Where(work => work is { IsCompleted: false })
        .ToList();
      var replacedWork = pending.Count switch
      {
        0 => Task.CompletedTask,
        1 => pending[0],
        _ => Task.WhenAll(pending),
      };
      lock (_taskSetChange)
      {
        _buildingTaskSet = NewTaskSet();
      }
      bool afterFailedSetUp;
      try
      {
        SetUpTasks();
        lock (_taskSetChange)
        {
          // Before the set is published: the tasks of a cyclic set would wait for each other for good, and so would
          // everyone waiting for them.
          CheckForCyclicDependecies(_buildingTaskSet.Values);
          if (!replacedWork.IsCompleted)
          {
            foreach (var task in _buildingTaskSet.Values)
              task.RunAfter(replacedWork);
          }
          Volatile.Write(ref _taskSet, _buildingTaskSet);
          _buildingTaskSet = null;
          afterFailedSetUp = _setUpFailed;
          _setUpFailed = false;
        }
      }
      catch (Exception ex)
      {
        // The set could not be built: none of it is published, and the set it replaces is gone too (its tasks were
        // ended). The engine is Faulted until a start builds a set; a prepared restart can start it again (see
        // PrepareRestartAsync).
        lock (_taskSetChange)
        {
          _buildingTaskSet = null;
          // The work the next set must wait for is still running: hand it on to that set, before a start can see the
          // failure and build it.
          if (!replacedWork.IsCompleted)
            InheritWork(replacedWork);
          Volatile.Write(ref _taskSet, NewTaskSet());
          _setUpFailed = true;
        }
        // A stop during the set-up (which may be why it threw) leaves the engine Stopped, as ComputeState would say.
        if (IsCancellationRequested)
        {
          State = EngineState.Stopped;
        }
        else
        {
          LogSetUpFailed(ex);
          State = EngineState.Faulted;
        }
        throw;
      }

      // Fire the start trigger for those that we're started yet.
      // Fix (EngineStatusEventTest): publish Starting before the tasks run. The state was otherwise only
      // recomputed when a task ended, so an engine went from Created straight to Ready (or Faulted) and a host
      // watching EngineStatusChanged never saw it start. Publishing it after the triggers (from a recompute) is not
      // enough: a task that ends before the recompute makes it compute Ready at once, and Starting is skipped again.
      // So is a start after a failed set-up, which left the engine Faulted.
      if (State == EngineState.Created || afterFailedSetUp)
        State = EngineState.Starting;
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

  private Task TriggerStartCoreAsync(IEnumerable<EngineTask> tasks = null)
  {
    // Set default task list.
    tasks ??= _tasks.Values;
    // Fire Start on all tasks:
    // Tasks without dependencies will go straight to Running.
    // Tasks with dependencies will go to WaitingForDependencies.
    var startTasks = tasks
        .Where(t => !t.IsStarted && t.State != EngineTaskState.Cancelled)
        .Select(t => t.FireAsync(t.State == EngineTaskState.Created
          ? EngineTaskTrigger.Start
          : EngineTaskTrigger.Reset))
        .ToArray();
    LogFiringStartTriggers();
    return Task.WhenAll(startTasks);
  }

  /// <summary>
  /// Fires Reset triggers on the tasks (Start on those that never started) and, if the engine has started, starts the
  /// reset tasks again and recomputes the engine state.
  /// </summary>
  /// <remarks>
  /// The engine state is only recomputed when a task completes or fails, and the reset tasks of an engine that has
  /// started are started by nobody else (its kick-start completed long ago). Without this, a Ready engine would keep
  /// reporting Ready while its tasks are reset, and a lookup that waits for a Starting engine could find tasks still to
  /// start. The tasks are started before the state is recomputed, so such a lookup finds nothing left to start and
  /// fires no triggers of its own. An engine that has not started yet is only reset (its kick-start starts it), and a
  /// stopped engine is not started again.
  /// </remarks>
  /// <param name="tasks">The tasks.</param>
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
    LogFiringResetTriggers();
    await Task.WhenAll(resetTasks.Except(notStarted).Select(t => t.FireAsync(EngineTaskTrigger.Reset))).ConfigureAwait(false);
    await Task.WhenAll(notStarted.Select(t => t.FireAsync(EngineTaskTrigger.Start))).ConfigureAwait(false);
    if (!startAgain)
      return;

    // Start what the reset left in Created: tasks without dependencies go straight to Running, the others to
    // WaitingForDependencies. Then publish the state (Starting while they run).
    // Fix (EngineStatusEventTest): Starting is published before the triggers, see StartAsync; a reset whose
    // tasks ended before the recompute went from Ready to Ready and was never announced. A reset of background tasks
    // only does not take the engine away from the host, so it is not announced.
    if (resetTasks.Any(t => t.RequiredForRunning))
      State = EngineState.Starting;
    await TriggerStartAsync(resetTasks).ConfigureAwait(false);
    RecomputeStateSynchronized();
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
  /// <param name="task"></param>
  /// <param name="token"></param>
  public virtual async Task EnsureReadyAsync(EngineTask task, CancellationToken token = default)
  {
    // After a prepared restart a task of the replaced set has ended; the caller looks the task up again.
    await WaitForRestartAsync().ConfigureAwait(false);
    var allTasks = task.GetThisAndAllDependsOn();
    // We should never await tasks that have not been fire to start yet.
    await TriggerStartAsync(allTasks).ConfigureAwait(false);
    // Wait for all tasks (CompletedWithSuccess / Failed / Cancelled)
    var completionTasks = allTasks
        // var completionTasks = task // We do not need to get its dependencies, since the tasks already have that stored in them.
        .Where(t => !t.IsCompleted)
        .Select(t => t.InnerTask).ToArray();
    LogWaitingForTask(task);
    await Task.WhenAll(completionTasks).ConfigureAwait(false);
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
    if (!_setUpFailed && !tasks.Any(needsRestart))
      return;
    Interlocked.CompareExchange(ref _restart, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);
    LogEndingTasksBeforeRestart();
    await Task.WhenAll(tasks.Select(t => t.EndAsync())).ConfigureAwait(false);
    State = EngineState.Created;
  }

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
  /// </remarks>
  /// <param name="reason">Why the engine is not started again, for logging.</param>
  public void AbandonRestart(string reason)
  {
    if (!RestartPending)
      return;
    LogRestartAbandoned(reason);
    State = EngineState.Faulted;
    CompleteRestart();
  }

  /// <summary>
  /// Cancels all engine activity.
  /// </summary>
  public void CancelAll()
  {
    LogCancellingAllTasks();
    _linkedCts.Cancel();
  }

  /// <summary>
  /// Creates an Engine tasks with the specified parameters.
  /// It is stored as a task that this engine maintains.
  /// </summary>
  /// <param name="name">Name the task is identified by.</param>
  /// <param name="workAsync">Worker that defines the execution of the task.</param>
  /// <param name="requiredForRunning">True (default) if this task must be completed before the engine is considered ready.
  /// False for other tasks, e.g. a background or monitoring task.</param>
  /// <returns>The task.</returns>
  /// <exception cref="ArgumentException">The engine already has a task named <paramref name="name"/> (ignoring case);
  /// see <see cref="AddTask"/>.</exception>
  protected EngineTask CreateTask(string name, Func<CancellationToken, Task> workAsync,
      bool requiredForRunning = true)
  {
    CheckNameFree(name);
    var task = new EngineTask(name, workAsync, requiredForRunning, Logger, _linkedCts.Token, TimeProvider);
    return AddTask(name, task);
  }

  /// <summary>
  /// Creates an Engine tasks with the specified parameters.
  /// It is stored as a task that this engine maintains.
  /// </summary>
  /// <param name="name">Name the task is identified by.</param>
  /// <param name="workAsync">Worker that defines the execution of the task.</param>
  /// <param name="requiredForRunning">True (default) if this task must be completed before the engine is considered ready.
  /// False for other tasks, e.g. a background or monitoring task.</param>
  /// <returns>The task.</returns>
  /// <exception cref="ArgumentException">The engine already has a task named <paramref name="name"/> (ignoring case);
  /// see <see cref="AddTask"/>.</exception>
  protected EngineTask CreateTask(string name, Action<CancellationToken> workAsync, bool requiredForRunning = true)
  {
    CheckNameFree(name);
    var task = new EngineTask(name, workAsync, requiredForRunning, Logger, _linkedCts.Token, TimeProvider);
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
    CollectAllDependents(task, visited);
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
    CollectAllDependents(task, visited);
    return visited;
  }

  // Worker for GetAllDependents(EngineTask root)
  private void CollectAllDependents(EngineTask task, HashSet<EngineTask> visited)
  {
    foreach (var dep in GetDependents(task))
    {
      // Add returns false if already present
      if (!visited.Add(dep)) continue;

      // Recurse only on first encounter
      CollectAllDependents(dep, visited);
    }
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

  [LoggerMessage(Level = LogLevel.Error, Message = "Setting up tasks and dependencies failed; the engine is Faulted.")]
  private partial void LogSetUpFailed(Exception exception);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Firing start triggers. Now wait for all tasks to reach a ready state...")]
  private partial void LogFiringStartTriggers();

  [LoggerMessage(Level = LogLevel.Debug, Message = "Firing reset triggers. Now wait for all tasks to reach a ready state...")]
  private partial void LogFiringResetTriggers();

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

  [LoggerMessage(Level = LogLevel.Debug, Message = "Cancelling all tasks via global token.")]
  private partial void LogCancellingAllTasks();
}
