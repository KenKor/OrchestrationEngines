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
}
