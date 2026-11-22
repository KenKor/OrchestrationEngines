using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Mechanic;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the equality of mechanic contexts: by the names in their descriptor sets.
/// </summary>
public class MechanicContextEqualityTest
{
  private static readonly Func<CancellationToken, Task<IEngine>> Factory = _ => Task.FromResult<IEngine>(null);

  private static MechanicContext Context(params string[] names)
  {
    var context = new MechanicContext();
    foreach (var name in names)
      context.EngineDescriptors.Add(new EngineDescriptor(name, Factory, requiredForRunning: true, 1));
    return context;
  }

  /// <summary>
  /// Verifies that contexts with the same descriptors, added in another order and with names in another case, are
  /// equal and have the same hash code.
  /// </summary>
  [Fact]
  public void MechanicContext_Equals_SameDescriptors_IsEqualWithTheSameHashCode()
  {
    var context = Context("Patch", "AsBuilt", "Planned");
    var same = Context("PLANNED", "patch", "AsBuilt");

    context.Equals(same).Should().BeTrue();
    context.Equals((object)same).Should().BeTrue();
    context.GetHashCode().Should().Be(same.GetHashCode(), "equal contexts must have equal hash codes");
  }

  /// <summary>
  /// Verifies that contexts with other descriptors are not equal, and that comparing with <c>null</c> is false.
  /// </summary>
  [Fact]
  public void MechanicContext_Equals_OtherDescriptorsOrNull_IsNotEqual()
  {
    var context = Context("Patch", "AsBuilt");

    context.Equals(Context("Patch")).Should().BeFalse();
    context.Equals(Context("Patch", "Planned")).Should().BeFalse();
    context.Equals((MechanicContext)null).Should().BeFalse();
    context.Equals((IEngineContext)null).Should().BeFalse();
    context.Equals((object)null).Should().BeFalse();
  }
}
