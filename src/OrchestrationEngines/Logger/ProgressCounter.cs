using System;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrchestrationEngines.Logger;

/// <summary>
/// Tracks progress of a repeatedly executed operation and periodically produces
/// a formatted progress message including throughput (items/second).
/// </summary>
/// <remarks>
/// This type is thread-safe for concurrent increments. Each report's count and rate are taken together, but
/// reports are formatted and logged outside the lock, so two reports due close together can be logged in either
/// order.
/// </remarks>
public sealed partial class ProgressCounter
{
  private const string defaultTemplate = "Processed {Count:n0} elements ({Rate:n0}/sec)";
  private static readonly Regex Placeholder =
      new(@"\{\{|\{(Count|Rate)(?=\s*[,:}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
  private readonly ILogger _logger;
  // Guards the counters below: Increment updates several of them together.
  private readonly Lock _sync = new();

  private readonly int _reportEvery;
  private readonly string _messageTemplate;

  private int _remainingUntilReport;
  private int _totalCount;

  private int _lastReportedCount;
  private long _lastReportedTimestamp;

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
    _messageTemplate = ToCompositeFormat(messageTemplate ?? throw new ArgumentNullException(nameof(messageTemplate)));

    _remainingUntilReport = reportEvery;
    _totalCount = 0;

    _lastReportedCount = 0;
    _lastReportedTimestamp = TimeProvider.System.GetTimestamp();
  }

  /// <summary>
  /// Total number of processed items so far.
  /// </summary>
  public int TotalCount => Volatile.Read(ref _totalCount);

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

    // Fix (ProgressCounterTest): the counters were updated without a lock, so concurrent increments lost counts and
    // reports. Formatting and logging stay outside the lock, so a slow logger never holds up other increments.
    int totalCount;
    double rate;
    lock (_sync)
    {
      _totalCount += delta;

      // Decrement countdown; when it reaches 0, emit a report and reset countdown.
      _remainingUntilReport -= delta;
      if (_remainingUntilReport > 0) { return null; }

      _remainingUntilReport = _reportEvery;

      var now = TimeProvider.System.GetTimestamp();

      var countDelta = _totalCount - _lastReportedCount;
      // Elapsed time, not the wall clock: a clock adjustment must not distort the rate.
      var seconds = TimeProvider.System.GetElapsedTime(_lastReportedTimestamp, now).TotalSeconds;
      rate = seconds > 0
          ? countDelta / seconds
          : 0.0;

      _lastReportedCount = _totalCount;
      _lastReportedTimestamp = now;
      totalCount = _totalCount;
    }
    var progressMessage = String.Format(_messageTemplate, totalCount, rate);
    LogProgress(progressMessage);
    return progressMessage;
  }

  /// <summary>
  /// Makes the next increment report, whatever its size.
  /// </summary>
  public void EnsureNextReport()
  {
    // Make sure we'll report the next round when we increment 1.
    lock (_sync)
      _remainingUntilReport = 1;
  }


  // Fix (ProgressCounter_Template_MapsOnlyItsPlaceholders): a culture-aware replace of "{Count" and "{Rate" also
  // rewrote escaped braces ("{{Count}}") and names that only start with them ("{CountX}").
  // Maps {Count} and {Rate}, in any case and with an optional ",alignment" or ":format", to the composite-format
  // indexes 0 and 1. Spaces after the name are allowed, as String.Format allows them after an index. An escaped
  // brace ("{{") is matched first, so it stays literal text.
  private static string ToCompositeFormat(string template)
    => Placeholder.Replace(template, match => !match.Groups[1].Success ? match.Value
                                       : match.Groups[1].Value.Equals("Count", StringComparison.OrdinalIgnoreCase)
                                           ? "{0"
                                           : "{1");

  // ---------------------------------------------------------------------
  // Logging methods
  // ---------------------------------------------------------------------

  [LoggerMessage(Level = LogLevel.Information, Message = "{Message}")]
  private partial void LogProgress(string message);
}
