using System;
using System.Collections.Concurrent;
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
/// Contains tests for <see cref="IEngine.EngineStatusChanged"/> and its <see cref="EngineStatusEventArgs.Error"/>: a
/// host subscribes through <see cref="IEngine"/>, and a Faulted engine says which required task faulted it, and why.
/// </summary>
public class EngineStatusErrorTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine whose tasks the test sets up.
  /// </summary>
  private sealed class TasksEngine : BaseEngine
  {
    private readonly Action<TasksEngine> _setUp;

    public TasksEngine(Action<TasksEngine> setUp) : base(NullLogger.Instance)
    {
      _setUp = setUp;
      // Without a context, an engine whose tasks completed is Stopped, not Ready.
      Context = new NoContext();
    }

    /// <summary>
    /// While set, the engine stays Starting, whatever its tasks do: the test lets several tasks fail before the state
    /// that reports them is computed.
    /// </summary>
    public volatile bool Hold;

    protected override void SetUpTasks() => _setUp(this);

    protected override EngineState ComputeState() => Hold ? EngineState.Starting : base.ComputeState();

    public void Recompute() => RecomputeStateSynchronized();

    public EngineTask Task(string name, Func<CancellationToken, Task> work, bool required = true) =>
      CreateTask(name, work, required);
  }

  /// <summary>
  /// Records the events of an engine, through <see cref="IEngine"/>, and signals the first Faulted.
  /// </summary>
  private sealed class EventLog
  {
    public ConcurrentQueue<EngineStatusEventArgs> Events { get; } = new();

    public TaskCompletionSource<EngineStatusEventArgs> Faulted { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EventLog(IEngine engine) =>
      engine.EngineStatusChanged += (_, e) =>
      {
        Events.Enqueue(e);
        if (e.State == EngineState.Faulted)
          Faulted.TrySetResult(e);
      };
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

  private static async Task<EngineStatusEventArgs> FaultedAsync(TasksEngine engine)
  {
    var log = new EventLog(engine);
    await engine.StartAsync();
    return await log.Faulted.Task.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
  }

  /// <summary>
  /// Verifies that a subscriber through <see cref="IEngine"/> receives Starting, Ready and Stopped, each without an
  /// error.
  /// </summary>
  [Fact]
  public async Task IEngine_EngineStatusChanged_ReadyAndStopped_CarryNoError()
  {
    IEngine engine = new TasksEngine(e => e.Task("Load", _ => Task.CompletedTask));
    var log = new EventLog(engine);

    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await engine.StopAsync();

    log.Events.Select(e => e.State).Should().Equal(EngineState.Starting, EngineState.Ready, EngineState.Stopped);
    log.Events.Should().OnlyContain(e => e.Error == null);
  }

  /// <summary>
  /// Verifies that a Faulted engine whose task's work threw carries an <see cref="EngineTaskNotReadyException"/> for
  /// that task, with the thrown exception inside.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_WorkThrows_ErrorHasTheTaskAndItsException()
  {
    var thrown = new TimeoutException("The database did not answer.");
    var engine = new TasksEngine(e => e.Task("Load", _ => throw thrown));

    var faulted = await FaultedAsync(engine);

    var error = faulted.Error.Should().BeOfType<EngineTaskNotReadyException>().Subject;
    error.TaskName.Should().Be("Load");
    error.State.Should().Be(EngineTaskState.Failed);
    error.InnerException.Should().BeSameAs(thrown);
    faulted.EngineName.Should().Be(engine.EngineName);
  }

  /// <summary>
  /// Verifies that a Faulted engine whose task timed out carries the task in state TimedOut, with no inner exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_TaskTimesOut_ErrorHasTimedOutAndNoInnerException()
  {
    var engine = new TasksEngine(e =>
      e.Task("Load", token => Task.Delay(Timeout.Infinite, token)).SetTimeOut(TimeSpan.FromMilliseconds(50)));

    var faulted = await FaultedAsync(engine);

    var error = faulted.Error.Should().BeOfType<EngineTaskNotReadyException>().Subject;
    error.TaskName.Should().Be("Load");
    error.State.Should().Be(EngineTaskState.TimedOut);
    error.InnerException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that, of several failed required tasks, the error names the one whose run ended first: not a dependent
  /// that failed with it, nor a task that failed later, though both come before it in the set.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_SeveralFailed_ErrorNamesTheTaskThatFailedFirst()
  {
    var ct = TestContext.Current.CancellationToken;
    var thrown = new InvalidOperationException("A failed.");
    var releaseLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new TasksEngine(e =>
    {
      // Added before A, so a search in the set's order finds them first.
      var dependent = e.Task("Dependent", _ => Task.CompletedTask);
      e.Task("Later", async _ =>
      {
        await releaseLater.Task;
        throw new InvalidOperationException("Later failed.");
      });
      var a = e.Task("A", _ => throw thrown);
      dependent.AddDependsOn(a);
    }) { Hold = true };
    var log = new EventLog(engine);

    await engine.StartAsync();
    await WaitUntilAsync(() => engine.Tasks["Dependent"].State == EngineTaskState.DependencyFailed, "A fails");
    releaseLater.SetResult();
    await WaitUntilAsync(() => engine.Tasks["Later"].State == EngineTaskState.Failed, "Later throws");
    engine.Hold = false;
    engine.Recompute();
    var faulted = await log.Faulted.Task.WaitAsync(MustComplete, ct);

    var error = faulted.Error.Should().BeOfType<EngineTaskNotReadyException>().Subject;
    error.TaskName.Should().Be("A");
    error.InnerException.Should().BeSameAs(thrown);
  }

  /// <summary>
  /// Verifies that an engine whose prepared restart is given up is Faulted with an
  /// <see cref="InvalidOperationException"/> carrying the reason.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_RestartAbandoned_ErrorHasTheReason()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new TasksEngine(e => e.Task("Load", _ => Task.CompletedTask));
    var log = new EventLog(engine);
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    await engine.ResetAllTasksAsync().WaitAsync(MustComplete, ct);

    engine.AbandonRestart("its context failed");

    var faulted = await log.Faulted.Task.WaitAsync(MustComplete, ct);
    faulted.Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("its context failed");
  }

  /// <summary>
  /// Verifies that a Faulted engine whose set-up threw carries that exception: there is no task to describe.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_SetUpThrows_ErrorIsTheSetUpException()
  {
    var thrown = new InvalidOperationException("The set-up failed.");
    var engine = new TasksEngine(_ => throw thrown);
    var log = new EventLog(engine);

    var start = () => engine.StartAsync();

    await start.Should().ThrowAsync<InvalidOperationException>();
    var faulted = await log.Faulted.Task.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    faulted.Error.Should().BeSameAs(thrown);
  }
}
