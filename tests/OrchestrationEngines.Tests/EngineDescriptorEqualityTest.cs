using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the equality of engine descriptors: by configuration, with names compared case-insensitively
/// and dependencies by content.
/// </summary>
public class EngineDescriptorEqualityTest
{
  private static readonly Func<CancellationToken, Task<IEngine>> Factory = _ => Task.FromResult<IEngine>(null);

  private static EngineDescriptor Descriptor(string name, params string[] dependsOn)
  {
    var descriptor = new EngineDescriptor(name, Factory, requiredForRunning: true, 1);
    foreach (var dependency in dependsOn)
      descriptor.AddDependsOn(dependency);
    return descriptor;
  }

  /// <summary>
  /// Verifies that descriptors that differ only in the case of names and the order of their dependencies are equal
  /// and have the same hash code.
  /// </summary>
  [Fact]
  public void EngineDescriptor_Equals_SameConfiguration_IsEqualWithTheSameHashCode()
  {
    var descriptor = Descriptor("Patch", "AsBuilt", "Planned");
    var same = Descriptor("PATCH", "planned", "ASBUILT");

    descriptor.Equals(same).Should().BeTrue();
    descriptor.Equals((object)same).Should().BeTrue();
    descriptor.GetHashCode().Should().Be(same.GetHashCode());
  }

  /// <summary>
  /// Verifies that descriptors with other dependencies, or another name, are not equal.
  /// </summary>
  [Fact]
  public void EngineDescriptor_Equals_OtherDependenciesOrName_IsNotEqual()
  {
    var descriptor = Descriptor("Patch", "AsBuilt");

    descriptor.Equals(Descriptor("Patch", "Planned")).Should().BeFalse();
    descriptor.Equals(Descriptor("Patch", "AsBuilt", "Planned")).Should().BeFalse();
    descriptor.Equals(Descriptor("Repair", "AsBuilt")).Should().BeFalse();
    descriptor.Equals(null).Should().BeFalse();
  }
}
