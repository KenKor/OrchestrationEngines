using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for engines that a generation switch rebuilds from scratch, because their descriptor has no restart
/// factory: the previous engine is stopped, and its data released, before its replacement is created, so two copies
/// of a large data set are never loaded at the same time. An engine with a restart factory keeps running until the
/// next generation has started, so that its replacement can take over its state.
/// </summary>
public class RebuiltEngineSwitchTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine that records whether it released its data.
  /// </summary>
  private sealed class DataEngine() : BaseEngine(NullLogger.Instance)
  {
    private int _released;

    public bool Released => Volatile.Read(ref _released) == 1;

    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);

    protected override void ReleaseResources() => Volatile.Write(ref _released, 1);
  }

  /// <summary>
  /// What an engine looked like when its replacement was created.
  /// </summary>
  private sealed record Snapshot(EngineState State, bool Released);

  /// <summary>
  /// Verifies that a switch stops an engine without a restart factory, and releases its data, before its replacement
  /// is created, while an engine with a restart factory is still running when its factory takes it over.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineCoordinator_RestartWithDefinitionsAsync_WithoutRestartFactory_StopsThePreviousEngineFirst(
    bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var instancesOfA = new List<DataEngine>();
    Snapshot previousAAtCreation = null;
    Snapshot previousBAtRestart = null;

    var coordinator = new EngineCoordinator(nameof(RebuiltEngineSwitchTest), NullLogger.Instance, useDispatcher);
    coordinator.RegisterEngine("A", _ =>
      {
        lock (instancesOfA)
        {
          if (instancesOfA.Count > 0)
          {
            var previous = instancesOfA[instancesOfA.Count - 1];
            previousAAtCreation = new Snapshot(previous.State, previous.Released);
          }
          var engine = new DataEngine();
          instancesOfA.Add(engine);
          return Task.FromResult<IEngine>(engine);
        }
      })
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    coordinator.RegisterEngine("B", _ => Task.FromResult<IEngine>(new DataEngine()))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartFactory((input, _) =>
      {
        var previous = (DataEngine)input.PreviousEngine;
        previousBAtRestart = new Snapshot(previous.State, previous.Released);
        return Task.FromResult<IEngine>(new DataEngine());
      })
      .AddDependsOn("A");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    previousAAtCreation.Should().Be(new Snapshot(EngineState.Stopped, Released: true),
      "an engine without a restart factory is stopped before its replacement is created");
    previousBAtRestart.Should().Be(new Snapshot(EngineState.Ready, Released: false),
      "an engine with a restart factory runs until the next generation has started, so it can be taken over");
    next.GetEngineIfReady("A").Should().BeSameAs(instancesOfA[1]);
    next.GetEngineIfReady("B").Should().NotBeNull();
    instancesOfA.Should().HaveCount(2);

    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a Faulted engine without a restart factory is stopped before the next generation starts, so that
  /// its kick-start does not recover it next to its replacement.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitionsAsync_FaultedEngineWithoutRestartFactory_IsStoppedFirst()
  {
    var ct = TestContext.Current.CancellationToken;
    var loads = 0;
    var first = new FailingEngine(() => Interlocked.Increment(ref loads));
    var coordinator = new EngineCoordinator(nameof(RebuiltEngineSwitchTest), NullLogger.Instance, useDispatcher: false);
    var created = 0;
    EngineState? firstAtReplacement = null;
    coordinator.RegisterEngine("A", _ =>
      {
        if (Interlocked.Increment(ref created) == 1)
          return Task.FromResult<IEngine>(first);
        firstAtReplacement = first.State;
        return Task.FromResult<IEngine>(new DataEngine());
      })
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      // A failed load must fault at once, not retry by the default restart policy.
      .WithoutRestarts();
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    first.State.Should().Be(EngineState.Faulted);

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    firstAtReplacement.Should().Be(EngineState.Stopped, "the Faulted engine is stopped before its replacement is created");
    first.IsCancellationRequested.Should().BeTrue();
    next.GetEngineIfReady("A").Should().NotBeNull().And.NotBeSameAs(first);
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// An engine whose load always fails.
  /// </summary>
  private sealed class FailingEngine(Action onLoad) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", _ =>
    {
      onLoad();
      throw new InvalidOperationException("The load failed.");
    });
  }
}
