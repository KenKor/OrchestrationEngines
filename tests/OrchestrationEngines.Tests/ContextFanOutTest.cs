using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for one engine feeding several dependents, the shape of a host whose session (an open workspace,
/// a connection) is an engine the others build on: the dependents load with the session's context, and a refreshed
/// context reaches them without a reload.
/// </summary>
public class ContextFanOutTest
{
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
    new(nameof(ContextFanOutTest), NullLogger.Instance);

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
  /// Verifies that a context refresh for an engine named in a different case still gives the engine its underlying
  /// engines: engine names are case-insensitive everywhere, also where the refresh looks up the engine's dependencies.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_NameInOtherCase_KeepsTheUnderlyingEngines()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, session, kernel, _, _) = await StartHostAsync(() => new ValueContext("workspace-A"));

    await coordinator.RefreshEngineContextAsync("KERNEL", new ValueContext("kernel-B"), ct).WaitAsync(MustComplete, ct);

    kernel.Context.Inner.Should().BeOfType<ValueContext>().Which.Value.Should().Be("kernel-B");
    kernel.Context.GetUnderlyingEngine("Session").Should().BeSameAs(session);
  }
}
