using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests that pin how an engine's state is written: only through <see cref="BaseEngine.State"/>, whose setter
/// publishes every change in order (see <see cref="EngineStatusEventTest"/>). A field that callers or derived engines
/// could write directly would bypass that.
/// </summary>
public class EngineStateEncapsulationTest
{
  /// <summary>
  /// Verifies that BaseEngine has no non-private instance field, declared or inherited, so neither a caller nor a derived
  /// engine can write its state past the setter. Before, the backing field <c>_state</c> was public.
  /// </summary>
  [Fact]
  public void BaseEngine_HasNoNonPrivateFields()
  {
    typeof(BaseEngine).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
      .Where(f => !f.IsPrivate)
      .Select(f => f.Name)
      .Should().BeEmpty("the state is written only through the State setter, which publishes it in order");
  }
}
