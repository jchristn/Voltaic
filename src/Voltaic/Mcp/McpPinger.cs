namespace Voltaic.Mcp
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Sends a <c>ping</c> at a fixed interval to check that the other side of a connection still answers, as the MCP
    /// ping utility recommends. Failed pings are logged, and after the configured number of consecutive failures the
    /// connection is treated as failed: the failure callback runs (the owner closes the connection) and pinging stops.
    /// Thread-safe; dispose to stop.
    /// </summary>
    internal sealed class McpPinger : IDisposable
    {
        private readonly CancellationTokenSource _Stop = new CancellationTokenSource();
        private int _Disposed;

        private McpPinger(int intervalMs, Func<CancellationToken, Task<bool>> ping, Action<string> log, Action? onFailure, int failureThreshold)
        {
            _ = Task.Run(() => LoopAsync(intervalMs, ping, log, onFailure, failureThreshold, _Stop.Token));
        }

        /// <summary>
        /// Starts pinging every <paramref name="intervalMs"/> milliseconds, or returns null when the interval is 0.
        /// </summary>
        /// <param name="intervalMs">The interval; 0 disables pinging.</param>
        /// <param name="ping">Sends one ping and returns true when it was answered in time.</param>
        /// <param name="log">Receives a message when a ping is not answered.</param>
        /// <param name="onFailure">Called when the connection is treated as failed, or null.</param>
        /// <param name="failureThreshold">Consecutive failed pings that make the connection failed; 0 only logs.</param>
        internal static McpPinger? Start(int intervalMs, Func<CancellationToken, Task<bool>> ping, Action<string> log, Action? onFailure = null, int failureThreshold = 1)
        {
            if (intervalMs <= 0) return null;
            return new McpPinger(intervalMs, ping, log, onFailure, failureThreshold);
        }

        /// <summary>
        /// Stops pinging.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 1) return;
            try
            {
                _Stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static async Task LoopAsync(int intervalMs, Func<CancellationToken, Task<bool>> ping, Action<string> log, Action? onFailure, int failureThreshold, CancellationToken token)
        {
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                bool answered;
                try
                {
                    await Task.Delay(intervalMs, token).ConfigureAwait(false);
                    answered = await ping(token).ConfigureAwait(false);
                    if (!answered) log("The other side did not answer ping in time.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log($"Ping failed: {ex.Message}");
                    answered = false;
                }

                failures = answered ? 0 : failures + 1;
                if (failureThreshold > 0 && failures >= failureThreshold)
                {
                    // Ping timeouts are connection failures (MCP ping utility): the owner closes the connection.
                    log($"Treating the connection as failed after {failures} unanswered ping(s).");
                    try
                    {
                        onFailure?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        log($"Closing the failed connection raised: {ex.Message}");
                    }

                    return;
                }
            }
        }
    }
}
