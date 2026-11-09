using System.Reflection;
using AwesomeAssertions;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests that pin how an engine's state is written: only through <see cref="BaseEngine.State"/>, whose setter
/// publishes every change in order (see EngineStatusEventTest). A field that callers could write directly would bypass
/// that.
/// </summary>
public class EngineStateEncapsulationTest
{
  /// <summary>
  /// Verifies that BaseEngine declares no public instance field, so its state cannot be written past the setter.
  /// Before, the backing field <c>_state</c> was public.
  /// </summary>
  [Fact]
  public void BaseEngine_HasNoPublicFields()
  {
    typeof(BaseEngine).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
      .Should().BeEmpty("the state is written only through the State setter, which publishes it in order");
  }
}
