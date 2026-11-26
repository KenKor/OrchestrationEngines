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
/// engine without a restart factory, a lookup that waits across a switch, and a restart factory that misbehaves.
/// </summary>
public class GenerationSwitchEdgeCaseTest
{
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

  /// <summary>
  /// Verifies a switch for an engine without a restart factory, as hosts register their engines: the next generation
  /// creates a fresh engine from the registered factory, the previous engine is stopped, and the replaced coordinator
  /// answers from the next generation.
  /// </summary>
  [Fact]
  public async Task Switch_WithoutRestartFactory_CreatesAFreshEngineFromTheRegisteredFactory()
  {
    var ct = TestContext.Current.CancellationToken;
    var created = new ConcurrentQueue<LoadEngine>();
    var coordinator = new EngineCoordinator("Switch", NullLogger.Instance);
    coordinator.RegisterEngine("A", _ =>
    {
      var engine = Quick();
      created.Enqueue(engine);
      return Task.FromResult<IEngine>(engine);
    });
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var first = coordinator.GetEngineIfReady("A");

    var next = await coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
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
  /// Verifies that a bounded lookup that waits on a generation that is then replaced (the
  /// switch cancels that generation's mechanic, which ends the wait) is answered by the next generation instead of
  /// failing with the cancellation.
  /// </summary>
  [Fact]
  public async Task Switch_AnswersAWaitingLookupFromTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var neverLoads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstGeneration = true;
    var coordinator = new EngineCoordinator("Switch", NullLogger.Instance);
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
      await Wait.UntilAsync(() => coordinator.GetEngineIfCreated("A") != null,
        MustComplete, "the first engine must exist");

      // No caller token on purpose: the wait then uses the mechanic's token, which the switch cancels.
#pragma warning disable xUnit1051
      var lookup = coordinator.GetEngineAsync("A", (int)MustComplete.TotalMilliseconds);
#pragma warning restore xUnit1051
      await Task.Delay(100, ct);
      lookup.IsCompleted.Should().BeFalse("the first engine is still loading");

      next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions())
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
  /// Verifies that a switch whose candidate cannot be copied (a null engine factory, which its setter does not check)
  /// throws and leaves the current coordinator current and running (#59, item 7a). The switch copies every candidate
  /// before it creates the next coordinator, so no next coordinator is created at all. The exception is the copy's own
  /// (the descriptor constructor's), not another <see cref="ArgumentException"/> on the switch path (#92).
  /// </summary>
  [Fact]
  public async Task Switch_CandidateCannotBeCopied_ThrowsAndKeepsTheCurrentGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator("Switch", NullLogger.Instance);
    var nextGenerations = 0;
    coordinator.NextGenerationCreated = _ => Interlocked.Increment(ref nextGenerations);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(Quick()));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var candidate = new EngineDescriptor("A", _ => Task.FromResult<IEngine>(Quick()), requiredForRunning: true, 1)
    {
      EngineFactory = null,
    };

    try
    {
      var act = () => coordinator.SwitchUntilReadyAsync([candidate], ct).WaitAsync(MustComplete, ct);

      (await act.Should().ThrowExactlyAsync<ArgumentNullException>()).WithParameterName("createEngineFactory");
      Volatile.Read(ref nextGenerations).Should().Be(0, "the copy fails before the next coordinator is created");
      coordinator.Current.Should().BeSameAs(coordinator);
      coordinator.State.Should().Be(EngineCoordinatorState.Running);
    }
    finally
    {
      await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }
  }

  /// <summary>
  /// Verifies that a restart factory that hands back the previous engine instead of a new one fails the next
  /// generation's engine with a clear reason: a switch must produce a fresh instance with a fresh token chain. The
  /// creation is not retried, also with a restart policy (#59, item 1): the factory would only return it again.
  /// </summary>
  [Fact]
  public async Task Switch_RestartFactoryReturnsThePreviousEngine_FailsThatEngineWithoutARetry()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var coordinator = new EngineCoordinator("Switch", logger);
    var restarts = 0;
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(Quick()))
      .SetRestartFactory((input, _) =>
      {
        Interlocked.Increment(ref restarts);
        return Task.FromResult(input.PreviousEngine);
      })
      .SetRestartPolicy(TestRestartPolicies.NoWait(5));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = await coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);

    try
    {
      next.GetEngineIfReady("A").Should().BeNull();
      next.GetEngineIfCreated("A").Should().BeNull("the creation failed");
      Volatile.Read(ref restarts).Should().Be(1, "the creation is not retried");
      logger.Collector.GetSnapshot().Should().Contain(r =>
        r.Level == LogLevel.Warning && r.Message.Contains("returned the previous instance during generation switch"));
    }
    finally
    {
      await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }
  }

  /// <summary>
  /// Verifies that a restart factory that returns null fails the creation the way a throwing one does (#151): the
  /// handover is kept, so the retry is offered the previous engine again instead of starting from the registered
  /// factory.
  /// </summary>
  [Fact]
  public async Task Switch_RestartFactoryReturnsNullOnce_RetryIsOfferedThePreviousEngineAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator("Switch", NullLogger.Instance);
    var offered = new ConcurrentQueue<IEngine>();
    var registered = 0;
    coordinator.RegisterEngine("A", _ =>
      {
        Interlocked.Increment(ref registered);
        return Task.FromResult<IEngine>(Quick());
      })
      .SetRestartFactory((input, _) =>
      {
        offered.Enqueue(input.PreviousEngine);
        return Task.FromResult<IEngine>(offered.Count == 1 ? null : Quick());
      })
      .SetRestartPolicy(TestRestartPolicies.NoWait(5));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var previous = coordinator.GetEngineIfReady("A");

    var next = await coordinator
      .SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);

    try
    {
      next.GetEngineIfReady("A").Should().NotBeNull();
      offered.Should().HaveCount(2).And.OnlyContain(e => ReferenceEquals(e, previous));
      Volatile.Read(ref registered).Should().Be(1, "only the first generation starts from the registered factory");
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
  [Fact]
  public async Task ShutdownAsync_ReplacedCoordinator_AlsoShutsDownTheCurrentGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator("Shutdown", NullLogger.Instance);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(Quick()));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var first = await coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);
    var second = await first.SwitchUntilReadyAsync(first.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);
    var engine = second.GetEngineIfReady("A");

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    second.State.Should().Be(EngineCoordinatorState.Stopped);
    engine.State.Should().Be(EngineState.Stopped);
  }

  /// <summary>
  /// Verifies that a generation switch asked of a coordinator that an earlier switch replaced goes to the current
  /// generation, as lookups and context operations do: the current generation is replaced and stopped, exactly one
  /// generation runs, and shutting down the first coordinator stops it.
  /// </summary>
  [Fact]
  public async Task Switch_OnAReplacedCoordinator_ReplacesTheCurrentGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator("Switch", NullLogger.Instance);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(Quick()));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var second = await coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);
    var secondEngine = second.GetEngineIfReady("A");

    var third = await coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);

    try
    {
      second.Successor.Should().BeSameAs(third, "the switch replaced the current generation");
      coordinator.Current.Should().BeSameAs(third);
      second.State.Should().Be(EngineCoordinatorState.Stopped);
      secondEngine.State.Should().Be(EngineState.Stopped);
      new[] { coordinator, second, third }.Should().ContainSingle(c => c.State == EngineCoordinatorState.Running);
      third.GetEngineIfReady("A").Should().NotBeNull();
    }
    finally
    {
      await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }

    third.State.Should().Be(EngineCoordinatorState.Stopped,
      "shutting down the first coordinator reaches the current one");
  }

  /// <summary>
  /// Verifies that a second switch asked of a coordinator while its first switch waits for the next generation to be
  /// ready leaves one generation running: the second switch hands over first, so the first one may no longer hand
  /// over and goes to the current generation instead.
  /// </summary>
  [Fact]
  public async Task Switch_TwoOnOneCoordinator_LeaveOneGenerationRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    var created = new ConcurrentQueue<LoadEngine>();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    EngineDescriptor Definition(Task engineGate) => new("A", _ =>
    {
      var engine = new LoadEngine(engineGate);
      created.Enqueue(engine);
      return Task.FromResult<IEngine>(engine);
    }, requiredForRunning: true);
    var coordinator = new EngineCoordinator("Switch", NullLogger.Instance);
    coordinator.RegisterEngineDescriptor(Definition(Task.CompletedTask));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var waiting = coordinator.SwitchUntilReadyAsync([Definition(gate.Task)], ct);
    await Wait.UntilAsync(() => created.Count == 2, MustComplete, "the first switch created its engine");
    var other = coordinator.SwitchUntilReadyAsync([Definition(Task.CompletedTask)], ct);
    await other.WaitAsync(MustComplete, ct);
    gate.SetResult();
    var results = await Task.WhenAll(waiting, other).WaitAsync(MustComplete, ct);

    try
    {
      var current = coordinator.Current;
      results.Should().Contain(current);
      current.State.Should().Be(EngineCoordinatorState.Running);
      new[] { coordinator }.Concat(results).Where(c => c != current).Should()
        .OnlyContain(c => c.State == EngineCoordinatorState.Stopped);
      created.Where(e => e.State != EngineState.Stopped).Should().ContainSingle("one generation has its engine loaded");
    }
    finally
    {
      await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }
  }

  /// <summary>
  /// Verifies that shutting a coordinator down a second time, also while the first shutdown is still running, is
  /// harmless and leaves it Stopped.
  /// </summary>
  [Fact]
  public async Task ShutdownAsync_Twice_LeavesTheCoordinatorStopped()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator("Shutdown", NullLogger.Instance);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(Quick()));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await Task.WhenAll(coordinator.ShutdownAsync(ct), coordinator.ShutdownAsync(ct)).WaitAsync(MustComplete, ct);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);

    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    coordinator.Engines.Values.Should().OnlyContain(e => e.State == EngineState.Stopped);
  }
}
