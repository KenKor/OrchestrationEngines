using System;
using Microsoft.Extensions.Logging;

namespace OrchestrationEngines.Tests;

/// <summary>
/// A logger that runs <paramref name="action"/> when a line it logs <paramref name="matches"/> (its level, message and
/// exception), on the thread that logs it: to throw from a logging provider, or to act at the moment the code under
/// test logs. Every line goes on to <paramref name="next"/> first, if given, so several of these watch one logger.
/// </summary>
internal sealed class OnMessageLogger(
  Func<LogLevel, string, Exception, bool> matches, Action action, ILogger next = null)
  : ILogger
{
  /// <summary>
  /// Runs <paramref name="action"/> when a line's message contains <paramref name="contains"/>.
  /// </summary>
  public OnMessageLogger(string contains, Action action, ILogger next = null)
    : this((_, message, _) => message.Contains(contains), action, next)
  {
  }

  /// <summary>
  /// Runs <paramref name="action"/> on every error logged with an exception, whatever its wording.
  /// </summary>
  public static OnMessageLogger OnError(Action action, ILogger next = null) =>
    new((level, _, exception) => level == LogLevel.Error && exception != null, action, next);

  public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
    Func<TState, Exception, string> formatter)
  {
    next?.Log(logLevel, eventId, state, exception, formatter);
    if (matches(logLevel, formatter(state, exception), exception))
      action();
  }
}
