using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
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

    public bool Stopped { get; private set; }

    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);

    public override async Task StopAsync()
    {
      stops.Enqueue(name);
      StopEntered.TrySetResult();
      await Gate.Task.ConfigureAwait(false);
      if (StopFailure != null)
        throw StopFailure;
      await base.StopAsync().ConfigureAwait(false);
      Stopped = true;
    }
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(StopOrderTest), NullLogger.Instance, useDispatcher: false);

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
  /// Verifies the waves of a diamond (B and C depend on A, D on both): D first, then B and C together, then A.
  /// </summary>
  [Fact]
  public async Task Shutdown_Diamond_StopsInWaves()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var engines = new[] { "A", "B", "C", "D" }.ToDictionary(n => n, n => new RecordingEngine(n, stops));
    var coordinator = CreateCoordinator();
    Register(coordinator, engines["A"], "A");
    Register(coordinator, engines["B"], "B").AddDependsOn("A");
    Register(coordinator, engines["C"], "C").AddDependsOn("A");
    Register(coordinator, engines["D"], "D").AddDependsOn("B").AddDependsOn("C");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = coordinator.ShutdownAsync(ct);
    await engines["D"].StopEntered.Task.WaitAsync(MustComplete, ct);
    engines["D"].Gate.SetResult();
    await Task.WhenAll(engines["B"].StopEntered.Task, engines["C"].StopEntered.Task).WaitAsync(MustComplete, ct);
    await Task.Delay(MustStayPending, ct);
    engines["A"].StopEntered.Task.IsCompleted.Should().BeFalse("A must wait for both B and C");
    engines["B"].Gate.SetResult();
    await Task.Delay(MustStayPending, ct);
    engines["A"].StopEntered.Task.IsCompleted.Should().BeFalse("A must wait for C as well");
    engines["C"].Gate.SetResult();
    engines["A"].Gate.SetResult();
    await shutdown.WaitAsync(MustComplete, ct);

    stops.First().Should().Be("D");
    stops.Last().Should().Be("A");
  }
}
