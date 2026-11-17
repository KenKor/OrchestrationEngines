using System;
using System.Linq;
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
  /// <summary>
  /// An engine whose set-up creates the tasks the test names, in order; "Use" depends on the first one.
  /// </summary>
  private sealed class NamedTasksEngine(params string[] names) : BaseEngine(NullLogger.Instance)
  {
    public EngineTask Add(string name) => CreateTask(name, _ => Task.CompletedTask);

    public void Add(string name, EngineTask task) => AddTask(name, task);

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

    start.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain(second).And.Contain(nameof(NamedTasksEngine));
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
  }

  private static async Task<NamedTasksEngine> StartedEngineAsync()
  {
    var engine = new NamedTasksEngine("Load") { Context = new NoContext() };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    return engine;
  }

  private static ProbeTask NewTask(string name) => new(name, _ => Task.CompletedTask, requiredForRunning: false);

  /// <summary>
  /// Verifies that a task refused for a name already in use lets go of its linked token source, so it holds no
  /// registration on the engine's token.
  /// </summary>
  [Fact]
  public async Task AddTask_DuplicateName_ReleasesTheRefusedTasksTokenSource()
  {
    var engine = await StartedEngineAsync();
    var refused = NewTask("LOAD");
    var token = refused.Source.Token;

    var add = Record.Exception(() => engine.Add("LOAD", refused));

    add.Should().BeOfType<ArgumentException>();
    await Wait.UntilAsync(() => Tokens.IsDisposed(token), MustComplete, "the refused task's token source is released");
  }

  /// <summary>
  /// Verifies that adding a task that is already in the set again, under its own name or another, is refused and
  /// leaves that task's token source alone: releasing it would cancel a task the engine still runs.
  /// </summary>
  [Theory]
  [InlineData("Load")]
  [InlineData("Other")]
  public async Task AddTask_TaskAlreadyInTheSet_ThrowsAndKeepsItsTokenSource(string name)
  {
    var engine = await StartedEngineAsync();
    var existing = engine.Tasks["Load"];
    var token = existing.CancellationToken;

    var add = Record.Exception(() => engine.Add(name, existing));

    add.Should().BeOfType<ArgumentException>();
    engine.Tasks["Load"].Should().BeSameAs(existing);
    Tokens.IsDisposed(token).Should().BeFalse();
    token.IsCancellationRequested.Should().BeFalse();
  }

  /// <summary>
  /// Verifies that a task added under a name other than its own is refused: the set is unchanged, the message names
  /// the engine and both names, and the refused task lets go of its linked token source.
  /// </summary>
  [Fact]
  public async Task AddTask_NameDiffersFromTheTasksName_ThrowsAndLeavesTheSetUnchanged()
  {
    var engine = await StartedEngineAsync();
    var before = engine.Tasks.Values.ToList();
    var refused = NewTask("Late");
    var token = refused.Source.Token;

    var add = Record.Exception(() => engine.Add("Other", refused));

    var thrown = add.Should().BeOfType<ArgumentException>().Which;
    thrown.ParamName.Should().Be("name");
    thrown.Message.Should().Contain("Other").And.Contain("Late").And.Contain(nameof(NamedTasksEngine));
    engine.Tasks.Values.Should().Equal(before, ReferenceEquals);
    await Wait.UntilAsync(() => Tokens.IsDisposed(token), MustComplete, "the refused task's token source is released");
  }

  /// <summary>
  /// Verifies that a task added under its own name in another case is accepted.
  /// </summary>
  [Fact]
  public async Task AddTask_NameDiffersOnlyInCase_AddsTheTask()
  {
    var engine = await StartedEngineAsync();
    var late = NewTask("Late");

    engine.Add("LATE", late);

    engine.Tasks["late"].Should().BeSameAs(late);
    Tokens.IsDisposed(late.CancellationToken).Should().BeFalse();
  }

  /// <summary>
  /// Verifies that a null name is refused with an ArgumentNullException for the parameter "name", from both
  /// <c>CreateTask</c> and <c>AddTask</c>.
  /// </summary>
  [Fact]
  public async Task CreateTaskAndAddTask_NullName_ThrowForTheNameParameter()
  {
    var engine = await StartedEngineAsync();
    var count = engine.Tasks.Count;

    var create = Record.Exception(() => engine.Add(null));
    var add = Record.Exception(() => engine.Add(null, NewTask("Late")));

    create.Should().BeOfType<ArgumentNullException>().Which.ParamName.Should().Be("name");
    add.Should().BeOfType<ArgumentNullException>().Which.ParamName.Should().Be("name");
    engine.Tasks.Should().HaveCount(count);
  }
}
