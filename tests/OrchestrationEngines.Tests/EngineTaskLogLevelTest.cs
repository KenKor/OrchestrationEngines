using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the levels an engine task logs its failures at: one failure is one error, not one for every task
/// that depends on it, and a cancellation is no error at all.
/// </summary>
/// <remarks>
/// A task writes its log lines in every build; the levels do the filtering. The cause of a failure is the one error,
/// entering the failed state and the routine lifecycle lines are Debug.
/// </remarks>
public class EngineTaskLogLevelTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  private static EngineTask NewTask(string name, Func<CancellationToken, Task> work, ILogger logger) =>
    new(name, work, requiredForRunning: true, logger, CancellationToken.None);

  private static Task Succeeds(CancellationToken _) => Task.CompletedTask;

  private static Task Throws(CancellationToken _) => throw new InvalidOperationException("No data.");

  private static Task IsCancelled(CancellationToken _) => throw new OperationCanceledException();

  /// <summary>
  /// Verifies that when a task fails, the tasks that depend on it log their failure at Debug, and that a task with two
  /// failed dependencies does not warn about the second FailDependency trigger.
  /// </summary>
  [Fact]
  public async Task FailedTask_Dependents_LogAtDebugWithoutUnhandledTriggerWarnings()
  {
    var logger = new FakeLogger();
    var root = NewTask("Root", Throws, logger);
    var left = NewTask("Left", Succeeds, logger).AddDependsOn(root);
    var right = NewTask("Right", Succeeds, logger).AddDependsOn(root);
    var bottom = NewTask("Bottom", Succeeds, logger).AddDependsOn(left).AddDependsOn(right);

    foreach (var task in new[] { bottom, left, right, root })
      await task.FireAsync(EngineTaskTrigger.Start);
    await Task.WhenAll(root.InnerTask, left.InnerTask, right.InnerTask, bottom.InnerTask)
      .WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    // The second FailDependency of Bottom is fired after Bottom completed; give it a moment to arrive.
    await Task.Delay(100, TestContext.Current.CancellationToken);

    var records = logger.Collector.GetSnapshot();
    records.Should().NotContain(r => r.Level == LogLevel.Warning, "a second failed dependency is benign");
    records.Where(r => r.Level == LogLevel.Error).Should().OnlyContain(r => r.Message.Contains("No data."),
      "only the failure itself is an error");
    records.Where(r => r.Message.Contains("Dependency failed.")).Should().OnlyContain(r => r.Level == LogLevel.Debug);
  }

  /// <summary>
  /// Verifies that starting a task whose dependency failed before it was started does not warn.
  /// </summary>
  [Fact]
  public async Task Start_AfterDependencyFailed_DoesNotWarn()
  {
    var logger = new FakeLogger();
    var root = NewTask("Root", Throws, logger);
    var dependent = NewTask("Dependent", Succeeds, logger).AddDependsOn(root);

    await root.FireAsync(EngineTaskTrigger.Start);
    await dependent.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await dependent.FireAsync(EngineTaskTrigger.Start);

    dependent.State.Should().Be(EngineTaskState.DependencyFailed);
    logger.Collector.GetSnapshot().Should().NotContain(r => r.Level == LogLevel.Warning);
  }

  /// <summary>
  /// Verifies that when the restart policy gives up, the error says what kept failing.
  /// </summary>
  [Fact]
  public async Task RestartLimitExceeded_ErrorNamesTheLastFailure()
  {
    var logger = new FakeLogger();
    var task = NewTask("Root", Throws, logger).SetRecovery(new RestartPolicy
    {
      MaxRestartsInWindow = 1,
      Window = TimeSpan.FromMinutes(1),
      InitialBackoff = TimeSpan.Zero,
      MaxBackoff = TimeSpan.Zero,
    });

    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    task.State.Should().Be(EngineTaskState.RecoveryFailed);
    task.FailReason.Should().StartWith("Restart limit exceeded for Root.").And.EndWith("(Recovering after an error: No data.)");
    logger.Collector.GetSnapshot().Should().Contain(r =>
      r.Level == LogLevel.Error && r.Message.Contains("Restart limit exceeded") && r.Message.Contains("(Recovering after an error: No data.)"));
  }

  /// <summary>
  /// Verifies that a cancelled task logs a warning and no error, and that the task depending on it logs at Debug.
  /// </summary>
  [Fact]
  public async Task CancelledTask_LogsNoError()
  {
    var logger = new FakeLogger();
    var root = NewTask("Root", IsCancelled, logger);
    var dependent = NewTask("Dependent", Succeeds, logger).AddDependsOn(root);

    await dependent.FireAsync(EngineTaskTrigger.Start);
    await root.FireAsync(EngineTaskTrigger.Start);
    await Task.WhenAll(root.InnerTask, dependent.InnerTask)
      .WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    // The task completes on entry into Failed, just before the Cancelled substate logs its warning.
    await Task.Delay(100, TestContext.Current.CancellationToken);

    root.State.Should().Be(EngineTaskState.Cancelled);
    var records = logger.Collector.GetSnapshot();
    records.Should().NotContain(r => r.Level == LogLevel.Error);
    records.Where(r => r.Level == LogLevel.Warning && r.Message.StartsWith("Root: ")).Should()
      .ContainSingle(r => r.Message.StartsWith("Root: Cancelled. Reason: "), "the Cancelled line is the one warning");
  }

  /// <summary>
  /// Verifies that a task whose work throws logs one error, the exception, and enters the failed state at Debug.
  /// </summary>
  [Fact]
  public async Task WorkThrows_LogsOneErrorWithTheException()
  {
    var logger = new FakeLogger();
    var task = NewTask("Root", Throws, logger);

    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    var records = logger.Collector.GetSnapshot();
    records.Where(r => r.Level == LogLevel.Error).Should().ContainSingle()
      .Which.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("No data.");
    records.Should().Contain(r => r.Level == LogLevel.Debug && r.Message.Contains("Entered Failed state"));
  }

  /// <summary>
  /// Verifies that a task that times out logs one error.
  /// </summary>
  [Fact]
  public async Task TimesOut_LogsOneError()
  {
    var logger = new FakeLogger();
    var task = NewTask("Root", token => Task.Delay(Timeout.Infinite, token), logger)
      .SetTimeOut(TimeSpan.FromMilliseconds(50));

    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    task.State.Should().Be(EngineTaskState.TimedOut);
    logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Error).Should().ContainSingle()
      .Which.Message.Should().Contain("timed out");
  }

  /// <summary>
  /// Verifies that a task that completes logs nothing at Information or above, and its lifecycle at Debug, in every
  /// build (CI runs this in Release).
  /// </summary>
  [Fact]
  public async Task Completes_LogsItsLifecycleAtDebugOnly()
  {
    var logger = new FakeLogger();
    var task = NewTask("Root", Succeeds, logger);

    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    var records = logger.Collector.GetSnapshot();
    records.Should().NotContain(r => r.Level >= LogLevel.Information);
    records.Where(r => r.Level == LogLevel.Debug).Select(r => r.Message).Should()
      .Contain(m => m.Contains("Work started.")).And.Contain(m => m.Contains("Entered CompletedWithSuccess state."));
    records.Should().Contain(r => r.Level == LogLevel.Trace && r.Message.Contains("Transition"));
  }
}
