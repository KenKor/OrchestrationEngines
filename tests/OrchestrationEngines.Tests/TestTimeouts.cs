// Every test file sees MustComplete without a using of its own. A class that needs a longer bound declares its own
// MustComplete, which hides this one.
global using static OrchestrationEngines.Tests.TestTimeouts;

using System;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Time bounds shared by the tests.
/// </summary>
internal static class TestTimeouts
{
  /// <summary>
  /// How long a test waits for something that must complete before it fails the test.
  /// </summary>
  public static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
}
