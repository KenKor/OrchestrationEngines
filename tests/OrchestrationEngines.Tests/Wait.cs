using System;
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
}
