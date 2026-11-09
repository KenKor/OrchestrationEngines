using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Coordinator;

/// <summary>
/// Declarative and immutable description of an engine managed by the <see cref="MechanicEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="EngineDescriptor"/> defines <em>what</em> an engine is and
/// <em>how</em> it should be created, configured, started, and recovered.
/// It does not represent a runtime engine instance.
/// </para>
///
/// <para>
/// Descriptors are consumed by the mechanic to construct an EngineTask DAG
/// consisting of creation, context resolution, and kick-start phases.
/// </para>
///
/// <para>
/// Instances of this class are typically created during application setup
/// and remain immutable in intent once the mechanic starts.
/// </para>
/// </remarks>
public class EngineDescriptor : IEquatable<EngineDescriptor>
{
  /// <summary>
  /// Logical name of the engine.
  /// </summary>
  /// <remarks>
  /// This name is used:
  /// <list type="bullet">
  /// <item>as the engine registry key,</item>
  /// <item>to derive EngineTask names,</item>
  /// <item>to express dependencies between engines.</item>
  /// </list>
  /// Names are compared case-insensitively.
  /// </remarks>
  public string Name { get; set; }

  /// <summary>
  /// Integer version of the engine definition.
  /// </summary>
  /// <remarks>
  /// Bumping this value indicates that a generation-switch operation should
  /// restart this engine (and its dependents) instead of treating it as
  /// unchanged. Version comparison is intentionally simple (integer inequality).
  /// </remarks>
  public int Version { get; set; } = 1;

  /// <summary>
  /// Factory used to create a <em>fresh</em> engine instance.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This factory must always return a new engine instance.
  /// Reusing instances across invocations is not supported.
  /// </para>
  ///
  /// <para>
  /// The factory is invoked by <see cref="EngineCreationTask"/> under
  /// orchestration of the mechanic.
  /// </para>
  /// </remarks>
  public Func<CancellationToken, Task<IEngine>> EngineFactory
  {
    get => _engineFactory;
    set => _engineFactory = RegisteredEngineFactory = value;
  }

  private Func<CancellationToken, Task<IEngine>> _engineFactory;

  /// <summary>
  /// The factory this engine was registered with.
  /// </summary>
  /// <remarks>
  /// A generation switch registers a copy of each descriptor whose <see cref="EngineFactory"/> hands over the previous
  /// engine (see <see cref="EngineCoordinator.RestartWithDefinitionsAsync"/>); the copy keeps the registered factory
  /// here. A later switch, which gets that copy back from <see cref="EngineCoordinator.GetRegisteredDefinitions"/>,
  /// starts from this factory instead of the handover, so the generations do not chain up and keep each other alive.
  /// </remarks>
  internal Func<CancellationToken, Task<IEngine>> RegisteredEngineFactory { get; set; }

  /// <summary>
  /// Optional factory used to create a new engine instance by reusing an old one.
  /// </summary>
  /// <remarks>
  /// This hook is intended for generation switches where a previous engine instance
  /// can seed caches or state for the new version. If not configured, callers should
  /// fall back to <see cref="EngineFactory"/>.
  /// </remarks>
  public Func<EngineRestartInput, CancellationToken, Task<IEngine>> RestartFromPrevious { get; set; } = null;

  /// <summary>
  /// Factory used to compute the runtime context for the engine.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Context computation occurs after engine creation but before kick-start.
  /// </para>
  ///
  /// <para>
  /// By default, engines receive a <see cref="NoContext"/> instance,
  /// indicating that no contextual data or dependencies are required.
  /// </para>
  /// </remarks>
  public Func<CancellationToken, Task<IEngineContext>> ContextFactory { get; set; } =
      _ => Task.FromResult<IEngineContext>(new NoContext());

