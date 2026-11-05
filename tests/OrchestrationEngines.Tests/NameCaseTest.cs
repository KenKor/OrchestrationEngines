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
/// Contains tests for engine and task names used in another case than they were registered or created with: names
/// are compared case-insensitively everywhere, in dependencies, lookups, context refreshes and task names.
/// </summary>
public class NameCaseTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine with one load task, which can add a task after its set-up.
  /// </summary>
  private sealed class LoadEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);

    public void AddLateTask(string name) =>
      AddTask(name, new EngineTask(name, _ => Task.CompletedTask, requiredForRunning: false, NullLogger.Instance,
        CancellationToken.None));
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(NameCaseTest), NullLogger.Instance, useDispatcher: false);

  /// <summary>
  /// Verifies that an engine whose dependency is named in another case starts, and reaches that dependency through
  /// its context by any case.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_DependsOnInAnotherCase_StartsAndWiresTheDependency()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var asBuilt = new LoadEngine();
    var patch = new LoadEngine();
    coordinator.RegisterEngine("AsBuilt", _ => Task.FromResult<IEngine>(asBuilt))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    coordinator.RegisterEngine("Patch", _ => Task.FromResult<IEngine>(patch))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .AddDependsOn("asbuilt");

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    (await coordinator.GetEngineAsync("PATCH", ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(patch);
    patch.Context.GetUnderlyingEngine("ASBUILT").Should().BeSameAs(asBuilt);
    coordinator.GetEngineIfReady("asBUILT").Should().BeSameAs(asBuilt);
  }

  /// <summary>
  /// Verifies that a lookup in another case that arrives before the engine has been created waits for it, instead of
  /// reporting it as unknown.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_AnotherCaseBeforeCreation_WaitsForTheEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var engine = new LoadEngine();
    var create = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.RegisterEngine("AsBuilt", async _ =>
      {
        await create.Task;
        return engine;
      })
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    await coordinator.StartAsync().WaitAsync(MustComplete, ct);

    var lookup = coordinator.GetEngineAsync("asbuilt", ct);
    create.SetResult(true);

    (await lookup.WaitAsync(MustComplete, ct)).Should().BeSameAs(engine);
  }

  /// <summary>
  /// Verifies that invalidating an engine's context by a name in another case reloads that engine.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContextForEngineAsync_AnotherCase_ReloadsTheEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var contexts = 0;
    coordinator.RegisterEngine("AsBuilt", _ => Task.FromResult<IEngine>(new LoadEngine()))
      .SetContextFactory(_ =>
      {
        Interlocked.Increment(ref contexts);
        return Task.FromResult<IEngineContext>(new NoContext());
      });
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await coordinator.InvalidateContextForEngineAsync("ASBUILT", ct).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    Volatile.Read(ref contexts).Should().Be(2);
  }

  /// <summary>
  /// Verifies that an engine's tasks are found by a name in another case, also a task added after the set-up.
  /// </summary>
  [Fact]
  public async Task BaseEngine_Tasks_AnotherCase_FindsTheTask()
  {
    var engine = new LoadEngine { Context = new NoContext() };
    await engine.StartAsync();
    engine.AddLateTask("Late");

    engine.Tasks.Should().ContainKey("LOAD").And.ContainKey("late");
    (await engine.EnsureTasksReadyAsync(["load"]).WaitAsync(MustComplete, TestContext.Current.CancellationToken))
      .Should().BeTrue();
    await engine.StopAsync();
  }
}
