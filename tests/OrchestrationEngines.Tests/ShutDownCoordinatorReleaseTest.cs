using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for what a coordinator that a generation switch shut down lets go of (#107): the token source of the
/// generation a switch replaces, and of the next generation of a switch that failed after starting it, is no longer
/// registered on the application token, while their public members still answer; and the clean-up of a failed switch
/// shuts the next generation down on its dispatcher.
/// </summary>
public class ShutDownCoordinatorReleaseTest
{
  private const string Name = "N";
  private const string CoordinatorName = nameof(ShutDownCoordinatorReleaseTest);

  /// <summary>
  /// An engine with one load task, whose work the test decides; counts its loads.
  /// </summary>
  private class NetworkEngine(Func<CancellationToken, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks() => CreateTask("Load", token =>
    {
      Interlocked.Increment(ref _loads);
      return loadAsync(token);
    });
  }

  private static NetworkEngine Quick() => new(_ => Task.CompletedTask);

  private static NetworkEngine Hanging() => new(token => Task.Delay(Timeout.Infinite, token));

  private static EngineDescriptor Descriptor(IEngine first, Func<IEngine> next) =>
    new EngineDescriptor(Name, _ => Task.FromResult(first), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts()
      .SetRestartFactory((_, _) => Task.FromResult(next()));

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
  /// Collects garbage until <paramref name="reference"/> is gone, or the time is up.
  /// </summary>
  private static async Task<bool> IsCollectedAsync(WeakReference reference)
  {
    var deadline = DateTime.UtcNow + MustComplete;
    while (DateTime.UtcNow < deadline)
    {
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
      if (!reference.IsAlive)
        return true;
      await Task.Delay(50);
    }
    return false;
  }

  /// <summary>
  /// Starts a coordinator on <paramref name="applicationToken"/>, switches it, and returns the new generation and a
  /// weak reference to the replaced one's token source. Not inlined, so that no local of the test keeps the replaced
  /// one.
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<(EngineCoordinator Current, WeakReference Replaced)> SwitchOnceAsync(
    CancellationToken applicationToken, bool useDispatcher, CancellationToken ct)
  {
    var coordinator = new EngineCoordinator(CoordinatorName, NullLogger.Instance, useDispatcher, applicationToken);
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var current = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(),
      waitUntilReady: true, ct).WaitAsync(MustComplete, ct);
    return (current, TokenSourceOf(coordinator));
  }

  /// <summary>
  /// Verifies that the token source of the generation a switch replaced, once nothing of the host refers to that
  /// generation, is not kept by the application token, which lives on.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Switch_ReplacedCoordinator_IsNotKeptByTheApplicationToken(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    using var application = new CancellationTokenSource();

    var (current, replaced) = await SwitchOnceAsync(application.Token, useDispatcher, ct);
    try
    {
      (await IsCollectedAsync(replaced)).Should().BeTrue("the replaced generation is shut down, and only the "
        + "application token, which is still alive, could keep its token source");
    }
    finally
    {
      await current.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
      current.Dispose();
    }
  }

  /// <summary>
  /// Starts a coordinator on <paramref name="applicationToken"/> and a switch whose ready-wait is cancelled after the
  /// next generation started; returns the current coordinator and a weak reference to the token source of the next
  /// one, which the switch abandoned. Not inlined, so that no local of the test keeps the next one.
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<(EngineCoordinator Current, WeakReference Next)> FailOneSwitchAsync(
    CancellationToken applicationToken, bool useDispatcher, CancellationToken ct)
  {
    var coordinator = new EngineCoordinator(CoordinatorName, NullLogger.Instance, useDispatcher, applicationToken);
    WeakReference nextGeneration = null;
    coordinator.NextGenerationCreated = next => nextGeneration = TokenSourceOf(next);
    var next = Hanging();
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), () => next));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

    var switching = coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true,
      cancel.Token);
    await Wait.UntilAsync(() => next.Loads == 1, MustComplete, "the next generation starts its engine");
    cancel.Cancel();
    var thrown = await Record.ExceptionAsync(() => switching.WaitAsync(MustComplete, ct));
    thrown.Should().BeAssignableTo<OperationCanceledException>();
    coordinator.NextGenerationCreated = null;
    return (coordinator, nextGeneration);
  }

  /// <summary>
  /// Verifies that the token source of the next generation of a switch that failed after it started it is, once that
  /// generation is shut down, not kept by the application token, which lives on.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Switch_FailedAfterStart_TheNextCoordinatorIsNotKeptByTheApplicationToken(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    using var application = new CancellationTokenSource();

    var (current, next) = await FailOneSwitchAsync(application.Token, useDispatcher, ct);
    try
    {
      next.Should().NotBeNull();
      (await IsCollectedAsync(next)).Should().BeTrue("the abandoned generation is shut down, and only the "
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
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Switch_ReplacedCoordinator_ItsTokenMembersStillAnswer(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    using var application = new CancellationTokenSource();
    var coordinator = new EngineCoordinator(CoordinatorName, NullLogger.Instance, useDispatcher, application.Token);
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var current = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(),
      waitUntilReady: true, ct).WaitAsync(MustComplete, ct);
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
    using var coordinator = new EngineCoordinator(CoordinatorName, NullLogger.Instance, useDispatcher: false,
      application.Token);
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    using var current = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(),
      waitUntilReady: true, ct).WaitAsync(MustComplete, ct);

    application.Cancel();

    current.IsCancellationRequested.Should().BeTrue();
    current.CancellationToken.IsCancellationRequested.Should().BeTrue();
  }

  /// <summary>
  /// Verifies that, with the dispatcher on, the clean-up of a switch that failed after it started the next generation
  /// shuts that generation down on the next generation's dispatcher thread, where its mechanic's monitor ticks run, and
  /// not on the thread of the switch.
  /// </summary>
  [Fact]
  public async Task Switch_FailedAfterStart_ShutsTheNextGenerationDownOnItsDispatcher()
  {
    var ct = TestContext.Current.CancellationToken;
    string shutdownThread = null;
    var logger = new OnMessageLogger(
      (_, message, _) => message.Contains($"'{CoordinatorName}>': Coordinator transition")
        && message.Contains("StartShutdown"),
      () => shutdownThread = Thread.CurrentThread.Name ?? "(unnamed)");
    using var coordinator = new EngineCoordinator(CoordinatorName, logger, useDispatcher: true);
    var next = Hanging();
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), () => next));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

    var switching = coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true,
      cancel.Token);
    await Wait.UntilAsync(() => next.Loads == 1, MustComplete, "the next generation starts its engine");
    cancel.Cancel();
    var thrown = await Record.ExceptionAsync(() => switching.WaitAsync(MustComplete, ct));

    thrown.Should().BeAssignableTo<OperationCanceledException>();
    shutdownThread.Should().Be("EngineMechanicThread", "the next generation's mechanic is stopped on its dispatcher");
    next.IsCancellationRequested.Should().BeTrue("the next generation's engines are stopped");
  }
}
