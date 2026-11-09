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
  /// A task whose work completes at once, that shows its state machine and numbers each one it builds.
  /// </summary>
  private sealed class CountingTask()
    : EngineTask("Count", _ => Task.CompletedTask, requiredForRunning: true, NullLogger.Instance, CancellationToken.None)
  {
    private readonly ConcurrentDictionary<StateMachine<EngineTaskState, EngineTaskTrigger>, int> _numbers = new();

    public int Built => _numbers.Count;

    public StateMachine<EngineTaskState, EngineTaskTrigger> Machine => StateMachine;

    /// <summary>
    /// The number of a machine this task built (0 for the first), or -1 for one it did not build.
    /// </summary>
    public int NumberOf(StateMachine<EngineTaskState, EngineTaskTrigger> machine) =>
      _numbers.TryGetValue(machine, out var number) ? number : -1;

    protected override StateMachine<EngineTaskState, EngineTaskTrigger> CreateStateMachine()
    {
      var machine = base.CreateStateMachine();
      // Numbered before it is published: the builds are serialized by the task's init lock.
      _numbers[machine] = _numbers.Count;
      return machine;
    }
  }

  /// <summary>
  /// Verifies that many threads touching a new task's state machine for the first time all see the same one, and that
  /// it is built once.
  /// </summary>
  [Fact]
  public void EngineTask_StateMachine_FirstTouchFromManyThreads_IsBuiltOnce()
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
  /// Verifies that a thread reading a task's state machine while the task is reset, again and again, never throws,
  /// only sees machines the task built, and never sees an older machine after a newer one.
  /// </summary>
  /// <remarks>
  /// A missing fence cannot be made to fail on x86-64 or ARM64 under .NET's memory model, so this guards the order of
  /// publication rather than proving the fence.
  /// </remarks>
  [Fact]
  public async Task EngineTask_StateMachine_ReadDuringResets_SeesOnlyBuiltMachinesInOrder()
  {
    var ct = TestContext.Current.CancellationToken;
    var task = new CountingTask();
    _ = task.Machine;
    using var done = new CancellationTokenSource();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var reads = 0;
    var reader = Task.Run(() =>
    {
      var last = 0;
      started.TrySetResult();
      while (!done.IsCancellationRequested && !ct.IsCancellationRequested)
      {
        var number = task.NumberOf(task.Machine);
        _ = task.State;
        number.Should().BeGreaterThanOrEqualTo(last, "a machine is never replaced by an older one");
        last = number;
        reads++;
        Thread.Yield();
      }
    }, ct);
    await started.Task.WaitAsync(MustComplete, ct);

    try
    {
      for (var cycle = 0; cycle < 200; cycle++)
      {
        await task.FireAsync(EngineTaskTrigger.Start);
        await task.InnerTask.WaitAsync(MustComplete, ct);
        await task.FireAsync(EngineTaskTrigger.Reset);
      }
    }
    finally
    {
      done.Cancel();
    }

    await reader.WaitAsync(MustComplete, ct);
    reads.Should().BePositive();
    task.Built.Should().Be(201, "one machine on first touch, and one per reset");
    task.State.Should().Be(EngineTaskState.Created);
  }
}
