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
  /// A display name that counts how often it is read. Reflection builds a new attribute on every lookup, so the count
  /// is the number of times the attribute was looked up.
  /// </summary>
  private sealed class CountedDisplayNameAttribute(string displayName) : DisplayNameAttribute(displayName)
  {
    private static int s_reads;

    public static int Reads => Volatile.Read(ref s_reads);

    public override string DisplayName
    {
      get
      {
        Interlocked.Increment(ref s_reads);
        return base.DisplayName;
      }
    }
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

    CountedDisplayNameAttribute.Reads.Should().Be(1, "the name of an engine type does not change");
  }
}
