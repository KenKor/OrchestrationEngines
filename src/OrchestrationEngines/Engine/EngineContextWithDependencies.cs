using System;
using System.Collections.Generic;
using OrchestrationEngines.Mechanic;

namespace OrchestrationEngines.Engine;

/// <summary>
/// Context wrapper that exposes dependency engines to an engine at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EngineContextWithDependencies"/> composes an existing
/// <see cref="IEngineContext"/> and augments it with references to
/// other engines that the current engine depends on.
/// </para>
///
/// <para>
/// This allows engines to access dependency engines through their context,
/// without introducing direct construction-time coupling between engines.
/// </para>
/// </remarks>
public sealed class EngineContextWithDependencies : IEngineContext
{
  /// <summary>
  /// Creates a new context wrapper around an existing context.
  /// </summary>
  /// <param name="inner">
  /// The base context being wrapped. Must not be <c>null</c>.
  /// </param>
  public EngineContextWithDependencies(IEngineContext inner)
  {
    Inner = inner ?? throw new ArgumentNullException(nameof(inner));
  }

  /// <inheritdoc/>
  public IEngine UnderlyingEngine(string name)
      => !String.IsNullOrEmpty(name) && UnderlyingEngines.TryGetValue(name, out var engine)
          ? engine
          : null;

  /// <summary>
  /// The wrapped (inner) context.
  /// </summary>
  /// <remarks>
  /// Context composition is hierarchical; callers may traverse
  /// <see cref="Inner"/> to access the original context.
  /// </remarks>
  public IEngineContext Inner { get; }

  /// <summary>
  /// Registers a dependency engine under the given name.
  /// </summary>
  /// <remarks>
  /// This method is internal because dependency wiring is the responsibility
  /// of the <see cref="MechanicEngine"/>, not of engine implementations.
  /// </remarks>
  internal bool AddUnderlyingEngine(string name, IEngine engine)
  {
    if (_underlyingEngines.ContainsKey(name)) return false;
    _underlyingEngines[name] = engine;
    return true;
  }

  /// <summary>
  /// Storage for dependency engines, keyed by engine name (case-insensitive, as engine names are everywhere).
  /// </summary>
  private readonly Dictionary<string, IEngine> _underlyingEngines = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Read-only view of dependency engines.
  /// </summary>
  public IReadOnlyDictionary<string, IEngine> UnderlyingEngines
      => _underlyingEngines;

  /// <inheritdoc/>
  public IEngine GetUnderlyingEngine(string name)
      => !String.IsNullOrEmpty(name) && UnderlyingEngines.TryGetValue(name, out var engine)
          ? engine
          : null;

  /// <summary>
  /// Determines equality with another engine context.
  /// </summary>
  /// <param name="other">The other.</param>
  /// <remarks>
  /// Equality is defined primarily by the wrapped inner context.
  /// Dependency engines are runtime objects and are intentionally
  /// excluded from equality comparison.
  /// </remarks>
  public bool Equals(IEngineContext other)
  {
    if (other is EngineContextWithDependencies o)
      return Inner.Equals(o.Inner);

    return Inner.Equals(other);
  }

  /// <inheritdoc />
  public override bool Equals(object obj)
      => obj is IEngineContext ctx && Equals(ctx);

  /// <summary>
  /// Computes a hash code based on the inner context.
  /// </summary>
  public override int GetHashCode() => Inner.GetHashCode();
}
