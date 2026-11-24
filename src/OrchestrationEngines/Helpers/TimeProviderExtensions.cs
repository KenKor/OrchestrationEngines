namespace System.Threading.Tasks
{
  /// <summary>
  /// A cancellation token source on a <see cref="TimeProvider"/>, so that a clock a test advances itself ends it.
  /// </summary>
  internal static class TimeProviderExtensions
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
