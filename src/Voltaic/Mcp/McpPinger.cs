namespace Voltaic.Mcp
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Sends a <c>ping</c> at a fixed interval to check that the other side of a connection still answers, as the MCP
    /// ping utility recommends. A ping that fails or times out is logged; the connection itself is left to the
    /// transport. Thread-safe; dispose to stop.
    /// </summary>
    internal sealed class McpPinger : IDisposable
    {
        private readonly CancellationTokenSource _Stop = new CancellationTokenSource();
        private int _Disposed;

        private McpPinger(int intervalMs, Func<CancellationToken, Task<bool>> ping, Action<string> log)
        {
            _ = Task.Run(() => LoopAsync(intervalMs, ping, log, _Stop.Token));
        }

        /// <summary>
        /// Starts pinging every <paramref name="intervalMs"/> milliseconds, or returns null when the interval is 0.
        /// </summary>
        /// <param name="intervalMs">The interval; 0 disables pinging.</param>
        /// <param name="ping">Sends one ping and returns true when it was answered in time.</param>
        /// <param name="log">Receives a message when a ping is not answered.</param>
        internal static McpPinger? Start(int intervalMs, Func<CancellationToken, Task<bool>> ping, Action<string> log)
        {
            if (intervalMs <= 0) return null;
            return new McpPinger(intervalMs, ping, log);
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

        private static async Task LoopAsync(int intervalMs, Func<CancellationToken, Task<bool>> ping, Action<string> log, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(intervalMs, token).ConfigureAwait(false);
                    if (!await ping(token).ConfigureAwait(false))
                    {
                        log("The other side did not answer ping in time; the connection may be stale.");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log($"Ping failed: {ex.Message}");
                }
            }
        }
    }
}
