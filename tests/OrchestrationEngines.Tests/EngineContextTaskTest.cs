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
/// Contains tests for <see cref="EngineContextTask"/>, the task that computes an engine's context.
/// </summary>
public class EngineContextTaskTest
{
  private sealed class ContextTask(MechanicEngine mechanic, EngineDescriptor descriptor)
    : EngineContextTask(mechanic, descriptor, NullLogger.Instance, CancellationToken.None)
  {
    public Task ComputeAsync(CancellationToken token) => CreateContextAsync(Mechanic, Descriptor, token);

    private MechanicEngine Mechanic { get; } = mechanic;
  }

  /// <summary>
  /// Verifies that a context factory that returns no context fails the task with an error that says so, and names the
  /// engine.
  /// </summary>
  [Fact]
  public async Task EngineContextTask_ContextFactoryReturnsNull_ThrowsAnErrorThatSaysSo()
  {
    var ct = TestContext.Current.CancellationToken;
    var descriptor = new EngineDescriptor("Network", DescriptorTestExtensions.NullEngineFactory)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(null));
    var task = new ContextTask(new MechanicEngine(NullLogger.Instance), descriptor);

    var thrown = await task.Awaiting(t => t.ComputeAsync(ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<InvalidEngineContextException>();

    thrown.Which.EngineName.Should().Be("Network");
    thrown.Which.Message.Should().Be("The context factory of engine 'Network' returned no context.");
  }
}
