namespace Voltaic.Core
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text.Json;

    /// <summary>
    /// Tracks the progress tokens of a client's requests in flight and decides which <c>notifications/progress</c>
    /// messages reach the application: only those for an active token (MCP: receivers should track active progress
    /// tokens), and no more often than <see cref="MinIntervalMs"/> per token, except the final one (MCP: both parties
    /// should rate-limit progress). Thread-safe.
    /// </summary>
    internal sealed class ProgressTracker
    {
        private readonly object _Lock = new object();
        private readonly Dictionary<string, int> _Active = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _LastDelivered = new Dictionary<string, long>(StringComparer.Ordinal);
        private int _MinIntervalMs = 20;

        /// <summary>
        /// Gets or sets whether the filter applies (MCP clients); when false every notification is delivered.
        /// </summary>
        internal bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the minimum interval between delivered progress notifications for one token, in milliseconds.
        /// 0 delivers every one. Default is 20.
        /// </summary>
        internal int MinIntervalMs
        {
            get => _MinIntervalMs;
            set
            {
                if (value < 0 || value > 60000) throw new ArgumentOutOfRangeException(nameof(value), "The progress interval must be between 0 and 60000 ms.");
                _MinIntervalMs = value;
            }
        }

        /// <summary>
        /// Records the progress token a request's parameters carry in <c>_meta.progressToken</c>, if any.
        /// </summary>
        internal void Track(object? parameters)
        {
            string? key = TokenOf(parameters);
            if (key == null) return;
            lock (_Lock)
            {
                _Active[key] = _Active.TryGetValue(key, out int count) ? count + 1 : 1;
            }
        }

        /// <summary>
        /// Forgets the progress token of a request that finished.
        /// </summary>
        internal void Untrack(object? parameters)
        {
            string? key = TokenOf(parameters);
            if (key == null) return;
            lock (_Lock)
            {
                if (!_Active.TryGetValue(key, out int count)) return;
                if (count > 1)
                {
                    _Active[key] = count - 1;
                    return;
                }

                _Active.Remove(key);
                _LastDelivered.Remove(key);
            }
        }

        /// <summary>
        /// Returns whether a notification reaches the application. Anything other than <c>notifications/progress</c>
        /// always does.
        /// </summary>
        internal bool Accept(JsonRpcRequest notification)
        {
            if (!Enabled || notification == null || !StringComparer.Ordinal.Equals(notification.Method, "notifications/progress")) return true;

            JsonElement parameters;
            try
            {
                parameters = notification.Params is JsonElement element ? element : JsonSerializer.SerializeToElement(notification.Params);
            }
            catch (Exception error) when (error is JsonException || error is NotSupportedException)
            {
                return false;
            }

            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("progressToken", out JsonElement token)) return false;
            string key = token.GetRawText();
            bool final = parameters.TryGetProperty("progress", out JsonElement progress) && progress.ValueKind == JsonValueKind.Number
                && parameters.TryGetProperty("total", out JsonElement total) && total.ValueKind == JsonValueKind.Number
                && progress.GetDouble() >= total.GetDouble();

            lock (_Lock)
            {
                if (!_Active.ContainsKey(key)) return false;
                long now = Stopwatch.GetTimestamp();
                if (!final && _MinIntervalMs > 0 && _LastDelivered.TryGetValue(key, out long last)
                    && (now - last) * 1000 / Stopwatch.Frequency < _MinIntervalMs)
                {
                    return false;
                }

                _LastDelivered[key] = now;
                return true;
            }
        }

        private static string? TokenOf(object? parameters)
        {
            if (parameters == null) return null;
            try
            {
                JsonElement element = parameters is JsonElement json ? json : JsonSerializer.SerializeToElement(parameters);
                if (element.ValueKind != JsonValueKind.Object) return null;
                if (!element.TryGetProperty("_meta", out JsonElement meta) || meta.ValueKind != JsonValueKind.Object) return null;
                if (!meta.TryGetProperty("progressToken", out JsonElement token)) return null;
                return token.ValueKind == JsonValueKind.String || token.ValueKind == JsonValueKind.Number ? token.GetRawText() : null;
            }
            catch (Exception error) when (error is JsonException || error is NotSupportedException)
            {
                return null;
            }
        }
    }
}
