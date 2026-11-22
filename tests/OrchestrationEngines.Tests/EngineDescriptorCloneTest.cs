using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for <see cref="EngineDescriptor.Clone"/>, the copy a generation switch runs on (#156).
/// </summary>
public class EngineDescriptorCloneTest
{
  private static readonly Func<CancellationToken, Task<IEngine>> Factory = _ => Task.FromResult<IEngine>(null);

  /// <summary>
  /// Verifies that a clone has the source's configuration and a dependency set of its own.
  /// </summary>
  [Fact]
  public void EngineDescriptor_Clone_CopiesTheConfigurationAndNotTheDependencySet()
  {
    var source = new EngineDescriptor("A", Factory, requiredForRunning: true, version: 3)
      .AddDependsOn("B")
      .SetRestartFactory((_, _) => Task.FromResult<IEngine>(null));

    var clone = source.Clone();
    clone.Should().Be(source, "a clone compares equal to its source");
    clone.AddDependsOn("C");

    clone.Should().NotBeSameAs(source);
    clone.RegisteredEngineFactory.Should().BeSameAs(Factory);
    clone.EngineFactory.Should().BeSameAs(Factory);
    clone.RestartFromPrevious.Should().BeSameAs(source.RestartFromPrevious);
    clone.Version.Should().Be(3);
    clone.RequiredForRunning.Should().BeTrue();
    source.DependsOn.Should().BeEquivalentTo(["B"], "a dependency added to the clone is not added to the source");
  }

  /// <summary>
  /// Verifies that a clone given an engine factory creates with it and keeps the source's registered factory, so a
  /// later switch still starts from the factory the descriptor was registered with.
  /// </summary>
  [Fact]
  public void EngineDescriptor_CloneWithAFactory_KeepsTheRegisteredFactory()
  {
    Func<CancellationToken, Task<IEngine>> handover = _ => Task.FromResult<IEngine>(null);
    var source = new EngineDescriptor("A", Factory);

    var clone = source.Clone(handover);

    clone.EngineFactory.Should().BeSameAs(handover);
    clone.RegisteredEngineFactory.Should().BeSameAs(Factory);
  }
}
