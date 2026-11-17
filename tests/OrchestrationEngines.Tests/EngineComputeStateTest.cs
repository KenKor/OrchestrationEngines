using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Stateless;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for how the engine's state follows from the state of a required task.
/// </summary>
public class EngineComputeStateTest
{
  /// <summary>
  /// A required task that stays in the state it is given: its state machine starts there and is never fired.
  /// </summary>
  private sealed class FixedStateTask(EngineTaskState state)
    : EngineTask("Fixed", _ => Task.CompletedTask, requiredForRunning: true, NullLogger.Instance, CancellationToken.None)
  {
    protected override StateMachine<EngineTaskState, EngineTaskTrigger> CreateStateMachine() => new(state);
  }

  /// <summary>
  /// An engine with a context, never started, that shows the state it computes.
  /// </summary>
  private sealed class ComputingEngine : BaseEngine
  {
    public ComputingEngine() : base(NullLogger.Instance)
    {
      // Without a context, an engine whose tasks completed is Stopped, not Ready.
      Context = new NoContext();
    }

    protected override void SetUpTasks()
    {
    }

    public void Add(EngineTask task) => AddTask(task.Name, task);

    public EngineState Compute() => ComputeState();
  }

  /// <summary>
  /// The engine's state for each state of its one required task.
  /// </summary>
  private static readonly Dictionary<EngineTaskState, EngineState> Expected = new()
  {
    [EngineTaskState.Created] = EngineState.Created,
    [EngineTaskState.Resetting] = EngineState.Faulted,
    [EngineTaskState.WaitingForDependencies] = EngineState.Starting,
    [EngineTaskState.Running] = EngineState.Starting,
    [EngineTaskState.Recovering] = EngineState.Starting,
    [EngineTaskState.CompletedWithSuccess] = EngineState.Ready,
    [EngineTaskState.Failed] = EngineState.Faulted,
    [EngineTaskState.Cancelled] = EngineState.Stopped,
    [EngineTaskState.RecoveryFailed] = EngineState.Faulted,
    [EngineTaskState.DependencyFailed] = EngineState.Faulted,
    [EngineTaskState.TimedOut] = EngineState.Faulted,
  };

  /// <summary>
  /// Verifies that every state a required task can be in gives the engine its own state, and that a new task state
  /// cannot be added without saying here what it makes the engine.
  /// </summary>
  [Fact]
  public void ComputeState_OneRequiredTask_EveryTaskStateGivesItsEngineState()
  {
    Expected.Keys.Should().BeEquivalentTo(Enum.GetValues(typeof(EngineTaskState)).Cast<EngineTaskState>(),
      "every task state needs an expected engine state");

    var actual = new Dictionary<EngineTaskState, EngineState>();
    foreach (var state in Expected.Keys)
    {
      var engine = new ComputingEngine();
      var task = new FixedStateTask(state);
      engine.Add(task);
      task.State.Should().Be(state, "the task stays in the state it was given");
      actual[state] = engine.Compute();
    }

    actual.Should().BeEquivalentTo(Expected);
  }
}
