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

    // Written only under the task's init lock, which serializes the builds.
    private int _built;

    public int Built => Volatile.Read(ref _built);

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
      _numbers[machine] = _built;
      Volatile.Write(ref _built, _built + 1);
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
  /// Verifies that a thread reading a task's state machine while the task is reset, again and again, never throws and
  /// only sees machines the task built, and that each reset builds exactly one new machine.
  /// </summary>
  /// <remarks>
  /// What guards the change is that only built machines are seen, and that 201 were built: one on first touch and one
  /// per reset. A missing fence cannot be made to fail on x86-64 or ARM64 under .NET's memory model, so this does not
  /// prove the fence.
  /// </remarks>
  [Fact]
  public async Task EngineTask_StateMachine_ReadDuringResets_SeesOnlyBuiltMachines()
  {
    var ct = TestContext.Current.CancellationToken;
    var task = new CountingTask();
    _ = task.Machine;
    using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var reads = 0;
    var foreign = 0;
    var reader = Task.Run(() =>
    {
      started.SetResult();
      while (!done.IsCancellationRequested)
      {
        // Compared in plain code and asserted once after the loop, so the loop does not allocate.
        if (task.NumberOf(task.Machine) < 0)
          foreign++;
        // Not checked: runs the unlocked State read path against the machine being replaced.
        _ = task.State;
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
    foreign.Should().Be(0, "only machines the task built are seen");
    task.Built.Should().Be(201, "one machine on first touch, and one per reset");
    task.State.Should().Be(EngineTaskState.Created);
  }
}
