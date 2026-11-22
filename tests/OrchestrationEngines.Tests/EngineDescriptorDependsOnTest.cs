using System;
using AwesomeAssertions;
using OrchestrationEngines.Coordinator;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for declaring a descriptor's dependencies with <see cref="EngineDescriptor.AddDependsOn"/>.
/// </summary>
public class EngineDescriptorDependsOnTest
{
  /// <summary>
  /// Verifies that a null dependency name is rejected where it is added, and is not added.
  /// </summary>
  [Fact]
  public void EngineDescriptor_AddDependsOnNull_ThrowsArgumentNullExceptionForTheParameter()
  {
    var descriptor = new EngineDescriptor("Patch", DescriptorTestExtensions.NullEngineFactory);

    var add = () => descriptor.AddDependsOn(null);

    add.Should().Throw<ArgumentNullException>().WithParameterName("parentEngineName");
    descriptor.DependsOn.Should().BeEmpty();
  }
}
