using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Tasks;
using Stateless.Reflection;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains snapshot tests of the task and coordinator state machines: every configured transition and ignored
/// trigger, so any change to them shows up in review. Guards are only marked, not described, since Stateless names
/// lambda guards after compiler-generated methods.
/// </summary>
public class StateMachineTransitionsTest
{
  private sealed class InspectableTask() : EngineTask("Task", _ => Task.CompletedTask, requiredForRunning: true,
    NullLogger.Instance, CancellationToken.None)
  {
    public StateMachineInfo Info => StateMachine.GetInfo();
  }

  private sealed class InspectableCoordinator() : EngineCoordinator(nameof(StateMachineTransitionsTest), NullLogger.Instance,
    useDispatcher: false)
  {
    public StateMachineInfo Info => AppMachine.GetInfo();
  }

  private static string[] Describe(StateMachineInfo info) => info.States
    .OrderBy(s => Convert.ToInt32(s.UnderlyingState))
    .SelectMany(Describe)
    .ToArray();

  private static IEnumerable<string> Describe(StateInfo state)
  {
    var name = state.UnderlyingState.ToString();
    if (state.Superstate != null)
      yield return $"{name} is in {state.Superstate.UnderlyingState}";
    // A dynamic transition decides its destination when it fires; it is listed once for each destination it declares.
    var fixedTransitions = state.FixedTransitions.Select(t => (
      Trigger: t.Trigger.UnderlyingTrigger.ToString(),
      Destination: t.DestinationState.UnderlyingState.ToString(),
      Mark: t.GuardConditionsMethodDescriptions.Any() ? " (guarded)" : String.Empty));
    var dynamicTransitions = state.DynamicTransitions.SelectMany(t => t.PossibleDestinationStates.Select(d => (
      Trigger: t.Trigger.UnderlyingTrigger.ToString(),
      Destination: d.DestinationState,
      Mark: " (dynamic)")));
    foreach (var transition in fixedTransitions.Concat(dynamicTransitions).OrderBy(t => t.Trigger).ThenBy(t => t.Destination))
      yield return $"{name} --{transition.Trigger}--> {transition.Destination}{transition.Mark}";
    foreach (var ignored in state.IgnoredTriggers.OrderBy(t => t.Trigger.UnderlyingTrigger.ToString()))
      yield return $"{name} ignores {ignored.Trigger.UnderlyingTrigger}";
  }

  /// <summary>
  /// Compares a state machine's table to its snapshot. On a mismatch the actual table is written to the test output,
  /// to copy into the snapshot after a deliberate change.
  /// </summary>
  private static void ShouldMatch(StateMachineInfo info, string[] snapshot)
  {
    var actual = Describe(info);
    TestContext.Current.TestOutputHelper.WriteLine(String.Join(Environment.NewLine, actual.Select(line => $"      \"{line}\",")));
    actual.Should().Equal(snapshot);
  }

  /// <summary>
  /// Verifies the engine task's state machine against its snapshot.
  /// </summary>
  [Fact]
  public void EngineTask_StateMachine_MatchesSnapshot() => ShouldMatch(new InspectableTask().Info,
    [
      "Created --Cancel--> Cancelled",
      "Created --CompleteDependency--> Running (dynamic)",
      "Created --CompleteDependency--> WaitingForDependencies (dynamic)",
      "Created --FailDependency--> DependencyFailed",
      "Created --Reset--> Resetting",
      "Created --Start--> DependencyFailed (dynamic)",
      "Created --Start--> Running (dynamic)",
      "Created --Start--> WaitingForDependencies (dynamic)",
      "Resetting --Reset--> Created",
      "Resetting ignores CompleteDependency",
      "WaitingForDependencies --Cancel--> Cancelled",
      "WaitingForDependencies --CompleteDependency--> Running (dynamic)",
      "WaitingForDependencies --CompleteDependency--> WaitingForDependencies (dynamic)",
      "WaitingForDependencies --FailDependency--> DependencyFailed",
      "WaitingForDependencies ignores Reset",
      "WaitingForDependencies ignores Start",
      "Running --Cancel--> Cancelled (guarded)",
      "Running --Cancel--> Cancelled (guarded)",
      "Running --Cancel--> Recovering (guarded)",
      "Running --Fail--> Failed (guarded)",
      "Running --Fail--> Recovering (guarded)",
      "Running --Reset--> Resetting",
      "Running --Succeed--> CompletedWithSuccess",
      "Running --TimeOut--> Recovering (guarded)",
      "Running --TimeOut--> TimedOut (guarded)",
      "Running ignores CompleteDependency",
      "Recovering --Cancel--> Cancelled",
      "Recovering --FailRecovery--> RecoveryFailed",
      "Recovering --Reset--> Resetting",
      "Recovering --SucceedRecovery--> Running",
      "Recovering ignores CompleteDependency",
      "CompletedWithSuccess --Reset--> Resetting",
      "CompletedWithSuccess ignores Cancel",
      "CompletedWithSuccess ignores CompleteDependency",
      "Failed --Reset--> Resetting",
      "Failed ignores CompleteDependency",
      "Failed ignores FailDependency",
      "Failed ignores Start",
      "Cancelled is in Failed",
      "RecoveryFailed is in Failed",
      "DependencyFailed is in Failed",
      "TimedOut is in Failed",
    ]);

  /// <summary>
  /// Verifies the coordinator's state machine against its snapshot.
  /// </summary>
  [Fact]
  public void EngineCoordinator_StateMachine_MatchesSnapshot() => ShouldMatch(new InspectableCoordinator().Info,
    [
      "Created --Start--> WaitingForEngines",
      "WaitingForEngines --EngineStarting--> Initializing",
      "WaitingForEngines --StartShutdown--> Stopping",
      "WaitingForEngines ignores EngineFailed",
      "WaitingForEngines ignores EngineReady",
      "WaitingForEngines ignores RefreshEngine",
      "WaitingForEngines ignores Start",
      "Initializing --EngineFailed--> WaitingForEngines",
      "Initializing --EngineReady--> Running",
      "Initializing --StartShutdown--> Stopping",
      "Initializing ignores EngineStarting",
      "Initializing ignores RefreshEngine",
      "Initializing ignores Start",
      "Running --EngineFailed--> WaitingForEngines",
      "Running --InvalidateContext--> Initializing",
      "Running --RefreshEngine--> Initializing",
      "Running --StartShutdown--> Stopping",
      "Running ignores EngineReady",
      "Running ignores EngineStarting",
      "Running ignores Start",
      "Stopping --CompleteShutdown--> Stopped",
      "Stopping ignores EngineFailed",
      "Stopping ignores EngineReady",
      "Stopping ignores EngineStarting",
      "Stopping ignores Start",
      "Stopping ignores StartShutdown",
      "Stopped ignores CompleteShutdown",
      "Stopped ignores EngineFailed",
      "Stopped ignores EngineReady",
      "Stopped ignores EngineStarting",
      "Stopped ignores Start",
      "Stopped ignores StartShutdown",
    ]);
}
