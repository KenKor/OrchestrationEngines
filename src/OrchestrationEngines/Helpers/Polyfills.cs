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

    extension(string text)
    {
      /// <summary>Replaces every occurrence of <paramref name="oldValue"/>, compared with <paramref name="comparisonType"/>.</summary>
      /// <param name="oldValue">The text to replace.</param>
      /// <param name="newValue">The replacement text.</param>
      /// <param name="comparisonType">How occurrences are matched.</param>
      /// <returns>The text with every occurrence replaced.</returns>
      public string Replace(string oldValue, string newValue, StringComparison comparisonType)
      {
        if (oldValue is null)
          throw new ArgumentNullException(nameof(oldValue));
        if (oldValue.Length == 0)
          throw new ArgumentException("The value cannot be an empty string.", nameof(oldValue));

        var result = new Text.StringBuilder(text.Length);
        int start = 0;
        int index;
        while ((index = text.IndexOf(oldValue, start, comparisonType)) >= 0)
        {
          result.Append(text, start, index - start).Append(newValue);
          start = index + oldValue.Length;
        }
        return result.Append(text, start, text.Length - start).ToString();
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
  /// Task members that newer .NET versions offer.
  /// </summary>
  internal static class TaskPolyfills
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
