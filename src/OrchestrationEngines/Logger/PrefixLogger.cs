using System;
using Microsoft.Extensions.Logging;

namespace OrchestrationEngines.Logger;

/// <summary>
/// A logger that puts a prefix in front of every message, for example the engine (<c>[G] </c>) or the engine task
/// (<c>Initialize Connections: </c>) a line is about. Wrapped in each other they give
/// <c>[G] Initialize Connections: ...</c>.
/// </summary>
/// <remarks>
/// The prefix is put in front of the formatted message; the structured state is passed on unchanged.
/// </remarks>
/// <param name="inner">The logger to write to.</param>
/// <param name="prefix">Gives the prefix, when a message is logged; none when it gives <c>null</c>.</param>
internal sealed class PrefixLogger(ILogger inner, Func<string> prefix) : ILogger
{
  /// <inheritdoc/>
  public IDisposable BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

  /// <inheritdoc/>
  public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

  /// <inheritdoc/>
  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
    Func<TState, Exception, string> formatter) =>
    inner.Log(logLevel, eventId, state, exception, (s, e) => prefix() + formatter(s, e));
}
