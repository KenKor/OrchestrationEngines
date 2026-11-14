using System;
using System.Collections.Concurrent;
using System.Linq;
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

    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override void ReleaseResources() => Released.TrySetResult();

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
  /// An engine that only records that it was stopped, for walks over many engines.
  /// </summary>
  private sealed class CountingEngine(string name, ConcurrentQueue<string> stops) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks()
    {
    }

    public override Task StopAsync()
    {
      stops.Enqueue(name);
      return Task.CompletedTask;
    }
  }

  private static EngineCoordinator CreateCoordinator(ILogger logger = null) =>
    new(nameof(StopOrderTest), logger ?? NullLogger.Instance, useDispatcher: false);

  private static EngineDescriptor Register(EngineCoordinator coordinator, RecordingEngine engine, string name) =>
    coordinator.RegisterEngine(name, _ => Task.FromResult<IEngine>(engine))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));

  private static RecordingEngine Open(string name)
  {
    var engine = new RecordingEngine(name, new ConcurrentQueue<string>());
    engine.Gate.SetResult();
    return engine;
  }

  /// <summary>
  /// Registers an engine that a generation switch rebuilds from scratch: its first engine is <paramref name="first"/>,
  /// every later one a new engine that stops at once.
  /// </summary>
  private static EngineDescriptor RegisterRebuilt(EngineCoordinator coordinator, RecordingEngine first, string name)
  {
    var created = 0;
    return coordinator.RegisterEngine(name, _ => Task.FromResult<IEngine>(Interlocked.Increment(ref created) == 1 ? first : Open(name)))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
  }

  /// <summary>
  /// Registers an engine that a generation switch hands over: the next generation's engine is a new one that stops at
  /// once, and <paramref name="first"/> stops when the replaced generation shuts down.
  /// </summary>
  private static EngineDescriptor RegisterHandedOver(EngineCoordinator coordinator, RecordingEngine first, string name) =>
    RegisterRebuilt(coordinator, first, name).SetRestartFactory((_, _) => Task.FromResult<IEngine>(Open(name)));

  /// <summary>
  /// A mechanic with <paramref name="engines"/> registered as if they had been created, without starting it. An engine
  /// with <c>null</c> for its dependencies has no descriptor.
  /// </summary>
  private static MechanicEngine CreateMechanic(ILogger logger, TimeProvider clock,
    params (string Name, IEngine Engine, string[] DependsOn)[] engines)
  {
    var context = new MechanicContext();
    var mechanic = new MechanicEngine(logger ?? NullLogger.Instance) { TimeProvider = clock ?? TimeProvider.System };
    foreach (var (name, engine, dependsOn) in engines)
    {
      if (dependsOn != null)
      {
        var descriptor = new EngineDescriptor(name, _ => Task.FromResult(engine), requiredForRunning: true, 1);
        foreach (var dependency in dependsOn)
          descriptor.AddDependsOn(dependency);
        context.EngineDescriptors.Add(descriptor);
      }
      mechanic.RegisterEngine(name, engine);
    }
    mechanic.Context = context;
    return mechanic;
  }

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
  /// Verifies that a shutdown in which an engine fails to stop still ends <see cref="EngineCoordinatorState.Stopped"/>
  /// and reports the failure, and that a second shutdown then returns quietly.
  /// </summary>
  [Fact]
  public async Task Shutdown_EngineThrowsOnStop_EndsStoppedAndThrows()
  {
    var ct = TestContext.Current.CancellationToken;
    var failure = new InvalidOperationException("stop failed");
    var a = new RecordingEngine("A", new ConcurrentQueue<string>()) { StopFailure = failure };
    a.Gate.SetResult();
    var coordinator = CreateCoordinator();
    Register(coordinator, a, "A");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = () => coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    (await shutdown.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    await shutdown.Should().NotThrowAsync("a second shutdown finds the coordinator stopped");
  }

  /// <summary>
  /// Verifies that a logging provider that throws on the shutdown's last transition does not hide the engine's stop
  /// failure: the shutdown still throws that failure, and the coordinator is <see cref="EngineCoordinatorState.Stopped"/>.
  /// </summary>
  [Fact]
  public async Task Shutdown_EngineThrowsOnStopAndTheTransitionLogThrows_ThrowsTheStopFailure()
  {
    var ct = TestContext.Current.CancellationToken;
    var failure = new InvalidOperationException("stop failed");
    var a = new RecordingEngine("A", new ConcurrentQueue<string>()) { StopFailure = failure };
    a.Gate.SetResult();
    var coordinator = CreateCoordinator(
      new OnMessageLogger("CompleteShutdown", () => throw new InvalidOperationException("The logging provider failed.")));
    Register(coordinator, a, "A");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var shutdown = () => coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    (await shutdown.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
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
    var coordinator = CreateCoordinator(
      OnMessageLogger.OnError(() => throw new InvalidOperationException("The logging provider failed.")));
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

  /// <summary>
  /// Verifies that a generation switch stops the engines it rebuilds from scratch dependents first: with C → B → A, C,
  /// then B, then A, each only once the one depending on it has stopped.
  /// </summary>
  [Fact]
  public async Task Switch_RebuiltChain_StopsDependentsFirst()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var a = new RecordingEngine("A", stops);
    var b = new RecordingEngine("B", stops);
    var c = new RecordingEngine("C", stops);
    var coordinator = CreateCoordinator();
    RegisterRebuilt(coordinator, a, "A");
    RegisterRebuilt(coordinator, b, "B").AddDependsOn("A");
    RegisterRebuilt(coordinator, c, "C").AddDependsOn("B");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct);
    try
    {
      await c.StopEntered.Task.WaitAsync(MustComplete, ct);
      await Task.Delay(MustStayPending, ct);
      stops.Should().Equal(["C"], "B must not stop while C, which depends on it, is still stopping");
      c.Gate.SetResult();
      await b.StopEntered.Task.WaitAsync(MustComplete, ct);
      await Task.Delay(MustStayPending, ct);
      stops.Should().Equal(["C", "B"], "A must not stop while B is still stopping");
    }
    finally
    {
      foreach (var engine in new[] { a, b, c })
        engine.Gate.TrySetResult();
    }
    await next.WaitAsync(MustComplete, ct);

    stops.Take(3).Should().Equal("C", "B", "A");
  }

  /// <summary>
  /// Verifies that a switch's stop of its rebuilt engines follows the whole graph: with B → H → A, where A and B are
  /// rebuilt and H is handed over, B stops before A, and H is not stopped until the replaced generation shuts down.
  /// </summary>
  [Fact]
  public async Task Switch_RebuiltEnginesThroughAHandedOverOne_StopDependentsFirst()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var a = new RecordingEngine("A", stops);
    var h = new RecordingEngine("H", stops);
    var b = new RecordingEngine("B", stops);
    h.Gate.SetResult();
    var coordinator = CreateCoordinator();
    RegisterRebuilt(coordinator, a, "A");
    RegisterHandedOver(coordinator, h, "H").AddDependsOn("A");
    RegisterRebuilt(coordinator, b, "B").AddDependsOn("H");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct);
    try
    {
      await b.StopEntered.Task.WaitAsync(MustComplete, ct);
      await Task.Delay(MustStayPending, ct);
      stops.Should().Equal(["B"], "A must wait for B, which depends on it through H");
      b.Gate.SetResult();
      await a.StopEntered.Task.WaitAsync(MustComplete, ct);
      stops.Should().Equal(["B", "A"], "H is handed over, so the switch does not stop it before the next generation starts");
    }
    finally
    {
      foreach (var engine in new[] { a, b })
        engine.Gate.TrySetResult();
    }
    await next.WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a switch's shutdown of the generation it replaced stops the handed-over engines dependents first.
  /// </summary>
  [Fact]
  public async Task Switch_ReplacedGeneration_StopsDependentsFirst()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var a = new RecordingEngine("A", stops);
    var b = new RecordingEngine("B", stops);
    var c = new RecordingEngine("C", stops);
    var coordinator = CreateCoordinator();
    RegisterHandedOver(coordinator, a, "A");
    RegisterHandedOver(coordinator, b, "B").AddDependsOn("A");
    RegisterHandedOver(coordinator, c, "C").AddDependsOn("B");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct);
    try
    {
      await c.StopEntered.Task.WaitAsync(MustComplete, ct);
      await Task.Delay(MustStayPending, ct);
      stops.Should().Equal(["C"], "B must not stop while C is still stopping");
      c.Gate.SetResult();
      await b.StopEntered.Task.WaitAsync(MustComplete, ct);
      await Task.Delay(MustStayPending, ct);
      stops.Should().Equal(["C", "B"], "A must not stop while B is still stopping");
    }
    finally
    {
      foreach (var engine in new[] { a, b, c })
        engine.Gate.TrySetResult();
    }
    await next.WaitAsync(MustComplete, ct);

    stops.Should().Equal("C", "B", "A");
  }

  /// <summary>
  /// Verifies that an engine whose stop does not return gets a warning that names it and the engine waiting for it, and
  /// a line once its stop has finished after all.
  /// </summary>
  [Fact]
  public async Task Stop_EngineStillStoppingAfterTheWarningDelay_IsNamedWithTheEnginesWaitingForIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var stops = new ConcurrentQueue<string>();
    var cache = new RecordingEngine("Cache", stops);
    var db = Open("Db");
    var warned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new OnMessageLogger(
      (level, message, _) => level == LogLevel.Warning && message.Contains("Cache") && message.Contains("Db")
        && message.Contains("not finished stopping"),
      () => warned.TrySetResult(),
      new OnMessageLogger(
        (level, message, _) => level == LogLevel.Information && message.Contains("Cache") && message.Contains("finished stopping"),
        () => finished.TrySetResult()));
    var mechanic = CreateMechanic(logger, clock, ("Db", db, []), ("Cache", cache, ["Db"]));

    var stop = mechanic.StopAsync();
    await cache.StopEntered.Task.WaitAsync(MustComplete, ct);
    clock.Advance(TimeSpan.FromSeconds(29));
    warned.Task.IsCompleted.Should().BeFalse("the warning comes after 30 s");
    clock.Advance(TimeSpan.FromSeconds(1));
    await warned.Task.WaitAsync(MustComplete, ct);
    db.StopEntered.Task.IsCompleted.Should().BeFalse("Db still waits for Cache");
    cache.Gate.SetResult();
    await stop.WaitAsync(MustComplete, ct);

    await finished.Task.WaitAsync(MustComplete, ct);
    db.Stopped.Should().BeTrue();
  }

  /// <summary>
  /// Verifies that an engine that stops in time gets no warning, and leaves no timer behind.
  /// </summary>
  [Fact]
  public async Task Stop_EngineStopsInTime_IsNotWarnedAbout()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var warnings = 0;
    var logger = new OnMessageLogger((level, _, _) => level == LogLevel.Warning, () => Interlocked.Increment(ref warnings));
    var mechanic = CreateMechanic(logger, clock, ("A", Open("A"), []));

    await mechanic.StopAsync().WaitAsync(MustComplete, ct);
    clock.Advance(TimeSpan.FromMinutes(1));

    Volatile.Read(ref warnings).Should().Be(0);
    clock.WaitingTimers.Should().Be(0);
  }

  /// <summary>
  /// Verifies that an engine without a descriptor (registered on the mechanic directly) is stopped, and still waits for
  /// the engines whose descriptors depend on it.
  /// </summary>
  [Fact]
  public async Task Stop_EngineWithoutADescriptor_IsStoppedAfterItsDependents()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var plain = new RecordingEngine("Plain", stops);
    var user = new RecordingEngine("User", stops);
    plain.Gate.SetResult();
    var mechanic = CreateMechanic(null, null, ("Plain", plain, null), ("User", user, ["Plain"]));

    var stop = mechanic.StopAsync();
    await user.StopEntered.Task.WaitAsync(MustComplete, ct);
    await Task.Delay(MustStayPending, ct);
    stops.Should().Equal(["User"], "Plain must wait for User, whose descriptor depends on it");
    user.Gate.SetResult();
    await stop.WaitAsync(MustComplete, ct);

    stops.Should().Equal("User", "Plain");
    plain.Stopped.Should().BeTrue();
  }

  /// <summary>
  /// Verifies that a cycle made after start (start rejects one) does not keep any engine from stopping.
  /// </summary>
  [Fact]
  public async Task Shutdown_CycleMadeAfterStart_StopsEveryEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var a = Open("A");
    var b = Open("B");
    var coordinator = CreateCoordinator();
    var descriptorOfA = Register(coordinator, a, "A");
    Register(coordinator, b, "B").AddDependsOn("A");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    descriptorOfA.AddDependsOn("B");

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    a.Stopped.Should().BeTrue();
    b.Stopped.Should().BeTrue();
  }

  /// <summary>
  /// Verifies that a chain of 10,000 engines stops dependents first without running out of stack. The walk runs on a
  /// thread with a small stack (256 KB), as a deep chain must not depend on how big the caller's stack happens to be.
  /// </summary>
  [Fact]
  public async Task Stop_ChainOf10000Engines_StopsDependentsFirst()
  {
    var ct = TestContext.Current.CancellationToken;
    const int length = 10_000;
    var stops = new ConcurrentQueue<string>();
    var engines = Enumerable.Range(0, length)
      .Select(i => ($"E{i}", (IEngine)new CountingEngine($"E{i}", stops), i == 0 ? Array.Empty<string>() : [$"E{i - 1}"]))
      .ToArray();
    var mechanic = CreateMechanic(null, null, engines);

    Task stop = null;
    var thread = new Thread(() => stop = mechanic.StopAsync(), maxStackSize: 256 * 1024);
    thread.Start();
    thread.Join();
    await stop.WaitAsync(TimeSpan.FromSeconds(30), ct);

    stops.Should().Equal(Enumerable.Range(0, length).Reverse().Select(i => $"E{i}"));
  }

  /// <summary>
  /// Verifies that an engine whose stop is cancelled (its cleanup cut short) gets a warning, is not reported as a
  /// failure, and does not keep its dependency running.
  /// </summary>
  [Fact]
  public async Task Shutdown_DependentsStopIsCancelled_WarnsAndStillStopsItsDependency()
  {
    var ct = TestContext.Current.CancellationToken;
    var stops = new ConcurrentQueue<string>();
    var a = new RecordingEngine("A", stops);
    var b = new RecordingEngine("B", stops) { StopFailure = new OperationCanceledException("The cleanup was cut short.") };
    a.Gate.SetResult();
    b.Gate.SetResult();
    var warned = 0;
    var errors = 0;
    var logger = new OnMessageLogger(
      (level, message, exception) => level == LogLevel.Warning && message.Contains("B") && message.Contains("cancelled")
        && exception is OperationCanceledException,
      () => Interlocked.Increment(ref warned),
      OnMessageLogger.OnError(() => Interlocked.Increment(ref errors)));
    var coordinator = CreateCoordinator(logger);
    Register(coordinator, a, "A");
    Register(coordinator, b, "B").AddDependsOn("A");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    stops.Should().Equal("B", "A");
    a.Stopped.Should().BeTrue();
    Volatile.Read(ref warned).Should().Be(1);
    Volatile.Read(ref errors).Should().Be(0, "a cancelled stop is not a failure to stop");
  }

  /// <summary>
  /// Verifies that an engine whose factory ignores its token, and returns only after the shutdown began, is stopped
  /// when it is registered, and that this is logged.
  /// </summary>
  [Fact]
  public async Task Shutdown_EngineCreatedAfterTheStopBegan_IsStoppedOnRegistration()
  {
    var ct = TestContext.Current.CancellationToken;
    var late = Open("Late");
    var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var factoryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new OnMessageLogger(
      (level, message, _) => level == LogLevel.Information && message.Contains("Late") && message.Contains("after the stop began"),
      () => logged.TrySetResult());
    var coordinator = CreateCoordinator(logger);
    coordinator.RegisterEngine("Late", async _ =>
      {
        factoryEntered.TrySetResult();
        await factoryGate.Task.ConfigureAwait(false);
        return (IEngine)late;
      })
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    _ = coordinator.StartAsync();
    await factoryEntered.Task.WaitAsync(MustComplete, ct);

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    factoryGate.SetResult();

    await late.Released.Task.WaitAsync(MustComplete, ct);
    await logged.Task.WaitAsync(MustComplete, ct);
  }
}