  /// <summary>
  /// Optional recovery policy enforcer for tasks associated with this engine.
  /// </summary>
  /// <remarks>
  /// <para>
  /// If specified, this helper governs how EngineTasks recover from failures
  /// (e.g. retry limits, backoff windows, restart suppression).
  /// </para>
  ///
  /// <para>
  /// Recovery applies to failures occurring within the scope of an EngineTask.
  /// Failures of dependency tasks are not recoverable here and must be handled
  /// by attaching recovery policies to those tasks instead.
  /// </para>
  ///
  /// <para>
  /// An enforcer keeps the restart count of the task it recovers. When it was set from a policy
  /// (<see cref="SetRecoveryHelper(RestartPolicy)"/>), each lifecycle task of the engine gets an enforcer of its own
  /// (see <see cref="CreateRecoveryHelper"/>); an enforcer set directly is shared by those tasks.
  /// </para>
  ///
  /// <para>
  /// When none is set, the lifecycle tasks recover by <see cref="RestartPolicy"/> instead.
  /// </para>
  /// </remarks>
  public IRestartPolicyEnforcer RecoveryHelper
  {
    get => _recoveryHelper;
    set
    {
      _recoveryHelper = value;
      // Set directly: the tasks share this enforcer, whatever policy was set before.
      RecoveryPolicy = null;
    }
  }

  private IRestartPolicyEnforcer _recoveryHelper;

  /// <summary>
  /// The policy that <see cref="RecoveryHelper"/> was created from with <see cref="SetRecoveryHelper(RestartPolicy)"/>,
  /// or null when the enforcer was set directly.
  /// </summary>
  /// <remarks>
  /// Setting <see cref="RecoveryHelper"/> clears it, so a copy sets it after that property.
  /// </remarks>
  internal RestartPolicy RecoveryPolicy { get; set; }

  /// <summary>
  /// Indicates whether this engine must be Ready before the application
  /// is considered started.
  /// </summary>
  /// <remarks>
  /// Engines marked as required block application readiness until they
  /// successfully complete initialization.
  /// </remarks>
  public bool RequiredForRunning { get; set; } = true;

  /// <summary>
  /// The restart policy of the engine's lifecycle tasks (creation, context, kick-start) when no
  /// <see cref="RecoveryHelper"/> is set; <see cref="RestartPolicy.Default"/> unless the host sets another, and null for
  /// no recovery.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Each lifecycle task gets an enforcer of its own from it (see <see cref="CreateRecoveryHelper"/>). An engine whose
  /// load fails therefore retries with backoff, staying Starting and logging each attempt, before it reports Faulted:
  /// with <see cref="RestartPolicy.Default"/> a permanent error faults after about 25 seconds (six attempts, the wait
  /// doubling from 1 s up to 10 s).
  /// </para>
  ///
  /// <para>
  /// A <see cref="RecoveryHelper"/> that is set (also through <see cref="SetRecoveryHelper(RestartPolicy)"/>) wins
  /// over this policy.
  /// </para>
  /// </remarks>
  public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.Default;

