namespace OrchestrationEngines.Coordinator;

/// <summary>
/// The states of the Engine Coordinator.
/// </summary>
/// <remarks>
/// The state is computed, not stored: Created until the start, Stopping and Stopped from the shutdown, and in between
/// it follows the engines registered as required for running. So it can go back: from Running to Initializing while
/// a context operation is in flight, and to WaitingForEngines when a required engine faults or stops.
/// </remarks>
public enum EngineCoordinatorState
{
    /// <summary>
    /// The coordinator has been constructed and not started yet.
    /// </summary>
    Created,

    /// <summary>
    /// Started, and either no required engine has begun to start yet, or a required engine is Faulted or Stopped and
    /// is not being started again.
    /// </summary>
    WaitingForEngines,

    /// <summary>
    /// A required engine is being created or started and not all of them are Ready yet, or a context operation is in
    /// flight.
    /// </summary>
    Initializing,

    /// <summary>
    /// All required engines are Ready; the application is operational.
    /// </summary>
    Running,

    /// <summary>
    /// The shutdown has started and has not completed yet.
    /// </summary>
    Stopping,

    /// <summary>
    /// The shutdown has completed; the terminal state.
    /// </summary>
    Stopped
}
