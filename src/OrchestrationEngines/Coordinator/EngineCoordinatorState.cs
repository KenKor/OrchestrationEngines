namespace OrchestrationEngines.Coordinator;

/// <summary>
/// States of the Engine Coordinator
/// 
/// It tracks the overall lifecycle of the coordinator and its engines.
/// Starts from Created, goes to WaitingForEngines when startup is requested,
/// then to Initializing while engines are starting up,
/// if all goes well it reaches Running.
/// On shutdown it goes to Stopping and finally Stopped.
/// </summary>
public enum EngineCoordinatorState
{
    /// <summary>
    /// Coordinator has been constructed but startup orchestration has not started yet.
    /// </summary>
    Created,

    /// <summary>
    /// Startup has been requested; coordinator is waiting for engines to begin transitioning.
    /// (In earlier designs this aligned with "waiting for context", but here context is already attached to the mechanic.)
    /// The engines are waiting for context to be provided. 
    /// </summary>
    WaitingForEngines,

    /// <summary>
    /// Engines are being created/started and required tasks are not yet all complete.
    /// </summary>
    Initializing,

    /// <summary>
    /// All required engines are Ready; application is operational.
    /// </summary>
    Running,

    /// <summary>
    /// Shutdown has started; coordinator will not accept operational triggers anymore.
    /// </summary>
    Stopping,

    /// <summary>
    /// Shutdown completed; terminal coordinator state.
    /// </summary>
    Stopped
}
