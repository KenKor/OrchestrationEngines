using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the definition diff of a generation switch: which engines must load again after a version
/// bump. Engines in a typical host are either independent, or data patches built on an underlying network (an engine that
/// depends on the engine of that network), possibly stacked.
/// </summary>
public class EngineDefinitionDiffTest
{
  private static EngineDescriptor Descriptor(string name, int version = 1, string dependsOn = null)
  {
    var descriptor = new EngineDescriptor(name, _ => Task.FromResult<IEngine>(null), requiredForRunning: true, version);
    if (dependsOn != null)
      descriptor.AddDependsOn(dependsOn);
    return descriptor;
  }

  // Maps both lists by name as a generation switch does, then diffs.
  private static EngineDefinitionDiff Diff(EngineDescriptor[] current, EngineDescriptor[] candidates)
    => EngineDefinitionDiff.Create(
      EngineDefinitionDiff.ToDictionaryByName(current, "current"),
      EngineDefinitionDiff.ToDictionaryByName(candidates, "candidates"));

  /// <summary>
  /// Verifies that a version bump of one network leaves an independent network alone, also when the bumped one is
  /// named in another case: engine names are case-insensitive across generations.
  /// </summary>
  [Fact]
  public void EngineDefinitionDiff_Create_VersionBumpOnOneNetwork_LeavesIndependentNetworkUnchanged()
  {
    var diff = Diff(
      [Descriptor("North"), Descriptor("South")],
      [Descriptor("NORTH", version: 2), Descriptor("South")]);

    diff.VersionChangedEngineNames.Should().Equal("NORTH");
    diff.RestartRequiredEngineNames.Should().Equal("NORTH");
  }

  /// <summary>
  /// Verifies that a version bump of the as-built network marks the whole stack of data patches on it as changed:
  /// the patch on it, and the patch on that patch.
  /// </summary>
  [Fact]
  public void EngineDefinitionDiff_Create_VersionBumpOnBase_MarksWholePatchChainChanged()
  {
    var diff = Diff(
      [Descriptor("AsBuilt"), Descriptor("Patch", dependsOn: "AsBuilt"), Descriptor("PatchOnPatch", dependsOn: "Patch")],
      [Descriptor("AsBuilt", version: 2), Descriptor("Patch", dependsOn: "AsBuilt"), Descriptor("PatchOnPatch", dependsOn: "Patch")]);

    diff.VersionChangedEngineNames.Should().Equal("AsBuilt");
    diff.RestartRequiredEngineNames.Should().BeEquivalentTo("AsBuilt", "Patch", "PatchOnPatch");
  }

  /// <summary>
  /// Verifies that a version bump of a data patch in the middle of a stack marks it and the patch on it as changed,
  /// but not the network it is built on.
  /// </summary>
  [Fact]
  public void EngineDefinitionDiff_Create_VersionBumpOnMiddlePatch_LeavesBaseUnchanged()
  {
    var diff = Diff(
      [Descriptor("AsBuilt"), Descriptor("Patch", dependsOn: "AsBuilt"), Descriptor("PatchOnPatch", dependsOn: "Patch")],
      [Descriptor("AsBuilt"), Descriptor("Patch", version: 2, dependsOn: "AsBuilt"), Descriptor("PatchOnPatch", dependsOn: "Patch")]);

    diff.RestartRequiredEngineNames.Should().BeEquivalentTo("Patch", "PatchOnPatch");
  }

  /// <summary>
  /// Verifies that a version bump of one of two data patches on the same network leaves the other patch and the
  /// network alone.
  /// </summary>
  [Fact]
  public void EngineDefinitionDiff_Create_TwoPatchesOnOneBase_BumpOneLeavesTheOtherUnchanged()
  {
    var diff = Diff(
      [Descriptor("AsBuilt"), Descriptor("Planned", dependsOn: "AsBuilt"), Descriptor("Repair", dependsOn: "AsBuilt")],
      [Descriptor("AsBuilt"), Descriptor("Planned", version: 2, dependsOn: "AsBuilt"), Descriptor("Repair", dependsOn: "AsBuilt")]);

    diff.RestartRequiredEngineNames.Should().Equal("Planned");
  }

  /// <summary>
  /// Verifies that two descriptors with the same name in a different case are rejected: engine names are
  /// case-insensitive, so the diff could not tell which one is meant.
  /// </summary>
  [Fact]
  public void EngineDefinitionDiff_Create_DuplicateNameInOtherCase_Throws()
  {
    var create = () => Diff(
      [Descriptor("North")],
      [Descriptor("North"), Descriptor("NORTH", version: 2)]);

    create.Should().Throw<System.ArgumentException>();
  }

  /// <summary>
  /// Verifies that an engine reached through two paths (a patch on two patches of one base) is restarted once.
  /// </summary>
  [Fact]
  public void EngineDefinitionDiff_Create_Diamond_RestartsTheBottomOnce()
  {
    var bottom = Descriptor("Merged", dependsOn: "Planned").AddDependsOn("Repair");
    var diff = Diff(
      [Descriptor("AsBuilt"), Descriptor("Planned", dependsOn: "AsBuilt"), Descriptor("Repair", dependsOn: "AsBuilt"),
        Descriptor("Merged", dependsOn: "Planned").AddDependsOn("Repair")],
      [Descriptor("AsBuilt", version: 2), Descriptor("Planned", dependsOn: "AsBuilt"),
        Descriptor("Repair", dependsOn: "AsBuilt"), bottom]);

    diff.RestartRequiredEngineNames.Should().Equal("AsBuilt", "Merged", "Planned", "Repair");
  }

  /// <summary>
  /// Verifies that a dependency on an engine that is not among the candidates is ignored, also when it has the same
  /// name as a removed engine.
  /// </summary>
  [Fact]
  public void EngineDefinitionDiff_Create_DependencyNotAmongTheCandidates_IsIgnored()
  {
    var diff = Diff(
      [Descriptor("Gone"), Descriptor("North")],
      [Descriptor("North", version: 2, dependsOn: "Gone"), Descriptor("South", dependsOn: "Elsewhere")]);

    diff.RestartRequiredEngineNames.Should().Equal("North");
  }
}
