using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Logger;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Tests for <see cref="ProgressCounter"/>.
/// </summary>
public class ProgressCounterTest
{
  /// <summary>
  /// Verifies that concurrent increments lose no count and report once per interval (#156): the type says it is
  /// thread-safe for concurrent increments.
  /// </summary>
  [Fact]
  public async Task ProgressCounter_ConcurrentIncrements_LoseNoCountAndReportOncePerInterval()
  {
    const int threads = 8;
    const int perThread = 200_000;
    const int reportEvery = 1_000;
    var counter = new ProgressCounter(NullLogger.Instance, reportEvery);
    var reports = 0;
    using var start = new ManualResetEventSlim();

    var workers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
    {
      start.Wait(TestContext.Current.CancellationToken);
      for (var i = 0; i < perThread; i++)
        if (counter.Increment() != null)
          Interlocked.Increment(ref reports);
    }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
    start.Set();
    await Task.WhenAll(workers).WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    counter.TotalCount.Should().Be(threads * perThread);
    reports.Should().Be(threads * perThread / reportEvery);
  }

  /// <summary>
  /// Verifies that only the placeholders <c>{Count}</c> and <c>{Rate}</c> are mapped (#156), in any case and with an
  /// alignment or format (also after a space), and that escaped braces stay literal text.
  /// </summary>
  [Theory]
  [InlineData("{count} items", "1 items")]
  [InlineData("[{Count,3}]", "[  1]")]
  [InlineData("{Count } and {Count :n0}", "1 and 1")]
  [InlineData("[{Count ,3}]", "[  1]")]
  [InlineData("{{Count}} is {Count}", "{Count} is 1")]
  [InlineData("{{Rate}}", "{Rate}")]
  public void ProgressCounter_Template_MapsOnlyItsPlaceholders(string template, string expected)
  {
    var counter = new ProgressCounter(NullLogger.Instance, reportEvery: 1, template);

    counter.Increment().Should().Be(expected);
  }
}
