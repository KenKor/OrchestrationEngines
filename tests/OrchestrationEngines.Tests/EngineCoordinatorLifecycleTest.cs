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

  private static EngineDescriptor Descriptor(string name, IEngine engine) => Descriptor(name, () => engine);

  private static EngineDescriptor Descriptor(string name, Func<IEngine> create) =>
    new EngineDescriptor(name, _ => Task.FromResult(create()), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();

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
    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.WaitingForEngines,
      MustComplete, "the failure must reach the coordinator");
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
    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.WaitingForEngines,
      MustComplete, "the failure must reach the coordinator");

    await coordinator.InvalidateContextForEngineAsync(Name, ct).WaitAsync(MustComplete, ct);
    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeSameAs(engine);
    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.Running,
      MustComplete, "the coordinator must run again");
  }

  /// <summary>
  /// Verifies that invalidating a context on a coordinator that is shut down throws, instead of resetting tasks that
  /// would never run again (#58, item 1): without a dispatcher the mechanic refuses (InvalidOperationException); with
  /// one, the dispatcher's queue no longer takes work (OperationCanceledException).
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineCoordinator_InvalidateContext_AfterShutdown_Throws(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator(nameof(EngineCoordinatorLifecycleTest), NullLogger.Instance, useDispatcher);
    coordinator.RegisterEngineDescriptor(Descriptor(Name, new NetworkEngine((_, _) => Task.CompletedTask)));
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().NotBeNull();
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    var act = () => coordinator.InvalidateContextForEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    if (useDispatcher)
      await act.Should().ThrowAsync<OperationCanceledException>();
    else
      await act.Should().ThrowAsync<InvalidOperationException>();
  }

  /// <summary>
  /// Verifies that shutting down a coordinator that never started ends it Stopped, as after a start (#153): it takes
  /// no more engines, creates none, and a later ready-wait behaves as on a coordinator shut down after its start.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineCoordinator_ShutdownAsync_NeverStarted_EndsStopped(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator(nameof(EngineCoordinatorLifecycleTest), NullLogger.Instance, useDispatcher);
    var created = 0;
    coordinator.RegisterEngineDescriptor(Descriptor(Name, () =>
    {
      Interlocked.Increment(ref created);
      return new NetworkEngine((_, _) => Task.CompletedTask);
    }));

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    coordinator.TryRegisterEngineDescriptor(Descriptor("Other", new NetworkEngine((_, _) => Task.CompletedTask)))
      .Should().BeFalse("a shut-down coordinator takes no more engines");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    // As after a start: with the dispatcher, its queue no longer takes work; without it, the wait returns.
    var ensureReady = () => coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    if (useDispatcher)
      await ensureReady.Should().ThrowAsync<OperationCanceledException>();
    else
      await ensureReady.Should().NotThrowAsync();

    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    created.Should().Be(0, "a coordinator shut down before its start never creates an engine");
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
    await Wait.UntilAsync(() => engine.State == EngineState.Starting, MustComplete, "the engine must be loading");
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
