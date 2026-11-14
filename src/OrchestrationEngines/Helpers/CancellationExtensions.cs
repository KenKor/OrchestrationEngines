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
  /// <remarks>
  /// A source that is cancelled already is left alone: a second cancel would do nothing, and the library disposes a
  /// source only once it is cancelled (a replaced task source, a stopped engine's). One cancelled and disposed by
  /// another thread between that check and the cancel is left alone too. Cancelling a disposed source that was never
  /// cancelled still throws <see cref="ObjectDisposedException"/>, which is a bug to see.
  /// </remarks>
  /// <param name="source">The token source to cancel.</param>
  /// <param name="logger">Where a callback's exception is logged.</param>
  public static void CancelBestEffort(this CancellationTokenSource source, ILogger logger)
  {
    if (source.IsCancellationRequested)
      return;
    try
    {
      source.Cancel();
    }
    catch (ObjectDisposedException) when (source.IsCancellationRequested)
    {
      // Cancelled and disposed meanwhile (a reset that replaced it, a stop): its callbacks have run already.
    }
    catch (AggregateException ex)
    {
      // A callback on a linked token source (a task's, under its engine's) throws from inside this one's callback, so
      // the exception arrives nested. A single one is logged as itself, so the log shows what threw, not the wrapper.
      var flat = ex.Flatten();
      LogCancelCallbackFailed(logger, flat.InnerExceptions.Count == 1 ? flat.InnerExceptions[0] : flat);
    }
  }

#if !NETCOREAPP3_0_OR_GREATER
  /// <summary>
  /// Stand-in for <c>CancellationToken.UnsafeRegister</c> (.NET Core 3.0 and later): registers
  /// <paramref name="callback"/> without capturing the caller's execution context, so it runs in the context of the
  /// thread that cancels.
  /// </summary>
  /// <param name="token">The token to register on.</param>
  /// <param name="callback">What to run when <paramref name="token"/> is cancelled.</param>
  /// <param name="state">What <paramref name="callback"/> is passed.</param>
  /// <returns>The registration, to dispose when the callback is no longer wanted.</returns>
  public static CancellationTokenRegistration UnsafeRegister(this CancellationToken token, Action<object> callback,
    object state)
  {
    // SuppressFlow throws when the flow is suppressed already; the registration captures nothing then anyway.
    if (ExecutionContext.IsFlowSuppressed())
      return token.Register(callback, state);
    using (ExecutionContext.SuppressFlow())
      return token.Register(callback, state);
  }
#endif

  [LoggerMessage(Level = LogLevel.Warning, Message = "A callback registered on a cancellation token threw; the cancellation went ahead.")]
  private static partial void LogCancelCallbackFailed(ILogger logger, Exception exception);
}
