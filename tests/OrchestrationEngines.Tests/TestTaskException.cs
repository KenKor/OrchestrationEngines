using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Tests;

/// <summary>
/// The tests' <see cref="EngineTaskException"/>: a condition another attempt cannot change.
/// </summary>
/// <param name="et">The task that throws it, if known.</param>
internal sealed class TestTaskException(EngineTask et = null) : EngineTaskException(et, "The task cannot go on.");
