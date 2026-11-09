namespace OrchestrationEngines.Engine;

/// <summary>
/// Base class for engine context implementations.
/// </summary>
/// <remarks>
/// <para>
/// An engine context represents the logical and/or runtime environment in which an
/// <see cref="IEngine"/> operates. Contexts may be layered, composed, or specialized
/// to influence engine behavior, dependency resolution, or execution scope.
/// </para>
/// <para>
/// <see cref="BaseEngineContext"/> provides a minimal foundation for context types that
/// do not wrap another context by default, but still participate in context equality
/// and engine resolution semantics.
/// </para>
/// </remarks>
public abstract class BaseEngineContext : IEngineContext
{
  /// <summary>
  /// Gets the inner (wrapped) engine context, if any.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The base implementation returns <c>null</c>, indicating that this context is not
  /// a wrapper around another context.
  /// </para>
  /// <para>
  /// Derived types may override this property to expose an underlying context when
  /// implementing context decoration or layering.
  /// </para>
  /// </remarks>
  public IEngineContext Inner => null;

  /// <summary>
  /// Resolves an underlying engine by name, bypassing any contextual adaptations.
  /// </summary>
  /// <param name="name">The name of the engine to resolve.</param>
  /// <returns>
  /// The underlying engine instance if available; otherwise <c>default</c>.
  /// </returns>
  /// <remarks>
  /// <para>
  /// This method is intended for contexts that delegate or forward engine resolution
  /// to another context or engine registry.
  /// </para>
  /// <para>
  /// The base implementation does not resolve engines and returns <c>default</c>.
  /// </para>
  /// </remarks>
  public virtual IEngine GetUnderlyingEngine(string name) => default;

  /// <summary>
  /// Determines whether this context is equal to another engine context.
  /// </summary>
  /// <param name="other">The other context to compare with.</param>
  /// <returns>
  /// <c>true</c> if the contexts are considered equivalent; otherwise <c>false</c>.
  /// </returns>
  /// <remarks>
  /// <para>
  /// Context equality is a semantic concept and may differ from reference equality.
  /// Implementations should compare the effective characteristics that influence
  /// engine behavior.
  /// </para>
  /// <para>
  /// This method is central to context invalidation and reuse logic within the
  /// engine coordination and mechanic subsystems.
  /// </para>
  /// </remarks>
  public abstract bool Equals(IEngineContext other);
}
