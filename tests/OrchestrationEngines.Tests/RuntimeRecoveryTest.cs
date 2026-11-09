using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for recovery as a host uses it: an engine that wraps an external process (a scripting kernel, a
/// database) fails to start now and then, and its background watcher fails while the engine is Ready. The
/// descriptor's recovery brings the engine up, the task's recovery brings the watcher back, and the engine's state
/// tells the host what it may use.
/// </summary>
public class RuntimeRecoveryTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

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
  /// An engine shaped like a process host: a required start task that fails the first <paramref name="failedStarts"/>
  /// times, and a background watcher (not required for running) that fails the first <paramref name="failedWatches"/>
  /// times and then keeps running until the engine stops.
  /// </summary>
  private sealed class KernelEngine(int failedStarts = 0, int failedWatches = 0, RestartPolicy watcherPolicy = null)
    : BaseEngine(NullLogger.Instance)
  {
    private int _starts;
    private int _watches;

    public int Starts => Volatile.Read(ref _starts);

    public int Watches => Volatile.Read(ref _watches);

    public EngineTask Watcher => Tasks["WatchKernel"];

    protected override void SetUpTasks()
    {
      var start = CreateTask("StartKernel", _ =>
      {
        if (Interlocked.Increment(ref _starts) <= failedStarts)
          throw new InvalidOperationException("The kernel process exited at once.");
        return Task.CompletedTask;
      });
      var watch = CreateTask("WatchKernel", async token =>
      {
        if (Interlocked.Increment(ref _watches) <= failedWatches)
          throw new InvalidOperationException("Lost the kernel's output pipe.");
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
      }, requiredForRunning: false);
      watch.AddDependsOn(start);
      if (watcherPolicy != null)
        watch.SetRecovery(watcherPolicy);
    }
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(RuntimeRecoveryTest), NullLogger.Instance, useDispatcher: false);

  private static EngineDescriptor Register(EngineCoordinator coordinator, string name, IEngine engine) =>
    coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));

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
  /// Verifies that the recovery set on the descriptor starts an engine again whose load failed, and that the
  /// coordinator is Running once a start succeeds.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_DescriptorRecovery_StartFailsTwice_EngineEndsReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedStarts: 2);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel).SetRecoveryHelper(NoWait(3));

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => kernel.State == EngineState.Ready, "the third start succeeds");

    kernel.Starts.Should().Be(3);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
    (await coordinator.GetEngineAsync("Kernel", throwErrorIfNotFound: true, token: ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(kernel);
  }

  /// <summary>
  /// Verifies that an engine whose starts keep failing ends Faulted once the descriptor's recovery gives up, after
  /// exactly the allowed number of restarts, and that the coordinator does not report Running.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_DescriptorRecovery_StartKeepsFailing_EngineEndsFaultedAfterTheLimit()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedStarts: int.MaxValue);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel).SetRecoveryHelper(NoWait(2));

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => kernel.State == EngineState.Faulted, "the recovery gives up");

    kernel.Starts.Should().Be(3, "one start and two restarts");
    coordinator.State.Should().NotBe(EngineCoordinatorState.Running);
    (await coordinator.GetEngineAsync("Kernel", throwErrorIfNotFound: true, token: ct).WaitAsync(MustComplete, ct)).Should().BeNull();
  }

  /// <summary>
  /// Verifies that the restarts in a row stop a kick-start whose engine keeps faulting after its context task
  /// succeeded, also without a restart window: the count must not start over during the kick-start's restarts, or the
  /// engine would never be given up on.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_DescriptorRecovery_StartKeepsFailing_StopsAfterTheConsecutiveRestarts()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedStarts: int.MaxValue);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel).SetRecoveryHelper(new RestartPolicy
    {
      MaxConsecutiveRestarts = 2,
      InitialBackoff = TimeSpan.Zero,
      MaxBackoff = TimeSpan.Zero,
    });

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => kernel.State == EngineState.Faulted, "the recovery gives up");
    // Give a restart that should not happen the time to happen.
    await Task.Delay(200, ct);

    kernel.Starts.Should().Be(3, "one start and two restarts");
    kernel.State.Should().Be(EngineState.Faulted);
    coordinator.State.Should().NotBe(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that an engine registered without any recovery setting recovers by the descriptor's default
  /// <see cref="EngineDescriptor.RestartPolicy"/>: its failed first start is retried after the backoff (1 s), and the
  /// engine becomes Ready.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_DefaultRestartPolicy_StartFailsOnce_Recovers()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedStarts: 1);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel);

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => kernel.State == EngineState.Ready, "the default policy starts it again");

    kernel.Starts.Should().Be(2);
  }

  /// <summary>
  /// Verifies that the descriptor's <see cref="EngineDescriptor.RestartPolicy"/> applies without
  /// <see cref="EngineDescriptor.SetRecoveryHelper(RestartPolicy)"/>, and that the engine faults once it gives up.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_DescriptorRestartPolicy_StartKeepsFailing_FaultsWhenThePolicyGivesUp()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedStarts: int.MaxValue);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel).RestartPolicy = new RestartPolicy
    {
      MaxConsecutiveRestarts = 2,
      InitialBackoff = TimeSpan.Zero,
      MaxBackoff = TimeSpan.Zero,
    };

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => kernel.State == EngineState.Faulted, "the policy gives up");
    // Give a restart that should not happen the time to happen.
    await Task.Delay(200, ct);

    kernel.Starts.Should().Be(3, "one start and two restarts");
  }

  /// <summary>
  /// Verifies that a <see cref="EngineDescriptor.RestartPolicy"/> of null turns recovery off: a failed start faults the
  /// engine at once.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_NullRestartPolicy_StartFails_FaultsAtOnce()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedStarts: 1);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel).RestartPolicy = null;

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => kernel.State == EngineState.Faulted, "the first start fails");
    // Give a restart that should not happen the time to happen.
    await Task.Delay(200, ct);

    kernel.Starts.Should().Be(1);
    kernel.State.Should().Be(EngineState.Faulted);
  }

  /// <summary>
  /// Verifies that a recovery helper set explicitly wins over the descriptor's
  /// <see cref="EngineDescriptor.RestartPolicy"/>: the helper's single restart is all the engine gets.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ExplicitRecoveryHelper_OverridesTheRestartPolicy()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedStarts: int.MaxValue);
    var coordinator = CreateCoordinator();
    var descriptor = Register(coordinator, "Kernel", kernel);
    descriptor.RestartPolicy = NoWait(10);
    descriptor.SetRecoveryHelper(new RestartPolicy
    {
      MaxConsecutiveRestarts = 1,
      InitialBackoff = TimeSpan.Zero,
      MaxBackoff = TimeSpan.Zero,
    });

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => kernel.State == EngineState.Faulted, "the helper gives up");
    await Task.Delay(200, ct);

    kernel.Starts.Should().Be(2, "one start and the helper's one restart");
  }

  /// <summary>
  /// Verifies that a background task with recovery runs again after it failed, and that the engine stays Ready
  /// throughout: a watcher's hiccup must not take the engine away from the host.
  /// </summary>
  [Fact]
  public async Task BaseEngine_BackgroundTaskRecovery_WatcherFailsTwice_EngineStaysReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedWatches: 2, watcherPolicy: NoWait(3));
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await WaitUntilAsync(() => kernel.Watches == 3 && kernel.Watcher.State == EngineTaskState.Running, "the third watch runs");

    kernel.State.Should().Be(EngineState.Ready);
    kernel.Starts.Should().Be(1, "only the watcher ran again");
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Documents that a background task whose recovery is exhausted leaves the engine Ready: the engine's state follows
  /// its required tasks only. A host that must know its watcher is gone looks at the task, or has the watcher reset
  /// the engine before it gives up.
  /// </summary>
  [Fact]
  public async Task BaseEngine_BackgroundTaskRecovery_Exhausted_EngineStaysReadyAndTheTaskIsFailed()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine(failedWatches: int.MaxValue, watcherPolicy: NoWait(2));
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await WaitUntilAsync(() => kernel.Watcher.IsFailed, "the watcher's recovery gives up");

    kernel.Watches.Should().Be(3, "one run and two restarts");
    kernel.State.Should().Be(EngineState.Ready);
    kernel.Tasks.Values.Where(t => t.IsFailed).Select(t => t.Name).Should().Equal("WatchKernel");
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that an engine reset by its host after its process died (the way a watcher reports it) loads again,
  /// is Ready afterwards with its watcher running, and that the coordinator stays Running while it does.
  /// </summary>
  [Fact]
  public async Task BaseEngine_TriggerResetAsync_AfterTheKernelDied_StartsAgainAndStaysInTheCoordinator()
  {
    var ct = TestContext.Current.CancellationToken;
    var kernel = new KernelEngine();
    var coordinator = CreateCoordinator();
    Register(coordinator, "Kernel", kernel);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    // A task is Running before its work is scheduled; wait for the work itself, or the reset may cancel the first
    // watch before it counted.
    await WaitUntilAsync(() => kernel.Watches == 1 && kernel.Watcher.State == EngineTaskState.Running, "the watcher runs");

    await kernel.TriggerResetAsync().WaitAsync(MustComplete, ct);
    var engine = await coordinator.GetEngineAsync("Kernel", throwErrorIfNotFound: true, token: ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => kernel.Watches == 2 && kernel.Watcher.State == EngineTaskState.Running, "the watcher runs again");

    engine.Should().BeSameAs(kernel);
    kernel.Starts.Should().Be(2);
    kernel.Watches.Should().Be(2);
    kernel.State.Should().Be(EngineState.Ready);
    // The coordinator learns of state changes through events, a moment after the engines made them.
    await WaitUntilAsync(() => coordinator.State == EngineCoordinatorState.Running, "the coordinator stays Running");
  }
}
