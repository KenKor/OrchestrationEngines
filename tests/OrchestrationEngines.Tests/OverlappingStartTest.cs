using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for two calls of <see cref="BaseEngine.StartAsync"/> on one engine at once: the second start waits
/// until the first has ended, and then runs as it would have run alone.
/// </summary>
public class OverlappingStartTest
{
  /// <summary>
  /// An engine that runs the set-ups the test queues, one per start, and whose first set-up can be held.
  /// </summary>
  private sealed class OverlapEngine : BaseEngine
  {
    public OverlapEngine() : base(NullLogger.Instance) => Context = new NoContext();

    public ConcurrentQueue<Action<OverlapEngine>> SetUps { get; } = new();

    public int SetUpCount => Volatile.Read(ref _setUpCount);

    private int _setUpCount;

    public ProbeTask Probe(string name, Func<CancellationToken, Task> work) =>
      (ProbeTask)AddTask(name, new ProbeTask(name, work, engineToken: CancellationToken));

    public void WaitFor(Task work) => StartAfter(work);

    protected override void SetUpTasks()
    {
      Interlocked.Increment(ref _setUpCount);
      SetUps.TryDequeue(out var setUp).Should().BeTrue("the test queues a set-up for every start");
      setUp(this);
    }
  }

  /// <summary>
  /// A set-up that signals <paramref name="entered"/>, waits for <paramref name="release"/>, and then runs
  /// <paramref name="then"/>.
  /// </summary>
  private static Action<OverlapEngine> Held(ManualResetEventSlim entered, ManualResetEventSlim release,
    Action<OverlapEngine> then) => e =>
  {
    entered.Set();
    release.Wait(MustComplete).Should().BeTrue("the test releases the held set-up");
    then(e);
  };

  /// <summary>
  /// Verifies that the work handed on to the next set is waited for when a second start comes during the first
  /// set-up: no task of either set runs before it has ended. Without the fix, the first set-up took the work and the
  /// second set-up found none, so its set ran next to the work.
  /// </summary>
  [Fact]
  public async Task StartAsync_SecondStartDuringASetUp_NoTaskRunsBeforeTheHandedOnWorkHasEnded()
  {
    var ct = TestContext.Current.CancellationToken;
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var handedOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var early = 0;
    Task Run(CancellationToken _)
    {
      if (!handedOn.Task.IsCompleted)
        Interlocked.Increment(ref early);
      return Task.CompletedTask;
    }
    var engine = new OverlapEngine();
    engine.SetUps.Enqueue(Held(entered, release, e => e.Probe("A", Run)));
    engine.SetUps.Enqueue(e => e.Probe("A", Run));
    engine.WaitFor(handedOn.Task);

    var first = Task.Run(engine.StartAsync, ct);
    entered.Wait(MustComplete, ct).Should().BeTrue("the first set-up starts");
    var second = engine.StartAsync();
    release.Set();
    await first.WaitAsync(MustComplete, ct);
    // Whatever the second start would run while the work is running, it runs within this time.
    await Task.Delay(200, ct);
    Volatile.Read(ref early).Should().Be(0, "the handed-on work has not ended yet");

    handedOn.SetResult();
    await second.WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.SetUpCount.Should().Be(2);
    engine.State.Should().Be(EngineState.Ready);
    Volatile.Read(ref early).Should().Be(0);
  }

  /// <summary>
  /// Verifies that a first set-up that fails while a second start is waiting does not take the second start's set
  /// with it: the second set is published and keeps its token sources, and the failed set's are released. Without the
  /// fix, the second set-up published its set during the first, and the first's failure replaced it with an empty
  /// set whose predecessor's sources were never released.
  /// </summary>
  [Fact]
  public async Task StartAsync_FirstSetUpFailsDuringASecondStart_TheSecondSetIsPublished()
  {
    var ct = TestContext.Current.CancellationToken;
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var thrown = new InvalidOperationException("First set-up failed.");
    CancellationToken failedToken = default, publishedToken = default;
    ProbeTask published = null;
    var engine = new OverlapEngine();
    engine.SetUps.Enqueue(Held(entered, release, e =>
    {
      failedToken = e.Probe("B", _ => Task.CompletedTask).Source.Token;
      throw thrown;
    }));
    engine.SetUps.Enqueue(e =>
    {
      published = e.Probe("A", _ => Task.CompletedTask);
      publishedToken = published.Source.Token;
    });

    var first = Task.Run(engine.StartAsync, ct);
    entered.Wait(MustComplete, ct).Should().BeTrue("the first set-up starts");
    var second = engine.StartAsync();
    release.Set();

    (await Record.ExceptionAsync(() => first.WaitAsync(MustComplete, ct))).Should().BeSameAs(thrown);
    await second.WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Ready);
    engine.Tasks.Keys.Should().BeEquivalentTo(["A"], "the second start's set is the one published");
    engine.Tasks["A"].Should().BeSameAs(published);
    Tokens.IsDisposed(publishedToken).Should().BeFalse("the published set keeps its token sources");
    Tokens.IsDisposed(failedToken).Should().BeTrue("the failed set-up's tasks never run");
  }

  /// <summary>
  /// Verifies that a stop while a second start waits for the first ends the second start without a set-up: stopping
  /// is final. Without the fix, the second start set up its own set at once, during the first.
  /// </summary>
  [Fact]
  public async Task StartAsync_StoppedWhileASecondStartWaits_TheSecondStartDoesNoSetUp()
  {
    var ct = TestContext.Current.CancellationToken;
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var engine = new OverlapEngine();
    engine.SetUps.Enqueue(Held(entered, release, e => e.Probe("A", _ => Task.CompletedTask)));
    engine.SetUps.Enqueue(e => e.Probe("A", _ => Task.CompletedTask));

    var first = Task.Run(engine.StartAsync, ct);
    entered.Wait(MustComplete, ct).Should().BeTrue("the first set-up starts");
    var second = engine.StartAsync();
    await engine.StopAsync().WaitAsync(MustComplete, ct);
    release.Set();

    await first.WaitAsync(MustComplete, ct);
    await second.WaitAsync(MustComplete, ct);

    engine.SetUpCount.Should().Be(1, "the start that waited came after the stop");
    engine.State.Should().Be(EngineState.Stopped);
  }
}
