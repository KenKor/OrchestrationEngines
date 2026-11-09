using System.Collections.Generic;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;

namespace OrchestrationEngines.Mechanic;

/// <summary>
/// Context object for the <see cref="MechanicEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="MechanicContext"/> provides the mechanic with the set of
/// <see cref="EngineDescriptor"/> instances that define which engines should
/// be created, how they depend on each other, and how they should be started.
/// </para>
///
/// <para>
/// This context is intentionally lightweight: it is a declarative description
/// of the engine topology rather than a runtime container of engine instances.
/// Actual engine instances are created and managed by the <see cref="MechanicEngine"/>.
/// </para>
/// </remarks>
public class MechanicContext : IEngineContext
{
  /// <summary>
  /// Descriptors for all engines managed by the mechanic.
  /// </summary>
  /// <remarks>
  /// Each <see cref="EngineDescriptor"/> describes one engine, including its
  /// factory, dependency list, and lifecycle policies.
  ///
  /// The mechanic uses this collection during task graph construction
  /// (<see cref="MechanicEngine.SetUpTasks"/>) to build the EngineTask DAG.
  /// </remarks>
  public HashSet<EngineDescriptor> EngineDescriptors { get; } = new(EngineDescriptorNameComparer.Instance);


  /// <summary>
  /// Inner context for composition.
  /// </summary>
  /// <remarks>
  /// The mechanic itself does not wrap another engine context, so this
  /// property always returns <c>null</c>.
  ///
  /// This exists to satisfy the <see cref="IEngineContext"/> contract and
  /// to allow uniform handling of contexts elsewhere in the system.
  /// </remarks>
  public IEngineContext Inner => null;

  /// <summary>
  /// Returns an underlying engine by name, if applicable.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <remarks>
  /// The mechanic context does not directly expose engine instances;
  /// engines are managed by <see cref="MechanicEngine"/>.
  ///
  /// This method therefore returns <c>default</c> and exists only to
  /// satisfy the <see cref="IEngineContext"/> interface.
  /// </remarks>
  public IEngine GetUnderlyingEngine(string name) => default;

  /*
   * Default IEquatable implementation
   */

  /// <summary>
  /// Compares this context with another <see cref="MechanicContext"/> for equality.
  /// </summary>
  /// <param name="other">The other.</param>
  /// <remarks>
  /// Equality is defined in terms of the <see cref="EngineDescriptors"/> set.
  /// Two contexts are considered equal if they contain the same set of descriptors.
  ///
  /// Note: this relies on <see cref="EngineDescriptor"/> providing a correct
  /// equality implementation.
  /// </remarks>
  public bool Equals(MechanicContext other)
      => EngineDescriptors.SetEquals(other.EngineDescriptors);

  /// <summary>
  /// Compares this context with another <see cref="IEngineContext"/>.
  /// </summary>
  /// <param name="other">The other.</param>
  public bool Equals(IEngineContext other)
  {
    if (other is null) return false;
    if (ReferenceEquals(this, other)) return true;
    if (other.GetType() != GetType()) return false;
    return Equals((MechanicContext)other);
  }

  /// <summary>
  /// Standard object equality override.
  /// </summary>
  /// <param name="obj">The obj.</param>
  public override bool Equals(object obj)
  {
    if (obj is null) return false;
    if (ReferenceEquals(this, obj)) return true;
    if (obj.GetType() != GetType()) return false;
    return Equals((MechanicContext)obj);
  }

  /// <summary>
  /// Computes a hash code for this context.
  /// </summary>
  /// <remarks>
  /// The hash code is derived from <see cref="EngineDescriptors"/>.
  /// This is sufficient for current usage patterns, but assumes that
  /// the descriptor set is not mutated while the context is used as a key.
  /// </remarks>
  public override int GetHashCode()
  {
    return EngineDescriptors.GetHashCode();
  }
}
