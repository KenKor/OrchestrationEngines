using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the order in which a shutdown stops the engines: dependents first, independent engines together.
/// </summary>
public class StopOrderTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan MustStayPending = TimeSpan.FromMilliseconds(200);

  /// <summary>
  /// An engine whose stop is recorded when it begins, and which can be held, or made to throw, before it stops.
  /// </summary>
  private sealed class RecordingEngine(string name, ConcurrentQueue<string> stops) : BaseEngine(NullLogger.Instance)
  {
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception StopFailure { get; init; }

    public TaskCompletionSource StopCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Stopped => StopCompleted.Task.IsCompleted;

    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);

    public override async Task StopAsync()
    {
      stops.Enqueue(name);
      StopEntered.TrySetResult();
      await Gate.Task.ConfigureAwait(false);
      if (StopFailure != null)
        throw StopFailure;
      await base.StopAsync().ConfigureAwait(false);
      StopCompleted.TrySetResult();
    }
  }

  /// <summary>
  /// A logger whose provider throws on the log line of a failed stop.
  /// </summary>
  private sealed class ThrowingOnStopFailureLogger : ILogger
  {
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
      Func<TState, Exception, string> formatter)
    {
      if (formatter(state, exception).Contains("failed to stop"))
        throw new InvalidOperationException("The logging provider failed.");
    }
  }

  private static EngineCoordinator CreateCoordinator(ILogger logger = null) =>
    new(nameof(StopOrderTest), logger ?? NullLogger.Instance, useDispatcher: false);

  private static EngineDescriptor Register(EngineCoordinator coordinator, RecordingEngine engine, string name) =>
    coordinator.RegisterEngine(name, _ => Task.FromResult<IEngine>(engine))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));

  /// <summary>
  /// Verifies that a chain A ← B ← C (C depends on B, B on A) stops in the order C, B, A, each only once the one
  /// depending on it has stopped.
  /// </summary>
  [Fact]
  public async Task Shutdown_Chain_StopsDependentsFirst()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var a = new RecordingEngine("A", stops);
    var b = new RecordingEngine("B", stops);
    var c = new RecordingEngine("C", stops);
    var coordinator = CreateCoordinator();
    Register(coordinator, a, "A");
    Register(coordinator, b, "B").AddDependsOn("A");
    Register(coordinator, c, "C").AddDependsOn("b");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = coordinator.ShutdownAsync(ct);
    await c.StopEntered.Task.WaitAsync(MustComplete, ct);
    await Task.Delay(MustStayPending, ct);
    stops.Should().Equal(["C"], "B must not stop while C, which depends on it, is still stopping");
    c.Gate.SetResult();
    await b.StopEntered.Task.WaitAsync(MustComplete, ct);
    await Task.Delay(MustStayPending, ct);
    stops.Should().Equal(["C", "B"], "A must not stop while B is still stopping");
    b.Gate.SetResult();
    a.Gate.SetResult();
    await shutdown.WaitAsync(MustComplete, ct);

    stops.Should().Equal("C", "B", "A");
    new[] { a, b, c }.Should().OnlyContain(e => e.Stopped);
  }

  /// <summary>
  /// Verifies that engines with no dependency between them stop together: each begins stopping before the other has
  /// finished.
  /// </summary>
  [Fact]
  public async Task Shutdown_IndependentEngines_StopConcurrently()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var x = new RecordingEngine("X", stops);
    var y = new RecordingEngine("Y", stops);
    var coordinator = CreateCoordinator();
    Register(coordinator, x, "X");
    Register(coordinator, y, "Y");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = coordinator.ShutdownAsync(ct);
    await Task.WhenAll(x.StopEntered.Task, y.StopEntered.Task).WaitAsync(MustComplete, ct);
    x.Gate.SetResult();
    y.Gate.SetResult();
    await shutdown.WaitAsync(MustComplete, ct);

    stops.Should().BeEquivalentTo(["X", "Y"]);
  }

  /// <summary>
  /// Verifies that a dependent that throws while stopping does not keep its dependency running: the dependency still
  /// stops, and the shutdown reports the failure once every engine has been stopped.
  /// </summary>
  [Fact]
  public async Task Shutdown_DependentThrowsOnStop_StillStopsItsDependency()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var failure = new InvalidOperationException("stop failed");
    var a = new RecordingEngine("A", stops);
    var b = new RecordingEngine("B", stops) { StopFailure = failure };
    a.Gate.SetResult();
    b.Gate.SetResult();
    var coordinator = CreateCoordinator();
    Register(coordinator, a, "A");
    Register(coordinator, b, "B").AddDependsOn("A");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = () => coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    (await shutdown.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
    stops.Should().Equal("B", "A");
    a.Stopped.Should().BeTrue();
    b.Stopped.Should().BeFalse();
  }

  /// <summary>
  /// Verifies that a logger that throws on the failed stop's log line neither keeps the dependency running nor hides
  /// the stop's failure.
  /// </summary>
  [Fact]
  public async Task Shutdown_DependentThrowsOnStopAndTheLoggerThrows_StillStopsItsDependency()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var failure = new InvalidOperationException("stop failed");
    var a = new RecordingEngine("A", stops);
    var b = new RecordingEngine("B", stops) { StopFailure = failure };
    a.Gate.SetResult();
    b.Gate.SetResult();
    var coordinator = CreateCoordinator(new ThrowingOnStopFailureLogger());
    Register(coordinator, a, "A");
    Register(coordinator, b, "B").AddDependsOn("A");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = () => coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    (await shutdown.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
    a.Stopped.Should().BeTrue();
  }

  /// <summary>
  /// Verifies a diamond (B and C depend on A, D on both): D first, then B and C together, then A once both have stopped.
  /// </summary>
  [Fact]
  public async Task Shutdown_Diamond_StopsTheSharedDependencyLast()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var a = new RecordingEngine("A", stops);
    var b = new RecordingEngine("B", stops);
    var c = new RecordingEngine("C", stops);
    var d = new RecordingEngine("D", stops);
    var coordinator = CreateCoordinator();
    Register(coordinator, a, "A");
    Register(coordinator, b, "B").AddDependsOn("A");
    Register(coordinator, c, "C").AddDependsOn("A");
    Register(coordinator, d, "D").AddDependsOn("B").AddDependsOn("C");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = coordinator.ShutdownAsync(ct);
    await d.StopEntered.Task.WaitAsync(MustComplete, ct);
    await Task.Delay(MustStayPending, ct);
    stops.Should().Equal(["D"], "B and C must wait for D");
    d.Gate.SetResult();
    await Task.WhenAll(b.StopEntered.Task, c.StopEntered.Task).WaitAsync(MustComplete, ct);
    b.Gate.SetResult();
    await Task.Delay(MustStayPending, ct);
    a.StopEntered.Task.IsCompleted.Should().BeFalse("A must wait for C as well as B");
    c.Gate.SetResult();
    a.Gate.SetResult();
    await shutdown.WaitAsync(MustComplete, ct);

    stops.Last().Should().Be("A");
  }

  /// <summary>
  /// Verifies that an engine that is slow to stop holds back only the engines it depends on: a chain unrelated to it
  /// stops in full meanwhile.
  /// </summary>
  [Fact]
  public async Task Shutdown_SlowIndependentEngine_DoesNotHoldBackAnUnrelatedChain()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var slow = new RecordingEngine("Slow", stops);
    var a = new RecordingEngine("A", stops);
    var b = new RecordingEngine("B", stops);
    a.Gate.SetResult();
    b.Gate.SetResult();
    var coordinator = CreateCoordinator();
    Register(coordinator, slow, "Slow");
    Register(coordinator, a, "A");
    Register(coordinator, b, "B").AddDependsOn("A");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = coordinator.ShutdownAsync(ct);
    try
    {
      await a.StopCompleted.Task.WaitAsync(MustComplete, ct);
      b.Stopped.Should().BeTrue();
      slow.Stopped.Should().BeFalse();
    }
    finally
    {
      slow.Gate.TrySetResult();
    }
    await shutdown.WaitAsync(MustComplete, ct);
  }
}
