using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
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
  /// This stands in for a thread that is preempted between the start's two looks at the task (IsStarted, then State,
  /// to choose between Start and Reset): the dependency, completing on another thread, moves the task from Created to
  /// Running in between. Without the wait the window is a few instructions wide, and 500 starts only rarely hit it.
  /// It guards against a return of that shape; a start that reads the state once never asks. It waits once per task,
  /// so a regression fails fast.
  /// </remarks>
  private sealed class SlowToLookTask(string name, Func<CancellationToken, Task> workAsync)
    : EngineTask(name, workAsync, requiredForRunning: true, NullLogger.Instance, CancellationToken.None)
  {
    private bool _waited;

    public override bool IsStarted
    {
      get
      {
        var started = base.IsStarted;
        if (!started && !_waited)
        {
          _waited = true;
          SpinWait.SpinUntil(() => State != EngineTaskState.Created, TimeSpan.FromMilliseconds(200));
        }
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
      var previous = CreateTask("Step0", Count(0));
      for (var i = 1; i < Length; i++)
        previous = AddTask($"Step{i}", new SlowToLookTask($"Step{i}", Count(i))).AddDependsOn(previous);
    }

    private Func<CancellationToken, Task> Count(int index) => _ =>
    {
      Interlocked.Increment(ref _runs[index]);
      return Task.CompletedTask;
    };
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

  /// <summary>
  /// Verifies that a Start that reaches a task after it has started (Running) or completed is ignored, without an
  /// "Unhandled trigger" warning: a start that found the task Created can fire it after a dependency started it.
  /// </summary>
  [Fact]
  public async Task Start_OnATaskThatHasStarted_IsIgnoredWithoutAWarning()
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = new EngineTask("Load", _ => release.Task, requiredForRunning: true, logger, CancellationToken.None);
    await task.FireAsync(EngineTaskTrigger.Start);
    task.State.Should().Be(EngineTaskState.Running);

    await task.FireAsync(EngineTaskTrigger.Start);
    task.State.Should().Be(EngineTaskState.Running);
    release.SetResult(true);
    await task.InnerTask.WaitAsync(MustComplete, ct);
    await task.FireAsync(EngineTaskTrigger.Start);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    logger.Collector.GetSnapshot().Should().NotContain(r => r.Level == LogLevel.Warning);
  }
}
