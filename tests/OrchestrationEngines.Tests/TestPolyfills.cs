// Polyfills for the .NET Framework test run: stand-ins for newer runtime APIs that the tests use.
// The net10.0 test run uses the real APIs and this file compiles to nothing.
#if NETFRAMEWORK

using System.Collections.Concurrent;

namespace System
{
  internal static class TestSystemPolyfills
  {
    extension(string text)
    {
      public int GetHashCode(StringComparison comparisonType) => comparisonType switch
      {
        StringComparison.Ordinal => StringComparer.Ordinal.GetHashCode(text),
        StringComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase.GetHashCode(text),
        StringComparison.CurrentCulture => StringComparer.CurrentCulture.GetHashCode(text),
        StringComparison.CurrentCultureIgnoreCase => StringComparer.CurrentCultureIgnoreCase.GetHashCode(text),
        StringComparison.InvariantCulture => StringComparer.InvariantCulture.GetHashCode(text),
        StringComparison.InvariantCultureIgnoreCase => StringComparer.InvariantCultureIgnoreCase.GetHashCode(text),
        _ => throw new ArgumentOutOfRangeException(nameof(comparisonType)),
      };
    }

    extension(ArgumentOutOfRangeException)
    {
      public static void ThrowIfLessThan<T>(T value, T other, [Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string paramName = null)
        where T : IComparable<T>
      {
        if (value.CompareTo(other) < 0)
          throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} ('{value}') must be greater than or equal to '{other}'.");
      }
    }
  }
}

namespace System.Collections.Generic
{
  internal static class TestCollectionPolyfills
  {
    public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
      => dictionary.TryGetValue(key, out var value) ? value : default;

    public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
      => dictionary.TryGetValue(key, out var value) ? value : defaultValue;

    public static void Clear<T>(this ConcurrentQueue<T> queue)
    {
      while (queue.TryDequeue(out _))
      {
      }
    }
  }
}

namespace System.Threading.Tasks
{
  internal static class TestTaskPolyfills
  {
    extension(Task task)
    {
      public bool IsCompletedSuccessfully => task.Status == TaskStatus.RanToCompletion;

      // WaitAsync(CancellationToken) comes from the library's own polyfill (Polyfills.cs), which the tests can see.

      public Task WaitAsync(TimeSpan timeout)
        => task.WaitAsync(timeout, CancellationToken.None);

      // Waits for the task to end, then passes on the task's own outcome, as the framework's WaitAsync does. The
      // Microsoft.Bcl.TimeProvider WaitAsync passes on a cancellation of a task it waits on without the task's token.
      public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
      {
        await Task.WhenAny(task).WaitAsync(timeout, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        await task.ConfigureAwait(false);
      }
    }

    extension<TResult>(Task<TResult> task)
    {
      public Task<TResult> WaitAsync(CancellationToken cancellationToken)
        => task.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);

      public Task<TResult> WaitAsync(TimeSpan timeout)
        => task.WaitAsync(timeout, CancellationToken.None);

      // As the non-generic one, then returns the result.
      public async Task<TResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
      {
        await ((Task)task).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
      }
    }

    extension(ValueTask)
    {
      public static ValueTask CompletedTask => default;
    }
  }
}

#endif
