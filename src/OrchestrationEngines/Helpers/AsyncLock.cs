using System;
using System.Threading;
using System.Threading.Tasks;

namespace OrchestrationEngines.Helpers;

/// <summary>
/// An asynchronous lock that is held for the scope of a <c>using</c>:
/// <c>using (await gate.LockAsync().ConfigureAwait(false)) { ... }</c>. It replaces the wait, try and finally around
/// a <see cref="SemaphoreSlim"/> of one.
/// </summary>
internal sealed class AsyncLock
{
  private readonly SemaphoreSlim _gate = new(1, 1);

  /// <summary>
  /// What <see cref="LockAsync"/> returns when the lock is free: one completed task for every such take, so an
  /// uncontended take allocates nothing.
  /// </summary>
  private readonly Task<Releaser> _taken;

  /// <summary>
  /// Creates a free lock.
  /// </summary>
  public AsyncLock() => _taken = Task.FromResult(new Releaser(this));

  /// <summary>
  /// Takes the lock, waiting while another holder has it.
  /// </summary>
  /// <returns>The lock's releaser, which releases it when disposed.</returns>
  public Task<Releaser> LockAsync()
  {
    var wait = _gate.WaitAsync();
    return wait.Status == TaskStatus.RanToCompletion ? _taken : TakenAfterAsync(wait);
  }

  private async Task<Releaser> TakenAfterAsync(Task wait)
  {
    await wait.ConfigureAwait(false);
    return new Releaser(this);
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
