using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for how the coordinator starts and waits for engines with the dispatcher on: a wait must not hold the
/// dispatcher, so that other calls are not queued behind an engine that takes long to load.
/// </summary>
public class DispatcherLifecycleTest
{
  /// <summary>
  /// Time given to a call that must complete.
  /// </summary>
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(10);

  /// <summary>
  /// Engine with a single task that runs until the gate is opened.
  /// </summary>
  private sealed class GatedEngine(Task gate) : BaseEngine(NullLogger.Instance)
  {
    private int _workStarted;
    private int _startCalled;

    public bool WorkStarted => Volatile.Read(ref _workStarted) == 1;

    public bool StartCalled => Volatile.Read(ref _startCalled) == 1;

    public override Task StartAsync()
    {
      Volatile.Write(ref _startCalled, 1);
      return base.StartAsync();
    }

    protected override void SetUpTasks() => CreateTask("Work", async token =>
    {
      Volatile.Write(ref _workStarted, 1);
      await Task.WhenAny(gate, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
      token.ThrowIfCancellationRequested();
    });
  }

  /// <summary>
  /// Engine whose only task always fails.
  /// </summary>
  private sealed class FailingEngine(Action onRun) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Work", _ =>
    {
      onRun();
      throw new InvalidOperationException("The engine cannot load.");
    });
  }

  /// <summary>
  /// Engine that records when it releases its resources.
  /// </summary>
  private sealed class ReleasingEngine(ConcurrentQueue<string> events) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Work", _ => Task.CompletedTask);

    protected override void ReleaseResources()
    {
      events.Enqueue("released");
      base.ReleaseResources();
    }
  }

  private static EngineCoordinator CreateCoordinator(bool useDispatcher) =>
    new("DispatcherLifecycleTest", NullLogger.Instance, useDispatcher);

  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow + MustComplete;
    while (!condition())
    {
      if (DateTime.UtcNow > deadline)
        throw new TimeoutException("Condition not met in time.");
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }
  }

  /// <summary>
  /// Verifies that waiting until all engines are ready does not hold the dispatcher: a bounded lookup made meanwhile is
  /// answered (with null, the engine is still loading) instead of queueing until the load is done.
  /// </summary>
  [Fact]
  public async Task EnsureReadyAsync_DoesNotHoldTheDispatcher_WhileEnginesLoad()
  {
    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    GatedEngine slow = null;
    var coordinator = CreateCoordinator(useDispatcher: true);
    coordinator.RegisterEngine("Slow", _ => Task.FromResult<IEngine>(slow = new GatedEngine(gate.Task)));

    try
    {
      var ready = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
      await WaitUntilAsync(() => slow != null && slow.WorkStarted);

      var probe = coordinator.GetEngineAsync("Slow", 50, TestContext.Current.CancellationToken);
      (await probe.WaitAsync(MustComplete, TestContext.Current.CancellationToken)).Should().BeNull();
      ready.IsCompleted.Should().BeFalse();

      gate.SetResult(true);
      await ready.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
      coordinator.GetEngineIfReady("Slow").Should().BeSameAs(slow);
    }
    finally
    {
      gate.TrySetResult(true);
      await coordinator.ShutdownAsync(TestContext.Current.CancellationToken);
      coordinator.Dispose();
    }
  }

  /// <summary>
  /// Verifies that a lookup that gives up after its time-out returns null without stopping the engine: it completes
  /// its load, and a later lookup gets it.
  /// </summary>
  [Fact]
  public async Task GetEngineAsync_WithTimeOut_ReturnsNullWhileLoading_AndTheEngineKeepsLoading()
  {
    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    GatedEngine slow = null;
    var coordinator = CreateCoordinator(useDispatcher: true);
    coordinator.RegisterEngine("Slow", _ => Task.FromResult<IEngine>(slow = new GatedEngine(gate.Task)));

    try
    {
      var ready = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
      await WaitUntilAsync(() => slow != null && slow.WorkStarted);

      (await coordinator.GetEngineAsync("Slow", true, 100, TestContext.Current.CancellationToken)).Should().BeNull();

      gate.SetResult(true);
      var later = await coordinator.GetEngineAsync("Slow", true, (int)MustComplete.TotalMilliseconds, TestContext.Current.CancellationToken);
      later.Should().BeSameAs(slow);
      await ready.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    }
    finally
    {
      gate.TrySetResult(true);
      await coordinator.ShutdownAsync(TestContext.Current.CancellationToken);
      coordinator.Dispose();
    }
  }

  /// <summary>
  /// Verifies that a bounded lookup with <c>throwErrorIfNotFound</c> set throws for an engine that is not registered.
  /// </summary>
  [Fact]
  public async Task GetEngineAsync_WithTimeOut_ThrowsForAnUnknownEngine_WhenAsked()
  {
    var coordinator = CreateCoordinator(useDispatcher: true);
    coordinator.RegisterEngine("Known", _ => Task.FromResult<IEngine>(new GatedEngine(Task.CompletedTask)));

    try
    {
      var lookup = () => coordinator.GetEngineAsync("Unknown", true, 100, TestContext.Current.CancellationToken);
      await lookup.Should().ThrowAsync<Exception>();
      (await coordinator.GetEngineAsync("Unknown", false, 100, TestContext.Current.CancellationToken)).Should().BeNull();
    }
    finally
    {
      await coordinator.ShutdownAsync(TestContext.Current.CancellationToken);
      coordinator.Dispose();
    }
  }

  /// <summary>
  /// Verifies that a restart policy without a window counts failures in a row: the first run and two restarts, then the
  /// engine stays failed.
  /// </summary>
  [Fact]
  public async Task RestartPolicy_WithoutWindow_GivesUpAfterFailuresInARow()
  {
    var attempts = 0;
    Task ready = Task.CompletedTask;
    var coordinator = CreateCoordinator(useDispatcher: true);
    coordinator.RegisterEngine("Failing", _ =>
        Task.FromResult<IEngine>(new FailingEngine(() => Interlocked.Increment(ref attempts))))
      .SetRecoveryHelper(new RestartPolicy
      {
        MaxRestartsInWindow = 2,
        Window = TimeSpan.Zero,
        InitialBackoff = TimeSpan.FromMilliseconds(10),
        MaxBackoff = TimeSpan.FromMilliseconds(20),
      });

    try
    {
      ready = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
      await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 3);

      await Task.Delay(500, TestContext.Current.CancellationToken);
      Volatile.Read(ref attempts).Should().Be(3);
    }
    finally
    {
      try
      {
        await ready;
      }
      catch (Exception)
      {
        // The engine failed, as intended.
      }

      await coordinator.ShutdownAsync(TestContext.Current.CancellationToken);
      coordinator.Dispose();
    }
  }

  /// <summary>
  /// Verifies that an engine that depends on another is only kick-started once that engine is Ready.
  /// </summary>
  [Fact]
  public async Task KickStart_StartsADependentEngine_OnlyWhenItsDependencyIsReady()
  {
    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    GatedEngine underlying = null;
    GatedEngine dependent = null;
    var coordinator = CreateCoordinator(useDispatcher: false);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(underlying = new GatedEngine(gate.Task)));
    coordinator.RegisterEngine("B", _ => Task.FromResult<IEngine>(dependent = new GatedEngine(Task.CompletedTask)))
      .AddDependsOn("A");

    try
    {
      var ready = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
      await WaitUntilAsync(() => underlying != null && underlying.WorkStarted);

      await Task.Delay(200, TestContext.Current.CancellationToken);
      (dependent?.StartCalled ?? false).Should().BeFalse();

      gate.SetResult(true);
      await ready.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
      dependent.Should().NotBeNull();
      dependent.State.Should().Be(EngineState.Ready);
    }
    finally
    {
      gate.TrySetResult(true);
      await coordinator.ShutdownAsync(TestContext.Current.CancellationToken);
      coordinator.Dispose();
    }
  }

  /// <summary>
  /// Verifies that stopping an engine calls its <c>ReleaseResources</c> hook.
  /// </summary>
  [Fact]
  public async Task StopAsync_CallsReleaseResources()
  {
    var events = new ConcurrentQueue<string>();
    var engine = new ReleasingEngine(events);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    events.Should().BeEmpty();

    await engine.StopAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    events.Should().Equal("released");
  }

  /// <summary>
  /// Verifies that an underlying engine is found whatever the case of its name, as engine names are compared
  /// case-insensitively everywhere else (dependencies, the registered engines).
  /// </summary>
  [Fact]
  public void UnderlyingEngine_IsFoundCaseInsensitively()
  {
    var underlying = new GatedEngine(Task.CompletedTask);
    var context = new EngineContextWithDependencies(new NoContext());
    context.AddUnderlyingEngine("Network", underlying).Should().BeTrue();

    context.UnderlyingEngine("network").Should().BeSameAs(underlying);
    context.UnderlyingEngine("NETWORK").Should().BeSameAs(underlying);
    context.AddUnderlyingEngine("NETWORK", underlying).Should().BeFalse();
  }
}
