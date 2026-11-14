using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Mechanic;

/// <summary>
/// EngineTask responsible for starting an engine and driving it to the Ready state.
/// </summary>
/// <remarks>
/// <para>
/// This task represents the final lifecycle step: launching the engine with a valid
/// context and ensuring it reaches a usable (Ready) state.
/// </para>
///
/// <para>
/// Separating kick-start from creation and context allows:
/// <list type="bullet">
/// <item>clear dependency ordering,</item>
/// <item>targeted recovery of runtime failures,</item>
/// <item>clean handling of context invalidation.</item>
/// </list>
/// </para>
/// </remarks>
public class EngineKickStartTask : EngineTask
{
    /// <summary>
    /// Postfix appended to the engine name to form the task name for kick-starting.
    /// </summary>
    public const string PostFix = "{+-}";

    /// <summary>
    /// The name of the kick-start task of the engine named <paramref name="engineName"/>.
    /// </summary>
    internal static string KickStartName(string engineName) => engineName + PostFix;

    /// <summary>
    /// Descriptor defining how the engine should be launched.
    /// </summary>
    public EngineDescriptor Descriptor { get; }

    /// <summary>
    /// The engine instance being started or most recently started by this task.
    /// </summary>
    /// <remarks>
    /// This reference is cleared on reset; a new instance may be associated on re-execution.
    /// </remarks>
    public IEngine EngineInstance { get; set; }

    /// <summary>
    /// The engine whose restart this task prepared when it was reset, until it starts the engine again.
    /// </summary>
    /// <remarks>
    /// Only this task starts the engine again; if it ends without doing so, the restart is given up (see
    /// <see cref="OnFailedEntered"/>).
    /// </remarks>
    private BaseEngine _engineToRestart;

    /// <summary>
    /// Creates a task that starts an engine and ensures it reaches Ready.
    /// </summary>
    public EngineKickStartTask(
        MechanicEngine m,
        EngineDescriptor engineDescriptor,
        ILogger Logger,
        CancellationToken token)
        : base(
            KickStartName(engineDescriptor.Name),
            async _ => await Task.CompletedTask.ConfigureAwait(false), // Placeholder; replaced immediately.
            engineDescriptor.RequiredForRunning,
            Logger is null ? null : new StartFailureLogger(Logger, canRestart: engineDescriptor.HasRecovery),
            token)
    {
        Descriptor = engineDescriptor;
        RecoveryHelper = engineDescriptor.CreateRecoveryHelper(TimeProvider);
        _workAsync = runtimeToken => KickStartAsync(m, engineDescriptor);
    }

    /// <summary>
    /// Starts the engine and waits until it is Ready.
    /// </summary>
    /// <param name="m">The m.</param>
    /// <param name="engineDescriptor">The engine descriptor.</param>
    /// <exception cref="EvaluateException">
    /// Thrown if the engine stops or faults during startup.
    /// </exception>
    protected async Task KickStartAsync(
        MechanicEngine m,
        EngineDescriptor engineDescriptor)
    {
        // Fix (RuntimeRecoveryTest): a kick-start that runs again after recovery finds the engine Faulted (or
        // Stopped by a task's own cancellation, see NeedsResetBeforeRestart) from the previous attempt. Reset its
        // failed tasks here, before the start, instead of in ExecuteRecoveryAsync:
        // there the reset ran after the base had already fired SucceedRecovery, which re-runs this work at once, so
        // the first retry found the engine still Faulted and failed without starting it (one restart wasted), and
        // when the policy gave up the reset still ran, leaving the engine Created instead of Faulted.
        if (NeedsResetBeforeRestart(EngineInstance))
            await EngineInstance.ResetFailedTasksAsync().ConfigureAwait(false);

        // Start the engine instance.
        EngineInstance = await m.StartEngineAsync(engineDescriptor.Name).ConfigureAwait(false);
        // Started again: a restart prepared by a reset of this task has been carried out.
        _engineToRestart = null;

        // Drive the engine to Ready (initialization complete).
        await MechanicEngine.EnsureEngineReadyAsync(EngineInstance).ConfigureAwait(false);

        // Validate final engine state.
        if (EngineInstance?.State >= EngineState.Stopped)
        {
            // Escalate to failure so recovery policy can decide next steps.
            throw new EngineNotStartedException(
                $"Engine execution failed: '{engineDescriptor.Name}', {EngineInstance}.");
        }
    }

