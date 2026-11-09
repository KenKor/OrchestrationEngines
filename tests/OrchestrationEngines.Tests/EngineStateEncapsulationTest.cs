using System.Collections.Generic;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests that pin how an engine's state is written: only through <see cref="BaseEngine.State"/>, whose setter
/// publishes every change in order. A field that callers could write directly would bypass that.
/// </summary>
public class EngineStateEncapsulationTest
{
  /// <summary>
  /// An engine with no tasks that records the changes its hook is told about.
  /// </summary>
  private sealed class RecordingEngine() : BaseEngine(NullLogger.Instance)
  {
    public List<(EngineState oldValue, EngineState newValue)> Changes { get; } = [];

    protected override void SetUpTasks()
    {
    }

    protected override void OnEngineStatusChanged(EngineState oldValue, EngineState value)
    {
      Changes.Add((oldValue, value));
      base.OnEngineStatusChanged(oldValue, value);
    }
  }

  /// <summary>
  /// Verifies that BaseEngine has no public instance field, so its state cannot be written past the setter. Before,
  /// the backing field <c>_state</c> was public.
  /// </summary>
  [Fact]
  public void BaseEngine_HasNoPublicFields()
  {
    typeof(BaseEngine).GetFields(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty();
  }

  /// <summary>
  /// Verifies that setting the state raises EngineStatusChanged once with the new value, and calls the hook once with
  /// the old and the new value.
  /// </summary>
  [Fact]
  public void State_Set_RaisesTheEventOnceWithTheOldAndNewValue()
  {
    var engine = new RecordingEngine();
    var events = new List<EngineState>();
    engine.EngineStatusChanged += (_, e) => events.Add(e.State);

    engine.State = EngineState.Starting;

    engine.State.Should().Be(EngineState.Starting);
    events.Should().Equal(EngineState.Starting);
    engine.Changes.Should().Equal((EngineState.Created, EngineState.Starting));
  }

  /// <summary>
  /// Verifies that setting the state it already has publishes nothing.
  /// </summary>
  [Fact]
  public void State_SetToTheSameValue_RaisesNothing()
  {
    var engine = new RecordingEngine();
    var events = new List<EngineState>();
    engine.EngineStatusChanged += (_, e) => events.Add(e.State);

    engine.State = EngineState.Created;

    events.Should().BeEmpty();
    engine.Changes.Should().BeEmpty();
  }
}
