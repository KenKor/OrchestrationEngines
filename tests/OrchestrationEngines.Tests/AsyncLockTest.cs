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
  /// Verifies that a free lock is taken at once, and that every such take hands out the same completed task, so an
  /// uncontended take allocates nothing.
  /// </summary>
  [Fact]
  public async Task LockAsync_Free_CompletesAtOnceWithoutANewTask()
  {
    var gate = new AsyncLock();

    var first = gate.LockAsync();
    first.IsCompleted.Should().BeTrue();
    (await first).Dispose();
    var second = gate.LockAsync();
    (await second).Dispose();

    second.Should().BeSameAs(first);
  }

  /// <summary>
  /// Verifies that a second take waits while the lock is held, and gets it once the holder releases it.
  /// </summary>
  [Fact]
  public async Task LockAsync_Held_WaitsUntilTheHolderReleases()
  {
    var gate = new AsyncLock();
    var holder = await gate.LockAsync();

    var waiter = gate.LockAsync();
    waiter.IsCompleted.Should().BeFalse();

    holder.Dispose();
    (await waiter.WaitAsync(MustComplete, TestContext.Current.CancellationToken)).Dispose();
    gate.LockAsync().IsCompleted.Should().BeTrue("the waiter released it");
  }
}
