namespace Voltaic.Mcp
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// A token bucket: it refills at a rate per second up to a burst of the same size, and each operation takes one
    /// token. Thread-safe.
    /// </summary>
    internal sealed class McpTokenBucket
    {
        private readonly object _Lock = new object();
        private double _Tokens = -1;
        private long _LastRefill;

        /// <summary>
        /// Gets the timestamp of the last take, for pruning idle buckets.
        /// </summary>
        internal long LastUsed => _LastRefill;

        /// <summary>
        /// Takes one token at <paramref name="ratePerSecond"/>; false when none is left. A rate of 0 or less always allows.
        /// </summary>
        internal bool TryTake(int ratePerSecond)
        {
            if (ratePerSecond <= 0) return true;
            lock (_Lock)
            {
                long now = Stopwatch.GetTimestamp();
                if (_Tokens < 0)
                {
                    _Tokens = ratePerSecond;
                }
                else
                {
                    double elapsedSeconds = (now - _LastRefill) / (double)Stopwatch.Frequency;
                    _Tokens = Math.Min(ratePerSecond, _Tokens + elapsedSeconds * ratePerSecond);
                }

                _LastRefill = now;
                if (_Tokens < 1) return false;
                _Tokens -= 1;
                return true;
            }
        }
    }
}
