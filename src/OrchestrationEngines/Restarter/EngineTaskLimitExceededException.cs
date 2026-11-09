using System;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Restarter;

/// <summary>
/// Exception thrown when an <see cref="EngineTask"/> exceeds its allowed restart rate
/// within a defined time window.
/// </summary>
/// <remarks>
/// <para>
/// This exception represents a hard failure condition indicating that an engine task
/// is no longer considered recoverable under its configured restart policy.
/// </para>
/// <para>
/// It is typically raised by restart policy enforcement logic when a task has been
/// restarted too frequently within a rolling or fixed time window, in order to prevent
/// infinite failure loops and resource exhaustion.
/// </para>
/// </remarks>
public class EngineTaskLimitExceededException : EngineTaskException
{
  /// <summary>
  /// Gets the maximum number of restarts allowed within the configured window.
  /// </summary>
  public int MaxRestartsInWindow { get; }

  /// <summary>
  /// Gets the time window during which restarts are counted.
  /// </summary>
  public TimeSpan Window { get; }

  /// <summary>
  /// Initializes a new instance of the class.
  /// </summary>
  /// <param name="et">The engine task that exceeded its restart limit.</param>
  /// <param name="maxRestartsInWindow">
  /// The maximum number of restarts permitted within the specified time window.
  /// </param>
  /// <param name="window">
  /// The time window during which restart attempts are counted.
  /// </param>
  /// <param name="lastFailure">
  /// The most recent exception that caused a restart attempt, if available.
  /// </param>
  /// <remarks>
  /// <para>
  /// The <paramref name="lastFailure"/> exception is preserved as the inner exception
  /// to aid diagnostics and root-cause analysis.
  /// </para>
  /// <para>
  /// Catching this exception generally indicates a terminal condition for the task,
  /// and callers should assume the task will not be restarted automatically.
  /// </para>
  /// </remarks>
  public EngineTaskLimitExceededException(
      EngineTask et,
      int maxRestartsInWindow,
      TimeSpan window,
      Exception lastFailure = null)
      : base(
          et,
          CreateMessage(et, maxRestartsInWindow, window),
          lastFailure)
  {
    MaxRestartsInWindow = maxRestartsInWindow;
    Window = window;
  }

  private EngineTaskLimitExceededException(EngineTask et, int maxConsecutiveRestarts, string message)
      : base(et, message)
  {
    MaxConsecutiveRestarts = maxConsecutiveRestarts;
  }

  /// <summary>
  /// Gets the maximum number of restarts in a row that was exceeded, or 0 when the window's limit was exceeded.
  /// </summary>
  public int MaxConsecutiveRestarts { get; }

  /// <summary>
  /// Creates the exception for a task that exceeded the restarts it may make in a row without a successful run
  /// (<see cref="RestartPolicy.MaxConsecutiveRestarts"/>).
  /// </summary>
  /// <param name="et">The engine task that exceeded its restart limit.</param>
  /// <param name="maxConsecutiveRestarts">The maximum number of restarts in a row.</param>
  /// <returns>The exception.</returns>
  public static EngineTaskLimitExceededException ForConsecutiveRestarts(EngineTask et, int maxConsecutiveRestarts) =>
    new(et, maxConsecutiveRestarts,
      $"Restart limit exceeded for {et.Name}. Maximum of {maxConsecutiveRestarts} restarts in a row without a successful run.");

  /// <summary>
  /// Creates a standardized exception message describing the restart limit violation.
  /// </summary>
  /// <param name="et">The engine task that exceeded its restart limit.</param>
  /// <param name="maxRestartsInWindow">
  /// The maximum number of restarts permitted within the window.
  /// </param>
  /// <param name="window">The time window used for restart counting.</param>
  /// <returns>
  /// A formatted, human-readable exception message. It ends with why the task last recovered (its
  /// <see cref="EngineTask.StateMessage"/> while it is <see cref="EngineTaskState.Recovering"/>, for example
  /// "Recovering after an error: ..."), so the error says what kept failing: a failed engine kick-start, for example,
  /// is only logged as a warning while the engine can still be started again.
  /// </returns>
  private static string CreateMessage(
      EngineTask et,
      int maxRestartsInWindow,
      TimeSpan window)
  {
    var message = $"Restart limit exceeded for {et.Name}. " +
                  $"Maximum of {maxRestartsInWindow} restarts allowed within a window of {window}.";
    return et.State == EngineTaskState.Recovering && !String.IsNullOrEmpty(et.StateMessage)
      ? $"{message} ({et.StateMessage})"
      : message;
  }
}
