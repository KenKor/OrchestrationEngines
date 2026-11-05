using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OrchestrationEngines.Mechanic;

/// <summary>
/// Runs all mechanic operations on a dedicated single thread.
/// </summary>
internal sealed partial class MechanicDispatcher : IDisposable
{
  private readonly ConcurrentQueue<IWorkItem> _queue = new();
  private readonly AutoResetEvent _signal = new AutoResetEvent(false);
  private readonly CancellationToken _engineToken;
  private readonly ILogger _logger;
  private readonly string _threadName;

  private Thread _thread;
  private int _started;

  /// <summary>
  /// Executes mechanic-related work items sequentially on a dedicated background thread.
  /// </summary>
  /// <param name="threadName">The thread name.</param>
  /// <param name="logger">The logger.</param>
  /// <param name="engineToken">The engine token.</param>
  /// <remarks>
  /// <para>
  /// The <see cref="MechanicDispatcher"/> provides strong thread-affinity guarantees for all mechanic
  /// operations. All dispatched work is executed on a single, long-lived background thread, ensuring
  /// that mechanic state transitions are serialized and free from cross-thread interference.
  /// </para>
  /// <para>
  /// The dispatcher is intentionally simple: it is not a general-purpose scheduler but a controlled
  /// execution environment for engine and mechanic coordination logic.
  /// </para>
  /// </remarks>
  public MechanicDispatcher(string threadName, ILogger logger, CancellationToken engineToken)
  {
    _threadName = string.IsNullOrWhiteSpace(threadName) ? "EngineMechanicThread" : threadName;
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _engineToken = engineToken;
  }

  /// <summary>
  /// Starts the dispatcher thread if it has not already been started.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method is idempotent: multiple calls are safe and will only start a single worker thread.
  /// </para>
  /// <para>
  /// The worker thread processes queued <see cref="IWorkItem"/> instances sequentially on a dedicated
  /// background thread. This ensures that mechanic operations execute on a single consistent thread.
  /// </para>
  /// </remarks>
  public void Start()
  {
    if (Interlocked.Exchange(ref _started, 1) == 1)
      return;

    _thread = new Thread(WorkerLoop)
    {
      IsBackground = true,
      Name = _threadName
    };
    _thread.Start();
  }

  /// <summary>
  /// Starts the dispatcher thread if it has not already been started.
  /// </summary>
  /// <param name="action">The action.</param>
  /// <param name="token">The token.</param>
  /// <remarks>
  /// <para>
  /// This method is idempotent: multiple calls are safe and will only start a single worker thread.
  /// </para>
  /// <para>
  /// The worker thread processes queued <see cref="IWorkItem"/> instances sequentially on a dedicated
  /// background thread. This ensures that mechanic operations execute on a single consistent thread.
  /// </para>
  /// </remarks>
  public Task InvokeAsync(Func<Task> action, CancellationToken token = default)
      => InvokeAsync(async () => { await action().ConfigureAwait(false); return true; }, token);

