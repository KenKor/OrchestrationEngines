using System;

namespace OrchestrationEngines.Engine;

/// <summary>
/// Represents the execution context of an engine.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IEngineContext"/> provides an engine with access to
/// configuration and to other engines it depends on, without directly
/// coupling engines at construction time.
/// </para>
///
/// <para>
/// Contexts are immutable in intent: when a context becomes invalid,
/// the engine is stopped and restarted with a newly computed context
/// rather than mutating the existing one.
/// </para>
/// </remarks>
public interface IEngineContext : IEquatable<IEngineContext>
{
    /// <summary>
    /// The inner (wrapped) context, if any.
    /// </summary>
    /// <remarks>
    /// This enables context composition (e.g. base context plus dependency engines).
    /// Contexts that do not wrap another context should return <c>null</c>.
    /// </remarks>
    IEngineContext Inner { get; }

    /// <summary>
    /// Retrieves an underlying dependency engine by name.
    /// </summary>
    /// <param name="name">The name of the dependency engine.</param>
    /// <returns>
    /// The underlying engine instance, or <c>null</c> if no such dependency exists.
    /// </returns>
    /// <remarks>
    /// This method allows engines to interact with dependency engines
    /// without requiring direct references at construction time.
    /// </remarks>
    IEngine GetUnderlyingEngine(string name);
}