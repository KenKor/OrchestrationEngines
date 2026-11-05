using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for generation switches (RestartWithDefinitionsAsync) that the other switch tests do not cover: an
/// engine without a restart factory, a switch with the dispatcher on, and a restart factory that misbehaves.
/// </summary>
public class GenerationSwitchEdgeCaseTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Engine with one load task that waits for a gate (already open for a quick engine).
  /// </summary>
  private sealed class LoadEngine(Task gate) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", async token =>
    {
      await Task.WhenAny(gate, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
      token.ThrowIfCancellationRequested();
    });
  }

  private static LoadEngine Quick() => new(Task.CompletedTask);

  private static async Task WaitUntilAsync(Func<bool> condition, string because)
  {
    var deadline = DateTime.UtcNow + MustComplete;
    while (!condition())
    {
      if (DateTime.UtcNow > deadline)
        throw new TimeoutException($"Condition not met in time: {because}.");
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }
  }

  /// <summary>
  /// Verifies a switch for an engine without a restart factory, as hosts register their engines: the next generation
  /// creates a fresh engine from the registered factory, the previous engine is stopped, and the replaced coordinator
  /// answers from the next generation.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Switch_WithoutRestartFactory_CreatesAFreshEngineFromTheRegisteredFactory(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var created = new ConcurrentQueue<LoadEngine>();
    var coordinator = new EngineCoordinator("Switch", NullLogger.Instance, useDispatcher);
    coordinator.RegisterEngine("A", _ =>
    {
      var engine = Quick();
      created.Enqueue(engine);
      return Task.FromResult<IEngine>(engine);
    });
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var first = coordinator.GetEngineIfReady("A");

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    try
    {
      var second = next.GetEngineIfReady("A");
      second.Should().NotBeNull().And.NotBeSameAs(first);
      created.Should().HaveCount(2, "the registered factory created the engine again");
      first.State.Should().Be(EngineState.Stopped);
      coordinator.GetEngineIfReady("A").Should().BeSameAs(second, "the replaced coordinator answers from the next one");
      coordinator.GetEngineIfCreated("A").Should().BeSameAs(second);
    }
    finally
    {
      await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }
  }

  /// <summary>
  /// Verifies, with the dispatcher on, that a bounded lookup that waits on a generation that is then replaced (the
  /// switch cancels that generation's mechanic, which ends the wait) is answered by the next generation instead of
  /// failing with the cancellation.
  /// </summary>
  [Fact]
  public async Task Switch_WithDispatcher_AnswersAWaitingLookupFromTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var neverLoads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstGeneration = true;
    var coordinator = new EngineCoordinator("Switch", NullLogger.Instance, useDispatcher: true);
    coordinator.RegisterEngine("A", _ =>
    {
      // The first generation's engine never finishes loading; the next one is quick.
      IEngine engine = firstGeneration ? new LoadEngine(neverLoads.Task) : Quick();
      firstGeneration = false;
      return Task.FromResult(engine);
    });
    EngineCoordinator next = null;

    try
    {
      await coordinator.StartAsync().WaitAsync(MustComplete, ct);
      await WaitUntilAsync(() => coordinator.GetEngineIfCreated("A") != null, "the first engine must exist");

      // No caller token on purpose: the wait then uses the mechanic's token, which the switch cancels.
#pragma warning disable xUnit1051
      var lookup = coordinator.GetEngineAsync("A", (int)MustComplete.TotalMilliseconds);
#pragma warning restore xUnit1051
      await Task.Delay(100, ct);
      lookup.IsCompleted.Should().BeFalse("the first engine is still loading");

      next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct)
        .WaitAsync(MustComplete, ct);

      var engine = await lookup.WaitAsync(MustComplete, ct);
      engine.Should().NotBeNull().And.BeSameAs(next.GetEngineIfCreated("A"));
      engine.State.Should().Be(EngineState.Ready);
    }
    finally
    {
      neverLoads.TrySetResult();
      if (next != null)
        await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
      await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }
  }

  /// <summary>
  /// Verifies that a restart factory that hands back the previous engine instead of a new one fails the next
  /// generation's engine with a clear reason: a switch must produce a fresh instance with a fresh token chain.
  /// </summary>
  [Fact]
  public async Task Switch_RestartFactoryReturnsThePreviousEngine_FailsThatEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var coordinator = new EngineCoordinator("Switch", logger, useDispatcher: false);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(Quick()))
      .SetRestartFactory((input, _) => Task.FromResult(input.PreviousEngine));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    try
    {
      next.GetEngineIfReady("A").Should().BeNull();
      next.GetEngineIfCreated("A").Should().BeNull("the creation failed");
      logger.Collector.GetSnapshot().Should().Contain(r =>
        r.Level == LogLevel.Error && r.Message.Contains("returned the previous instance during generation switch"));
    }
    finally
    {
      await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }
  }

  /// <summary>
  /// Verifies that shutting down the coordinator a host started with, after a generation switch replaced it, also shuts
  /// down the current generation: lookups on that coordinator are answered by it, so its shutdown must reach it too.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ShutdownAsync_ReplacedCoordinator_AlsoShutsDownTheCurrentGeneration(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator("Shutdown", NullLogger.Instance, useDispatcher);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(Quick()));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var first = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);
    var second = await first.RestartWithDefinitionsAsync(first.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);
    var engine = second.GetEngineIfReady("A");

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    second.State.Should().Be(EngineCoordinatorState.Stopped);
    engine.State.Should().Be(EngineState.Stopped);
  }

  /// <summary>
  /// Verifies that shutting a coordinator down a second time, also while the first shutdown is still running, is
  /// harmless and leaves it Stopped. With the dispatcher on, the first shutdown stops the dispatcher: the later calls
  /// must still complete instead of waiting for a dispatcher that no longer runs.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ShutdownAsync_Twice_LeavesTheCoordinatorStopped(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator("Shutdown", NullLogger.Instance, useDispatcher);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(Quick()));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await Task.WhenAll(coordinator.ShutdownAsync(ct), coordinator.ShutdownAsync(ct)).WaitAsync(MustComplete, ct);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    coordinator.Engines.Values.Should().OnlyContain(e => e.State == EngineState.Stopped);
  }
}
