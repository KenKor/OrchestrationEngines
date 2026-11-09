using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrchestrationEngines.Logger;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Engine;

/// <summary>
/// Represents a runtime engine managed by the <see cref="MechanicEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IEngine"/> encapsulates a long-lived execution unit with its own
/// internal task graph, lifecycle state, and cancellation scope.
/// </para>
///
/// <para>
/// Engines are created, configured, started, stopped, and restarted exclusively
/// through the mechanic. Direct calls to lifecycle methods are typically made
/// only by <see cref="EngineTask"/> implementations.
/// </para>
///
/// <para>
/// The engine lifecycle is conceptually:
/// <list type="number">
/// <item>Created (instance exists, no work running)</item>
/// <item>Starting (initialization underway)</item>
/// <item>Ready (required work completed, engine usable)</item>
/// <item>Stopping / Stopped (shutdown in progress or complete)</item>
/// </list>
/// </para>
/// </remarks>
public interface IEngine
{
  /// <summary>
  /// Cancellation token governing the lifetime of this engine instance.
  /// </summary>
  /// <remarks>
  /// Cancelling this token should signal all internal tasks and run-loops
  /// to stop as soon as possible. It is typically linked to the mechanic’s
  /// cancellation scope.
  /// </remarks>
  CancellationToken CancellationToken { get; }

  /// <summary>
  /// Execution context for this engine.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The context is assigned before the engine is started and may include
  /// references to dependency engines (via <see cref="EngineContextWithDependencies"/>).
  /// </para>
  ///
  /// <para>
  /// Context invalidation causes the engine to be stopped and restarted
  /// with a newly computed context.
  /// </para>
  /// </remarks>
  IEngineContext Context { get; set; }

  /// <summary>
  /// Current lifecycle state of the engine.
  /// </summary>
  /// <remarks>
  /// This state is observed by the mechanic and by kick-start tasks to determine
  /// whether the engine is usable, stopping, or terminal.
  /// </remarks>
  EngineState State { get; }

  /// <summary>
  /// Logger scoped to this engine instance.
  /// </summary>
  ILogger Logger { get; }

  /// <summary>
  /// Starts the engine.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This transitions the engine from <c>Created</c> to <c>Starting</c>.
  /// The engine should not be considered usable until <see cref="EnsureReadyAsync"/>
  /// has completed successfully.
  /// </para>
  ///
  /// <para>
  /// This method is typically invoked by <see cref="EngineKickStartTask"/>.
  /// </para>
  /// </remarks>
  Task StartAsync();

  /// <summary>
  /// Ensures that the engine has completed all work required to be considered Ready.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method completes when all tasks marked <c>RequiredForRunning</c>
  /// have completed successfully.
  /// </para>
  ///
  /// <para>
  /// The engine may continue running background or optional tasks after this
  /// method completes.
  /// </para>
  /// </remarks>
  Task EnsureReadyAsync();

  /// <summary>
  /// Ensures that the engine has completed all work required to be considered Ready.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Same as <c>EnsureReadyAsync</c> but can be called without Async.
  /// </para>
  /// <para>
  /// The engine may continue running background or optional tasks after this
  /// method completes.
  /// </para>
  /// </remarks>
  void EnsureReadyWait();

  /// <summary>
  /// Starts every task that has not started yet, and waits until the tasks required for running have completed.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Background tasks (not required for running) are started but not waited for: a monitoring task keeps running
  /// until the engine stops, so waiting for it would never end.
  /// </para>
  /// </remarks>
  Task EnsureAllReadyAsync();

  /// <summary>
  /// Ensures that one or more engine tasks have completed.
  /// </summary>
  /// <returns> 
  /// False if none of the tasks were not found.
  /// </returns>
  Task<bool> EnsureTasksReadyAsync(string[] taskNames);

  /// <summary>
  /// Starts the task named <paramref name="taskName"/> and the tasks it depends on if they have not started, and waits
  /// until it has completed with success.
  /// </summary>
  /// <param name="taskName">The name of the task (compared ignoring case).</param>
  /// <param name="token">Ends the wait when cancelled (it then throws <see cref="OperationCanceledException"/>); the
  /// tasks run on.</param>
  /// <remarks>
  /// The engine's tasks are set up when it starts: on an engine that has not started, every name is unknown. A restart
  /// or reset that ends the task during the wait is not its answer: the wait goes on with the task's next run.
  /// </remarks>
  /// <exception cref="ArgumentException">The engine has no task of that name.</exception>
  /// <exception cref="EngineTaskNotReadyException">
  /// The task ended Failed (or one of its kinds: TimedOut, DependencyFailed, RecoveryFailed) or Cancelled. It carries
  /// the task's name, its final state and its fail reason, and the work's exception as inner exception if it threw.
  /// </exception>
  Task EnsureTaskReadyAsync(string taskName, CancellationToken token = default);

  /// <summary>
  /// Requests the engine to stop execution.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The engine should cancel ongoing work, transition toward a stopped state,
  /// and release resources as appropriate.
  /// </para>
  ///
  /// <para>
  /// This method is idempotent and may be called multiple times.
  /// </para>
  /// </remarks>
  Task StopAsync();

  /// <summary>
  /// Resets all internal engine tasks, regardless of their completion state.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is typically used during full engine restart scenarios or
  /// context invalidation cascades.
  /// </para>
  /// <para>
  /// It prepares a restart: until the caller follows up with <see cref="StartAsync"/> or <see cref="StopAsync"/> (or, on a
  /// <see cref="BaseEngine"/>, gives the restart up with <see cref="BaseEngine.AbandonRestart"/>), every wait until the
  /// engine is ready waits.
  /// </para>
  /// </remarks>
  Task ResetAllTasksAsync();

  /// <summary>
  /// Resets only the internal tasks that have failed.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is primarily used during recovery after a failed start or run,
  /// allowing healthy tasks to remain intact.
  /// </para>
  /// <para>
  /// As <see cref="ResetAllTasksAsync"/>, it prepares a restart that the caller must follow up with
  /// <see cref="StartAsync"/> or <see cref="StopAsync"/>.
  /// </para>
  /// </remarks>
  Task ResetFailedTasksAsync();
}
