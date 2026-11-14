using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for how a descriptor's name and version are set (#66): the name only when it is made, and the version
/// only through the constructor and <see cref="EngineDescriptor.SetVersion"/>, which both check it.
/// </summary>
public class EngineDescriptorIdentityTest
{
  private static readonly Func<CancellationToken, Task<IEngine>> Factory = _ => Task.FromResult<IEngine>(null);

  /// <summary>
  /// Verifies that a null name given in an object initializer is rejected, as the constructor rejects it.
  /// </summary>
  [Fact]
  public void EngineDescriptor_NameInitializedToNull_Throws()
  {
    var make = () => new EngineDescriptor("A", Factory) { Name = null };

    make.Should().Throw<ArgumentNullException>();
  }

  /// <summary>
  /// Verifies that the name can only be set when the descriptor is made (an init accessor): a registered descriptor
  /// cannot be renamed, which would leave the registry keyed by its old name.
  /// </summary>
  [Fact]
  public void EngineDescriptor_Name_IsInitOnly()
  {
    var setter = typeof(EngineDescriptor).GetProperty(nameof(EngineDescriptor.Name)).SetMethod;

    setter.Should().NotBeNull();
    setter.ReturnParameter.GetRequiredCustomModifiers()
      .Should().Contain(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
  }

  /// <summary>
  /// Verifies that the version has no public setter, so that a version below 0 cannot be set without the check of the
  /// constructor and <see cref="EngineDescriptor.SetVersion"/>.
  /// </summary>
  [Fact]
  public void EngineDescriptor_Version_HasNoPublicSetter()
  {
    var setter = typeof(EngineDescriptor).GetProperty(nameof(EngineDescriptor.Version)).SetMethod;

    (setter == null || !setter.IsPublic).Should().BeTrue();
  }

  /// <summary>
  /// Verifies that a version below 0 is rejected by the constructor and by <see cref="EngineDescriptor.SetVersion"/>.
  /// </summary>
  [Fact]
  public void EngineDescriptor_NegativeVersion_Throws()
  {
    var make = () => new EngineDescriptor("A", Factory, version: -1);
    var set = () => new EngineDescriptor("A", Factory).SetVersion(-1);

    make.Should().Throw<ArgumentOutOfRangeException>();
    set.Should().Throw<ArgumentOutOfRangeException>();
  }
}
