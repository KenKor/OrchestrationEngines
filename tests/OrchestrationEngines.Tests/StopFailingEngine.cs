using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;

namespace OrchestrationEngines.Tests;

/// <summary>
/// An engine with one load task, whose stop fails with <paramref name="failure"/> after the engine has stopped (without
/// <paramref name="failure"/>, the stop does not fail). The load
/// runs <paramref name="loadAsync"/>, or ends at once without it; the engine counts its loads. With
/// <paramref name="holdStop"/>, the stop waits for <see cref="ReleaseStop"/> before it ends, so a test can act while a
/// stop is in progress.
/// </summary>
internal sealed class StopFailingEngine(
  Exception failure, Func<CancellationToken, Task> loadAsync = null, bool holdStop = false)
  : BaseEngine(NullLogger.Instance)
{
  private int _loads;

  /// <summary>
  /// The loads that have begun.
  /// </summary>
  public int Loads => Volatile.Read(ref _loads);

  /// <summary>
  /// Completes once a stop has stopped the engine, before it fails.
  /// </summary>
  public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>
  /// Lets a held stop fail; completed from the start without <c>holdStop</c>.
  /// </summary>
  public TaskCompletionSource ReleaseStop { get; } = NewReleaseStop(holdStop);

  private static TaskCompletionSource NewReleaseStop(bool holdStop)
  {
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    if (!holdStop)
      release.SetResult();
    return release;
  }

  protected override void SetUpTasks() => CreateTask("Load", token =>
  {
    Interlocked.Increment(ref _loads);
    return loadAsync?.Invoke(token) ?? Task.CompletedTask;
  });

  public override async Task StopAsync()
  {
    await base.StopAsync().ConfigureAwait(false);
    Stopping.TrySetResult();
    await ReleaseStop.Task.ConfigureAwait(false);
    if (failure != null)
      throw failure;
  }
}