  /// <summary>
  /// Enqueues an asynchronous function for execution on the dispatcher thread.
  /// </summary>
  /// <typeparam name="T">The result type produced by the dispatched function.</typeparam>
  /// <param name="action">The function to execute on the dispatcher thread.</param>
  /// <param name="token">
  /// A cancellation token associated with the caller. If cancelled before execution begins,
  /// the work item is cancelled without executing <paramref name="action"/>.
  /// </param>
  /// <returns>
  /// A task that completes with the function's result once it has executed on the dispatcher thread.
  /// </returns>
  /// <remarks>
  /// <para>
  /// This method ensures the dispatcher thread is started and then queues the work item.
  /// </para>
  /// <para>
  /// The queued work is executed synchronously on the dispatcher thread via
  /// <c>GetAwaiter().GetResult()</c>. This is intentional because the dispatcher thread is dedicated
  /// to processing this queue and should not return to the queue loop until the work is completed.
  /// </para>
  /// </remarks>
  public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken token = default)
  {
    ArgumentNullException.ThrowIfNull(action);

    // Fix (MechanicDispatcherTest): the worker loop ends once the engine token is cancelled; an item queued after that
    // never ran nor was cancelled, and its caller waited forever.
    if (_engineToken.IsCancellationRequested)
      return Task.FromCanceled<T>(_engineToken);

    Start();

    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    _queue.Enqueue(new WorkItem<T>(action, tcs, token));
    _signal.Set();

    // Cancelled while this item was being queued: the worker loop may already have drained the queue for the last time.
    if (_engineToken.IsCancellationRequested)
      CancelQueuedItems();

    return tcs.Task;
  }

  /// <summary>
  /// Cancels every queued item: with the engine token once that is cancelled, otherwise with the item's caller token.
  /// </summary>
  private void CancelQueuedItems()
  {
    while (_queue.TryDequeue(out var item))
      item.Cancel(_engineToken.IsCancellationRequested ? _engineToken : item.CallerToken);
  }

  /// <summary>
  /// Main dispatcher loop running on the dedicated worker thread.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The loop wakes periodically or when signaled, drains the queue, and executes each
  /// <see cref="IWorkItem"/> sequentially.
  /// </para>
  /// <para>
  /// Cancellation behavior:
  /// <list type="bullet">
  /// <item>If the engine token is cancelled, queued items are cancelled with the engine token.</item>
  /// <item>If the caller token is cancelled before execution, that item is cancelled with the caller token.</item>
  /// </list>
  /// </para>
  /// <para>
  /// Any unhandled exception thrown during item execution is logged and propagated to the item’s
  /// <see cref="TaskCompletionSource{TResult}"/> via <see cref="IWorkItem.Fail(Exception)"/>.
  /// </para>
  /// </remarks>
  private void WorkerLoop()
  {
    try
    {
      while (!_engineToken.IsCancellationRequested)
      {
        _signal.WaitOne(200);

        while (_queue.TryDequeue(out var item))
        {
          if (_engineToken.IsCancellationRequested)
          {
            item.Cancel(_engineToken);
            continue;
          }

          if (item.CallerToken.IsCancellationRequested)
          {
            item.Cancel(item.CallerToken);
            continue;
          }

          try
          {
            item.Execute();
          }
          catch (Exception ex)
          {
            LogWorkItemFailed(ex);
            item.Fail(ex);
          }
        }
      }
    }
    catch (Exception ex)
    {
      LogDispatcherThreadTerminated(ex);
    }
    finally
    {
      CancelQueuedItems();
    }
  }

  /// <summary>
  /// Signals and disposes dispatcher synchronization resources.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This does not forcibly stop the worker thread; the worker loop exits when the engine token
  /// is cancelled. Disposing the signal unblocks any pending wait.
  /// </para>
  /// <para>
  /// Disposal is best-effort to avoid throwing during shutdown paths.
  /// </para>
  /// </remarks>
  public void Dispose()
  {
    try { _signal.Set(); } catch { /* ignore */ }
    try { _signal.Dispose(); } catch { /* ignore */ }
  }

  /// <summary>
  /// Represents a unit of work executed by the <see cref="MechanicDispatcher"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A work item encapsulates both execution logic and completion signaling. It is responsible
  /// for synchronously executing its workload on the dispatcher thread and completing its
  /// associated <see cref="TaskCompletionSource{TResult}"/> accordingly.
  /// </para>
  /// <para>
  /// Work items must be resilient to cancellation and failure; the dispatcher guarantees
  /// that exactly one of <see cref="Execute"/>, <see cref="Cancel"/>, or <see cref="Fail"/> is
  /// invoked for each item.
  /// </para>
  /// </remarks>
  private interface IWorkItem
  {
    /// <summary>
    /// Gets the cancellation token provided by the caller that enqueued this work item.
    /// </summary>
    /// <remarks>
    /// This token is evaluated before execution begins. If it is already cancelled,
    /// the work item is cancelled without executing.
    /// </remarks>
    CancellationToken CallerToken { get; }

    /// <summary>
    /// Executes the work item synchronously on the dispatcher thread.
    /// </summary>
    /// <remarks>
    /// Implementations are expected to block until the underlying asynchronous
    /// operation has completed (typically via <c>GetAwaiter().GetResult()</c>).
    /// </remarks>
    void Execute();

    /// <summary>
    /// Cancels the work item without executing it.
    /// </summary>
    /// <param name="token">
    /// The cancellation token responsible for the cancellation (caller or engine).
    /// </param>
    void Cancel(CancellationToken token);

    /// <summary>
    /// Marks the work item as failed due to an unhandled exception.
    /// </summary>
    /// <param name="ex">The exception that caused the failure.</param>
    void Fail(Exception ex);
  }

  /// <summary>
  /// Generic implementation of <see cref="IWorkItem"/> that executes an asynchronous function
  /// and completes a <see cref="TaskCompletionSource{TResult}"/> with its result.
  /// </summary>
  /// <typeparam name="T">The result type produced by the work.</typeparam>
  /// <remarks>
  /// <para>
  /// The wrapped asynchronous function is executed synchronously on the dispatcher thread
  /// using <c>GetAwaiter().GetResult()</c>. This preserves single-threaded execution semantics
  /// while allowing callers to use asynchronous APIs.
  /// </para>
  /// <para>
  /// Completion is guaranteed to be terminal: exactly one of success, cancellation, or failure
  /// is reported to the associated task.
  /// </para>
  /// </remarks>
  private sealed class WorkItem<T> : IWorkItem
  {
    private readonly Func<Task<T>> _work;
    private readonly TaskCompletionSource<T> _tcs;

    /// <inheritdoc />
    public CancellationToken CallerToken { get; }

    /// <summary>
    /// Initializes a new <see cref="WorkItem{T}"/>.
    /// </summary>
    /// <param name="work">The asynchronous function to execute on the dispatcher thread.</param>
    /// <param name="tcs">
    /// The task completion source that will be completed when execution finishes,
    /// fails, or is cancelled.
    /// </param>
    /// <param name="callerToken">
    /// Cancellation token supplied by the caller when the work item was enqueued.
    /// </param>
    public WorkItem(
        Func<Task<T>> work,
        TaskCompletionSource<T> tcs,
        CancellationToken callerToken)
    {
      _work = work;
      _tcs = tcs;
      CallerToken = callerToken;
    }

    /// <summary>
    /// Executes the wrapped asynchronous function and completes the task with its result.
    /// </summary>
    /// <remarks>
    /// This method is called exclusively on the dispatcher thread.
    /// </remarks>
    public void Execute()
    {
      var result = _work().GetAwaiter().GetResult();
      _tcs.TrySetResult(result);
    }

    /// <summary>
    /// Cancels the associated task.
    /// </summary>
    /// <param name="token">The token that caused cancellation.</param>
    public void Cancel(CancellationToken token)
        => _tcs.TrySetCanceled(token);

    /// <summary>
    /// Completes the associated task with an exception.
    /// </summary>
    /// <param name="ex">The exception that occurred during execution.</param>
    public void Fail(Exception ex)
        => _tcs.TrySetException(ex);
  }

  // ---------------------------------------------------------------------
  // Logging methods
  // ---------------------------------------------------------------------

  [LoggerMessage(Level = LogLevel.Error, Message = "MechanicDispatcher: unhandled exception in dispatched work item.")]
  private partial void LogWorkItemFailed(Exception exception);

  [LoggerMessage(Level = LogLevel.Error, Message = "MechanicDispatcher: dispatcher thread terminated unexpectedly.")]
  private partial void LogDispatcherThreadTerminated(Exception exception);
}
