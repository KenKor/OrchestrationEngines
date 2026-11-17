using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Tests;

/// <summary>
/// A task that shows its current token source, so a test can see when it is replaced or released.
/// </summary>
internal sealed class ProbeTask(string name, Func<CancellationToken, Task> workAsync, bool requiredForRunning = true,
    CancellationToken engineToken = default)
  : EngineTask(name, workAsync, requiredForRunning, NullLogger.Instance, engineToken)
{
  public CancellationTokenSource Source => _linkedCts;
}

/// <summary>
/// Checks on cancellation tokens shared by the tests.
/// </summary>
internal static class Tokens
{
  /// <summary>
  /// Whether the source behind <paramref name="token"/> has been disposed.
  /// </summary>
  public static bool IsDisposed(CancellationToken token)
  {
    try
    {
      _ = token.WaitHandle;
      return false;
    }
    catch (ObjectDisposedException)
    {
      return true;
    }
  }
}
