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
/// Contains tests for an engine's lifecycle around its tokens: a stopped engine stays stopped, a task's replaced token
/// source is disposed, and a wait for a task observes its token.
/// </summary>
public class EngineLifecycleTokenTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// One required task that counts its runs, and fails while <see cref="Fail"/> is set.
  /// </summary>
  private sealed class CountingEngine : BaseEngine
  {
    private int _runs;

    public CountingEngine() : base(NullLogger.Instance) => Context = new NoContext();

    public CountingEngine(CancellationToken engineToken) : base(NullLogger.Instance, engineToken) =>
      Context = new NoContext();

    public int Runs => Volatile.Read(ref _runs);

    public bool Fail { get; set; }

    protected override void SetUpTasks() => CreateTask("Load", _ =>
    {
      Interlocked.Increment(ref _runs);
      return Fail ? throw new InvalidOperationException("No data.") : Task.CompletedTask;
    });
  }

  /// <summary>
  /// A task that shows its current token source.
  /// </summary>
  private sealed class ProbeTask(Func<CancellationToken, Task> workAsync, CancellationToken engineToken = default)
    : EngineTask("Probe", workAsync, requiredForRunning: true, NullLogger.Instance, engineToken)
  {
    public CancellationTokenSource Source => _linkedCts;
  }

  /// <summary>
  /// One <see cref="ProbeTask"/> whose work completes at once; a new one for each set.
  /// </summary>
  private sealed class ProbeEngine : BaseEngine
  {
    public ProbeEngine() : base(NullLogger.Instance) => Context = new NoContext();

    public ProbeTask Probe { get; private set; }

    protected override void SetUpTasks() =>
      Probe = (ProbeTask)AddTask("Probe", new ProbeTask(_ => Task.CompletedTask, CancellationToken));
  }

  private static bool IsDisposed(CancellationToken token)
  {
    try
    {
      _ = token.WaitHandle;
      return false;
    }
    catch (ObjectDisposedException)
    {
      return true;
    }
  }

  /// <summary>
  /// Verifies that a stopped engine is not set to Created by a reset of all its tasks, and that its tasks do not run
  /// again, not even when it is started.
  /// </summary>
  [Fact]
  public async Task ResetAllTasksAsync_AfterStop_StaysStopped()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new CountingEngine();
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    await engine.StopAsync().WaitAsync(MustComplete, ct);

    await engine.ResetAllTasksAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Stopped, "a stopped engine is not prepared for a restart");
    await engine.StartAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Stopped);
    engine.Runs.Should().Be(1, "a stopped engine is not started again");
  }

  /// <summary>
  /// Verifies that a stopped engine with a failed task is not set to Created by a reset of its failed tasks.
  /// </summary>
  [Fact]
  public async Task ResetFailedTasksAsync_AfterStop_StaysStopped()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new CountingEngine { Fail = true };
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Faulted);
    await engine.StopAsync().WaitAsync(MustComplete, ct);

    engine.Fail = false;
    await engine.ResetFailedTasksAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Stopped, "a stopped engine is not prepared for a restart");
    await engine.StartAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Stopped);
    engine.Runs.Should().Be(1, "a stopped engine is not started again");
  }

  /// <summary>
  /// Verifies that a reset disposes the task's previous token source, and that the task's next run still works.
  /// </summary>
  [Fact]
  public async Task Reset_DisposesThePreviousTokenSource()
  {
    var ct = TestContext.Current.CancellationToken;
    var task = new ProbeTask(_ => Task.CompletedTask);
    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, ct);
    var previous = task.Source;
    var previousToken = previous.Token;

    await task.FireAsync(EngineTaskTrigger.Reset);

    task.Source.Should().NotBeSameAs(previous);
    IsDisposed(previousToken).Should().BeTrue();
    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, ct);
    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that the token source of a task whose set a restart replaced is disposed once its run has ended, and that
  /// the new set's task runs.
  /// </summary>
  [Fact]
  public async Task Restart_DisposesTheReplacedSetsTokenSources()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new ProbeEngine();
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    var replaced = engine.Probe;
    var replacedToken = replaced.Source.Token;

    await engine.ResetAllTasksAsync().WaitAsync(MustComplete, ct);
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.Probe.Should().NotBeSameAs(replaced);
    SpinWait.SpinUntil(() => IsDisposed(replacedToken), MustComplete).Should().BeTrue(
      "the replaced task's source is released once its run has ended");
    IsDisposed(engine.Probe.Source.Token).Should().BeFalse();
    engine.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that stopping an engine disposes its token source, while its token still reports the cancellation.
  /// </summary>
  [Fact]
  public async Task StopAsync_DisposesTheEnginesTokenSource()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new CountingEngine();
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    await engine.StopAsync().WaitAsync(MustComplete, ct);

    IsDisposed(engine.CancellationToken).Should().BeTrue();
    engine.CancellationToken.IsCancellationRequested.Should().BeTrue();
    engine.IsCancellationRequested.Should().BeTrue();
    await FluentActions.Awaiting(() => engine.StopAsync().WaitAsync(MustComplete, ct)).Should().NotThrowAsync(
      "a second stop does not cancel the disposed source");
  }

  /// <summary>
  /// Verifies that an engine whose parent token is cancelled before it starts reports Stopped when it is started, and
  /// runs nothing.
  /// </summary>
  [Fact]
  public async Task StartAsync_WithACancelledParentToken_ReportsStopped()
  {
    var ct = TestContext.Current.CancellationToken;
    using var parent = new CancellationTokenSource();
    var engine = new CountingEngine(parent.Token);
    parent.Cancel();

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Stopped);
    engine.Runs.Should().Be(0);
  }

  /// <summary>
  /// Verifies that an engine whose restart is prepared (a reset of its tasks) and whose parent token is then cancelled
  /// reports Stopped when it is started, and that a wait for it ends.
  /// </summary>
  [Fact]
  public async Task StartAsync_AfterAResetAndAParentCancel_ReportsStopped()
  {
    var ct = TestContext.Current.CancellationToken;
    using var parent = new CancellationTokenSource();
    var engine = new CountingEngine(parent.Token);
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    await engine.ResetAllTasksAsync().WaitAsync(MustComplete, ct);
    parent.Cancel();

    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Stopped);
    engine.Runs.Should().Be(1);
  }

  /// <summary>
  /// Verifies that a reset, then another reset and a cancel, never cancel a disposed source.
  /// </summary>
  [Fact]
  public async Task Reset_ThenResetAndCancel_DoNotThrow()
  {
    var ct = TestContext.Current.CancellationToken;
    var task = new ProbeTask(token => Task.Delay(Timeout.Infinite, token));
    await task.FireAsync(EngineTaskTrigger.Start);
    await task.FireAsync(EngineTaskTrigger.Reset);
    await task.FireAsync(EngineTaskTrigger.Start);

    await FluentActions.Awaiting(async () =>
    {
      await task.FireAsync(EngineTaskTrigger.Reset);
      await task.FireAsync(EngineTaskTrigger.Start);
      await task.FireAsync(EngineTaskTrigger.Cancel);
    }).Should().NotThrowAsync();

    await task.InnerTask.WaitAsync(MustComplete, ct);
    task.State.Should().Be(EngineTaskState.Cancelled);
  }

  /// <summary>
  /// Verifies that a stop right after a reset of the engine's tasks does not throw.
  /// </summary>
  [Fact]
  public async Task StopAsync_RightAfterAReset_DoesNotThrow()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new CountingEngine();
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    await engine.TriggerResetAsync().WaitAsync(MustComplete, ct);

    await FluentActions.Awaiting(() => engine.StopAsync().WaitAsync(MustComplete, ct)).Should().NotThrowAsync();
    engine.State.Should().Be(EngineState.Stopped);
  }

  /// <summary>
  /// Verifies that a wait for a task that never completes ends cancelled soon after its token is cancelled, and leaves
  /// the task running.
  /// </summary>
  [Fact]
  public async Task EnsureReadyAsync_ForATaskWithAToken_ObservesTheToken()
  {
    var ct = TestContext.Current.CancellationToken;
    var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new HangingEngine(running);
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await running.Task.WaitAsync(MustComplete, ct);
    var task = engine.Tasks.Values.Single();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

    var wait = engine.EnsureReadyAsync(task, cts.Token);

    await FluentActions.Awaiting(() => wait.WaitAsync(TimeSpan.FromSeconds(1), ct))
      .Should().ThrowAsync<OperationCanceledException>();
    task.State.Should().Be(EngineTaskState.Running);
    await engine.StopAsync().WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// One required task whose work runs until it is cancelled.
  /// </summary>
  private sealed class HangingEngine : BaseEngine
  {
    private readonly TaskCompletionSource<bool> _running;

    public HangingEngine(TaskCompletionSource<bool> running) : base(NullLogger.Instance)
    {
      _running = running;
      Context = new NoContext();
    }

    protected override void SetUpTasks() => CreateTask("Hang", async token =>
    {
      _running.TrySetResult(true);
      await Task.Delay(Timeout.Infinite, token);
    });
  }
}
