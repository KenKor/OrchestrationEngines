using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for waiting until one named task has completed with success, on an engine and on the coordinator.
/// </summary>
public class EnsureTaskReadyTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// The exception the failing task's work throws.
  /// </summary>
  private sealed class LoadException(string message) : Exception(message);

  /// <summary>
  /// An engine with a task that completes, one that fails, one that is cancelled and one that depends on the failing
  /// one. None is required for running, so the engine itself does not fault.
  /// </summary>
  private sealed class MixedEngine() : BaseEngine(NullLogger.Instance)
  {
    public static readonly LoadException Thrown = new("The file is missing.");

    protected override void SetUpTasks()
    {
      CreateTask("Load", _ => Task.CompletedTask, requiredForRunning: false);
      var broken = CreateTask("Broken", _ => throw Thrown, requiredForRunning: false);
      CreateTask("Cancelled", _ => throw new OperationCanceledException("The query timed out."), requiredForRunning: false);
      CreateTask("AfterBroken", _ => Task.CompletedTask, requiredForRunning: false).AddDependsOn(broken);
    }
  }

  private static async Task<MixedEngine> StartedEngineAsync(CancellationToken ct)
  {
    var engine = new MixedEngine { Context = new NoContext() };
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    return engine;
  }

  /// <summary>
  /// Verifies that waiting for a task that completes with success returns.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TaskCompletes_Returns()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    await engine.EnsureTaskReadyAsync("load").WaitAsync(MustComplete, ct);

    engine.Tasks["Load"].State.Should().Be(EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that waiting for a task whose work throws reports the task, its state, its reason and the exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TaskFails_ThrowsWithItsReasonAndException()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("Broken").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().Be("Broken");
    thrown.Which.State.Should().Be(EngineTaskState.Failed);
    thrown.Which.FailReason.Should().Be("The file is missing.");
    thrown.Which.InnerException.Should().BeSameAs(MixedEngine.Thrown);
  }

  /// <summary>
  /// Verifies that waiting for a task that is cancelled reports its state, without an inner exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TaskCancelled_ThrowsWithItsState()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("Cancelled").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().Be("Cancelled");
    thrown.Which.State.Should().Be(EngineTaskState.Cancelled);
    thrown.Which.InnerException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that waiting for a task whose dependency failed reports DependencyFailed, without the dependency's
  /// exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_DependencyFails_ThrowsDependencyFailed()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("AfterBroken").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.State.Should().Be(EngineTaskState.DependencyFailed);
    thrown.Which.InnerException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that a reset clears the exception a failed task recorded.
  /// </summary>
  [Fact]
  public async Task EngineTask_Reset_ClearsFailException()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    var broken = engine.Tasks["Broken"];
    broken.FailException.Should().BeSameAs(MixedEngine.Thrown);

    await broken.FireAsync(EngineTaskTrigger.Reset);

    broken.FailException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that waiting for a task the engine does not have throws an ArgumentException naming the parameter.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_UnknownName_ThrowsArgumentException()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("Missing").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<ArgumentException>();

    thrown.Which.ParamName.Should().Be("taskName");
  }

  private static EngineCoordinator CoordinatorWith(IEngine engine)
  {
    var coordinator = new EngineCoordinator(nameof(EnsureTaskReadyTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine("Mixed", _ => Task.FromResult(engine), requiredForRunning: false)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    return coordinator;
  }

  /// <summary>
  /// Verifies that the coordinator, not started yet, starts and waits for an engine's task that completes.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_TaskCompletes_Returns()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new MixedEngine();
    var coordinator = CoordinatorWith(engine);

    await coordinator.EnsureEngineTaskReadyAsync("mixed", "Load", ct).WaitAsync(MustComplete, ct);

    engine.Tasks["Load"].State.Should().Be(EngineTaskState.CompletedWithSuccess);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that the coordinator passes on the exception of an engine's task that fails.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_TaskFails_Throws()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(new MixedEngine());

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Mixed", "Broken", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().Be("Broken");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that the coordinator reports an engine that could not be created by its creation task, which bears the
  /// engine's name.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_EngineNotCreated_ThrowsForTheEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator(nameof(EnsureTaskReadyTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine("Mixed", _ => throw new InvalidOperationException("No connection."), requiredForRunning: false)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Mixed", "Load", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().BeEquivalentTo("Mixed");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that the coordinator reports an engine that was created but never started (its context failed) by its
  /// kick-start task, not as an unknown task.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_EngineNotStarted_ThrowsForTheKickStart()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator(nameof(EnsureTaskReadyTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine("Mixed", _ => Task.FromResult<IEngine>(new MixedEngine()), requiredForRunning: false)
      .SetContextFactory(_ => throw new InvalidOperationException("No context."));

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Mixed", "Load", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().Be("Mixed" + EngineKickStartTask.PostFix);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that the coordinator throws an ArgumentException for an engine that is not registered.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_UnknownEngine_ThrowsArgumentException()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(new MixedEngine());

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Missing", "Load", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<ArgumentException>();

    thrown.Which.ParamName.Should().Be("engineName");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
