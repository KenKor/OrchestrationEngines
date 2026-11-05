using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Runs tests that keep threads busy on their own, so that they do not starve the tests that run in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StressCollection
{
  /// <summary>
  /// The collection's name.
  /// </summary>
  public const string Name = "Stress";
}

/// <summary>
/// Contains a stress test for the task set of an engine that a start replaces while other threads read it.
/// </summary>
[Collection(StressCollection.Name)]
public class TaskSetConcurrencyTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine with many quick tasks, so that building its task set takes a while.
  /// </summary>
  private sealed class ManyTasksEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks()
    {
      for (var i = 0; i < 200; i++)
        CreateTask($"Task{i}", _ => Task.CompletedTask);
    }
  }

  /// <summary>
  /// Verifies that the task set can be read and enumerated by another thread (as the states endpoint, state recomputes
  /// and queries do) while the engine rebuilds it again and again, without "collection was modified", and that a reader
  /// always sees a complete set.
  /// </summary>
  [Fact]
  public async Task BaseEngine_Tasks_ReadWhileRebuilt_IsAlwaysAWholeSet()
  {
    var engine = new ManyTasksEngine { Context = new NoContext() };
    await engine.StartAsync();
    using var stop = new CancellationTokenSource();
    var sizes = new System.Collections.Concurrent.ConcurrentBag<int>();
    var reader = Task.Run(() =>
    {
      while (!stop.IsCancellationRequested)
      {
        var count = 0;
        foreach (var task in engine.Tasks.Values)
          count += task.Name.Length > 0 ? 1 : 0;
        sizes.Add(count);
        _ = engine.IsRequiredCompletedWithSuccess();
      }
    }, TestContext.Current.CancellationToken);

    for (var i = 0; i < 25; i++)
      await engine.StartAsync();
    stop.Cancel();

    await reader.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    sizes.Should().NotBeEmpty().And.OnlyContain(size => size == 200, "a reader never sees a set that is being built");
  }
}
