using System;

namespace OrchestrationEngines.Helpers;

/// <summary>
/// Raises an event on behalf of the library, where a subscriber that throws must not keep the others from it.
/// </summary>
internal static class DelegateExtensions
{
  /// <summary>
  /// Calls each subscriber in <paramref name="handlers"/> in turn through <paramref name="call"/>. One that throws is
  /// passed to <paramref name="onError"/>, and the subscribers after it are still called, even if
  /// <paramref name="onError"/> throws too (a logging provider that fails).
  /// </summary>
  /// <typeparam name="T">The event's delegate type.</typeparam>
  /// <param name="handlers">The event's subscribers, or null when it has none.</param>
  /// <param name="call">Calls one subscriber with the event's arguments.</param>
  /// <param name="onError">Logs the exception of a subscriber that threw.</param>
  public static void InvokeEach<T>(this T handlers, Action<T> call, Action<Exception> onError) where T : Delegate
  {
    if (handlers == null)
      return;
    foreach (var handler in handlers.GetInvocationList())
    {
      try
      {
        call((T)handler);
      }
      catch (Exception ex)
      {
        try
        {
          onError(ex);
        }
        catch
        {
          // Nothing is left to report it to; the next subscriber must still be called.
        }
      }
    }
  }
}
