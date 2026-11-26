using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Engines and a descriptor shared by the generation-switch tests: one engine named <see cref="Name"/>, whose next
/// engine in a switch the test gives.
/// </summary>
internal static class SwitchTestEngines
{
  /// <summary>
  /// The name of the descriptor <see cref="Descriptor"/> builds.
  /// </summary>
  public const string Name = "N";

  /// <summary>
  /// An engine with one load task, whose work the test decides; counts its loads.
  /// </summary>
  internal sealed class NetworkEngine(Func<CancellationToken, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks() => CreateTask("Load", token =>
    {
      Interlocked.Increment(ref _loads);
      return loadAsync(token);
    });
  }

  /// <summary>
  /// An engine whose load completes at once.
  /// </summary>
  public static NetworkEngine Quick() => new(_ => Task.CompletedTask);

  /// <summary>
  /// An engine whose load runs until it is stopped.
  /// </summary>
  public static NetworkEngine Hanging() => new(token => Task.Delay(Timeout.Infinite, token));

  /// <summary>
  /// A descriptor whose first engine is <paramref name="first"/> and whose next one, in a switch, comes from
  /// <paramref name="next"/>; without <paramref name="next"/> a switch builds the engine from scratch.
  /// </summary>
  public static EngineDescriptor Descriptor(IEngine first, Func<IEngine> next = null)
  {
    var descriptor = new EngineDescriptor(Name, _ => Task.FromResult(first), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    return next == null ? descriptor : descriptor.SetRestartFactory((_, _) => Task.FromResult(next()));
  }

  /// <summary>
  /// A descriptor whose task graph names an engine that is not registered, so a next coordinator that has it fails to
  /// start (a <see cref="System.Collections.Generic.KeyNotFoundException"/>).
  /// </summary>
  public static EngineDescriptor Unstartable() =>
    new EngineDescriptor("Planned", _ => Task.FromResult<IEngine>(Quick()), requiredForRunning: true, 1)
      .AddDependsOn("NotRegistered");
}
