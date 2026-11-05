using System;

namespace OrchestrationEngines.Tasks;

/// <summary>
/// Base type for exceptions that are intentionally tied to a specific <see cref="EngineTask"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use this exception hierarchy for failures that carry domain meaning in the EngineTask state machine,
/// as opposed to incidental exceptions thrown by implementation details.
/// </para>
///
/// <para>
/// The primary purpose is to retain the originating <see cref="EngineTask"/> instance so that higher-level
/// orchestration (Mechanic/Coordinator) can make policy decisions (e.g., cancel, fail fast, classify as non-recoverable)
/// and can log with full task context.
/// </para>
///
/// <para>
/// Note: In <see cref="EngineTask"/>, catching <see cref="EngineTaskException"/> currently maps to cancellation
/// rather than recovery. This is a design choice: these exceptions typically indicate a non-recoverable task condition.
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
