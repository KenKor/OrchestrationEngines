using System;
using System.Threading;
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

    start.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain(second).And.Contain(nameof(NamedTasksEngine));
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// A task that reports whether the linked token source its constructor made has been disposed.
  /// </summary>
  private sealed class ProbeTask(string name)
    : EngineTask(name, _ => Task.CompletedTask, requiredForRunning: false, NullLogger.Instance, CancellationToken.None)
  {
    public bool SourceDisposed
    {
      get
      {
        try
        {
          _ = _linkedCts.Token;
          return false;
        }
        catch (ObjectDisposedException)
        {
          return true;
        }
      }
    }
  }

  /// <summary>
  /// An engine with one load task that adds tasks through <c>AddTask</c>, under the name the test gives.
  /// </summary>
  private sealed class AddingEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);

    public void Add(string name, EngineTask task) => AddTask(name, task);

    public void Create(string name) => CreateTask(name, _ => Task.CompletedTask);
  }

  private static async Task<AddingEngine> StartedAddingEngineAsync()
  {
    var engine = new AddingEngine { Context = new NoContext() };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    return engine;
  }

  /// <summary>
  /// Verifies that a task refused for a name already in use lets go of its linked token source, so it holds no
  /// registration on the engine's token.
  /// </summary>
  [Fact]
  public async Task AddTask_DuplicateName_ReleasesTheRefusedTasksTokenSource()
  {
    var engine = await StartedAddingEngineAsync();
    var refused = new ProbeTask("LOAD");

    var add = Record.Exception(() => engine.Add("LOAD", refused));

    add.Should().BeOfType<ArgumentException>();
    await Wait.UntilAsync(() => refused.SourceDisposed, MustComplete, "the refused task's token source is released");
  }

  /// <summary>
  /// Verifies that a task added under a name other than its own is refused: the set is unchanged, the message names
  /// the engine and both names, and the refused task lets go of its linked token source.
  /// </summary>
  [Fact]
  public async Task AddTask_NameDiffersFromTheTasksName_ThrowsAndLeavesTheSetUnchanged()
  {
    var engine = await StartedAddingEngineAsync();
    var existing = engine.Tasks["Load"];
    var refused = new ProbeTask("Late");

    var add = Record.Exception(() => engine.Add("Other", refused));

    var thrown = add.Should().BeOfType<ArgumentException>().Which;
    thrown.ParamName.Should().Be("name");
    thrown.Message.Should().Contain("Other").And.Contain("Late").And.Contain(nameof(AddingEngine));
    engine.Tasks.Should().ContainSingle().Which.Value.Should().BeSameAs(existing);
    await Wait.UntilAsync(() => refused.SourceDisposed, MustComplete, "the refused task's token source is released");
  }

  /// <summary>
  /// Verifies that a task added under its own name in another case is accepted.
  /// </summary>
  [Fact]
  public async Task AddTask_NameDiffersOnlyInCase_AddsTheTask()
  {
    var engine = await StartedAddingEngineAsync();
    var late = new ProbeTask("Late");

    engine.Add("LATE", late);

    engine.Tasks["late"].Should().BeSameAs(late);
    late.SourceDisposed.Should().BeFalse();
  }

  /// <summary>
  /// Verifies that a null name is refused with an ArgumentNullException for the parameter "name", from both
  /// <c>CreateTask</c> and <c>AddTask</c>.
  /// </summary>
  [Fact]
  public async Task CreateTaskAndAddTask_NullName_ThrowForTheNameParameter()
  {
    var engine = await StartedAddingEngineAsync();

    var create = Record.Exception(() => engine.Create(null));
    var add = Record.Exception(() => engine.Add(null, new ProbeTask("Late")));

    create.Should().BeOfType<ArgumentNullException>().Which.ParamName.Should().Be("name");
    add.Should().BeOfType<ArgumentNullException>().Which.ParamName.Should().Be("name");
    engine.Tasks.Should().ContainSingle();
  }
}