  /// <summary>
  /// Names of engines that this engine depends on.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Dependencies express a partial ordering between engines.
  /// An engine will not be started until all dependency engines
  /// have reached Ready.
  /// </para>
  ///
  /// <para>
  /// Cyclic dependencies are invalid and should be detected
  /// before the mechanic starts.
  /// </para>
  /// </remarks>
  public HashSet<string> DependsOn { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Creates a new engine descriptor.
  /// </summary>
  /// <param name="name">Logical engine name.</param>
  /// <param name="createEngineFactory">Factory for creating engine instances.</param>
  /// <param name="requiredForRunning">
  /// Whether the engine must be Ready for application startup.
  /// </param>
  /// <param name="version">
  /// Integer definition version for restart-diff decisions.
  /// </param>
  public EngineDescriptor(
      string name,
      Func<CancellationToken, Task<IEngine>> createEngineFactory,
      bool requiredForRunning = false,
      int version = 1)
  {
    if (version < 0)
      throw new ArgumentOutOfRangeException(nameof(version), "Version must be zero or greater.");

    Name = name ?? throw new ArgumentNullException(nameof(name));
    EngineFactory = createEngineFactory ?? throw new ArgumentException(nameof(createEngineFactory));
    RequiredForRunning = requiredForRunning;
    Version = version;
  }

  /// <summary>
  /// Declares a dependency on another engine.
  /// </summary>
  /// <param name="parentEngineName">Name of the engine this engine depends on.</param>
  /// <remarks>
  /// When the parent engine reaches a terminal state, dependent engine tasks
  /// are signaled via dependency triggers.
  /// </remarks>
  public virtual EngineDescriptor AddDependsOn(string parentEngineName)
  {
    DependsOn.Add(parentEngineName);
    return this;
  }

  /// <summary>
  /// Sets the definition version.
  /// </summary>
  public EngineDescriptor SetVersion(int version)
  {
    if (version < 0)
      throw new ArgumentOutOfRangeException(nameof(version), "Version must be zero or greater.");

    Version = version;
    return this;
  }

  /// <summary>
  /// Sets a warm-restart factory that can reuse a previous engine instance.
  /// </summary>
  public EngineDescriptor SetRestartFactory(Func<EngineRestartInput, CancellationToken, Task<IEngine>> restartFactory)
  {
    RestartFromPrevious = restartFactory;
    return this;
  }

  /// <summary>
  /// Creates an engine by optionally reusing a previous engine instance.
  /// </summary>
  /// <remarks>
  /// If <see cref="RestartFromPrevious"/> is not configured, this method falls back
  /// to <see cref="EngineFactory"/>.
  /// </remarks>
  public Task<IEngine> CreateEngineFromPreviousAsync(IEngine previousEngine, EngineDescriptor previousDescriptor,
      bool definitionChanged, bool dependencyChanged, CancellationToken token)
  {
    // The registered factory, not EngineFactory: on a descriptor from an earlier generation switch, that is the earlier
    // handover, which would hand over the wrong (an older) engine.
    if (RestartFromPrevious == null)
      return RegisteredEngineFactory(token);

    var input = new EngineRestartInput(
        Name,
        this,
        previousEngine,
        previousDescriptor,
        definitionChanged,
        dependencyChanged);
    return RestartFromPrevious(input, token);
  }

  /// <summary>
  /// Backward-compatible overload that treats this as a plain handover.
  /// </summary>
  public Task<IEngine> CreateEngineFromPreviousAsync(IEngine previousEngine, EngineDescriptor previousDescriptor, CancellationToken token)
      => CreateEngineFromPreviousAsync(previousEngine, previousDescriptor, false, false, token);

  /// <summary>
  /// Sets a custom context factory for this engine.
  /// </summary>
  public EngineDescriptor SetContextFactory(Func<CancellationToken, Task<IEngineContext>> contextFactory)
  {
    ContextFactory = contextFactory;
    return this;
  }

  /// <summary>
  /// Assigns a restart policy to this engine.
  /// </summary>
  /// <remarks>
  /// Each lifecycle task of the engine gets an enforcer of its own from this policy (see <see cref="CreateRecoveryHelper"/>).
  /// </remarks>
  public EngineDescriptor SetRecoveryHelper(RestartPolicy policy)
  {
    RecoveryHelper = policy.GetEnforcer();
    RecoveryPolicy = policy;
    return this;
  }

  /// <summary>
  /// Returns the recovery enforcer for one of the engine's lifecycle tasks (creation, context, kick-start).
  /// </summary>
  /// <remarks>
  /// A new one for each task when <see cref="RecoveryHelper"/> was set from a policy, since an enforcer keeps the restart
  /// count of one task: shared, the tasks added up each other's restarts, and a task that completed reset the count of
  /// the others. Otherwise the enforcer that was set directly, shared, as there is no telling how to create another.
  /// Without either, a new one from <see cref="RestartPolicy"/>, or null (no recovery) when that is null.
  /// </remarks>
  /// <param name="timeProvider">The clock of the task, for an enforcer created from the policy.</param>
  internal IRestartPolicyEnforcer CreateRecoveryHelper(TimeProvider timeProvider) =>
    RecoveryPolicy?.GetEnforcer(timeProvider) ?? RecoveryHelper ?? RestartPolicy?.GetEnforcer(timeProvider);

  /// <summary>
  /// Whether the engine's lifecycle tasks recover from a failure (see <see cref="CreateRecoveryHelper"/>).
  /// </summary>
  internal bool HasRecovery => RecoveryHelper != null || RestartPolicy != null;

  /// <summary>
  /// Assigns a concrete recovery enforcer to this engine.
  /// </summary>
  public EngineDescriptor SetRecoveryHelper(IRestartPolicyEnforcer recoveryHelper)
  {
    RecoveryHelper = recoveryHelper;
    return this;
  }

  /// <summary>
  /// Assigns a custom recovery delegate to this engine.
  /// </summary>
  /// <remarks>
  /// This overload exists to support advanced scenarios where recovery logic
  /// cannot be expressed via a standard <see cref="RestartPolicy"/>.
  /// </remarks>
  public EngineDescriptor SetRecoveryHelper(Func<EngineTask, CancellationToken, Task> recoveryAsync)
  {
    RecoveryHelper = new CustomRestarter(recoveryAsync);
    return this;
  }

  /*
   * Default IEquatable implementation
   */

  /// <summary>
  /// Determines equality with another engine descriptor.
  /// </summary>
  /// <remarks>
  /// Equality is defined over configuration identity rather than runtime state.
  /// This method assumes that delegates and policies implement meaningful equality.
  /// </remarks>
  public bool Equals(EngineDescriptor other)
  {
    if (other is null) return false;
    if (ReferenceEquals(this, other)) return true;

    // Fix (EngineDescriptorEqualityTest): the name was compared case-sensitively (engine names are not, and the hash
    // code already was not), and DependsOn by reference, so two descriptors were never equal by their dependencies.
    return String.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
           Version == other.Version &&
           EngineFactory.Equals(other.EngineFactory) &&
           (RestartFromPrevious == other.RestartFromPrevious || RestartFromPrevious != null && RestartFromPrevious.Equals(other.RestartFromPrevious)) &&
           RequiredForRunning == other.RequiredForRunning &&
           (ContextFactory == other.ContextFactory || ContextFactory != null && ContextFactory.Equals(other.ContextFactory)) &&
           // An enforcer created from a policy holds runtime state; the policy is the configuration.
           (RecoveryPolicy != null || other.RecoveryPolicy != null
             ? Equals(RecoveryPolicy, other.RecoveryPolicy)
             : RecoveryHelper == other.RecoveryHelper || RecoveryHelper != null && Equals(RecoveryHelper, other.RecoveryHelper)) &&
           (RestartPolicy == other.RestartPolicy || RestartPolicy != null && RestartPolicy.Equals(other.RestartPolicy)) &&
           DependsOn.SetEquals(other.DependsOn);
  }

  /// <inheritdoc />
  public override bool Equals(object obj)
  {
    if (obj is null) return false;
    if (ReferenceEquals(this, obj)) return true;
    if (obj.GetType() != GetType()) return false;
    return Equals((EngineDescriptor)obj);
  }

  /// <summary>
  /// Computes a hash code for this descriptor.
  /// </summary>
  /// <remarks>
  /// The hash code reflects the descriptor’s configuration identity.
  /// Mutating a descriptor while it is used as a key is undefined behavior.
  /// </remarks>
  public override int GetHashCode()
  {
    var hash = new HashCode();
    hash.Add(Name, StringComparer.OrdinalIgnoreCase);
    hash.Add(Version);
    hash.Add(EngineFactory);
    hash.Add(RestartFromPrevious);
    hash.Add(ContextFactory);
    hash.Add((object)RecoveryPolicy ?? RecoveryHelper);
    hash.Add(RequiredForRunning);
    hash.Add(RestartPolicy);
    // Fix (EngineDescriptorEqualityTest): by content, in a fixed order, as Equals now compares them; it was by reference.
    foreach (var dependencyName in DependsOn.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
      hash.Add(dependencyName, StringComparer.OrdinalIgnoreCase);
    return hash.ToHashCode();
  }
}
