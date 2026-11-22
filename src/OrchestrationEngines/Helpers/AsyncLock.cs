using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OrchestrationEngines.Helpers;

/// <summary>
/// An asynchronous lock that is held for the scope of a <c>using</c>:
/// <c>using (await gate.LockAsync()) { ... }</c>. It replaces the wait, try and finally around a
/// <see cref="SemaphoreSlim"/> of one.
/// </summary>
internal sealed class AsyncLock
{
  private readonly SemaphoreSlim _gate = new(1, 1);

  /// <summary>
  /// Takes the lock, waiting while another holder has it. The await does not resume on the caller's context
  /// (<c>ConfigureAwait(false)</c> is built in), and allocates no more than the semaphore's own wait does.
  /// </summary>
  /// <returns>An awaitable whose result is the lock's releaser, which releases it when disposed.</returns>
  public Taking LockAsync() => new(this, _gate.WaitAsync());

  /// <summary>
  /// The awaitable <see cref="LockAsync"/> returns.
  /// </summary>
  internal readonly struct Taking
  {
    private readonly AsyncLock _owner;
    private readonly Task _wait;

    internal Taking(AsyncLock owner, Task wait)
    {
      _owner = owner;
      _wait = wait;
    }

    /// <summary>
    /// Gets the awaiter of the semaphore's wait, without the caller's context.
    /// </summary>
    /// <returns>The awaiter.</returns>
    public Awaiter GetAwaiter() => new(_owner, _wait.ConfigureAwait(false).GetAwaiter());
  }

  /// <summary>
  /// Awaits the semaphore's wait and hands out the releaser once it is taken.
  /// </summary>
  internal readonly struct Awaiter : ICriticalNotifyCompletion
  {
    private readonly AsyncLock _owner;
    private readonly ConfiguredTaskAwaitable.ConfiguredTaskAwaiter _wait;

    internal Awaiter(AsyncLock owner, ConfiguredTaskAwaitable.ConfiguredTaskAwaiter wait)
    {
      _owner = owner;
      _wait = wait;
    }

    /// <summary>
    /// Whether the lock is taken already.
    /// </summary>
    public bool IsCompleted => _wait.IsCompleted;

    /// <inheritdoc/>
    public void OnCompleted(Action continuation) => _wait.OnCompleted(continuation);

    /// <inheritdoc/>
    public void UnsafeOnCompleted(Action continuation) => _wait.UnsafeOnCompleted(continuation);

    /// <summary>
    /// Ends the wait.
    /// </summary>
    /// <returns>The lock's releaser.</returns>
    public Releaser GetResult()
    {
      _wait.GetResult();
      return new Releaser(_owner);
    }
  }

  /// <summary>
  /// Releases the lock it was taken from when disposed.
  /// </summary>
  internal readonly struct Releaser : IDisposable
  {
    private readonly AsyncLock _owner;

    internal Releaser(AsyncLock owner) => _owner = owner;

    /// <summary>
    /// Releases the lock.
    /// </summary>
    public void Dispose() => _owner?._gate.Release();
  }
}
