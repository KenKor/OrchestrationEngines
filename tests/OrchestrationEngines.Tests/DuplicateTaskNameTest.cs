using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a task added under a name the engine already uses (names ignore case). It must be refused, not
/// put in place of the existing task: the replaced task stayed wired as a dependency but was never started, so its
/// dependents, and every wait for the engine, hung without an error.
/// </summary>
public class DuplicateTaskNameTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine whose set-up creates the tasks the test names, in order; "Use" depends on the first one.
  /// </summary>
  private sealed class NamedTasksEngine(params string[] names) : BaseEngine(NullLogger.Instance)
  {
    public EngineTask Add(string name) => CreateTask(name, _ => Task.CompletedTask);

    protected override void SetUpTasks()
    {
      var first = Add(names[0]);
      for (var i = 1; i < names.Length; i++)
        Add(names[i]);
      Add("Use").AddDependsOn(first);
    }
  }

  /// <summary>
  /// Verifies that a set-up that creates a task twice, under the same name or one that differs only in case, fails
  /// the start with an ArgumentException that names the task, and leaves the engine Faulted rather than hanging.
  /// </summary>
  [Theory]
  [InlineData("Load", "Load")]
  [InlineData("Load", "load")]
  public async Task StartAsync_DuplicateNameInSetUp_ThrowsAndFaults(string first, string second)
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NamedTasksEngine(first, second) { Context = new NoContext() };

    var start = await Record.ExceptionAsync(() => engine.StartAsync());

    start.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain(second);
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that adding a task after the set-up under a name already in use throws, and leaves the existing task in
  /// place.
  /// </summary>
  [Fact]
  public async Task AddTask_DuplicateNameAfterSetUp_ThrowsAndKeepsTheExistingTask()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NamedTasksEngine("Load") { Context = new NoContext() };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    var existing = engine.Tasks["Load"];

    var add = Record.Exception(() => engine.Add("LOAD"));

    add.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain("LOAD");
    engine.Tasks.Should().HaveCount(2);
    engine.Tasks["Load"].Should().BeSameAs(existing);
    engine.State.Should().Be(EngineState.Ready);
  }
}
