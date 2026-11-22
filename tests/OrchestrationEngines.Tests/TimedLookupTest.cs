using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Mechanic;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a lookup of an engine that never settles, ended by its time-out or by the caller (#156): the time
/// it waits comes from the mechanic's time provider, and the wait inside it ends with it.
/// </summary>
public class TimedLookupTest
{
  private const string Name = "Hanging";

  /// <summary>An engine factory that does not return until it is cancelled: the engine never settles.</summary>
  private static async Task<IEngine> NeverCreated(CancellationToken token)
  {
    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
    return null;
  }

  /// <summary>A started mechanic with one engine that never settles.</summary>
  private static async Task<MechanicEngine> StartMechanicAsync(TimeProvider clock)
  {
    var context = new MechanicContext();
    context.EngineDescriptors.Add(new EngineDescriptor(Name, NeverCreated, requiredForRunning: true, 1));
    var mechanic = new MechanicEngine(NullLogger.Instance) { TimeProvider = clock, Context = context };
    await mechanic.StartAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    return mechanic;
  }

  /// <summary>
  /// Starts a lookup under a copy of <see cref="Name"/> that only the lookup holds, and returns a weak reference to
  /// that copy with the lookup. Not inlined, so the copy is not kept alive by a local of the test.
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static (WeakReference Name, Task<IEngine> Lookup) StartLookup(Func<string, Task<IEngine>> start)
  {
    var name = new string(Name.ToCharArray());
    return (new WeakReference(name), start(name));
  }

  /// <summary>
  /// Verifies that a timed lookup measures its time-out on the mechanic's time provider: it answers null once that
  /// clock has passed the time-out, however little real time went by.
  /// </summary>
  [Fact]
  public async Task MechanicEngine_TimedLookup_TimesOutOnTheMechanicsClock()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var mechanic = await StartMechanicAsync(clock);
    var timersBefore = clock.WaitingTimers;

    var lookup = mechanic.GetEngineWhenReadyAsync(Name, (int)TimeSpan.FromMinutes(10).TotalMilliseconds, ct);
    await Wait.UntilAsync(() => clock.WaitingTimers > timersBefore, MustComplete,
      "the lookup must wait on the mechanic's clock");
    lookup.IsCompleted.Should().BeFalse("the engine never settles and the clock has not moved");
    clock.Advance(TimeSpan.FromMinutes(10));

    (await lookup.WaitAsync(MustComplete, ct)).Should().BeNull();
    clock.WaitingTimers.Should().Be(timersBefore, "the lookup's timer ends with it");
    await mechanic.StopAsync().WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a timed lookup that ends, by its time-out or by the caller's cancellation, also ends the wait inside
  /// it: polling an engine that never settles does not pile up waiters.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task MechanicEngine_TimedLookupEnds_ReleasesItsInnerWait(bool cancelled)
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var mechanic = await StartMechanicAsync(clock);
    using var caller = new CancellationTokenSource();
    var timersBefore = clock.WaitingTimers;

    var (name, lookup) = StartLookup(
      n => mechanic.GetEngineWhenReadyAsync(n, (int)TimeSpan.FromMinutes(10).TotalMilliseconds, caller.Token));
    await Wait.UntilAsync(() => clock.WaitingTimers > timersBefore, MustComplete, "the lookup must be waiting");
    if (cancelled)
    {
      caller.Cancel();
      (await lookup.Invoking(l => l.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>())
        .Which.CancellationToken.Should().Be(caller.Token);
    }
    else
    {
      clock.Advance(TimeSpan.FromMinutes(10));
      (await lookup.WaitAsync(MustComplete, ct)).Should().BeNull();
    }

    (await Wait.UntilCollectedAsync(MustComplete, name)).Should().BeTrue(
      "nothing may still wait for the engine once the lookup has ended");
    await mechanic.StopAsync().WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that an untimed lookup on the coordinator that the caller cancels also ends the wait inside it.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_CancelledLookup_ReleasesItsInnerWait()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator(nameof(TimedLookupTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine(Name, NeverCreated);
    await coordinator.StartAsync().WaitAsync(MustComplete, ct);
    using var caller = new CancellationTokenSource();

    var (name, lookup) = StartLookup(n => coordinator.GetEngineAsync(n, caller.Token));
    caller.Cancel();
    (await lookup.Invoking(l => l.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>())
      .Which.CancellationToken.Should().Be(caller.Token);

    (await Wait.UntilCollectedAsync(MustComplete, name)).Should().BeTrue(
      "nothing may still wait for the engine once the lookup has ended");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
