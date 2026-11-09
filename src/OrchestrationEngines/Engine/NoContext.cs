namespace OrchestrationEngines.Engine;

/// <summary>
/// A singleton-style context representing the absence of engine-specific context.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NoContext"/> is used when an engine does not require any runtime
/// context or dependencies. It provides a concrete implementation of
/// <see cref="IEngineContext"/> so that engines can uniformly assume a context
/// object is present.
/// </para>
///
/// <para>
/// All instances of <see cref="NoContext"/> are considered equal. The class is
/// stateless and interchangeable.
/// </para>
/// </remarks>
public sealed class NoContext : IEngineContext
{
  /// <summary>
  /// Determines equality with another engine context.
  /// </summary>
  /// <param name="other">The other.</param>
  /// <remarks>
  /// Any instance of <see cref="NoContext"/> is considered equal to any other.
  /// </remarks>
  public bool Equals(IEngineContext other) => other is NoContext;

  /// <inheritdoc />
  public override bool Equals(object obj) => obj is NoContext;

  /// <summary>
  /// Returns a constant hash code.
  /// </summary>
  /// <remarks>
  /// Because the class is stateless and all instances are equal,
  /// a constant hash code is sufficient.
  /// </remarks>
  public override int GetHashCode() => 0;

  /// <summary>
  /// <inheritdoc/>
  /// No inner context exists.
  /// </summary>
  public IEngineContext Inner => null;

  /// <summary>
  /// <inheritdoc/>
  /// No dependency engines are exposed by this context.
  /// </summary>
  /// <returns>Always returns <c>null</c>.</returns>
  public IEngine GetUnderlyingEngine(string name) => default;
}
