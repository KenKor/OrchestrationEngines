using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for one engine feeding several dependents, the shape of a host whose session (an open workspace,
/// a connection) is an engine the others build on: replacing the session's context must reload every dependent
/// with the new context, leave the independent engines alone, and keep the coordinator Running throughout.
/// </summary>
public class ContextFanOutTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// A context that carries a value, for example the path of the open workspace.
  /// </summary>
  private sealed class ValueContext(string value) : BaseEngineContext
  {
    public string Value { get; } = value;

    public override bool Equals(IEngineContext other) => other is ValueContext o && o.Value == Value;

    public override bool Equals(object obj) => obj is IEngineContext ctx && Equals(ctx);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
  }

  /// <summary>
  /// An engine whose load records the value of the context it loaded with: its own, or when it depends on the
  /// session engine, the session's. The session engine is reached through the context, as the Engine wires it.
  /// </summary>
  private sealed class RecordingEngine(string sessionName = null) : BaseEngine(NullLogger.Instance)
  {
    private readonly ConcurrentQueue<string> _loadedWith = new();

    public IReadOnlyCollection<string> LoadedWith => _loadedWith;

    protected override void SetUpTasks() => CreateTask("Load", _ =>
    {
      var source = sessionName == null ? this : (RecordingEngine)Context.GetUnderlyingEngine(sessionName);
      _loadedWith.Enqueue((source.Context.Inner ?? source.Context) is ValueContext v ? v.Value : "<none>");
      return Task.CompletedTask;
    });
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(ContextFanOutTest), NullLogger.Instance, useDispatcher: false);

  private static EngineDescriptor Register(EngineCoordinator coordinator, string name, IEngine engine,
    Func<IEngineContext> context = null, string dependsOn = null)
  {
    var descriptor = coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult(context?.Invoke() ?? new NoContext()));
    if (dependsOn != null)
      descriptor.AddDependsOn(dependsOn);
    return descriptor;
  }

  /// <summary>
  /// Sets up a session engine with two dependents and one independent engine, and starts them all.
  /// </summary>
  private static async Task<(EngineCoordinator coordinator, RecordingEngine session, RecordingEngine kernel,
    RecordingEngine assistant, RecordingEngine map)> StartHostAsync(Func<IEngineContext> sessionContext)
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var session = new RecordingEngine();
    var kernel = new RecordingEngine("Session");
    var assistant = new RecordingEngine("Session");
    var map = new RecordingEngine();
    Register(coordinator, "Session", session, sessionContext);
    Register(coordinator, "Kernel", kernel, dependsOn: "Session");
    Register(coordinator, "Assistant", assistant, dependsOn: "Session");
    Register(coordinator, "Map", map);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
    return (coordinator, session, kernel, assistant, map);
  }

  /// <summary>
  /// Verifies that the dependents load with the session's context, and the independent engine with its own.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_StartAsync_DependentsLoadWithTheSessionContext()
  {
    var (_, session, kernel, assistant, map) = await StartHostAsync(() => new ValueContext("workspace-A"));

    session.LoadedWith.Should().Equal("workspace-A");
    kernel.LoadedWith.Should().Equal("workspace-A");
    assistant.LoadedWith.Should().Equal("workspace-A");
    map.LoadedWith.Should().Equal("<none>");
  }

  /// <summary>
  /// Verifies that invalidating the session's context (the host opened another workspace) reloads both dependents
  /// with the context the factory now produces, leaves the independent engine alone, and ends Running again.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_Session_ReloadsAllDependentsWithTheNewContext()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var (coordinator, session, kernel, assistant, map) = await StartHostAsync(() => new ValueContext(current));

    current = "workspace-B";
    await coordinator.InvalidateContextForEngineAsync("Session", ct).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    session.LoadedWith.Should().Equal("workspace-A", "workspace-B");
    kernel.LoadedWith.Should().Equal("workspace-A", "workspace-B");
    assistant.LoadedWith.Should().Equal("workspace-A", "workspace-B");
    map.LoadedWith.Should().ContainSingle("the map does not depend on the session");
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
    coordinator.GetEngineIfReady("Kernel").Should().BeSameAs(kernel);
    coordinator.GetEngineIfReady("Assistant").Should().BeSameAs(assistant);
  }

  /// <summary>
  /// Verifies that refreshing the session with a context the host supplies pushes that context into the live
  /// session engine without reloading anything: the dependents read the new value through the session engine the
  /// next time they look, and the coordinator stays Running. (Reloading on a refresh is the engine's own decision,
  /// in its ContextChanged hook.)
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_Session_PushesTheContextWithoutReloading()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, session, kernel, assistant, map) = await StartHostAsync(() => new ValueContext("workspace-A"));
    var supplied = new ValueContext("workspace-C");

    await coordinator.RefreshEngineContextAsync("Session", supplied, ct).WaitAsync(MustComplete, ct);

    session.Context.Should().BeSameAs(supplied);
    ((RecordingEngine)kernel.Context.GetUnderlyingEngine("Session")).Context.Should().BeSameAs(supplied);
    foreach (var engine in new[] { session, kernel, assistant, map })
    {
      engine.LoadedWith.Should().ContainSingle("a refresh does not reload");
      engine.State.Should().Be(EngineState.Ready);
    }
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that invalidating one dependent reloads only that dependent: the session, its other dependent and
  /// the independent engine keep their loads.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_OneDependent_LeavesTheOthersLoaded()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, session, kernel, assistant, map) = await StartHostAsync(() => new ValueContext("workspace-A"));

    await coordinator.InvalidateContextForEngineAsync("Kernel", ct).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    kernel.LoadedWith.Should().HaveCount(2);
    session.LoadedWith.Should().HaveCount(1);
    assistant.LoadedWith.Should().HaveCount(1);
    map.LoadedWith.Should().HaveCount(1);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that switching the session twice in a row ends with every dependent loaded with the last context
  /// and nothing stuck in between.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_SessionTwice_EndsWithTheLastContext()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var (coordinator, session, kernel, assistant, _) = await StartHostAsync(() => new ValueContext(current));

    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await Task.WhenAll(first, second).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var last = new List<RecordingEngine> { session, kernel, assistant };
    last.Should().AllSatisfy(engine =>
    {
      engine.State.Should().Be(EngineState.Ready);
      engine.LoadedWith.Should().EndWith("workspace-C");
    });
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }
}
