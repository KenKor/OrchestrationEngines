using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;
using static OrchestrationEngines.Tests.SwitchTestEngines;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a timed lookup that a generation switch passes on to the next generation (#156): it keeps the
/// deadline it started with, in the next generation and while it waits for that generation to start.
/// </summary>
public class LookupAcrossGenerationsTest
{
  private static readonly TimeSpan TimeOut = TimeSpan.FromMinutes(10);

  /// <summary>An engine whose load never ends and whose stop waits for <see cref="ReleaseStop"/>.</summary>
  private sealed class HeldStopEngine() : BaseEngine(NullLogger.Instance)
  {
    public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override void SetUpTasks() => CreateTask("Load", token => Task.Delay(Timeout.Infinite, token));

    public override async Task StopAsync()
    {
      await base.StopAsync().ConfigureAwait(false);
      Stopping.TrySetResult();
      await ReleaseStop.Task.ConfigureAwait(false);
    }
  }

  /// <summary>
  /// A started coordinator on <paramref name="clock"/> with the engine <paramref name="descriptor"/>.
  /// </summary>
  private static async Task<EngineCoordinator> StartAsync(ManualTimeProvider clock, EngineDescriptor descriptor)
  {
    var coordinator = new EngineCoordinator(
      nameof(LookupAcrossGenerationsTest), NullLogger.Instance, useDispatcher: false)
    {
      TimeProvider = clock,
    };
    coordinator.RegisterEngineDescriptor(descriptor);
    await coordinator.StartAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    return coordinator;
  }

  /// <summary>
  /// Starts a lookup with a time-out of <see cref="TimeOut"/>, waits until it waits on the clock, and uses all but one
  /// minute of it; returns the lookup and its deadline.
  /// </summary>
  private static async Task<(Task<IEngine> Lookup, DateTimeOffset Deadline)> StartLookupAsync(
    EngineCoordinator coordinator, ManualTimeProvider clock)
  {
    var deadline = clock.GetUtcNow() + TimeOut;
    var lookup = coordinator.GetEngineAsync(
      Name, (int)TimeOut.TotalMilliseconds, TestContext.Current.CancellationToken);
    await Wait.UntilAsync(() => clock.DueTimesSince(0).Contains(deadline), MustComplete, "the lookup must be waiting");
    clock.Advance(TimeOut - TimeSpan.FromMinutes(1));
    return (lookup, deadline);
  }

  /// <summary>
  /// Waits until a timer created since <paramref name="timersBefore"/> is due at <paramref name="deadline"/>, then
  /// moves the clock to it and verifies that the lookup answers null.
  /// </summary>
  private static async Task EndsAtTheDeadlineAsync(
    Task<IEngine> lookup, ManualTimeProvider clock, int timersBefore, DateTimeOffset deadline, string because)
  {
    await Wait.UntilAsync(() => lookup.IsCompleted || clock.DueTimesSince(timersBefore).Contains(deadline),
      MustComplete, because);
    lookup.IsCompleted.Should().BeFalse("the engine never gets Ready and the deadline has not passed");
    clock.Advance(deadline - clock.GetUtcNow());
    (await lookup.WaitAsync(MustComplete, TestContext.Current.CancellationToken)).Should().BeNull();
  }

  /// <summary>
  /// Verifies that a timed lookup waiting on an engine that a switch replaces goes on in the next generation with the
  /// time it has left: it ends at the deadline it started with.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_TimedLookupPassedToTheNextGeneration_KeepsItsDeadline()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var coordinator = await StartAsync(clock, Descriptor(Hanging(), Hanging));
    var (lookup, deadline) = await StartLookupAsync(coordinator, clock);

    var timersBeforeSwitch = clock.TimersCreated;
    var next = await coordinator
      .RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct)
      .WaitAsync(MustComplete, ct);

    await EndsAtTheDeadlineAsync(lookup, clock, timersBeforeSwitch, deadline,
      "the next generation must wait until the lookup's first deadline, not a whole time-out after the switch");
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a timed lookup waiting for a rebuilding switch's next generation to start, while the switch still
  /// stops the previous engine, ends at its deadline too.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_TimedLookupWaitingForTheNextGenerationToStart_KeepsItsDeadline()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var first = new HeldStopEngine();
    var created = 0;
    var descriptor = new EngineDescriptor(Name,
        _ => Task.FromResult<IEngine>(Interlocked.Increment(ref created) == 1 ? first : Hanging()),
        requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    var coordinator = await StartAsync(clock, descriptor);
    var (lookup, deadline) = await StartLookupAsync(coordinator, clock);

    var timersBeforeSwitch = clock.TimersCreated;
    var switching = coordinator.RestartWithDefinitionsAsync(
      coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct);
    await first.Stopping.Task.WaitAsync(MustComplete, ct);

    await EndsAtTheDeadlineAsync(lookup, clock, timersBeforeSwitch, deadline,
      "the wait for the next generation to start must end at the lookup's deadline");
    switching.IsCompleted.Should().BeFalse("the previous engine is still stopping");
    first.ReleaseStop.SetResult();
    var next = await switching.WaitAsync(MustComplete, ct);
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
