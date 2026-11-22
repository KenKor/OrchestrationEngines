using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Polling waits shared by the tests.
/// </summary>
internal static class Wait
{
  /// <summary>
  /// Polls <paramref name="condition"/> every 10 ms until it holds, and fails the test with
  /// <paramref name="because"/> once <paramref name="timeout"/> has passed.
  /// </summary>
  public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string because)
  {
    var deadline = DateTime.UtcNow + timeout;
    while (!condition())
    {
      DateTime.UtcNow.Should().BeBefore(deadline, because);
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }
  }

  /// <summary>
  /// Collects garbage every 50 ms until none of <paramref name="references"/> is alive, or <paramref name="timeout"/>
  /// has passed; returns whether they are all gone.
  /// </summary>
  public static async Task<bool> UntilCollectedAsync(TimeSpan timeout, params WeakReference[] references)
  {
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
      if (!references.Any(r => r.IsAlive))
        return true;
      if (DateTime.UtcNow >= deadline)
        return false;
      await Task.Delay(50, TestContext.Current.CancellationToken);
    }
  }
}
