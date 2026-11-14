using System;
using Microsoft.Extensions.Logging;

namespace OrchestrationEngines.Tests;

/// <summary>
/// A logger that runs <paramref name="action"/> when a line it logs contains <paramref name="contains"/>, on the thread
/// that logs it: to throw from a logging provider, or to act at the moment the code under test logs. Every line goes on
/// to <paramref name="next"/> first, if given, so several of these watch one logger.
/// </summary>
internal sealed class OnMessageLogger(string contains, Action action, ILogger next = null) : ILogger
{
  public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
    Func<TState, Exception, string> formatter)
  {
    next?.Log(logLevel, eventId, state, exception, formatter);
    if (formatter(state, exception).Contains(contains))
      action();
  }
}
