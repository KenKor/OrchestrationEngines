using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for callbacks that throw: a subscriber to a task's or engine's state events, and a callback
/// registered on a work's cancellation token. The engine and its tasks carry on, and log what was swallowed.
/// </summary>
public class ThrowingCallbackTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Load, then Use, which depends on it. A subscriber that throws is attached to Load before Use subscribes.
  /// </summary>
  private sealed class SubscribedEngine : BaseEngine
  {
    private readonly bool _failLoad;

    public SubscribedEngine(ILogger logger, bool failLoad) : base(logger)
    {
      _failLoad = failLoad;
      Context = new NoContext();
    }

    public EngineTask Load { get; private set; }

    protected override void SetUpTasks()
    {
      Load = CreateTask("Load", _ => _failLoad ? throw new InvalidOperationException("No data.") : Task.CompletedTask);
      Load.CompletedStateReached += _ => throw new InvalidOperationException("Subscriber failed.");
      Load.FailedStateReached += _ => throw new InvalidOperationException("Subscriber failed.");
      CreateTask("Use", _ => Task.CompletedTask).AddDependsOn(Load);
    }
  }

  /// <summary>
  /// Load, then Use, which depends on it, with no subscribers of their own.
  /// </summary>
  private class PlainEngine : BaseEngine
  {
    public PlainEngine(ILogger logger) : base(logger) => Context = new NoContext();

    public EngineTask Load { get; private set; }

    protected override void SetUpTasks()
    {
      Load = CreateTask("Load", _ => Task.CompletedTask);
      CreateTask("Use", _ => Task.CompletedTask).AddDependsOn(Load);
    }
  }

  /// <summary>
  /// A <see cref="PlainEngine"/> whose override of <see cref="BaseEngine.OnEngineStatusChanged"/> throws on Starting,
  /// after it has published the change.
  /// </summary>
  private sealed class ThrowingOverrideEngine(ILogger logger) : PlainEngine(logger)
  {
    protected override void OnEngineStatusChanged(EngineState oldValue, EngineState value)
    {
      base.OnEngineStatusChanged(oldValue, value);
      if (value == EngineState.Starting)
        throw new InvalidOperationException("Override failed.");
    }
  }

  /// <summary>
  /// One task whose work registers a throwing callback on its token, then waits until it is cancelled.
  /// </summary>
  private sealed class HangingEngine(ILogger logger) : BaseEngine(logger)
  {
    public TaskCompletionSource<bool> Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Released { get; private set; }

    protected override void SetUpTasks() => CreateTask("Hang", token => HangAsync(token, Running));

    protected override void ReleaseResources() => Released = true;
  }

  /// <summary>
  /// One task whose work throws an exception with an empty message, with or without a recovery.
  /// </summary>
  private sealed class EmptyMessageEngine(bool withRecovery) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks()
    {
      var task = CreateTask("Load", _ => throw new Exception(""));
      if (withRecovery)
        task.SetRecovery(TestRestartPolicies.NoWait(1));
    }
  }

  /// <summary>
  /// A logger that throws, as a failing logging provider does, on a line logged with an exception whose message is
  /// <paramref name="message"/>.
  /// </summary>
  private static OnMessageLogger LoggerThrowingOn(string message) =>
    new((_, _, exception) => exception?.Message == message,
      () => throw new InvalidOperationException("Logger failed."));

  private static async Task HangAsync(CancellationToken token, TaskCompletionSource<bool> running)
  {
    token.Register(() => throw new InvalidOperationException("Cancel callback failed."));
    running.TrySetResult(true);
    await Task.Delay(Timeout.Infinite, token);
  }

  private static bool Logged(FakeLogger logger, string message) =>
    logger.Collector.GetSnapshot().Any(r => r.Exception?.Message == message);

  /// <summary>
  /// Verifies that a subscriber that throws when Load completes does not stop Use from running, nor Load's latch from
  /// completing, and that the exception is logged.
  /// </summary>
  [Fact]
  public async Task StartAsync_CompletedSubscriberThrows_DependentsRunAndLatchCompletes()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var engine = new SubscribedEngine(logger, failLoad: false);

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    await engine.Load.InnerTask.WaitAsync(MustComplete, ct);
    engine.Tasks.Values.Should().OnlyContain(t => t.State == EngineTaskState.CompletedWithSuccess);
    engine.State.Should().Be(EngineState.Ready);
    Logged(logger, "Subscriber failed.").Should().BeTrue("a swallowed exception is logged");
  }

  /// <summary>
  /// Verifies that a subscriber that throws when Load fails does not stop Use from failing with it, nor Load's latch
  /// from completing.
  /// </summary>
  [Fact]
  public async Task StartAsync_FailedSubscriberThrows_DependentsFailAndLatchCompletes()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var engine = new SubscribedEngine(logger, failLoad: true);

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    await engine.Load.InnerTask.WaitAsync(MustComplete, ct);
    engine.Tasks.Values.Single(t => t.Name == "Use").State.Should().Be(EngineTaskState.DependencyFailed);
    engine.State.Should().Be(EngineState.Faulted);
    Logged(logger, "Subscriber failed.").Should().BeTrue("a swallowed exception is logged");
  }

  /// <summary>
  /// Verifies that an engine status subscriber that throws on every change does not stop the start, and that a
  /// subscriber after it still receives every change, in order, up to Ready; both tasks complete.
  /// </summary>
  [Fact]
  public async Task StartAsync_StatusSubscriberThrows_OthersStillReceiveEveryChange()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var engine = new PlainEngine(logger);
    var seen = new ConcurrentQueue<EngineState>();
    engine.EngineStatusChanged += (_, _) => throw new InvalidOperationException("Status subscriber failed.");
    engine.EngineStatusChanged += (_, e) => seen.Enqueue(e.State);

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    await engine.Load.InnerTask.WaitAsync(MustComplete, ct);
    engine.Tasks.Values.Should().OnlyContain(t => t.State == EngineTaskState.CompletedWithSuccess);
    engine.State.Should().Be(EngineState.Ready);
    seen.Should().Equal([EngineState.Starting, EngineState.Ready], "each change is delivered once, in order");
    Logged(logger, "Status subscriber failed.").Should().BeTrue("a swallowed exception is logged");
  }

  /// <summary>
  /// Verifies that when the logger throws on the exception of an engine status subscriber, a subscriber after it still
  /// receives every change, in order, up to Ready.
  /// </summary>
  [Fact]
  public async Task StartAsync_StatusSubscriberAndLoggerThrow_OthersStillReceiveEveryChange()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new PlainEngine(LoggerThrowingOn("Status subscriber failed."));
    var seen = new ConcurrentQueue<EngineState>();
    engine.EngineStatusChanged += (_, _) => throw new InvalidOperationException("Status subscriber failed.");
    engine.EngineStatusChanged += (_, e) => seen.Enqueue(e.State);

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Ready);
    seen.Should().Equal([EngineState.Starting, EngineState.Ready], "each change is delivered once, in order");
  }

  /// <summary>
  /// Verifies that an override of OnEngineStatusChanged that throws on Starting does not fail the start, and that the
  /// change after it (Ready) is still published.
  /// </summary>
  [Fact]
  public async Task StartAsync_StatusOverrideThrows_LaterChangesStillPublished()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var engine = new ThrowingOverrideEngine(logger);
    var seen = new ConcurrentQueue<EngineState>();
    engine.EngineStatusChanged += (_, e) => seen.Enqueue(e.State);

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Ready);
    seen.Should().Equal([EngineState.Starting, EngineState.Ready], "each change is delivered once, in order");
    var record = logger.Collector.GetSnapshot().Should().ContainSingle(r => r.Exception != null).Which;
    record.Exception.Message.Should().Be("Override failed.");
    record.Level.Should().Be(LogLevel.Error);
    record.Message.Should().Be($"{engine.EngineName} " +
      "OnEngineStatusChanged threw on the change to Starting; the change stands and later changes are still published.");
  }

  /// <summary>
  /// Verifies that when the logger throws on the exception of an override of OnEngineStatusChanged, the start does not
  /// fail and the change after it (Ready) is still published.
  /// </summary>
  [Fact]
  public async Task StartAsync_StatusOverrideAndLoggerThrow_LaterChangesStillPublished()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new ThrowingOverrideEngine(LoggerThrowingOn("Override failed."));
    var seen = new ConcurrentQueue<EngineState>();
    engine.EngineStatusChanged += (_, e) => seen.Enqueue(e.State);

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Ready);
    seen.Should().Equal([EngineState.Starting, EngineState.Ready], "each change is delivered once, in order");
  }

  /// <summary>
  /// Verifies that stopping an engine whose running work has a cancel callback that throws returns without throwing,
  /// leaves the engine Stopped, and logs the exception.
  /// </summary>
  [Fact]
  public async Task StopAsync_CancelCallbackThrows_StopsAndLogs()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var engine = new HangingEngine(logger);
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.Running.Task.WaitAsync(MustComplete, ct);

    await engine.StopAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Stopped);
    engine.Released.Should().BeTrue("the stop goes on to release the engine's resources");
    await engine.Tasks.Values.Single().InnerTask.WaitAsync(MustComplete, ct);
    Logged(logger, "Cancel callback failed.").Should().BeTrue("a swallowed exception is logged");
  }

  /// <summary>
  /// Verifies that stopping an engine whose running work has a cancel callback that throws, with a logger that throws
  /// on that exception, returns without throwing, leaves the engine Stopped and releases its resources.
  /// </summary>
  [Fact]
  public async Task StopAsync_CancelCallbackAndLoggerThrow_Stops()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new HangingEngine(LoggerThrowingOn("Cancel callback failed."));
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.Running.Task.WaitAsync(MustComplete, ct);

    await engine.StopAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Stopped);
    engine.Released.Should().BeTrue("the stop goes on to release the engine's resources");
    await engine.Tasks.Values.Single().InnerTask.WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that shutting down a coordinator whose token has a callback that throws completes, ends the coordinator
  /// Stopped, and logs the exception.
  /// </summary>
  [Fact]
  public async Task ShutdownAsync_TokenCallbackThrows_CompletesAndLogs()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var coordinator = new EngineCoordinator(nameof(ThrowingCallbackTest), logger, useDispatcher: false);
    coordinator.RegisterEngineDescriptor(
      new EngineDescriptor("E", _ => Task.FromResult<IEngine>(new PlainEngine(logger)), requiredForRunning: true, 1)
        .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
        .WithoutRestarts());
    (await coordinator.GetEngineAsync("E", ct).WaitAsync(MustComplete, ct)).Should().NotBeNull();
    coordinator.CancellationToken.Register(() => throw new InvalidOperationException("Token callback failed."));

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    Logged(logger, "Token callback failed.").Should().BeTrue("a swallowed exception is logged");
  }

  /// <summary>
  /// Verifies that cancelling a task whose work has a cancel callback that throws ends the task Cancelled.
  /// </summary>
  [Fact]
  public async Task Cancel_CancelCallbackThrows_EndsCancelled()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = new EngineTask("Hang", token => HangAsync(token, running), requiredForRunning: true, logger,
      CancellationToken.None);

    await task.FireAsync(EngineTaskTrigger.Start);
    await running.Task.WaitAsync(MustComplete, ct);
    await task.FireAsync(EngineTaskTrigger.Cancel);

    await task.InnerTask.WaitAsync(MustComplete, ct);
    task.State.Should().Be(EngineTaskState.Cancelled);
    Logged(logger, "Cancel callback failed.").Should().BeTrue("a swallowed exception is logged");
  }

  /// <summary>
  /// Verifies that ending a task (as a restart does), whose work has a cancel callback that throws, ends it Cancelled.
  /// </summary>
  [Fact]
  public async Task End_CancelCallbackThrows_EndsCancelled()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = new EngineTask("Hang", token => HangAsync(token, running), requiredForRunning: true, logger,
      CancellationToken.None);

    await task.FireAsync(EngineTaskTrigger.Start);
    await running.Task.WaitAsync(MustComplete, ct);
    await task.EndAsync().WaitAsync(MustComplete, ct);

    await task.InnerTask.WaitAsync(MustComplete, ct);
    task.State.Should().Be(EngineTaskState.Cancelled);
    Logged(logger, "Cancel callback failed.").Should().BeTrue("a swallowed exception is logged");
  }

  /// <summary>
  /// Verifies that a task that times out while its work has a cancel callback that throws ends TimedOut.
  /// </summary>
  [Fact]
  public async Task TimeOut_CancelCallbackThrows_EndsTimedOut()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = new EngineTask("Hang", token => HangAsync(token, running), requiredForRunning: true, logger,
      CancellationToken.None).SetTimeOut(TimeSpan.FromMilliseconds(100));

    await task.FireAsync(EngineTaskTrigger.Start);

    await task.InnerTask.WaitAsync(MustComplete, ct);
    task.State.Should().Be(EngineTaskState.TimedOut);
    Logged(logger, "Cancel callback failed.").Should().BeTrue("a swallowed exception is logged");
  }

  /// <summary>
  /// Verifies that a task with a recovery that times out while its work has a cancel callback that throws still
  /// recovers, as it does when the callback does not throw.
  /// </summary>
  [Fact]
  public async Task TimeOut_WithRecovery_CancelCallbackThrows_Recovers()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = new EngineTask("Hang", token => HangAsync(token, running), requiredForRunning: true, logger,
      CancellationToken.None).SetTimeOut(TimeSpan.FromMilliseconds(100)).SetRecovery(TestRestartPolicies.NoWait(3));

    await task.FireAsync(EngineTaskTrigger.Start);

    await Wait.UntilAsync(() => logger.Collector.GetSnapshot().Any(r => r.Message.StartsWith("Hang: Recovery start")),
      MustComplete, "the timed-out task must recover");
    Logged(logger, "Cancel callback failed.").Should().BeTrue("a swallowed exception is logged");
    await task.FireAsync(EngineTaskTrigger.Cancel);
  }

  /// <summary>
  /// Verifies that work that throws an exception with an empty message ends its task failed and the engine Faulted,
  /// with or without a recovery (#24 part 3: Fail and FailRecovery are fired without their reason then).
  /// </summary>
  [Theory]
  [InlineData(false, EngineTaskState.Failed)]
  [InlineData(true, EngineTaskState.RecoveryFailed)]
  public async Task StartAsync_WorkThrowsEmptyMessage_TaskFailsAndEngineFaults(bool withRecovery,
    EngineTaskState expected)
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new EmptyMessageEngine(withRecovery);

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    var task = engine.Tasks.Values.Single();
    await task.InnerTask.WaitAsync(MustComplete, ct);
    task.State.Should().Be(expected);
    engine.State.Should().Be(EngineState.Faulted);
  }
}
