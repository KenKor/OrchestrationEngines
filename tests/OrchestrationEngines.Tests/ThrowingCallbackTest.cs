using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
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

    protected override void SetUpTasks() => CreateTask("Hang", token => HangAsync(token, Running));
  }

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
    Logged(logger, "Override failed.").Should().BeTrue("a swallowed exception is logged");
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
    await engine.Tasks.Values.Single().InnerTask.WaitAsync(MustComplete, ct);
    Logged(logger, "Cancel callback failed.").Should().BeTrue("a swallowed exception is logged");
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
}
