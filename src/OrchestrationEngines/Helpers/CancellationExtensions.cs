using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace OrchestrationEngines.Helpers;

/// <summary>
/// Cancels a token source on behalf of the library, where a callback that someone registered on its token must not
/// stop the cancellation from going ahead.
/// </summary>
internal static partial class CancellationExtensions
{
  /// <summary>
  /// Cancels <paramref name="source"/>. A callback registered on its token that throws is logged and swallowed: every
  /// callback still runs (<see cref="CancellationTokenSource.Cancel()"/> runs them all before it throws), and the
  /// caller carries on with the state change that the cancellation belongs to.
  /// </summary>
  /// <param name="source">The token source to cancel.</param>
  /// <param name="logger">Where a callback's exception is logged.</param>
  public static void CancelBestEffort(this CancellationTokenSource source, ILogger logger)
  {
    try
    {
      source.Cancel();
    }
    catch (AggregateException ex)
    {
      // A callback on a linked token source (a task's, under its engine's) throws from inside this one's callback, so
      // the exception arrives nested.
      var flat = ex.Flatten();
      LogCancelCallbackFailed(logger, flat.InnerExceptions.Count == 1 ? flat.InnerExceptions[0] : flat);
    }
  }

  [LoggerMessage(Level = LogLevel.Warning, Message = "A callback registered on a cancellation token threw; the cancellation went ahead.")]
  private static partial void LogCancelCallbackFailed(ILogger logger, Exception exception);
}
