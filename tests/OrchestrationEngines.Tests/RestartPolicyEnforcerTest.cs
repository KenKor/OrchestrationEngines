using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the restart policy enforcer: the wait between restarts, the limit and its window, and policy
/// validation. The enforcer runs on a <see cref="ManualTimeProvider"/>, so no test waits for real time.
/// </summary>
public class RestartPolicyEnforcerTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  private static EngineTask NewTask(Func<CancellationToken, Task> workAsync) =>
    new("Load", workAsync, requiredForRunning: true, NullLogger.Instance, CancellationToken.None);

  private static async Task RunToEndAsync(EngineTask task)
  {
    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
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
  /// Verifies that the wait before each restart doubles, from the initial backoff up to the maximum backoff.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Invoke_WaitDoublesUpToMaxBackoff()
  {
    var clock = new ManualTimeProvider();
    var enforcer = new RestartPolicyEnforcer(new RestartPolicy
    {
      MaxRestartsInWindow = 4,
      Window = TimeSpan.FromMinutes(10),
      InitialBackoff = TimeSpan.FromSeconds(1),
      MaxBackoff = TimeSpan.FromSeconds(4),
    }, clock);
    var waits = new List<TimeSpan>();
    var task = NewTask(_ => throw new InvalidOperationException("Every run fails."))
      .SetRecovery(new CustomRestarter(async (et, token) =>
      {
        var wait = enforcer.CurrentBackoff;
        waits.Add(wait);
        var restart = enforcer.Invoke(et, token);
        // Let the backoff pass at once.
        clock.Advance(wait);
        await restart;
      }));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.RecoveryFailed);
    waits.Should().Equal(
      TimeSpan.FromSeconds(1),
      TimeSpan.FromSeconds(2),
      TimeSpan.FromSeconds(4),
      TimeSpan.FromSeconds(4),
      TimeSpan.FromSeconds(4));
  }

  /// <summary>
  /// Verifies that the backoff ends when the token the enforcer is given is cancelled, also while the task's own token
  /// is live: after a reset during a recovery, the task's token is already the next run's, which cancelling the recovery
  /// does not reach.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Invoke_BackoffObservesTheGivenToken()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var enforcer = new RestartPolicyEnforcer(new RestartPolicy
    {
      InitialBackoff = TimeSpan.FromMinutes(1),
      MaxBackoff = TimeSpan.FromMinutes(1),
    }, clock);
    using var recovery = new CancellationTokenSource();
    var invoked = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = NewTask(_ => throw new InvalidOperationException("The run fails."))
      .SetRecovery(new CustomRestarter((et, _) =>
      {
        var restart = enforcer.Invoke(et, recovery.Token);
        invoked.TrySetResult(restart);
        return restart;
      }));
    await task.FireAsync(EngineTaskTrigger.Start);
    var backoff = await invoked.Task.WaitAsync(MustComplete, ct);
    task.CancellationToken.IsCancellationRequested.Should().BeFalse("the task's own token is live during the backoff");

    recovery.Cancel();
    var ended = await Record.ExceptionAsync(() => backoff.WaitAsync(MustComplete, ct));

    ended.Should().BeAssignableTo<OperationCanceledException>("the backoff ends with the given token, not when it times out");
  }

  /// <summary>
  /// Verifies that a restart waits for its backoff on the enforcer's clock: it does not run again until that much
  /// time has passed.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Invoke_WaitsForBackoffOnItsClock()
  {
    var clock = new ManualTimeProvider();
    var runs = 0;
    var task = NewTask(_ => Interlocked.Increment(ref runs) == 1 ? throw new InvalidOperationException("First run fails.") : Task.CompletedTask)
      .SetRecovery(new RestartPolicy
      {
        MaxRestartsInWindow = 5,
        Window = TimeSpan.FromMinutes(10),
        InitialBackoff = TimeSpan.FromSeconds(10),
        MaxBackoff = TimeSpan.FromSeconds(10),
      }.GetEnforcer(clock));

    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => clock.WaitingTimers == 1, "the restart must wait for its backoff");

    clock.Advance(TimeSpan.FromSeconds(9));
    await Task.Delay(100, TestContext.Current.CancellationToken);
    task.State.Should().Be(EngineTaskState.Recovering, "the backoff has not passed yet");
    Volatile.Read(ref runs).Should().Be(1);

    clock.Advance(TimeSpan.FromSeconds(1));
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    Volatile.Read(ref runs).Should().Be(2);
  }

  /// <summary>
  /// Verifies that the jitter shortens the wait by its share of a random fraction of the backoff (#59, item 3): with a
  /// random draw of 0.5, a 10-second backoff waits 10 seconds without jitter, 7.5 with half, and 5 with full jitter.
  /// The next backoff still doubles from the backoff without jitter.
  /// </summary>
  [Theory]
  [InlineData(0.0, 10_000)]
  [InlineData(0.5, 7_500)]
  [InlineData(1.0, 5_000)]
  public async Task RestartPolicyEnforcer_Invoke_JitterShortensTheWait(double jitter, int expectedWaitMs)
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var enforcer = new RestartPolicyEnforcer(new RestartPolicy
    {
      InitialBackoff = TimeSpan.FromSeconds(10),
      MaxBackoff = TimeSpan.FromSeconds(40),
      Jitter = jitter,
    }, clock, nextRandom: () => 0.5);
    var invoked = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = NewTask(_ => throw new InvalidOperationException("The run fails."))
      .SetRecovery(new CustomRestarter((et, token) =>
      {
        var restart = enforcer.Invoke(et, token);
        invoked.TrySetResult(restart);
        return restart;
      }));
    await task.FireAsync(EngineTaskTrigger.Start);
    var backoff = await invoked.Task.WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => clock.WaitingTimers == 1, "the restart must wait for its backoff");

    clock.Advance(TimeSpan.FromMilliseconds(expectedWaitMs - 1));
    await Task.Delay(100, ct);
    backoff.IsCompleted.Should().BeFalse("the wait has not passed yet");
    clock.Advance(TimeSpan.FromMilliseconds(1));
    await backoff.WaitAsync(MustComplete, ct);

    enforcer.CurrentBackoff.Should().Be(TimeSpan.FromSeconds(20), "the backoff doubles without the jitter");
  }

  /// <summary>
  /// Verifies that the jitter is part of a policy's equality and hash code.
  /// </summary>
  [Fact]
  public void RestartPolicy_Equals_ComparesTheJitter()
  {
    var half = new RestartPolicy { Jitter = 0.5 };

    half.Should().Be(new RestartPolicy { Jitter = 0.5 });
    half.GetHashCode().Should().Be(new RestartPolicy { Jitter = 0.5 }.GetHashCode());
    half.Should().NotBe(new RestartPolicy { Jitter = 0.25 });
    new RestartPolicy().Jitter.Should().Be(0, "no jitter unless the host sets one");
  }

  /// <summary>
  /// Verifies that with full jitter and the enforcer's own random source, the wait never exceeds the backoff.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Invoke_FullJitter_WaitsNoLongerThanTheBackoff()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var enforcer = new RestartPolicyEnforcer(new RestartPolicy
    {
      InitialBackoff = TimeSpan.FromSeconds(10),
      MaxBackoff = TimeSpan.FromSeconds(10),
      Jitter = 1,
    }, clock);
    var invoked = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = NewTask(_ => throw new InvalidOperationException("The run fails."))
      .SetRecovery(new CustomRestarter((et, token) =>
      {
        var restart = enforcer.Invoke(et, token);
        invoked.TrySetResult(restart);
        return restart;
      }));
    await task.FireAsync(EngineTaskTrigger.Start);
    var backoff = await invoked.Task.WaitAsync(MustComplete, ct);

    clock.Advance(TimeSpan.FromSeconds(10));

    await backoff.WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that restarts older than the window no longer count towards the limit: the third restart would exceed
  /// a limit of two, but the first two lie outside the window by then.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Invoke_RestartsOutsideWindow_DoNotCount()
  {
    var clock = new ManualTimeProvider();
    var enforcer = new RestartPolicyEnforcer(new RestartPolicy
    {
      MaxRestartsInWindow = 2,
      Window = TimeSpan.FromMinutes(1),
      InitialBackoff = TimeSpan.Zero,
      MaxBackoff = TimeSpan.Zero,
    }, clock);
    var runs = 0;
    var restarts = 0;
    var task = NewTask(_ => ++runs <= 3 ? throw new InvalidOperationException("Fails.") : Task.CompletedTask)
      .SetRecovery(new CustomRestarter((et, token) =>
      {
        if (++restarts == 3)
          clock.Advance(TimeSpan.FromMinutes(2));
        return enforcer.Invoke(et, token);
      }));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    runs.Should().Be(4);
  }

  /// <summary>
  /// Verifies that restarts within the window do count: the third restart exceeds a limit of two.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Invoke_RestartsWithinWindow_ExceedLimit()
  {
    var clock = new ManualTimeProvider();
    var enforcer = new RestartPolicyEnforcer(new RestartPolicy
    {
      MaxRestartsInWindow = 2,
      Window = TimeSpan.FromMinutes(1),
      InitialBackoff = TimeSpan.Zero,
      MaxBackoff = TimeSpan.Zero,
    }, clock);
    var runs = 0;
    var restarts = 0;
    var task = NewTask(_ => ++runs <= 3 ? throw new InvalidOperationException("Fails.") : Task.CompletedTask)
      .SetRecovery(new CustomRestarter((et, token) =>
      {
        if (++restarts == 3)
          clock.Advance(TimeSpan.FromSeconds(30));
        return enforcer.Invoke(et, token);
      }));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.RecoveryFailed);
    runs.Should().Be(3);
  }

  /// <summary>
  /// Verifies that a load that takes long to fail (a minute per attempt, so older restarts leave a 3 minute window of 5)
  /// still gives up after the restarts it may make in a row; the window alone would retry it forever.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Invoke_SlowFailures_GiveUpAfterConsecutiveRestarts()
  {
    var clock = new ManualTimeProvider();
    var runs = 0;
    var task = NewTask(_ =>
      {
        // Each attempt takes a minute to fail. Succeed after many attempts, so that the test ends also when the
        // policy never gives up.
        clock.Advance(TimeSpan.FromMinutes(1));
        return ++runs <= 30 ? throw new InvalidOperationException("The load timed out.") : Task.CompletedTask;
      })
      .SetRecovery(new RestartPolicy
      {
        MaxRestartsInWindow = 5,
        Window = TimeSpan.FromMinutes(3),
        MaxConsecutiveRestarts = 5,
        InitialBackoff = TimeSpan.Zero,
        MaxBackoff = TimeSpan.Zero,
      }.GetEnforcer(clock));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.RecoveryFailed);
    runs.Should().Be(6, "the first run and the five restarts in a row the policy allows");
    task.FailReason.Should().Contain("restarts in a row");
  }

  /// <summary>
  /// Verifies that a reset (as after a successful run, or a successful poll of a change monitor) starts counting the
  /// restarts in a row again.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Reset_StartsCountingRestartsInARowAgain()
  {
    var enforcer = new RestartPolicyEnforcer(new RestartPolicy
    {
      MaxConsecutiveRestarts = 3,
      MaxRestartsInWindow = 0,
      InitialBackoff = TimeSpan.Zero,
      MaxBackoff = TimeSpan.Zero,
    }, new ManualTimeProvider());
    var runs = 0;
    var task = NewTask(_ => ++runs <= 5 ? throw new InvalidOperationException("Fails.") : Task.CompletedTask)
      .SetRecovery(new CustomRestarter((et, token) =>
      {
        // Reset halfway, as a successful poll does.
        if (enforcer.ConsecutiveRestarts == 2)
          enforcer.Reset();
        return enforcer.Invoke(et, token);
      }));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess, "the reset kept the restarts in a row below 3");
    runs.Should().Be(6);
  }

  /// <summary>
  /// Verifies the restarts in a row of the built-in policies; a policy created without it does not check them.
  /// </summary>
  [Fact]
  public void RestartPolicy_Presets_LimitTheRestartsInARow()
  {
    RestartPolicy.Default.MaxConsecutiveRestarts.Should().Be(5);
    RestartPolicy.Critical.MaxConsecutiveRestarts.Should().Be(3);
    RestartPolicy.External.MaxConsecutiveRestarts.Should().Be(10);
    RestartPolicy.Unlimited.MaxConsecutiveRestarts.Should().Be(0);
    new RestartPolicy().MaxConsecutiveRestarts.Should().Be(0);
  }

  /// <summary>
  /// Verifies that a policy without a restart limit (0) keeps restarting a failing task until it succeeds.
  /// </summary>
  [Fact]
  public async Task RestartPolicyEnforcer_Invoke_NoLimit_NeverGivesUp()
  {
    var runs = 0;
    var task = NewTask(_ => ++runs <= 20 ? throw new InvalidOperationException("Fails.") : Task.CompletedTask)
      .SetRecovery(new RestartPolicy
      {
        MaxRestartsInWindow = 0,
        Window = TimeSpan.Zero,
        InitialBackoff = TimeSpan.Zero,
        MaxBackoff = TimeSpan.Zero,
      }.GetEnforcer(new ManualTimeProvider()));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    runs.Should().Be(21);
  }

  /// <summary>
  /// Verifies that the enforcer rejects a policy with a negative limit, a negative window or backoff, a maximum
  /// backoff below the initial one, or a jitter outside 0..1.
  /// </summary>
  [Fact]
  public void RestartPolicyEnforcer_Constructor_InvalidPolicy_Throws()
  {
    var invalidPolicies = new[]
    {
      new RestartPolicy { MaxRestartsInWindow = -1 },
      new RestartPolicy { MaxConsecutiveRestarts = -1 },
      new RestartPolicy { Window = TimeSpan.FromSeconds(-1) },
      new RestartPolicy { InitialBackoff = TimeSpan.FromSeconds(-1) },
      new RestartPolicy { InitialBackoff = TimeSpan.FromSeconds(5), MaxBackoff = TimeSpan.FromSeconds(1) },
      new RestartPolicy { Jitter = -0.1 },
      new RestartPolicy { Jitter = 1.1 },
      new RestartPolicy { Jitter = double.NaN },
    };

    foreach (var policy in invalidPolicies)
    {
      var create = () => new RestartPolicyEnforcer(policy);
      create.Should().Throw<ArgumentOutOfRangeException>();
    }
  }
}
