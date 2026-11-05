using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a run whose predecessor was ended (a reset, a time-out, a restart with a new task set) while its
/// work was still running: a reset cancels that work but cannot stop it, so the next run starts once it has ended and
/// never runs next to it.
/// </summary>
public class PreviousRunTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan MustStayPending = TimeSpan.FromMilliseconds(200);

  /// <summary>
  /// A restart policy that never waits between restarts, so tests do not depend on the backoff.
  /// </summary>
  private static RestartPolicy NoWait(int maxRestarts) => new()
  {
    MaxRestartsInWindow = maxRestarts,
    Window = TimeSpan.FromMinutes(10),
    InitialBackoff = TimeSpan.Zero,
    MaxBackoff = TimeSpan.Zero,
  };

  /// <summary>
  /// Work that counts its runs and how many of them run at the same time. The first run ignores its token and only
  /// ends when the test releases it; later runs end at once.
  /// </summary>
  private sealed class Work
  {
    private readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runs;
    private int _active;
    private int _maxActive;

    public int Runs => Volatile.Read(ref _runs);

    public int MaxActive => Volatile.Read(ref _maxActive);

    public void ReleaseFirstRun() => _firstRun.TrySetResult();

    public async Task RunAsync(CancellationToken token)
    {
      var run = Interlocked.Increment(ref _runs);
      var active = Interlocked.Increment(ref _active);
      InterlockedMax(ref _maxActive, active);
      try
      {
        if (run == 1)
          await _firstRun.Task.ConfigureAwait(false);
      }
      finally
      {
        Interlocked.Decrement(ref _active);
      }
    }

    private static void InterlockedMax(ref int target, int value)
    {
      int current;
      while (value > (current = Volatile.Read(ref target)) &&
        Interlocked.CompareExchange(ref target, value, current) != current)
      {
      }
    }
  }

  private static async Task WaitUntilAsync(Func<bool> condition, string because)
  {
    var deadline = DateTime.UtcNow + MustComplete;
    while (!condition())
    {
      DateTime.UtcNow.Should().BeBefore(deadline, because);
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }
  }

  /// <summary>
  /// Verifies that a task that timed out and recovers runs its work again only once the timed-out work, which ignores
  /// its token, has ended.
  /// </summary>
  [Fact]
  public async Task EngineTask_TimedOutWorkStillRunning_RecoveryRunsAfterIt()
  {
    var clock = new ManualTimeProvider();
    var work = new Work();
    var task = new EngineTask("Index", work.RunAsync, requiredForRunning: true, NullLogger.Instance, CancellationToken.None, clock)
      .SetTimeOut(TimeSpan.FromMinutes(90))
      .SetRecovery(NoWait(maxRestarts: 5));
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => work.Runs == 1 && clock.WaitingTimers == 1, "the work must run with its time-out");

    clock.Advance(TimeSpan.FromMinutes(90));
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    work.Runs.Should().Be(1, "the timed-out work has not ended yet");
    task.IsCompleted.Should().BeFalse();

    work.ReleaseFirstRun();
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    work.Runs.Should().Be(2);
    work.MaxActive.Should().Be(1, "the runs never ran at the same time");
  }

  /// <summary>
  /// Verifies that a run that waits for a previous run that does not notice its cancellation says so after
  /// <see cref="EngineTask.PreviousRunWarningAfter"/>, and still waits.
  /// </summary>
  [Fact]
  public async Task EngineTask_PreviousRunIgnoresItsToken_WarnsAndKeepsWaiting()
  {
    var clock = new ManualTimeProvider();
    var work = new Work();
    var task = new EngineTask("Load", work.RunAsync, requiredForRunning: true, NullLogger.Instance, CancellationToken.None, clock);
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => work.Runs == 1, "the first run must have started");

    await task.FireAsync(EngineTaskTrigger.Reset);
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => clock.WaitingTimers == 1, "the next run must wait, with its warning timer");
    (task.StateMessage ?? "").Should().NotContain("previous run", "the message only changes once the wait takes long");

    clock.Advance(EngineTask.PreviousRunWarningAfter);
    await WaitUntilAsync(() => task.StateMessage.Contains("has not noticed its cancellation"), "the wait must warn");
    work.Runs.Should().Be(1, "the warning does not start the next run");

    work.ReleaseFirstRun();
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    work.MaxActive.Should().Be(1);
  }

  /// <summary>
  /// Verifies that a reset cancels a run whose work checks its token, and that the next run then starts without
  /// waiting for the previous run to finish what it was doing.
  /// </summary>
  [Fact]
  public async Task EngineTask_ResetWhileWorkChecksItsToken_NextRunStartsOnceItNotices()
  {
    var runs = 0;
    var cancelledRuns = 0;
    var task = new EngineTask("Sections", async token =>
    {
      if (Interlocked.Increment(ref runs) > 1)
        return;
      try
      {
        // A long build that checks its token between steps.
        for (var step = 0; step < 100_000; step++)
        {
          token.ThrowIfCancellationRequested();
          await Task.Delay(10, CancellationToken.None);
        }
      }
      catch (OperationCanceledException)
      {
        Interlocked.Increment(ref cancelledRuns);
        throw;
      }
    }, requiredForRunning: true, NullLogger.Instance, CancellationToken.None);
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => Volatile.Read(ref runs) == 1, "the first run must have started");

    await task.FireAsync(EngineTaskTrigger.Reset);
    await task.FireAsync(EngineTaskTrigger.Start);

    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    Volatile.Read(ref cancelledRuns).Should().Be(1, "the reset cancelled the first run");
    Volatile.Read(ref runs).Should().Be(2);
  }

  /// <summary>
  /// Verifies that work sees its own task as the running one (work reports its progress to it, rather than to a task
  /// looked up by name, which may be its successor), and that there is none outside a task's work.
  /// </summary>
  [Fact]
  public async Task EngineTask_Running_IsTheTaskWhoseWorkRuns()
  {
    EngineTask seen = null;
    var task = new EngineTask("Load", _ =>
    {
      seen = EngineTask.Running;
      return Task.CompletedTask;
    }, requiredForRunning: true, NullLogger.Instance, CancellationToken.None);

    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    seen.Should().BeSameAs(task);
    EngineTask.Running.Should().BeNull();
  }

  /// <summary>
  /// An engine whose load fails its first run, next to an enrichment that runs on (it ignores its token) when the load
  /// has failed.
  /// </summary>
  private sealed class RetriedEngine(Work enrich) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks()
    {
      CreateTask("Load", _ => Interlocked.Increment(ref _loads) == 1
        ? throw new InvalidOperationException("The first load fails.")
        : Task.CompletedTask);
      CreateTask("Enrich", enrich.RunAsync);
    }
  }

  /// <summary>
  /// Verifies that an engine started again with a new task set (as the kick-start's retry does) starts the new set's
  /// tasks only once the work of the replaced set has ended: the enrichment of the failed attempt, which ignores its
  /// token, does not run next to the enrichment of the retry.
  /// </summary>
  [Fact]
  public async Task BaseEngine_RestartWhileReplacedWorkRuns_NewSetStartsAfterIt()
  {
    var enrich = new Work();
    var engine = new RetriedEngine(enrich) { Context = new NoContext() };
    await engine.StartAsync();
    await WaitUntilAsync(() => engine.Loads == 1 && enrich.Runs == 1, "the load must have failed while the enrichment runs");

    await engine.ResetFailedTasksAsync();
    await engine.StartAsync();
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    engine.Loads.Should().Be(1, "the new set waits for the replaced set's work");
    enrich.Runs.Should().Be(1);
    engine.State.Should().Be(EngineState.Starting);

    enrich.ReleaseFirstRun();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2);
    enrich.Runs.Should().Be(2);
    enrich.MaxActive.Should().Be(1, "the enrichments never ran at the same time");
  }

  /// <summary>
  /// An engine whose next start waits for work outside it (see <see cref="BaseEngine.StartAfter"/>), as an engine that
  /// takes over a previous engine's data waits for that engine's work.
  /// </summary>
  private sealed class InheritingEngine(Task inheritedWork) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    public Task StartAfterInheritedWorkAsync()
    {
      StartAfter(inheritedWork);
      return StartAsync();
    }

    protected override void SetUpTasks() => CreateTask("Load", _ =>
    {
      Interlocked.Increment(ref _loads);
      return Task.CompletedTask;
    });
  }

  /// <summary>
  /// Verifies that a start that waits for inherited work which does not end says so after
  /// <see cref="EngineTask.PreviousRunWarningAfter"/> in the waiting task's message, and still waits: the wait has no
  /// limit, but it is not silent.
  /// </summary>
  [Fact]
  public async Task BaseEngine_InheritedWorkDoesNotEnd_WarnsAndKeepsWaiting()
  {
    var clock = new ManualTimeProvider();
    var inherited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new InheritingEngine(inherited.Task) { Context = new NoContext(), TimeProvider = clock };
    await engine.StartAfterInheritedWorkAsync();
    await WaitUntilAsync(() => clock.WaitingTimers == 1, "the load must wait, with its warning timer");
    engine.Loads.Should().Be(0, "the load waits for the inherited work");

    clock.Advance(EngineTask.PreviousRunWarningAfter);
    await WaitUntilAsync(() => (engine.Tasks["Load"].StateMessage ?? "").Contains("has not noticed its cancellation"),
      "the wait must warn");
    engine.Loads.Should().Be(0, "the warning does not start the load");

    inherited.SetResult();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(1);
  }
}
