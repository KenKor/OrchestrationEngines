using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using Xunit;
using static OrchestrationEngines.Tests.SwitchTestEngines;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for what a coordinator that a generation switch shut down lets go of (#107): the token source of the
/// generation a switch replaces, and of the next generation of a switch that failed after starting it, is no longer
/// registered on the application token, while their public members still answer.
/// </summary>
public class ShutDownCoordinatorReleaseTest
{
  private const string CoordinatorName = nameof(ShutDownCoordinatorReleaseTest);

  /// <summary>
  /// A weak reference to the token source that governs <paramref name="coordinator"/>'s lifetime (its only one), the
  /// object a registration on the application token keeps. The coordinator itself is not kept once its source is
  /// cancelled: a cancelled source drops the callbacks that refer to it.
  /// </summary>
  private static WeakReference TokenSourceOf(EngineCoordinator coordinator) =>
    new(typeof(EngineCoordinator).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
      .Single(f => f.FieldType == typeof(CancellationTokenSource))
      .GetValue(coordinator));

  /// <summary>
  /// Starts a coordinator on <paramref name="applicationToken"/>, switches it, and returns the new generation and a
  /// weak reference to the replaced one's token source. Not inlined, so that no local of the test keeps the replaced
  /// one.
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<(EngineCoordinator Current, WeakReference Replaced)> SwitchOnceAsync(
    CancellationToken applicationToken, CancellationToken ct)
  {
    var coordinator = new EngineCoordinator(CoordinatorName, NullLogger.Instance, applicationToken);
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var current = await coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);
    return (current, TokenSourceOf(coordinator));
  }

  /// <summary>
  /// Verifies that the token source of the generation a switch replaced, once nothing of the host refers to that
  /// generation, is not kept by the application token, which lives on.
  /// </summary>
  [Fact]
  public async Task Switch_ReplacedCoordinator_IsNotKeptByTheApplicationToken()
  {
    var ct = TestContext.Current.CancellationToken;
    using var application = new CancellationTokenSource();

    var (current, replaced) = await SwitchOnceAsync(application.Token, ct);
    try
    {
      (await Wait.UntilCollectedAsync(MustComplete, replaced)).Should().BeTrue("the replaced generation is shut down, and only the "
        + "application token, which is still alive, could keep its token source");
    }
    finally
    {
      await current.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
      current.Dispose();
    }
  }

  /// <summary>
  /// Starts a coordinator on <paramref name="applicationToken"/> and a switch whose next generation fails to start (an
  /// engine depends on one that is not registered); returns the current coordinator and a weak reference to the token
  /// source of the next one, which the switch abandoned. Not inlined, so that no local of the test keeps the next one.
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<(EngineCoordinator Current, WeakReference Next)> FailOneSwitchAsync(
    CancellationToken applicationToken, CancellationToken ct)
  {
    var coordinator = new EngineCoordinator(CoordinatorName, NullLogger.Instance, applicationToken);
    WeakReference nextGeneration = null;
    coordinator.NextGenerationCreated = next => nextGeneration = TokenSourceOf(next);
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var candidates = new List<EngineDescriptor>(coordinator.GetRegisteredDefinitions())
    {
      Unstartable(),
    };

    var thrown = await Record.ExceptionAsync(() =>
      coordinator.RestartWithDefinitionsAsync(candidates).WaitAsync(MustComplete, ct));
    thrown.Should().BeOfType<KeyNotFoundException>();
    coordinator.NextGenerationCreated = null;
    return (coordinator, nextGeneration);
  }

  /// <summary>
  /// Verifies that the token source of the next generation of a switch that failed after it started it is, once that
  /// generation is shut down, not kept by the application token, which lives on.
  /// </summary>
  [Fact]
  public async Task Switch_FailedAfterStart_TheNextCoordinatorIsNotKeptByTheApplicationToken()
  {
    var ct = TestContext.Current.CancellationToken;
    using var application = new CancellationTokenSource();

    var (current, next) = await FailOneSwitchAsync(application.Token, ct);
    try
    {
      next.Should().NotBeNull();
      (await Wait.UntilCollectedAsync(MustComplete, next)).Should().BeTrue("the abandoned generation is shut down, and only the "
        + "application token, which is still alive, could keep its token source");
    }
    finally
    {
      await current.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
      current.Dispose();
    }
  }

  /// <summary>
  /// Verifies that the public members of a replaced coordinator that read its token still answer once it is shut
  /// down, and that disposing it afterwards does not throw.
  /// </summary>
  [Fact]
  public async Task Switch_ReplacedCoordinator_ItsTokenMembersStillAnswer()
  {
    var ct = TestContext.Current.CancellationToken;
    using var application = new CancellationTokenSource();
    var coordinator = new EngineCoordinator(CoordinatorName, NullLogger.Instance, application.Token);
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var current = await coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);
    try
    {
      coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
      coordinator.IsCancellationRequested.Should().BeTrue();
      coordinator.CancellationToken.IsCancellationRequested.Should().BeTrue();
      current.IsCancellationRequested.Should().BeFalse("the application token was not cancelled");
      coordinator.Invoking(c => c.Dispose()).Should().NotThrow();
    }
    finally
    {
      await current.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
      current.Dispose();
    }
  }

  /// <summary>
  /// Verifies that cancelling the application token still shuts down the coordinator that is current, after a switch.
  /// </summary>
  [Fact]
  public async Task Switch_ApplicationTokenCancelled_CancelsTheCurrentGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    using var application = new CancellationTokenSource();
    using var coordinator = new EngineCoordinator(CoordinatorName, NullLogger.Instance,
      application.Token);
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    using var current = await coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
      .WaitAsync(MustComplete, ct);

    application.Cancel();

    current.IsCancellationRequested.Should().BeTrue();
    current.CancellationToken.IsCancellationRequested.Should().BeTrue();
  }
}
