using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Mechanic;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the error paths of the dispatcher that runs the coordinator's operations one at a time: a call
/// that throws, a caller that gives up while its call is queued, and calls that meet a shutdown.
/// </summary>
public class MechanicDispatcherTest
{
  /// <summary>
  /// Verifies that a call that throws faults only its own caller, and that the dispatcher goes on with the next call.
  /// </summary>
  [Fact]
  public async Task InvokeAsync_ActionThrows_FaultsOnlyThatCall()
  {
    var ct = TestContext.Current.CancellationToken;
    using var engineCts = new CancellationTokenSource();
    using var dispatcher = new MechanicDispatcher("Test", NullLogger.Instance, engineCts.Token);

    try
    {
      var failing = dispatcher.InvokeAsync(() => Task.FromException(new InvalidOperationException("Broken.")), ct);
      var next = dispatcher.InvokeAsync(() => Task.FromResult(42), ct);

      await failing.Awaiting(t => t.WaitAsync(MustComplete, ct)).Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("Broken.");
      (await next.WaitAsync(MustComplete, ct)).Should().Be(42);
    }
    finally
    {
      engineCts.Cancel();
    }
  }

  /// <summary>
  /// Verifies that a call whose caller cancels while it waits behind another call is cancelled with the caller's
  /// token without running.
  /// </summary>
  [Fact]
  public async Task InvokeAsync_CallerCancelsWhileQueued_CancelsWithoutRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    using var engineCts = new CancellationTokenSource();
    using var dispatcher = new MechanicDispatcher("Test", NullLogger.Instance, engineCts.Token);
    var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var callerCts = new CancellationTokenSource();
    var ran = false;

    try
    {
      var blocking = dispatcher.InvokeAsync(() => blocker.Task, ct);
      var queued = dispatcher.InvokeAsync(() => { ran = true; return Task.CompletedTask; }, callerCts.Token);

      callerCts.Cancel();
      blocker.SetResult();
      await blocking.WaitAsync(MustComplete, ct);

      var cancelled = await queued.Awaiting(t => t.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>();
      cancelled.Which.CancellationToken.Should().Be(callerCts.Token);
      ran.Should().BeFalse();
    }
    finally
    {
      blocker.TrySetResult();
      engineCts.Cancel();
    }
  }

  /// <summary>
  /// Verifies that calls still queued when the engine token is cancelled are cancelled with that token, and that a
  /// call made after that is cancelled at once instead of waiting for a dispatcher that no longer runs.
  /// </summary>
  [Fact]
  public async Task InvokeAsync_EngineTokenCancelled_CancelsQueuedAndLaterCalls()
  {
    var ct = TestContext.Current.CancellationToken;
    using var engineCts = new CancellationTokenSource();
    using var dispatcher = new MechanicDispatcher("Test", NullLogger.Instance, engineCts.Token);
    var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    try
    {
      var blocking = dispatcher.InvokeAsync(() => { running.SetResult(); return blocker.Task; }, ct);
      var queued = dispatcher.InvokeAsync(() => Task.CompletedTask, ct);
      // Cancel only once the first call runs: a call the worker has not picked up yet would be cancelled as well.
      await running.Task.WaitAsync(MustComplete, ct);

      engineCts.Cancel();
      blocker.SetResult();
      await blocking.WaitAsync(MustComplete, ct);

      var cancelled = await queued.Awaiting(t => t.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>();
      cancelled.Which.CancellationToken.Should().Be(engineCts.Token);

      var later = dispatcher.InvokeAsync(() => Task.CompletedTask, ct);
      await later.Awaiting(t => t.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>();
    }
    finally
    {
      blocker.TrySetResult();
    }
  }

  /// <summary>
  /// Verifies that disposing the dispatcher while the engine token is not cancelled (a coordinator disposed without a
  /// shutdown) ends its thread cleanly: a call queued before the dispose still runs, as it did when the worker drained
  /// its queue before each wait, nothing is logged as an error, and a call after the dispose is refused.
  /// </summary>
  [Fact]
  public async Task Dispose_EngineTokenNotCancelled_EndsThreadWithoutError()
  {
    var ct = TestContext.Current.CancellationToken;
    var errors = 0;
    var logger = OnMessageLogger.OnError(() => Interlocked.Increment(ref errors));
    var dispatcher = new MechanicDispatcher("Test", logger, CancellationToken.None);
    var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);

    try
    {
      var blocking = dispatcher.InvokeAsync(() => { running.SetResult(Thread.CurrentThread); return blocker.Task; },
        ct);
      var queued = dispatcher.InvokeAsync(() => Task.FromResult(42), ct);
      var thread = await running.Task.WaitAsync(MustComplete, ct);

      dispatcher.Dispose();
      blocker.SetResult();

      await blocking.WaitAsync(MustComplete, ct);
      (await queued.WaitAsync(MustComplete, ct)).Should().Be(42);
      thread.Join(MustComplete).Should().BeTrue("the dispatcher's thread ends once it is disposed");
      Volatile.Read(ref errors).Should().Be(0, "a disposed dispatcher is not a crashed one");
      Action invokeAfterDispose = () => dispatcher.InvokeAsync(() => Task.CompletedTask, ct);
      invokeAfterDispose.Should().Throw<ObjectDisposedException>();
    }
    finally
    {
      blocker.TrySetResult();
    }
  }

  /// <summary>
  /// A coordinator with a dispatcher that tells which thread its dispatcher runs on.
  /// </summary>
  private sealed class ThreadRevealingCoordinator(ILogger logger)
    : EngineCoordinator(nameof(MechanicDispatcherTest), logger, useDispatcher: true)
  {
    public Task<Thread> DispatcherThreadAsync(CancellationToken token) =>
      DispatchAsync(() => Task.FromResult(Thread.CurrentThread), token);
  }

  /// <summary>
  /// Verifies that a coordinator with a dispatcher, disposed without a shutdown, ends the dispatcher's thread without
  /// the dispatcher logging an error, although the coordinator disposes the engine token's source first.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_DisposeWithoutShutdown_EndsDispatcherThreadWithoutError()
  {
    var ct = TestContext.Current.CancellationToken;
    var errors = 0;
    var logger = new OnMessageLogger(nameof(MechanicDispatcher), () => Interlocked.Increment(ref errors));
    var coordinator = new ThreadRevealingCoordinator(logger);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(new GraphEngine(("Load", []))));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var dispatcherThread = await coordinator.DispatcherThreadAsync(ct).WaitAsync(MustComplete, ct);

    coordinator.Dispose();

    dispatcherThread.Join(MustComplete).Should()
      .BeTrue("the dispatcher's thread ends once the coordinator is disposed");
    Volatile.Read(ref errors).Should().Be(0, "a disposed coordinator's dispatcher is not a crashed one");
  }

  /// <summary>
  /// Verifies that a null action is rejected.
  /// </summary>
  [Fact]
  public void InvokeAsync_NullAction_Throws()
  {
    using var dispatcher = new MechanicDispatcher("Test", NullLogger.Instance, CancellationToken.None);

    Action invoke = () => dispatcher.InvokeAsync<int>(null);

    invoke.Should().Throw<ArgumentNullException>();
  }
}
