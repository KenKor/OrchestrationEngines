using OrchestrationEngines.Mechanic;

namespace OrchestrationEngines.Engine;

/// <summary>
/// Triggers that drive the internal state machine of an <see cref="IEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// These triggers represent *external or internal events* that cause
/// lifecycle transitions. They are typically fired by the engine itself
/// or by orchestration code (e.g. <see cref="MechanicEngine"/>).
/// </para>
///
/// <para>
/// Not all triggers are valid in all states; invalid triggers are typically
/// ignored or rejected by the engine's internal state machine.
/// </para>
/// </remarks>
public enum EngineTrigger
{
    /// <summary>
    /// A request has been made to start the engine.
    /// </summary>
    StartRequested,

    /// <summary>
    /// The engine has confirmed that startup has begun.
    /// </summary>
    StartedConfirmed,

    /// <summary>
    /// The engine has confirmed that it is fully initialized and Ready.
    /// </summary>
    ReadyConfirmed,

    /// <summary>
    /// The engine has encountered a fault.
    /// </summary>
    Faulted,

    /// <summary>
    /// A request has been made to stop the engine.
    /// </summary>
    StopRequested,

    /// <summary>
    /// The engine has confirmed that it has fully stopped.
    /// </summary>
    StoppedConfirmed
}
