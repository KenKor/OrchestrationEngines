using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the coordinator's lifecycle: registration, getting back to Running after a failure, time-outs,
/// and shutting down while engines load.
/// </summary>
public class EngineCoordinatorLifecycleTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private const string Name = "E";

  /// <summary>
  /// An engine with one load task, whose work the test decides per run.
  /// </summary>
  private sealed class NetworkEngine(Func<int, CancellationToken, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    protected override void SetUpTasks() => CreateTask("Load", token => loadAsync(Interlocked.Increment(ref _loads), token));
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(EngineCoordinatorLifecycleTest), NullLogger.Instance, useDispatcher: false);

  private static EngineDescriptor Descriptor(string name, IEngine engine) =>
    new EngineDescriptor(name, _ => Task.FromResult(engine), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();

  private static async Task WaitUntilAsync(Func<bool> condition, string because)
  {
    var deadline = DateTime.UtcNow + MustComplete;
    while (!condition())
    {
      DateTime.UtcNow.Should().BeBefore(deadline, because);
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }
  }

  /// <summary>
  /// Verifies that registering a second engine under the same name, in another case, is rejected.
  /// </summary>
  [Fact]
  public void EngineCoordinator_RegisterEngineDescriptor_SameNameOtherCase_Throws()
  {
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor("AsBuilt", new NetworkEngine((_, _) => Task.CompletedTask)));

    var registerAgain = () => coordinator.RegisterEngineDescriptor(Descriptor("ASBUILT", new NetworkEngine((_, _) => Task.CompletedTask)));

    registerAgain.Should().Throw<ArgumentException>();
  }

  /// <summary>
  /// Verifies that registering an engine once the engines are loading is rejected: a running coordinator only takes
  /// new engines through a generation switch.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RegisterEngineDescriptor_WhileLoading_Throws()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, new NetworkEngine((_, _) => Task.CompletedTask)));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var register = () => coordinator.RegisterEngineDescriptor(Descriptor("Other", new NetworkEngine((_, _) => Task.CompletedTask)));

    register.Should().Throw<InvalidOperationException>();
  }

  /// <summary>
  /// Verifies that registering an engine after a failed first load (the coordinator is then back in WaitingForEngines,
  /// as while the mechanic builds its tasks) is rejected, and that trying to returns false: such a descriptor would never
  /// be loaded.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RegisterEngineDescriptor_AfterFailedFirstLoad_IsRejected()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, new NetworkEngine((_, _) => throw new InvalidOperationException("The load failed."))));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => coordinator.State == EngineCoordinatorState.WaitingForEngines, "the failure must reach the coordinator");
    var other = Descriptor("Other", new NetworkEngine((_, _) => Task.CompletedTask));

    coordinator.TryRegisterEngineDescriptor(other).Should().BeFalse();
    var register = () => coordinator.RegisterEngineDescriptor(other);
    register.Should().Throw<InvalidOperationException>();
    coordinator.GetRegisteredDefinitions().Should().ContainSingle();
  }

  /// <summary>
  /// Verifies that the lookup with a time-out returns null when the engine is still loading at the time-out.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_TimeOutWhileLoading_ReturnsNull()
  {
    var ct = TestContext.Current.CancellationToken;
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, new NetworkEngine((_, token) => gate.Task.WaitAsync(token))));

    var engine = await coordinator.GetEngineAsync(Name, 100, ct).WaitAsync(MustComplete, ct);

    engine.Should().BeNull();
    gate.SetResult();
  }

  /// <summary>
  /// Verifies that a coordinator whose engine failed returns to Running once the engine's context is invalidated and
  /// the engine loads after all.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_AfterEngineFailed_ReturnsToRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var engine = new NetworkEngine((run, _) => run == 1 ? throw new InvalidOperationException("First load fails.") : Task.CompletedTask);
    coordinator.RegisterEngineDescriptor(Descriptor(Name, engine));
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().BeNull("the first load fails");
    await WaitUntilAsync(() => coordinator.State == EngineCoordinatorState.WaitingForEngines, "the failure must reach the coordinator");

    await coordinator.InvalidateContextForEngineAsync(Name, ct).WaitAsync(MustComplete, ct);
    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeSameAs(engine);
    await WaitUntilAsync(() => coordinator.State == EngineCoordinatorState.Running, "the coordinator must run again");
  }

  /// <summary>
  /// Verifies that shutting down while an engine loads ends a lookup that waits for that engine, instead of leaving
  /// it waiting forever.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ShutdownAsync_WhileLoading_ReleasesWaitingLookup()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, new NetworkEngine((_, token) => Task.Delay(Timeout.Infinite, token))));
    var lookup = coordinator.GetEngineAsync(Name, ct);
    await Task.Delay(100, ct);
    lookup.IsCompleted.Should().BeFalse("the engine is still loading");

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    var ended = await Task.WhenAny(lookup, Task.Delay(MustComplete, ct));
    ended.Should().BeSameAs(lookup, "the shutdown must end the wait");
    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    if (lookup.IsCompletedSuccessfully)
      (await lookup).Should().BeNull();
  }

  /// <summary>
  /// Verifies that a context refresh of an engine that is still loading is pushed into it while the coordinator waits
  /// for it (as a host does for an edit that arrives during a load), and that the coordinator runs once the engine is ready;
  /// and that the engine can be found while it loads, though it is not ready.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_WhileTheEngineLoads_PushesTheContext()
  {
    var ct = TestContext.Current.CancellationToken;
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new NetworkEngine((_, token) => gate.Task.WaitAsync(token));
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, engine));
    var ready = coordinator.EnsureReadyAsync(ct);
    await WaitUntilAsync(() => engine.State == EngineState.Starting, "the engine must be loading");
    coordinator.GetEngineIfReady(Name).Should().BeNull();
    coordinator.GetEngineIfCreated(Name).Should().BeSameAs(engine);
    var fresh = new NoContext();

    await coordinator.RefreshEngineContextAsync(Name, fresh, ct).WaitAsync(MustComplete, ct);

    engine.Context.Should().BeSameAs(fresh);
    coordinator.State.Should().Be(EngineCoordinatorState.Initializing, "the engine is still loading");
    gate.SetResult();
    await ready.WaitAsync(MustComplete, ct);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }
}
