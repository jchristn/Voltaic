namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;

    /// <summary>
    /// Token buckets per client and operation kind, for <see cref="McpRateLimits"/>. Buckets idle for a minute are
    /// dropped when the table grows. Thread-safe.
    /// </summary>
    internal sealed class McpRateLimiter
    {
        private const int _PruneThreshold = 10000;
        private readonly ConcurrentDictionary<string, McpTokenBucket> _Buckets = new ConcurrentDictionary<string, McpTokenBucket>(StringComparer.Ordinal);

        /// <summary>
        /// Takes one operation of <paramref name="kind"/> for <paramref name="clientKey"/>; false when over the rate.
        /// </summary>
        internal bool TryAcquire(string clientKey, string kind, int ratePerSecond)
        {
            if (ratePerSecond <= 0) return true;
            if (_Buckets.Count > _PruneThreshold) Prune();
            McpTokenBucket bucket = _Buckets.GetOrAdd(kind + "|" + clientKey, _ => new McpTokenBucket());
            bool allowed = bucket.TryTake(ratePerSecond);
            Voltaic.Core.VoltaicInstruments.RateLimitDecision(kind, allowed);
            return allowed;
        }

        private void Prune()
        {
            long cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 60;
            foreach (KeyValuePair<string, McpTokenBucket> entry in _Buckets)
            {
                if (entry.Value.LastUsed < cutoff) _Buckets.TryRemove(entry.Key, out McpTokenBucket? _);
            }
        }
    }
}
