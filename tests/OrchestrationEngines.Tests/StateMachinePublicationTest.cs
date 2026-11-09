using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Tasks;
using Stateless;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for how a task publishes its state machine: once on first touch, and anew on each reset.
/// </summary>
public class StateMachinePublicationTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// A task whose work completes at once, that shows its state machine and counts how often one is built.
  /// </summary>
  private sealed class CountingTask()
    : EngineTask("Count", _ => Task.CompletedTask, requiredForRunning: true, NullLogger.Instance, CancellationToken.None)
  {
    private int _built;

    public int Built => Volatile.Read(ref _built);

    public StateMachine<EngineTaskState, EngineTaskTrigger> Machine => StateMachine;

    protected override StateMachine<EngineTaskState, EngineTaskTrigger> CreateStateMachine()
    {
      Interlocked.Increment(ref _built);
      return base.CreateStateMachine();
    }
  }

  /// <summary>
  /// Verifies that many threads touching a new task's state machine for the first time all see the same one, and that
  /// it is built once.
  /// </summary>
  [Fact]
  public void FirstTouch_FromManyThreads_SeesOneMachine()
  {
    const int Threads = 16;
    for (var attempt = 0; attempt < 50; attempt++)
    {
      var task = new CountingTask();
      using var barrier = new Barrier(Threads);
      var seen = new ConcurrentBag<StateMachine<EngineTaskState, EngineTaskTrigger>>();
      var threads = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
      {
        barrier.SignalAndWait();
        seen.Add(task.Machine);
      })).ToArray();

      foreach (var thread in threads)
        thread.Start();
      foreach (var thread in threads)
        thread.Join();

      seen.Distinct().Should().ContainSingle("attempt {0}", attempt);
      task.Built.Should().Be(1, "attempt {0}", attempt);
    }
  }

  /// <summary>
  /// Verifies that reading a task's state while it is reset, again and again, never throws and only ever sees a state
  /// of the old machine or of the new one.
  /// </summary>
  [Fact]
  public async Task State_ReadDuringResets_NeverThrows()
  {
    var ct = TestContext.Current.CancellationToken;
    var task = new CountingTask();
    using var done = new CancellationTokenSource();
    var reads = 0;
    var reader = Task.Run(() =>
    {
      while (!done.IsCancellationRequested)
      {
        var state = task.State;
        Enum.IsDefined(typeof(EngineTaskState), state).Should().BeTrue();
        reads++;
      }
    }, ct);

    for (var cycle = 0; cycle < 200; cycle++)
    {
      await task.FireAsync(EngineTaskTrigger.Start);
      await task.InnerTask.WaitAsync(MustComplete, ct);
      await task.FireAsync(EngineTaskTrigger.Reset);
    }
    done.Cancel();

    await reader.WaitAsync(MustComplete, ct);
    reads.Should().BePositive();
    task.Built.Should().Be(201, "one machine on first touch, and one per reset");
    task.State.Should().Be(EngineTaskState.Created);
  }
}
