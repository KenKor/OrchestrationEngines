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
/// Contains tests for a start whose task set cannot be built: a cycle in its dependencies, or a set-up that throws.
/// None of that set may be published, the engine must be Faulted, and whoever waits on it must not wait forever.
/// </summary>
public class TaskSetSetUpFailureTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine whose set-up the test chooses per start.
  /// </summary>
  private sealed class SetUpEngine : BaseEngine
  {
    public SetUpEngine() : base(NullLogger.Instance) => Context = new NoContext();

    public Action<SetUpEngine> SetUp { get; set; }

    public EngineTask Make(string name) => CreateTask(name, _ => Task.CompletedTask);

    protected override void SetUpTasks() => SetUp(this);
  }

  private static void Cyclic(SetUpEngine e)
  {
    var a = e.Make("A");
    var b = e.Make("B").AddDependsOn(a);
    a.AddDependsOn(b);
  }

  private static void Valid(SetUpEngine e) => e.Make("B").AddDependsOn(e.Make("A"));

  /// <summary>
  /// Verifies that a cyclic set is not published and leaves the engine Faulted, and that a wait for the engine ends.
  /// Without the fix, the cyclic set was published before the check: the engine stayed Created and the wait hung on
  /// tasks that wait for each other.
  /// </summary>
  [Fact]
  public async Task StartAsync_CyclicTaskSet_FaultsWithoutPublishingTheSet()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine { SetUp = Cyclic };

    var start = await Record.ExceptionAsync(() => engine.StartAsync());

    start.Should().BeOfType<CyclicEngineTaskDependencyException>();
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty("no part of a set that failed its check may be published");
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Faulted);
  }

  /// <summary>
  /// Verifies that a set-up that throws faults the engine the same way, and does not publish the tasks it created
  /// before it threw.
  /// </summary>
  [Fact]
  public async Task StartAsync_SetUpThrows_FaultsWithoutPublishingTheSet()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine
    {
      SetUp = e =>
      {
        e.Make("A");
        throw new InvalidOperationException("The set-up fails.");
      }
    };

    var start = await Record.ExceptionAsync(() => engine.StartAsync());

    start.Should().BeOfType<InvalidOperationException>();
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a failed set-up replaces a running set too, and that the next start with a valid set-up makes the
  /// engine Ready again: the failure does not stick.
  /// </summary>
  [Fact]
  public async Task StartAsync_AfterAFailedSetUp_ValidSetUpMakesTheEngineReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine { SetUp = Valid };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Ready);

    engine.SetUp = Cyclic;
    var restart = await Record.ExceptionAsync(() => engine.StartAsync());
    restart.Should().BeOfType<CyclicEngineTaskDependencyException>();
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty("the previous set was replaced");

    engine.SetUp = Valid;
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Ready);
    engine.Tasks.Should().HaveCount(2);
  }
}
