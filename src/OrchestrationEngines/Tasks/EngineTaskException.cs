using System;

namespace OrchestrationEngines.Tasks;

/// <summary>
/// Base type for exceptions that end an <see cref="EngineTask"/>'s run without a retry: a condition another attempt
/// cannot change.
/// </summary>
/// <remarks>
/// <para>
/// Use this exception hierarchy for failures that carry domain meaning in the EngineTask state machine,
/// as opposed to incidental exceptions thrown by implementation details.
/// </para>
///
/// <para>
/// It can carry the originating <see cref="EngineTask"/>, so that higher-level orchestration (Mechanic/Coordinator) can
/// log with full task context. <see cref="EngineTask"/> is null when the exception is raised where no task can be
/// reached, such as by a factory (<c>EngineReturnedPreviousInstanceException</c>).
/// </para>
///
/// <para>
/// An <see cref="EngineTaskException"/> thrown by a task's work is not retried by that task: it ends
/// <see cref="EngineTaskState.Cancelled"/>, with "Cancelled by the work: " and the message as its reason, also when it
/// has a recovery policy. Derive from it for a condition that another attempt cannot change. A task of an engine that
/// ends this way stops the engine, it does not fault it; the engine's kick-start may still start the engine again
/// under its descriptor's restart policy, which then runs the task again.
/// </para>
/// </remarks>
public abstract class EngineTaskException : Exception
{
    /// <summary>
    /// The task instance associated with this exception.
    /// </summary>
    /// <remarks>
    /// This provides the orchestration layer with direct access to the task name, state, dependencies,
    /// and other diagnostics when handling the exception.
    /// </remarks>
    public EngineTask EngineTask { get; }

    /// <summary>
    /// Creates a task-scoped exception with a message and optional inner exception.
    /// </summary>
    /// <param name="et">The <see cref="EngineTask"/> that raised or is associated with this exception.</param>
    /// <param name="message">Human-readable reason for the failure.</param>
    /// <param name="innerException">Optional underlying exception that caused this task-scoped exception.</param>
    protected EngineTaskException(
        EngineTask et,
        string message,
        Exception innerException = null)
        : base(message, innerException)
    {
        EngineTask = et;
    }
}
