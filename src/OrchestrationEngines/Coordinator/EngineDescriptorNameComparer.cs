using System;
using System.Collections.Generic;

namespace OrchestrationEngines.Coordinator;

/// <summary>
/// Equality comparer for <see cref="EngineDescriptor"/> that compares descriptors
/// solely based on their engine name.
/// </summary>
/// <remarks>
/// <para>
/// This comparer defines the identity of an <see cref="EngineDescriptor"/> as its
/// logical engine name, using a case-insensitive comparison.
/// </para>
///
/// <para>
/// It is intended for use in collections (such as <see cref="HashSet{T}"/>)
/// where engine names must be unique and where descriptors represent
/// declarative topology rather than runtime state.
/// </para>
///
/// <para>
/// This comparer deliberately ignores all other descriptor properties
/// (factories, dependencies, policies, etc.). As a result, two descriptors
/// with the same name are considered equal even if their configurations differ.
/// </para>
/// </remarks>
public sealed class EngineDescriptorNameComparer : IEqualityComparer<EngineDescriptor>
{
  /// <summary>
  /// Singleton instance of the comparer.
  /// </summary>
  /// <remarks>
  /// The comparer is stateless and reusable; a single shared instance is sufficient.
  /// </remarks>
  public static readonly EngineDescriptorNameComparer Instance = new();

  /// <summary>
  /// Private constructor to enforce singleton usage.
  /// </summary>
  private EngineDescriptorNameComparer() { }

  /// <summary>
  /// Determines whether two <see cref="EngineDescriptor"/> instances are equal
  /// by comparing their names.
  /// </summary>
  /// <param name="x">First descriptor.</param>
  /// <param name="y">Second descriptor.</param>
  /// <returns>
  /// <c>true</c> if both descriptors have the same name (case-insensitive);
  /// otherwise <c>false</c>.
  /// </returns>
  public bool Equals(EngineDescriptor x, EngineDescriptor y)
  {
    if (ReferenceEquals(x, y)) return true;
    if (x is null || y is null) return false;

    return StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
  }

  /// <summary>
  /// Computes a hash code for the given <see cref="EngineDescriptor"/> based on its name.
  /// </summary>
  /// <param name="obj">The descriptor for which to compute the hash code.</param>
  /// <returns>
  /// A hash code derived from the engine name using a case-insensitive comparer.
  /// </returns>
  /// <remarks>
  /// <para>
  /// Correct behavior of hash-based collections requires that the engine name
  /// is non-null and remains immutable for the lifetime of the descriptor
  /// once it has been added to a collection.
  /// </para>
  /// </remarks>
  public int GetHashCode(EngineDescriptor obj)
  {
    if (obj is null || obj.Name is null)
      return 0;

    return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
  }
}
