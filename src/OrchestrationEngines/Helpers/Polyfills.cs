// Polyfills for netstandard2.0: small stand-ins for runtime APIs that only exist in newer .NET versions.
// On the modern targets the real APIs are used and this file compiles to nothing.
#if !NET9_0_OR_GREATER

using System.Runtime.CompilerServices;

namespace System.Threading
{
  /// <summary>
  /// Stand-in for the .NET 9 <c>System.Threading.Lock</c> type, built on <see cref="Monitor"/> (what <c>lock</c> on a
  /// plain object uses). The compiler turns <c>lock</c> on this type into a call to <see cref="EnterScope"/>.
  /// </summary>
  internal sealed class Lock
  {
    private readonly object _monitor = new();

    /// <summary>Enters the lock; disposing the returned scope exits it.</summary>
    /// <returns>The scope that holds the lock.</returns>
    public Scope EnterScope()
    {
      Monitor.Enter(_monitor);
      return new Scope(_monitor);
    }

    /// <summary>Holds the lock until it is disposed.</summary>
    public ref struct Scope
    {
      private readonly object _monitor;

      internal Scope(object monitor) => _monitor = monitor;

      /// <summary>Exits the lock.</summary>
      public void Dispose() => Monitor.Exit(_monitor);
    }
  }
}

#endif

#if !NET8_0_OR_GREATER

namespace System
{
  /// <summary>
  /// Guard methods that newer .NET versions offer as static members of the exception types.
  /// </summary>
  internal static class ExceptionPolyfills
  {
    extension(ArgumentNullException)
    {
      /// <summary>Throws an <see cref="ArgumentNullException"/> if <paramref name="argument"/> is <c>null</c>.</summary>
      /// <param name="argument">The reference type argument to validate as non-null.</param>
      /// <param name="paramName">The name of the parameter with which <paramref name="argument"/> corresponds.</param>
      public static void ThrowIfNull(object argument, [Runtime.CompilerServices.CallerArgumentExpression(nameof(argument))] string paramName = null)
      {
        if (argument is null)
          throw new ArgumentNullException(paramName);
      }
    }

    extension(ArgumentOutOfRangeException)
    {
      /// <summary>Throws an <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is negative or zero.</summary>
      /// <param name="value">The argument to validate as non-zero or non-negative.</param>
      /// <param name="paramName">The name of the parameter with which <paramref name="value"/> corresponds.</param>
      public static void ThrowIfNegativeOrZero(int value, [Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string paramName = null)
      {
        if (value <= 0)
          throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} ('{value}') must be a non-negative and non-zero value.");
      }
    }
  }
}

namespace System.Threading.Tasks
{
  /// <summary>
  /// Options for awaiting a task, as in .NET 8. Only <see cref="SuppressThrowing"/> is supported.
  /// </summary>
  [Flags]
  internal enum ConfigureAwaitOptions
  {
    /// <summary>No options: continue on any context and throw when the task failed.</summary>
    None = 0,

    /// <summary>Do not throw when the awaited task failed or was cancelled.</summary>
    SuppressThrowing = 2,
  }

  /// <summary>
  /// Task members that newer .NET versions offer; each part sits in the block of the version that brought it.
  /// </summary>
  internal static partial class TaskPolyfills
  {
    extension(Task task)
    {
      /// <summary>
      /// Awaits the task without throwing when <see cref="ConfigureAwaitOptions.SuppressThrowing"/> is given. The
      /// continuation does not return to the captured context, as with the .NET 8 API without ContinueOnCapturedContext.
      /// </summary>
      /// <param name="options">The await options.</param>
      /// <returns>The awaitable.</returns>
      public ConfiguredTaskAwaitable ConfigureAwait(ConfigureAwaitOptions options)
      {
        if ((options & ~ConfigureAwaitOptions.SuppressThrowing) != 0)
          throw new ArgumentOutOfRangeException(nameof(options), options, "Only SuppressThrowing is supported on this target.");
        if ((options & ConfigureAwaitOptions.SuppressThrowing) == 0)
          return task.ConfigureAwait(false);

        // Reading Exception marks a failure as observed, as the .NET 8 implementation does.
        return task.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
          .ConfigureAwait(false);
      }
    }

    extension(Task)
    {
      /// <summary>Creates a task that completes after <paramref name="delay"/>, measured by <paramref name="timeProvider"/>.</summary>
      /// <param name="delay">The time to wait.</param>
      /// <param name="timeProvider">The time provider that measures the delay.</param>
      /// <param name="cancellationToken">Cancels the delay.</param>
      /// <returns>The delay task.</returns>
      public static Task Delay(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
        => timeProvider.Delay(delay, cancellationToken);
    }
  }
}

#endif

#if !NET5_0_OR_GREATER

namespace System.Threading.Tasks
{
  /// <summary>
  /// Stand-in for the .NET 5 non-generic <c>TaskCompletionSource</c>, built on <see cref="TaskCompletionSource{TResult}"/>.
  /// </summary>
  internal sealed class TaskCompletionSource
  {
    private readonly TaskCompletionSource<bool> _inner;

    /// <summary>Creates a completion source.</summary>
    public TaskCompletionSource() => _inner = new TaskCompletionSource<bool>();

    /// <summary>Creates a completion source with the given options.</summary>
    /// <param name="creationOptions">The options for the underlying task.</param>
    public TaskCompletionSource(TaskCreationOptions creationOptions) => _inner = new TaskCompletionSource<bool>(creationOptions);

    /// <summary>The task controlled by this completion source.</summary>
    public Task Task => _inner.Task;

    /// <summary>Completes the task successfully.</summary>
    public void SetResult() => _inner.SetResult(true);