    // Fix (RuntimeRecoveryTest): the ExecuteRecoveryAsync override that reset the engine's failed tasks after
    // the base recovery is gone; KickStartAsync resets them before it starts the engine again.

    /// <summary>
    /// Returns whether a kick-start that runs again must reset the engine's failed tasks before starting it.
    /// </summary>
    /// <remarks>
    /// A Faulted engine, and a Stopped engine that was not stopped on purpose: one of its tasks ended Cancelled while
    /// the engine's own token is still live, typically through an <see cref="OperationCanceledException"/> that is not
    /// ours (a timed-out HTTP or database call). Such a failure is usually transient, so it is retried like a fault.
    /// An engine whose token was cancelled was stopped on purpose and is never started again; neither is a Stopped
    /// engine that is not a <see cref="BaseEngine"/>, since there is no telling why it stopped.
    /// </remarks>
    private static bool NeedsResetBeforeRestart(IEngine engine) => engine switch
    {
        null => false,
        { State: EngineState.Faulted } => true,
        BaseEngine { State: EngineState.Stopped, IsCancellationRequested: false } => true,
        _ => false,
    };

    /// <summary>
    /// Resets this task and the associated engine tasks in preparation for re-execution.
    /// </summary>
    protected override async Task OnResetEnteredAsync()
    {
        // Reset all engine-level tasks before restarting. That prepares a restart which only this task carries out, when it
        // runs again.
        if (EngineInstance != null)
        {
            _engineToRestart = EngineInstance as BaseEngine;
            await EngineInstance.ResetAllTasksAsync().ConfigureAwait(false);
        }

        // Clear instance reference; it will be re-established on next run.
        EngineInstance = null;

        await base.OnResetEnteredAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Gives up the restart of the engine that this task prepared when it was reset, if the task ends without starting
    /// the engine again (for example because its context task failed), so whoever waits until that engine is ready is
    /// released instead of waiting until it stops.
    /// </summary>
    /// <inheritdoc/>
    protected override void OnFailedEntered(string reasonFromTrigger = null, bool cancelled = false)
    {
        base.OnFailedEntered(reasonFromTrigger, cancelled);
        Interlocked.Exchange(ref _engineToRestart, null)?.AbandonRestart($"its kick-start ended without starting it ({FailReason})");
    }

    /// <summary>
    /// Thrown when the engine stopped or faulted instead of becoming Ready.
    /// </summary>
    /// <remarks>
    /// An <see cref="EvaluateException"/>, as before, so that code that catches that still does.
    /// </remarks>
    private sealed class EngineNotStartedException(string message) : EvaluateException(message);

    /// <summary>
    /// The logger of the kick-start task: it logs an engine that did not start as a warning, not as an error, while the
    /// restart policy can still start the engine again.
    /// </summary>
    /// <remarks>
    /// An engine that did not start only sums up the failure (or cancellation) of one of the engine's own tasks, which
    /// that task has logged already, and the stack trace is always that of <see cref="KickStartAsync"/>. So it is a
    /// warning without the stack trace; once the policy gives up, the recovery logs the error. Everything else is
    /// logged unchanged.
    /// </remarks>
    /// <param name="inner">The logger to write to.</param>
    /// <param name="canRestart">Whether the task has a restart policy.</param>
    private sealed class StartFailureLogger(ILogger inner, bool canRestart) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (canRestart && logLevel == LogLevel.Error && exception is EngineNotStartedException)
                inner.Log(LogLevel.Warning, eventId, state, null,
                    (s, _) => formatter(s, exception) + " The restart policy decides whether the engine is started again.");
            else
                inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
