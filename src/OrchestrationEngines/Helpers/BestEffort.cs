using System;

namespace OrchestrationEngines.Helpers;

/// <summary>
/// Logs on a path that must go on whatever the logging provider does.
/// </summary>
internal static class BestEffort
{
  /// <summary>
  /// Calls <paramref name="log"/>, and ignores an exception from it: a logging provider that throws must not end a
  /// stop, a switch or a shutdown, or lose the failure that is being logged.
  /// </summary>
  /// <param name="log">Writes the log entry.</param>
  public static void Log(Action log)
  {
    try
    {
      log();
    }
    catch
    {
      // Nothing is left to report it to.
    }
  }
}
