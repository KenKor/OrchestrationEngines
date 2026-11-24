using System;
using System.Data;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Helpers;
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
/// <item>a new context without a new engine.</item>
/// </list>
/// </para>
/// </remarks>
public partial class EngineKickStartTask : EngineTask
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
    /// The engine instance this task most recently started, once the start has returned; null until then.
    /// </summary>
    /// <remarks>
    /// This reference is cleared on reset, and set again by the next run that starts the engine. A run ended by a reset
    /// while it was starting the engine does not set it (#85).
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
    /// The engine a run is starting, from the start of the run until the start returns; null otherwise.
    /// </summary>
    /// <remarks>
    /// A reset that comes in meanwhile restarts this engine, though the run has not recorded it in
    /// <see cref="EngineInstance"/> yet (#85).
    /// </remarks>
    private IEngine _starting;

    /// <summary>
    /// Counts the resets of this task. A run whose start returns after a newer reset leaves the state that reset set up
    /// as it is: the run was ended, and the next run carries out the restart (#85).
    /// </summary>
    private int _generation;

    /// <summary>
    /// The engine this task gave up while a run it ended was still starting it, with the reason; null otherwise. That run
    /// gives it up again once its start returns, since the start leaves it running on the previous context (#86 item 7).
    /// </summary>
    private (BaseEngine Engine, string Reason)? _givenUpWhileStarting;

    /// <summary>
    /// The give-up of an engine that a run this task ended had started already (see <see cref="OnFailedEntered"/>). The
    /// next run waits until it has ended, or it finds the engine still Ready on the previous context and completes, and the
    /// give-up then faults the engine under it.
    /// </summary>
    private Task _pendingGiveUp = Task.CompletedTask;

    /// <summary>
    /// Set by a reset in a <see cref="ResetRecordOnlyAsync"/>, cleared by any other reset; the next run takes it. That
    /// run only waits for the engine: it does not run the engine's failed tasks again first (#58 item 2).
    /// </summary>
    private bool _waitOnly;

    /// <summary>
    /// Guards <see cref="_generation"/>, <see cref="_starting"/>, <see cref="EngineInstance"/>,
    /// <see cref="_engineToRestart"/>, <see cref="_givenUpWhileStarting"/>, <see cref="_pendingGiveUp"/> and
    /// <see cref="_waitOnly"/> between a run and a reset that ends it.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// The scope of a <see cref="ResetRecordOnlyAsync"/> in this flow; null otherwise. The runs that the reset starts copy
    /// it with the flow, so its kick-start is taken by the first reset of that task that enters, and the scope is emptied
    /// when the reset ends: any later reset of the task, wherever it comes from (a reset of an engine it depends on),
    /// restarts the engine.
    /// </summary>
    /// <remarks>The box holds the kick-start whose next reset clears only its record; null once taken.</remarks>
    private static readonly AsyncLocal<StrongBox<EngineKickStartTask>> s_recordOnly = new();

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
            engineDescriptor.RequiredForRunning,
            Logger is null ? null : new StartFailureLogger(Logger, canRestart: engineDescriptor.HasRecovery),
            token)
    {
        Descriptor = engineDescriptor;
        RecoveryHelper = engineDescriptor.CreateRecoveryHelper(TimeProvider);
        _workAsync = runtimeToken => KickStartAsync(m, engineDescriptor, runtimeToken);
    }

    /// <summary>
    /// Starts the engine and waits until it is Ready.
    /// </summary>
    /// <param name="m">The m.</param>
    /// <param name="engineDescriptor">The engine descriptor.</param>
    /// <param name="token">The run's token: once the run is ended (a reset of this task), it stops waiting for the
    /// engine. Required: without it, a reset during the wait for the engine leaves this run and the next one waiting
    /// for each other (#85).</param>
    /// <exception cref="EvaluateException">
    /// Thrown if the engine stops or faults during startup.
    /// </exception>
    protected async Task KickStartAsync(
        MechanicEngine m,
        EngineDescriptor engineDescriptor,
        CancellationToken token)
    {
        if (!m.Engines.TryGetValue(engineDescriptor.Name, out var engine))
            throw new ArgumentException($"Unknown engine '{engineDescriptor.Name}'");

        // A give-up of the engine that an earlier run started with the previous context may still be under way: let it end
        // first. It does not throw. A reset that came in meanwhile has ended this run.
        Task pendingGiveUp;
        lock (_gate)
            pendingGiveUp = _pendingGiveUp;
        await pendingGiveUp.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        // Marked before the first wait below: a reset that comes in during any of those waits ends this run, and must
        // restart the engine this run starts, with the context it starts it with (#85).
        int generation;
        BaseEngine toRestart;
        bool waitOnly;
        lock (_gate)
        {
            generation = _generation;
            waitOnly = _waitOnly;
            _waitOnly = false;
            _starting = engine;
            toRestart = _engineToRestart;
            _givenUpWhileStarting = null;
        }

        var started = false;
        (BaseEngine Engine, string Reason)? giveUpAgain = null;
        try
        {
            // Fix (RuntimeRecoveryTest): a kick-start that runs again after recovery finds the engine Faulted (or
            // Stopped by a task's own cancellation, see NeedsResetBeforeRestart) from the previous attempt. Reset its
            // failed tasks here, before the start, instead of in ExecuteRecoveryAsync:
            // there the reset ran after the base had already fired SucceedRecovery, which re-runs this work at once, so
            // the first retry found the engine still Faulted and failed without starting it (one restart wasted), and
            // when the policy gave up the reset still ran, leaving the engine Created instead of Faulted.
            // Not in the run after a ResetRecordOnlyAsync: that one only waits for the engine.
            if (!waitOnly && NeedsResetBeforeRestart(EngineInstance))
                await EngineInstance.ResetFailedTasksAsync().ConfigureAwait(false);

            // A reset found the engine still Created, because a run it ended was starting it, so it could not prepare
            // a restart then. That run may have started the engine since, with the previous context: restart it now.
            // On an engine that is still Created with its restart prepared, this changes nothing (#85).
            if (toRestart != null)
                await toRestart.ResetAllTasksAsync().ConfigureAwait(false);

            await MechanicEngine.StartEngineAsync(engine).ConfigureAwait(false);
            started = true;
        }
        finally
        {
            lock (_gate)
            {
                _starting = null;
                // Started again: a restart prepared by a reset of this task has been carried out. Unless a newer reset
                // ended this run meanwhile: the restart that one prepared is the next run's (#85).
                if (started && generation == _generation)
                {
                    EngineInstance = engine;
                    _engineToRestart = null;
                }
                // A newer run gave this engine up while this one was still starting it: the start has undone that.
                if (_givenUpWhileStarting is { } givenUp && givenUp.Engine == engine)
                {
                    _givenUpWhileStarting = null;
                    if (started)
                        giveUpAgain = givenUp;
                }
            }
        }

        if (giveUpAgain is { } again)
            await GiveUpAsync(again.Engine, again.Reason).ConfigureAwait(false);

        // Drive the engine to Ready (initialization complete).
        // A reset of this task ends this run and prepares a restart of the engine, which only the next run carries out;
        // and the next run starts once this one's work has ended. So stop waiting once this run is ended, or the two wait
        // for each other (#85).
        await MechanicEngine.EnsureEngineReadyAsync(engine).WaitObservedAsync(token).ConfigureAwait(false);

        // Validate final engine state.
        if (engine.State >= EngineState.Stopped)
        {
            // One of its required tasks was cancelled by its own work (an EngineTaskException): another start cannot
            // change that, so this task ends Failed without a retry (#90; see RetriesFailureOf). Failed, not
            // Cancelled, so that the mechanic faults and the coordinator hears of it.
            if (CancelledByItsWork(engine) is { } cancelled)
                throw new EngineCancelledByItsWorkException(
                    $"Engine '{engineDescriptor.Name}' is not started again: its task '{cancelled.Name}' ended with \"{cancelled.FailReason}\".");

            // Escalate to failure so recovery policy can decide next steps.
            throw new EngineNotStartedException(
                $"Engine execution failed: '{engineDescriptor.Name}', {engine}.");
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
    /// ours (a timed-out HTTP or database call), and not one cancelled by its own work (an
    /// <see cref="EngineTaskException"/>), which ends the kick-start without a retry. Such a failure is usually
    /// transient, so it is retried like a fault. An engine whose token was cancelled was stopped on purpose and is never
    /// started again; neither is a Stopped engine that is not a <see cref="BaseEngine"/>, since there is no telling why
    /// it stopped.
    /// </remarks>
    private static bool NeedsResetBeforeRestart(IEngine engine) => engine switch
    {
        null => false,
        { State: EngineState.Faulted } => true,
        BaseEngine { State: EngineState.Stopped, IsCancellationRequested: false } => true,
        _ => false,
    };

    /// <summary>
    /// Returns the task of <paramref name="engine"/> that its own work cancelled with an
    /// <see cref="EngineTaskException"/> and that ended a task required for running: that required task itself, or a
    /// task it depends on, directly or not. Null if there is none.
    /// </summary>
    /// <remarks>
    /// Only a task that ended a required one counts: the engine's state, and so whether it is started again, depends
    /// only on the required tasks. So an optional task cancelled by its work that no required task depends on does not
    /// keep the engine from being started again; one that a required task depends on does, since another start would
    /// end the same way. The engine may also be Faulted, by another required task that failed; it is not started again
    /// either, for the same reason.
    /// </remarks>
    private static EngineTask CancelledByItsWork(IEngine engine) =>
        (engine as BaseEngine)?.Tasks.Values
            .Where(t => t.RequiredForRunning && t.IsFailed)
            .SelectMany(t => t.GetThisAndAllDependsOn())
            .FirstOrDefault(t => t.State == EngineTaskState.Cancelled && t.CancelledByTheWork);

    /// <summary>
    /// Returns false for the failure of an engine whose task was cancelled by its own work, which another start cannot
    /// change: this task then ends Failed without a retry under the descriptor's restart policy.
    /// </summary>
    /// <inheritdoc/>
    internal override bool RetriesFailureOf(Exception exception) => exception is not EngineCancelledByItsWorkException;

    /// <summary>
    /// Resets this task with <paramref name="mechanic"/>'s <see cref="BaseEngine.ResetTasksAsync"/>, and the tasks that
    /// depend on it, without restarting its engine: only this task's own outcome is cleared, and it runs again on the
    /// engine as it is (#58 item 2).
    /// </summary>
    /// <remarks>
    /// For an engine whose start was given up and whose tasks were then reset and run again: the next run finds the
    /// engine started, so it only waits until the engine is Ready, and fails again if the engine has failed again by
    /// then (a retry of it runs the engine's failed tasks again first, as usual). The tasks that depend on this one are
    /// reset as usual. The task keeps its record of the engine, so a reset of it that comes in later, from anywhere,
    /// restarts the engine.
    /// </remarks>
    internal async Task ResetRecordOnlyAsync(BaseEngine mechanic)
    {
        var scope = new StrongBox<EngineKickStartTask>(this);
        var previous = s_recordOnly.Value;
        s_recordOnly.Value = scope;
        try
        {
            await mechanic.ResetTasksAsync([Name], resetDependentTasks: true).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref scope.Value, null);
            s_recordOnly.Value = previous;
        }
    }

    /// <summary>
    /// Resets this task and the associated engine tasks in preparation for re-execution.
    /// </summary>
    protected override async Task OnResetEnteredAsync()
    {
        // Reset all engine-level tasks before restarting. That prepares a restart which only this task carries out, when it
        // runs again. A run that is still starting the engine (this reset ended it, but its work goes on) has not recorded
        // it yet: restart the engine it is starting all the same, or a second reset that comes in meanwhile leaves
        // the engine loaded with the previous context (#85). If that engine is still Created, the reset prepares nothing;
        // the next run restarts it once the ended run has started it. An engine that no run started needs no restart: the
        // next run starts it.
        // A reset in a ResetRecordOnlyAsync restarts nothing and keeps the record of the engine, so a reset that comes in
        // before the next run has started (a reset of an engine it depends on) still restarts it.
        var recordOnly = s_recordOnly.Value is { } scope && Interlocked.CompareExchange(ref scope.Value, null, this) == this;
        IEngine engine = null;
        lock (_gate)
        {
            _generation++;
            _waitOnly = recordOnly;
            if (!recordOnly)
            {
                engine = EngineInstance ?? _starting;
                // Clear instance reference; it will be re-established on next run.
                EngineInstance = null;
                if (engine != null)
                    _engineToRestart = engine as BaseEngine;
            }
        }

        if (engine != null)
            await engine.ResetAllTasksAsync().ConfigureAwait(false);

        await base.OnResetEnteredAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Gives up the restart of the engine that this task prepared when it was reset, if the task ends without starting
    /// the engine again (for example because its context task failed), so whoever waits until that engine is ready is
    /// released instead of waiting until it stops.
    /// </summary>
    /// <inheritdoc/>
    /// <remarks>
    /// A reset that found the engine still Created, because a run it ended was starting it, prepared no restart. That run
    /// starts the engine with the previous context. The engine is then given up as well (Faulted, as when the restart
    /// was prepared), and not left Ready on the previous context, which looks the same as a right answer (#86 item 7):
    /// by that run once its start returns, or here if it has returned already.
    /// </remarks>
    protected override void OnFailedEntered(string reasonFromTrigger = null, bool cancelled = false)
    {
        base.OnFailedEntered(reasonFromTrigger, cancelled);
        BaseEngine abandoned;
        string reason;
        lock (_gate)
        {
            abandoned = _engineToRestart;
            _engineToRestart = null;
            if (abandoned == null)
                return;
            reason = $"its kick-start ended without starting it ({FailReason})";
            if (_starting == abandoned)
            {
                _givenUpWhileStarting = (abandoned, reason);
            }
            else if (abandoned is { State: EngineState.Starting or EngineState.Ready })
            {
                // Started meanwhile; the next run waits until this has ended.
                _pendingGiveUp = Task.Run(() => GiveUpAsync(abandoned, reason));
                return;
            }
        }
        abandoned.AbandonRestart(reason);
    }

    /// <summary>
    /// Gives up an engine that a run this task ended started with the previous context: ends its tasks, as a restart
    /// does, and gives that restart up, so the engine ends Faulted (Stopped if it is stopped meanwhile) and whoever waits
    /// until it is ready is released (#86 item 7).
    /// </summary>
    /// <remarks>Does not throw: a failure is logged, since nobody waits for its outcome.</remarks>
    private async Task GiveUpAsync(BaseEngine engine, string reason)
    {
        try
        {
            await engine.ResetAllTasksAsync().ConfigureAwait(false);
            engine.AbandonRestart(reason);
        }
        catch (Exception ex)
        {
            BestEffort.Log(() => LogGiveUpFailed(Logger, ex, engine.EngineName, reason));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not give up engine '{EngineName}': {Reason}")]
    private static partial void LogGiveUpFailed(ILogger logger, Exception exception, string engineName, string reason);

    /// <summary>
    /// Thrown when the engine stopped or faulted instead of becoming Ready.
    /// </summary>
    /// <remarks>
    /// An <see cref="EvaluateException"/>, as before, so that code that catches that still does.
    /// </remarks>
    private sealed class EngineNotStartedException(string message) : EvaluateException(message);

    /// <summary>
    /// Thrown when the engine stopped because one of its required tasks was cancelled by its own work: not retried (see
    /// <see cref="RetriesFailureOf"/>).
    /// </summary>
    /// <remarks>
    /// An <see cref="EvaluateException"/>, like <see cref="EngineNotStartedException"/>, and not an
    /// <see cref="EngineTaskException"/>, which would end this task Cancelled.
    /// </remarks>
    private sealed class EngineCancelledByItsWorkException(string message) : EvaluateException(message);

    /// <summary>
    /// The logger of the kick-start task: it logs an engine that did not start as a warning, not as an error, while the
    /// restart policy can still start the engine again. An engine not started again because one of its tasks was
    /// cancelled by its own work is not retried, so that is logged as an error.
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
