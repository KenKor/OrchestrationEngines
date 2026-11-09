using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the triggers a start fires on an engine's tasks.
/// </summary>
public class StartTriggerTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// A task that, when asked whether it has started while it has not, waits briefly for its dependency to start it.
  /// </summary>
  /// <remarks>
  /// This stands in for a thread that is preempted between a start's look at the task and its choice of trigger: the
  /// dependency, completing on another thread, moves the task from Created to Running in between. Without the wait
  /// the window is a few instructions wide, and 500 starts only rarely hit it.
  /// </remarks>
  private sealed class SlowToLookTask(string name, Func<CancellationToken, Task> workAsync)
    : EngineTask(name, workAsync, requiredForRunning: true, NullLogger.Instance, CancellationToken.None)
  {
    public override bool IsStarted
    {
      get
      {
        var started = base.IsStarted;
        if (!started)
          SpinWait.SpinUntil(() => State != EngineTaskState.Created, TimeSpan.FromMilliseconds(200));
        return started;
      }
    }
  }

  /// <summary>
  /// A chain of tasks, each depending on the one before it, whose work completes at once and counts its runs. Every
  /// task after the first is a <see cref="SlowToLookTask"/>.
  /// </summary>
  private sealed class ChainEngine : BaseEngine
  {
    public const int Length = 5;

    private readonly int[] _runs = new int[Length];

    public ChainEngine() : base(NullLogger.Instance) => Context = new NoContext();

    public int[] Runs => Enumerable.Range(0, Length).Select(i => Volatile.Read(ref _runs[i])).ToArray();

    protected override void SetUpTasks()
    {
      EngineTask previous = null;
      for (var i = 0; i < Length; i++)
      {
        var index = i;
        Func<CancellationToken, Task> work = _ =>
        {
          Interlocked.Increment(ref _runs[index]);
          return Task.CompletedTask;
        };
        var task = previous == null
          ? CreateTask($"Step{i}", work)
          : AddTask($"Step{i}", new SlowToLookTask($"Step{i}", work));
        if (previous != null)
          task.AddDependsOn(previous);
        previous = task;
      }
    }
  }

  /// <summary>
  /// Verifies that a start runs the work of each task in a chain of instant tasks exactly once: a task that its
  /// dependency started while the start was still going through the set is not reset and run again.
  /// </summary>
  [Fact]
  public async Task StartAsync_ChainOfInstantTasks_RunsEachTaskOnce()
  {
    var ct = TestContext.Current.CancellationToken;
    for (var attempt = 0; attempt < 500; attempt++)
    {
      var engine = new ChainEngine();

      await engine.StartAsync().WaitAsync(MustComplete, ct);
      await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

      engine.State.Should().Be(EngineState.Ready, "attempt {0}", attempt);
      engine.Runs.Should().OnlyContain(runs => runs == 1, "each task runs once per start (attempt {0})", attempt);
      await engine.StopAsync().WaitAsync(MustComplete, ct);
    }
  }
}
