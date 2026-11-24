using System;
using OrchestrationEngines.Engine;

namespace OrchestrationEngines.Tests;

/// <summary>
/// A test context equal to another one with the same value, so that assigning one with another value is a change.
/// </summary>
internal sealed class ValueContext(string value) : BaseEngineContext
{
  public string Value { get; } = value;

  public override bool Equals(IEngineContext other) => other is ValueContext o && o.Value == Value;

  public override bool Equals(object obj) => obj is IEngineContext ctx && Equals(ctx);

  public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
}
