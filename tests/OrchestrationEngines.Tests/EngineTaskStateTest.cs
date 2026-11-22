using System;
using AwesomeAssertions;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the <see cref="EngineTaskStateExtension"/> conversions and texts.
/// </summary>
public class EngineTaskStateTest
{
  /// <summary>
  /// A number outside the range of the enum's short values is no state: the current state is kept. Before, 65540
  /// was cast to short unchecked and became Running.
  /// </summary>
  [Fact]
  public void FromValue_NumberOutOfShortRange_KeepsTheCurrentState()
  {
    EngineTaskState.Created.FromValue((int?)65540).Should().Be(EngineTaskState.Created);
    EngineTaskState.Created.FromValue((long?)65540).Should().Be(EngineTaskState.Created);
    EngineTaskState.Created.FromValue((int?)4).Should().Be(EngineTaskState.Running);
    EngineTaskState.Created.FromValue((long?)4).Should().Be(EngineTaskState.Running);
  }

  /// <summary>
  /// A text that parses to no single defined state, such as a combination of flags or an undefined number, keeps
  /// the current state.
  /// </summary>
  [Theory]
  [InlineData("Running, Failed")]
  [InlineData("6")]
  [InlineData("65540")]
  [InlineData("NoSuchState")]
  public void FromValue_TextOfNoDefinedState_KeepsTheCurrentState(string value)
  {
    EngineTaskState.Created.FromValue(value).Should().Be(EngineTaskState.Created);
  }

  /// <summary>
  /// A state's name, in any case, is that state.
  /// </summary>
  [Fact]
  public void FromValue_NameOfAState_IsThatState()
  {
    EngineTaskState.Created.FromValue("timedout").Should().Be(EngineTaskState.TimedOut);
  }

  /// <summary>
  /// Every state has a description, and a sub state of Failed says so.
  /// </summary>
  [Fact]
  public void Description_EveryState_HasText()
  {
    foreach (EngineTaskState state in Enum.GetValues(typeof(EngineTaskState)))
      state.Description().Should().NotBeNullOrEmpty(state.ToString());
    EngineTaskState.TimedOut.Description().Should().Contain("sub state of 'Failed'");
  }
}
