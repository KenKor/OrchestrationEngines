using System;
using System.Collections.Generic;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for when an engine's ContextChanged hook is called: when the context changes by value, not when an
/// equal context (a new instance of the same workspace, say) replaces it.
/// </summary>
public class EngineContextChangedTest
{
  /// <summary>
  /// A context that carries a value; two contexts with the same value are equal.
  /// </summary>
  private sealed class ValueContext(string value) : BaseEngineContext
  {
    public string Value { get; } = value;

    public override bool Equals(IEngineContext other) => other is ValueContext o && o.Value == Value;

    public override bool Equals(object obj) => obj is IEngineContext ctx && Equals(ctx);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
  }

  /// <summary>
  /// An engine that records the contexts its ContextChanged hook was called with.
  /// </summary>
  private sealed class RecordingEngine() : BaseEngine(NullLogger.Instance)
  {
    public List<(IEngineContext Old, IEngineContext New)> Changes { get; } = [];

    protected override void SetUpTasks()
    {
    }

    protected override void ContextChanged(IEngineContext oldValue, IEngineContext value) => Changes.Add((oldValue, value));
  }

  /// <summary>
  /// Verifies that setting an equal context stores the new instance without calling the hook, and that a context with
  /// another value, or none, does call it.
  /// </summary>
  [Fact]
  public void BaseEngine_Context_CallsContextChangedOnlyWhenTheValueChanges()
  {
    var engine = new RecordingEngine();
    var first = new ValueContext("workspace-A");
    var sameValue = new ValueContext("workspace-A");
    var other = new ValueContext("workspace-B");

    engine.Context = first;
    engine.Context = first;
    engine.Context = sameValue;
    engine.Context.Should().BeSameAs(sameValue, "the new instance is stored, also when it is equal");
    engine.Context = other;
    engine.Context = null;
    engine.Context = null;

    engine.Changes.Should().HaveCount(3);
    engine.Changes[0].Should().Be((null, first));
    engine.Changes[1].Should().Be((sameValue, other));
    engine.Changes[2].Should().Be((other, null));
  }
}
