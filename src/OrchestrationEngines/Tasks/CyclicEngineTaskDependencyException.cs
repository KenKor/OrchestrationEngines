namespace OrchestrationEngines.Tasks;

/// <summary>
/// Exception thrown when an <see cref="EngineTask"/> is found to (transitively) depend on itself.
/// </summary>
/// <remarks>
/// <para>
/// This exception indicates a structural error in the task dependency graph: the graph is not a DAG.
/// Cyclic dependencies cannot be resolved by ordering or retries and therefore represent a
/// configuration or wiring error rather than a runtime failure.
/// </para>
///
/// <para>
/// This exception is typically thrown during validation (e.g. <c>CheckForCyclicDependencies()</c>)
/// before tasks are executed. It should be treated as fatal for the affected engine or coordinator,
/// as execution semantics are undefined in the presence of cycles.
/// </para>
///
/// <para>
/// By inheriting from <see cref="EngineTaskException"/>, this exception preserves the originating
/// task context, allowing higher-level orchestration to report meaningful diagnostics.
/// </para>
/// </remarks>
public sealed class CyclicEngineTaskDependencyException : EngineTaskException
{
    /// <summary>
    /// Creates a new exception indicating that the specified task participates in a cyclic dependency.
    /// </summary>
    /// <param name="et">The task whose dependency graph contains a cycle.</param>
    public CyclicEngineTaskDependencyException(
        EngineTask et)
        : base(
            et,
            CreateMessage(et))
    {
    }

    /// <summary>
    /// Builds a human-readable error message describing the cyclic dependency.
    /// </summary>
    /// <param name="et">The et.</param>
    private static string CreateMessage(
        EngineTask et)
    {
        return $"Engine task '{et.Name}' contains dependencies that are dependent on itself.";
    }
}
