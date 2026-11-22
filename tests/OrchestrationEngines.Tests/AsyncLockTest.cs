using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Helpers;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for <see cref="AsyncLock"/>, the lock that guards a task's triggers.
/// </summary>
public class AsyncLockTest
{
  /// <summary>
  /// Verifies that a free lock is taken at once, again after it was released.
  /// </summary>
  [Fact]
  public async Task LockAsync_Free_CompletesAtOnce()
  {
    var gate = new AsyncLock();

    for (var take = 0; take < 2; take++)
    {
      var taking = gate.LockAsync();
      taking.GetAwaiter().IsCompleted.Should().BeTrue();
      (await taking).Dispose();
    }
  }

  /// <summary>
  /// Verifies that a second take waits while the lock is held, and gets it once the holder releases it.
  /// </summary>
  [Fact]
  public async Task LockAsync_Held_WaitsUntilTheHolderReleases()
  {
    var gate = new AsyncLock();
    var holder = await gate.LockAsync();

    var waiter = TakeAndReleaseAsync(gate);
    waiter.IsCompleted.Should().BeFalse();

    holder.Dispose();
    await waiter.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    var again = gate.LockAsync();
    again.GetAwaiter().IsCompleted.Should().BeTrue("the waiter released it");
    (await again).Dispose();
  }

  private static async Task TakeAndReleaseAsync(AsyncLock gate) => (await gate.LockAsync()).Dispose();
}
