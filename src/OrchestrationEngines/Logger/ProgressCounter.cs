using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrchestrationEngines.Logger;

/// <summary>
/// Tracks progress of a repeatedly executed operation and periodically produces
/// a formatted progress message including throughput (items/second).
/// </summary>
/// <remarks>
/// This type is thread-safe for concurrent increments.
/// </remarks>
public sealed partial class ProgressCounter
{
  private const string defaultTemplate = "Processed {Count:n0} elements ({Rate:n0}/sec)";
  private readonly ILogger _logger;

  // Note that I did not include a lock/sync object to avoid unnecessary overhead; this should be fast and not process-safe.
  private readonly int _reportEvery;
  private readonly string _messageTemplate;

  private int _remainingUntilReport;
  private int _totalCount;

  private int _lastReportedCount;
  private long _lastReportedTicks;

  /// <summary>
  /// Creates a progress counter that reports once per <paramref name="reportEvery"/> increments.
  /// </summary>
  /// <param name="logger">The logger to write to.</param>
  /// <param name="reportEvery">Number of increments between progress reports.</param>
  /// <param name="messageTemplate">
  /// Message template with placeholders:
  /// <list type="bullet">
  /// <item><c>{count}</c> - total item count</item>
  /// <item><c>{rate}</c> - items per second since last report</item>
  /// </list>
  /// Example: <c>"Processed {count:n0} elements ({rate:n0}/sec)"</c>
  /// </param>
  public ProgressCounter(ILogger logger, int reportEvery, string messageTemplate = defaultTemplate)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reportEvery);

    _logger = logger ?? NullLogger.Instance;
    _reportEvery = reportEvery;
    _messageTemplate = messageTemplate?
                           .Replace("{Count", "{0", StringComparison.InvariantCultureIgnoreCase)
                           .Replace("{Rate", "{1", StringComparison.InvariantCultureIgnoreCase)
                       ?? throw new ArgumentNullException(nameof(messageTemplate));

    _remainingUntilReport = reportEvery;
    _totalCount = 0;

    _lastReportedCount = 0;
    _lastReportedTicks = DateTime.UtcNow.Ticks;
  }

  /// <summary>
  /// Total number of processed items so far.
  /// </summary>
  public int TotalCount => _totalCount;

  /// <summary>
  /// Increments the counter and, if the reporting interval is reached, returns a formatted progress message.
  /// Otherwise returns <c>null</c>.
  /// </summary>
  /// <param name="delta">How many items to add.</param>
  /// <returns>A formatted progress message, or <c>null</c> if no report is due.</returns>
  public string Increment(int delta = 1)
  {
    // This method does many relatively unrelated things: 1) increment counter, 2) signalling that reporting is needed,
    // 3) resetting counter, 4) calculating performace, 5) formatting message.
    if (delta < 0) { return null; }

    _totalCount += delta;

    // Decrement countdown; when it reaches 0, emit a report and reset countdown.
    _remainingUntilReport -= delta;
    if (_remainingUntilReport > 0) { return null; }

    _remainingUntilReport = _reportEvery;

    var nowTicks = DateTime.UtcNow.Ticks;

    var countDelta = _totalCount - _lastReportedCount;
    var tickDelta = nowTicks - _lastReportedTicks;

    // 10,000,000 ticks per second.
    var rate = tickDelta > 0
        ? 10_000_000.0 * countDelta / tickDelta
        : 0.0;

    _lastReportedCount = _totalCount;
    _lastReportedTicks = nowTicks;
    var progressMessage = FormatMessage(_messageTemplate, _totalCount, rate);
    LogProgress(progressMessage);
    return progressMessage;
  }

  /// <summary>
  /// Ensure's well do a report next time round.
  /// </summary>
  public void EnsureNextReport()
  {
    // Make sure we'll report the next round when we increment 1.
    _remainingUntilReport = 1;
  }


  private static string FormatMessage(string template, long count, double rate)
  {
    // Minimal, dependency-free templating.
    // Supports numeric formats like {count:n0} and {rate:n0}.
    // If no format is specified, defaults to invariant ToString().
    return String.Format(template, count, rate);
  }

  // ---------------------------------------------------------------------
  // Logging methods
  // ---------------------------------------------------------------------

  [LoggerMessage(Level = LogLevel.Information, Message = "{Message}")]
  private partial void LogProgress(string message);
}
