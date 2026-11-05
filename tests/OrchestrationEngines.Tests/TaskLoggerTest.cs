using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the logger of an engine task: every message logged through it says which task it is about.
/// </summary>
public class TaskLoggerTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Verifies that the task's own messages (its state changes) and the messages its work logs through the task's logger
  /// both start with the task's name.
  /// </summary>
  [Fact]
  public async Task EngineTask_Logger_TagsEveryMessageWithTheTaskName()
  {
    var logger = new FakeLogger();
    EngineTask task = null;
    task = new EngineTask("Load Connections", _ =>
    {
      task.Logger.LogInformation("Processed {Count} elements", 42);
      return Task.CompletedTask;
    }, requiredForRunning: true, logger, CancellationToken.None);

    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    var messages = logger.Collector.GetSnapshot().Select(r => r.Message).ToArray();
    messages.Should().Contain("Load Connections: Processed 42 elements");
    messages.Should().OnlyContain(m => m.StartsWith("Load Connections: ", StringComparison.Ordinal));
  }

  /// <summary>
  /// An engine named G with one task that logs through its logger.
  /// </summary>
  private sealed class NetworkEngine(ILogger logger) : BaseEngine(logger)
  {
    public override string EngineName => "[G]";

    protected override void SetUpTasks() => CreateTask("Load Connections", _ =>
    {
      Tasks["Load Connections"].Logger.LogInformation("Processed {Count} elements", 42);
      return Task.CompletedTask;
    });
  }

  /// <summary>
  /// Verifies that the messages of a task that an engine created also name the engine (for example, the data set it serves), so the
  /// lines of networks that load at the same time can be told apart; and that the engine's own lines do as well.
  /// </summary>
  [Fact]
  public async Task BaseEngine_TaskLogger_TagsEveryMessageWithTheEngineAndTaskName()
  {
    var logger = new FakeLogger();
    var engine = new NetworkEngine(logger) { Context = new NoContext() };

    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    var messages = logger.Collector.GetSnapshot().Select(r => r.Message).ToArray();
    messages.Should().Contain("[G] Load Connections: Processed 42 elements");
    messages.Should().OnlyContain(m => m.StartsWith("[G] ", StringComparison.Ordinal), "the engine's own lines name the network too");
  }
}