    /// <summary>Tries to complete the task successfully.</summary>
    /// <returns><c>true</c> when this call completed the task.</returns>
    public bool TrySetResult() => _inner.TrySetResult(true);

    /// <summary>Completes the task with an exception.</summary>
    /// <param name="exception">The exception.</param>
    public void SetException(Exception exception) => _inner.SetException(exception);

    /// <summary>Tries to complete the task with an exception.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns><c>true</c> when this call completed the task.</returns>
    public bool TrySetException(Exception exception) => _inner.TrySetException(exception);

    /// <summary>Completes the task as cancelled.</summary>
    public void SetCanceled() => _inner.SetCanceled();

    /// <summary>Tries to complete the task as cancelled.</summary>
    /// <returns><c>true</c> when this call completed the task.</returns>
    public bool TrySetCanceled() => _inner.TrySetCanceled();

    /// <summary>Tries to complete the task as cancelled by the given token.</summary>
    /// <param name="cancellationToken">The token that caused the cancellation.</param>
    /// <returns><c>true</c> when this call completed the task.</returns>
    public bool TrySetCanceled(CancellationToken cancellationToken) => _inner.TrySetCanceled(cancellationToken);
  }
}

#endif

#if !NET6_0_OR_GREATER

namespace System
{
  /// <summary>
  /// The shared, thread-safe <see cref="Random"/> that .NET 6 offers as <c>Random.Shared</c>.
  /// </summary>
  internal static class RandomPolyfills
  {
    private static readonly Random SharedInstance = new LockedRandom();

    extension(Random)
    {
      /// <summary>A <see cref="Random"/> that can be used from any thread at once.</summary>
      public static Random Shared => SharedInstance;
    }

    /// <summary>
    /// A <see cref="Random"/> that takes a lock around each draw, since <see cref="Random"/> itself is not thread-safe.
    /// </summary>
    private sealed class LockedRandom : Random
    {
      private readonly object _lock = new();

      public override int Next() { lock (_lock) return base.Next(); }

      public override int Next(int maxValue) { lock (_lock) return base.Next(maxValue); }

      public override int Next(int minValue, int maxValue) { lock (_lock) return base.Next(minValue, maxValue); }

      public override double NextDouble() { lock (_lock) return base.NextDouble(); }

      public override void NextBytes(byte[] buffer) { lock (_lock) base.NextBytes(buffer); }
    }
  }
}

namespace System.Threading.Tasks
{
  /// <summary>
  /// The task members that .NET 6 brought.
  /// </summary>
  internal static partial class TaskPolyfills
  {
    extension(Task task)
    {
      /// <summary>
      /// Stand-in for <c>Task.WaitAsync(CancellationToken)</c> (.NET 6 and later): waits for the task until
      /// <paramref name="cancellationToken"/> is cancelled. Cancelling ends the wait, not the task.
      /// </summary>
      /// <param name="cancellationToken">Ends the wait.</param>
      /// <returns>The task's own outcome, or a cancellation when the token is cancelled first.</returns>
      public Task WaitAsync(CancellationToken cancellationToken)
        => task.IsCompleted || !cancellationToken.CanBeCanceled ? task : WaitCoreAsync(task, cancellationToken);
    }

    private static async Task WaitCoreAsync(Task task, CancellationToken cancellationToken)
    {
      // A second task completes when the token is cancelled; the wait ends with whichever comes first.
      var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      using var registration = cancellationToken.UnsafeRegister(
        _ => cancelled.TrySetCanceled(cancellationToken), null);
      var first = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
      await first.ConfigureAwait(false);
    }
  }
}

#endif

#if !NETCOREAPP3_0_OR_GREATER

namespace System.Threading
{
  /// <summary>
  /// Cancellation token members that newer .NET versions offer.
  /// </summary>
  internal static class CancellationTokenPolyfills
  {
    extension(CancellationToken token)
    {
      /// <summary>
      /// Stand-in for <c>CancellationToken.UnsafeRegister</c> (.NET Core 3.0 and later): registers
      /// <paramref name="callback"/> without capturing the caller's execution context, so it runs in the context of the
      /// thread that cancels.
      /// </summary>
      /// <param name="callback">What to run when the token is cancelled.</param>
      /// <param name="state">What <paramref name="callback"/> is passed.</param>
      /// <returns>The registration, to dispose when the callback is no longer wanted.</returns>
      public CancellationTokenRegistration UnsafeRegister(Action<object> callback, object state)
      {
        // The one place that may call Register: this is the stand-in the ban points to, and it suppresses the flow.
#pragma warning disable RS0030
        // SuppressFlow throws when the flow is suppressed already; the registration captures nothing then anyway.
        if (ExecutionContext.IsFlowSuppressed())
          return token.Register(callback, state);
        using (ExecutionContext.SuppressFlow())
          return token.Register(callback, state);
#pragma warning restore RS0030
      }
    }
  }
}

#endif

#if NET8_0_OR_GREATER

namespace System.Threading.Tasks
{
  /// <summary>
  /// The other way round: on netstandard2.0 the Microsoft.Bcl.TimeProvider package offers this extension, on .NET 8 the
  /// constructor <c>CancellationTokenSource(TimeSpan, TimeProvider)</c> does the same.
  /// </summary>
  internal static class TimeProviderPolyfills
  {
    extension(TimeProvider timeProvider)
    {
      /// <summary>
      /// Creates a source that is cancelled once <paramref name="delay"/> has passed on the time provider.
      /// </summary>
      /// <param name="delay">
      /// The time until the source is cancelled; <see cref="Timeout.InfiniteTimeSpan"/> for never.
      /// </param>
      /// <returns>The source.</returns>
      public CancellationTokenSource CreateCancellationTokenSource(TimeSpan delay) => new(delay, timeProvider);
    }
  }
}

#endif
