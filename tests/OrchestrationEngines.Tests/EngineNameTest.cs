using System.ComponentModel;
using System.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Tests for <see cref="BaseEngine.EngineName"/>: the display name, else the type name, read once per engine type.
/// </summary>
public class EngineNameTest
{
  private abstract class NoTasksEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks()
    {
    }
  }

  [DisplayName("Network")]
  private sealed class NamedEngine : NoTasksEngine;

  private sealed class UnnamedEngine : NoTasksEngine;

  /// <summary>
  /// A display name that counts how often it is built. Reflection builds a new attribute on every lookup, so the count
  /// is the number of times the attribute was looked up.
  /// </summary>
  private sealed class CountedDisplayNameAttribute : DisplayNameAttribute
  {
    private static int s_built;

    public CountedDisplayNameAttribute(string displayName) : base(displayName) => Interlocked.Increment(ref s_built);

    public static int Built => Volatile.Read(ref s_built);
  }

  [CountedDisplayName("Counted")]
  private sealed class CountedEngine : NoTasksEngine;

  /// <summary>
  /// Verifies that an engine with a display name is named by it.
  /// </summary>
  [Fact]
  public void EngineName_WithDisplayName_IsTheDisplayName() => new NamedEngine().EngineName.Should().Be("Network");

  /// <summary>
  /// Verifies that an engine without a display name is named by its type.
  /// </summary>
  [Fact]
  public void EngineName_WithoutDisplayName_IsTheTypeName() =>
    new UnnamedEngine().EngineName.Should().Be(nameof(UnnamedEngine));

  /// <summary>
  /// Verifies that the name is read through reflection once per engine type, not on every read: it is read on every
  /// enabled log line of the engine and its tasks.
  /// </summary>
  [Fact]
  public void EngineName_ReadManyTimes_ReadsTheAttributeOncePerType()
  {
    var first = new CountedEngine();
    var second = new CountedEngine();

    for (var i = 0; i < 3; i++)
    {
      first.EngineName.Should().Be("Counted");
      second.EngineName.Should().Be("Counted");
    }

    CountedDisplayNameAttribute.Built.Should().Be(1, "the attribute of an engine type does not change");
  }

  /// <summary>
  /// A display name that changes after it is first read, as a localised one does with the current culture.
  /// </summary>
  private sealed class ChangingDisplayNameAttribute() : DisplayNameAttribute
  {
    public static volatile string Current = "First";

    public override string DisplayName => Current;
  }

  [ChangingDisplayName]
  private sealed class ChangingEngine : NoTasksEngine;

  /// <summary>
  /// Verifies that the display name is read on every read: a <see cref="DisplayNameAttribute"/> subclass may compute
  /// it, for example for the current culture.
  /// </summary>
  [Fact]
  public void EngineName_DisplayNameChanges_IsTheCurrentDisplayName()
  {
    var engine = new ChangingEngine();
    engine.EngineName.Should().Be("First");

    ChangingDisplayNameAttribute.Current = "Second";

    engine.EngineName.Should().Be("Second");
    new ChangingEngine().EngineName.Should().Be("Second");
  }
}
