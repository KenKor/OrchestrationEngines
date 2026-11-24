using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for an engine that loads again after its first start, as a host's change monitor makes it do when it
/// finds changes (reset the tasks after the delta load): the engine reports Starting at once, and the blocking lookup
/// waits for it instead of returning null.
/// </summary>
public class EngineReloadTest
{
  private const string Name = "E";

  /// <summary>
  /// The work of one task: open by default; after <see cref="Close"/> the task's next run waits until <see cref="Open"/>.
  /// </summary>
  private sealed class Gate
  {
    private volatile TaskCompletionSource _completion = Opened();

    public Task Task => _completion.Task;

    public void Close() => _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _completion.TrySetResult();

    public void Fail() => _completion.TrySetException(new InvalidOperationException("The load failed."));

    private static TaskCompletionSource Opened()
    {
      var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      completion.SetResult();
      return completion;
    }
  }

  /// <summary>
  /// An engine with two required tasks, a load and an index built on it, each waiting for its own gate.
  /// </summary>
  private sealed class TwoStepEngine() : BaseEngine(NullLogger.Instance)
  {
    public Gate Load { get; } = new();
    public Gate Index { get; } = new();

    protected override void SetUpTasks()
    {
      var load = CreateTask("Load", _ => Load.Task);
      CreateTask("Index", _ => Index.Task).AddDependsOn(load);
    }
  }

  /// <summary>
  /// Starts a coordinator with one <see cref="TwoStepEngine"/> and waits until it is Ready.
  /// </summary>
  private static async Task<(EngineCoordinator coordinator, TwoStepEngine engine)> StartedAsync()
  {
    var engine = new TwoStepEngine();
    var coordinator = new EngineCoordinator(nameof(EngineReloadTest), NullLogger.Instance);
    coordinator.RegisterEngineDescriptor(new EngineDescriptor(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext())));
    await coordinator.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    coordinator.GetEngineIfReady(Name).Should().BeSameAs(engine);
    return (coordinator, engine);
  }

  /// <summary>
  /// Makes the engine load again as a change monitor does after changes (reset its tasks), with both tasks waiting
  /// for their gate.
  /// </summary>
  private static async Task LoadAgainAsync(TwoStepEngine engine)
  {
    engine.Load.Close();
    engine.Index.Close();
    await engine.TriggerResetAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
  }

  private static async Task ShouldStillWaitAsync(Task waiting)
  {
    await Task.Delay(200, TestContext.Current.CancellationToken);
    waiting.IsCompleted.Should().BeFalse("the engine is still loading");
  }

  /// <summary>
  /// Verifies that resetting a Ready engine's tasks starts them again at once, each still after the tasks it depends
  /// on, and makes the engine report Starting, so the fast lookup no longer returns it while it loads.
  /// </summary>
  [Fact]
  public async Task BaseEngine_TriggerResetAsync_ReadyEngine_ReportsStartingAndRunsTheTasksAgain()
  {
    var (coordinator, engine) = await StartedAsync();

    await LoadAgainAsync(engine);

    engine.State.Should().Be(EngineState.Starting);
    engine.Tasks["Load"].State.Should().Be(EngineTaskState.Running, "nobody else starts the reset tasks");
    engine.Tasks["Index"].State.Should().Be(EngineTaskState.WaitingForDependencies, "it depends on the load, which runs again");
    coordinator.GetEngineIfReady(Name).Should().BeNull();
    engine.Load.Open();
    await Wait.UntilAsync(() => engine.Tasks["Index"].State == EngineTaskState.Running,
      MustComplete, "the task must reach the expected state");
  }

  /// <summary>
  /// Verifies that the blocking lookup waits for an engine that is loading again, whose kick-start completed at its
  /// first start, and returns it once its tasks have completed.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_EngineLoadingAgain_WaitsUntilReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, engine) = await StartedAsync();
    await LoadAgainAsync(engine);

    var waiting = coordinator.GetEngineAsync(Name, throwErrorIfNotFound: true, token: ct);
    await ShouldStillWaitAsync(waiting);
    engine.Load.Open();
    await ShouldStillWaitAsync(waiting);
    engine.Index.Open();

    (await waiting.WaitAsync(MustComplete, ct)).Should().BeSameAs(engine);
    coordinator.GetEngineIfReady(Name).Should().BeSameAs(engine);
  }

  /// <summary>
  /// Verifies that a load that fails while the engine loads again ends the blocking lookup with null.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_LoadFailsWhileLoadingAgain_ReturnsNull()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, engine) = await StartedAsync();
    await LoadAgainAsync(engine);

    var waiting = coordinator.GetEngineAsync(Name, throwErrorIfNotFound: true, token: ct);
    await ShouldStillWaitAsync(waiting);
    engine.Load.Fail();

    (await waiting.WaitAsync(MustComplete, ct)).Should().BeNull();
    engine.State.Should().Be(EngineState.Faulted);
  }

  /// <summary>
  /// Verifies that stopping an engine while it loads again ends the blocking lookup with null.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_EngineStoppedWhileLoadingAgain_ReturnsNull()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, engine) = await StartedAsync();
    await LoadAgainAsync(engine);

    var waiting = coordinator.GetEngineAsync(Name, throwErrorIfNotFound: true, token: ct);
    await ShouldStillWaitAsync(waiting);
    await engine.StopAsync().WaitAsync(MustComplete, ct);

    (await waiting.WaitAsync(MustComplete, ct)).Should().BeNull();
  }

  /// <summary>
  /// Verifies that a caller who cancels the blocking lookup while the engine loads again gets a cancellation that
  /// carries the caller's own token, and that the engine goes on loading (#156).
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_CallerCancelsWhileLoadingAgain_ThrowsWithTheCallersToken()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, engine) = await StartedAsync();
    await LoadAgainAsync(engine);
    using var caller = new CancellationTokenSource();

    var waiting = coordinator.GetEngineAsync(Name, throwErrorIfNotFound: true, token: caller.Token);
    await ShouldStillWaitAsync(waiting);
    caller.Cancel();

    (await waiting.Invoking(w => w.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>())
      .Which.CancellationToken.Should().Be(caller.Token);
    engine.State.Should().Be(EngineState.Starting, "the caller's cancellation ends only its own wait");
    engine.Load.Open();
    engine.Index.Open();
    (await coordinator.GetEngineAsync(Name, throwErrorIfNotFound: true, token: ct).WaitAsync(MustComplete, ct))
      .Should().BeSameAs(engine);
  }

  /// <summary>
  /// Verifies that a stopped engine's tasks are not started again by a reset.
  /// </summary>
  [Fact]
  public async Task BaseEngine_TriggerResetAsync_StoppedEngine_DoesNotStartTheTasks()
  {
    var ct = TestContext.Current.CancellationToken;
    var (_, engine) = await StartedAsync();
    await engine.StopAsync().WaitAsync(MustComplete, ct);

    await engine.TriggerResetAsync().WaitAsync(MustComplete, ct);

    engine.Tasks["Load"].State.Should().NotBe(EngineTaskState.Running);
    engine.Tasks["Index"].State.Should().NotBe(EngineTaskState.Running);
  }
}
