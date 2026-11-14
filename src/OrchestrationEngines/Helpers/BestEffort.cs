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

  /// <summary>
  /// Calls <paramref name="log"/> with <paramref name="arg"/>, as <see cref="Log(Action)"/> does, without a closure.
  /// </summary>
  /// <typeparam name="T">The argument's type.</typeparam>
  /// <param name="log">Writes the log entry.</param>
  /// <param name="arg">The argument to write it with.</param>
  public static void Log<T>(Action<T> log, T arg)
  {
    try
    {
      log(arg);
    }
    catch
    {
      // Nothing is left to report it to.
    }
  }
}
