namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The MCP protocol state of one connection or session: whether it completed the <c>initialize</c> handshake,
    /// the negotiated version and client capabilities, the log level set with <c>logging/setLevel</c>, resource
    /// subscriptions, and the requests in flight. Thread-safe.
    /// </summary>
    internal sealed class McpSessionState
    {
        private readonly object _Lock = new object();
        private readonly HashSet<string> _Subscriptions = new HashSet<string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, McpInFlightRequest> _InFlight = new ConcurrentDictionary<string, McpInFlightRequest>(StringComparer.Ordinal);
        // Cancellations that arrived before their request started (requests start on the thread pool, notifications
        // are handled in order), kept briefly so the request is cancelled when it begins.
        private readonly ConcurrentDictionary<string, DateTime> _EarlyCancels = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly TimeSpan _EarlyCancelLifetime = TimeSpan.FromSeconds(30);
        // IDs of requests that finished recently: a cancellation for one of them arrived too late and is ignored, so
        // it can never cancel a later request that reuses the ID (allowed once the response was sent).
        private readonly ConcurrentDictionary<string, DateTime> _Completed = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        // Pings this server sent to the client, by request ID JSON, completed by the client's response.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _PendingPings = new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);
        private McpPinger? _Pinger;
        private readonly CancellationTokenSource _Closed = new CancellationTokenSource();
        private const int _MaxEarlyCancels = 256;
        private const int _MaxFinished = 4096;
        private string? _NegotiatedVersion;
        private JsonElement? _ClientCapabilities;
        private string? _LogLevel;
        private bool _IsInitialized;
        private bool _ClientInitialized;
        private int _InitializeClaimed;
        private int _NotificationsReady;

        /// <param name="requireInitialize">True when requests other than <c>initialize</c> and <c>ping</c> must wait for the handshake (MCP connections); false for Voltaic's sessionless <c>/rpc</c> endpoint.</param>
        internal McpSessionState(bool requireInitialize = true)
        {
            RequireInitialize = requireInitialize;
        }

        /// <summary>
        /// Gets or sets the transport object this state belongs to (for example its <c>ClientConnection</c>).
        /// </summary>
        internal object? Owner { get; set; }

        /// <summary>
        /// Gets whether handshake-era requests must follow a successful <c>initialize</c>.
        /// </summary>
        internal bool RequireInitialize { get; }

        // True for stream transports, whose connection can carry a ping request to the client at any time.
        internal bool CanPingClient { get; set; }

        // Closes the connection; used when the client stops answering pings.
        internal Action? Terminate { get; set; }

        /// <summary>
        /// Gets or sets how server-initiated messages (list changes, resource updates, log messages) reach the client, or
        /// null when the transport has no such channel.
        /// </summary>
        internal Func<string, CancellationToken, Task>? Push { get; set; }

        internal string? NegotiatedVersion
        {
            get { lock (_Lock) return _NegotiatedVersion; }
        }

        internal JsonElement? ClientCapabilities
        {
            get { lock (_Lock) return _ClientCapabilities; }
        }

        internal bool IsInitialized
        {
            get { lock (_Lock) return _IsInitialized; }
        }

        internal bool ClientInitialized
        {
            get { lock (_Lock) return _ClientInitialized; }
            set { lock (_Lock) _ClientInitialized = value; }
        }

        /// <summary>
        /// Gets or sets the minimum log level set with <c>logging/setLevel</c>, or null when the client set none.
        /// </summary>
        internal string? LogLevel
        {
            get { lock (_Lock) return _LogLevel; }
            set { lock (_Lock) _LogLevel = value; }
        }

        /// <summary>
        /// Records a successful <c>initialize</c>. Returns false when the session was already initialized.
        /// </summary>
        internal bool TryCompleteInitialize(string negotiatedVersion, JsonElement? clientCapabilities)
        {
            lock (_Lock)
            {
                if (_IsInitialized) return false;
                _IsInitialized = true;
                _NegotiatedVersion = negotiatedVersion;
                _ClientCapabilities = clientCapabilities;
                return true;
            }
        }

        // True once the initialize response has been sent (or, on HTTP, the session exists), so server notifications
        // can no longer overtake it.
        internal bool NotificationsReady => Volatile.Read(ref _NotificationsReady) == 1;

        internal void MarkNotificationsReady()
        {
            if (IsInitialized) Interlocked.Exchange(ref _NotificationsReady, 1);
        }

        // Claims the right to run initialize; false when one already ran or is running.
        internal bool TryClaimInitialize()
        {
            if (IsInitialized) return false;
            return Interlocked.CompareExchange(ref _InitializeClaimed, 1, 0) == 0;
        }

        // Releases the claim after an initialize that failed, so the client may try again.
        internal void ReleaseInitializeClaim()
        {
            Interlocked.Exchange(ref _InitializeClaimed, 0);
        }

        internal void Subscribe(string uri)
        {
            lock (_Lock) _Subscriptions.Add(uri);
        }

        internal void Unsubscribe(string uri)
        {
            lock (_Lock) _Subscriptions.Remove(uri);
        }

        internal bool IsSubscribed(string uri)
        {
            lock (_Lock) return _Subscriptions.Contains(uri);
        }

        /// <summary>
        /// Registers a request. Returns null when a request with the same ID is already in flight (IDs must be unique).
        /// </summary>
        internal McpInFlightRequest? TryBeginRequest(string idKey, string method, JsonElement? progressToken, CancellationToken parent)
        {
            McpInFlightRequest request = new McpInFlightRequest(idKey, method, progressToken, parent);
            if (_InFlight.TryAdd(idKey, request))
            {
                _Completed.TryRemove(idKey, out DateTime _);
                if (method != "initialize" && _EarlyCancels.TryRemove(idKey, out DateTime _)) request.Cancel();
                return request;
            }

            request.Dispose();
            return null;
        }

        internal void EndRequest(McpInFlightRequest request)
        {
            request.MarkCompleted();
            _InFlight.TryRemove(new KeyValuePair<string, McpInFlightRequest>(request.IdKey, request));
            RecordFinished(request.IdKey);
            request.Dispose();
        }

        /// <summary>
        /// Cancels an in-flight request by its raw JSON ID. <c>initialize</c> cannot be cancelled. Returns true when a
        /// request was cancelled.
        /// </summary>
        internal bool Cancel(string idKey)
        {
            if (_InFlight.TryGetValue(idKey, out McpInFlightRequest? request) && request.Method != "initialize")
            {
                request.Cancel();
                return true;
            }

            if (request == null && !_Completed.ContainsKey(idKey)) RememberEarlyCancel(idKey);
            return false;
        }

        // Records that a request with this ID was answered (or rejected); unless the ID is in flight again.
        internal void RecordFinished(string idKey)
        {
            if (_InFlight.ContainsKey(idKey)) return;

            // A cancellation that arrived for this ID before it was rejected is spent; it must not cancel a later
            // request that reuses the ID.
            _EarlyCancels.TryRemove(idKey, out DateTime _);
            _Completed[idKey] = DateTime.UtcNow;
            Prune(_Completed, _MaxFinished);
        }

        private static void Prune(ConcurrentDictionary<string, DateTime> entries, int limit)
        {
            if (entries.Count < limit) return;
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, DateTime> entry in entries)
            {
                if (now - entry.Value > _EarlyCancelLifetime) entries.TryRemove(entry.Key, out DateTime _);
            }

            // Keep the set bounded even when many requests finish within the lifetime: drop the oldest entries.
            if (entries.Count >= limit * 2)
            {
                foreach (KeyValuePair<string, DateTime> oldest in entries.OrderBy(entry => entry.Value).Take(entries.Count - limit).ToList())
                {
                    entries.TryRemove(oldest.Key, out DateTime _);
                }
            }
        }

        private void RememberEarlyCancel(string idKey)
        {
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, DateTime> entry in _EarlyCancels)
            {
                if (now - entry.Value > _EarlyCancelLifetime) _EarlyCancels.TryRemove(entry.Key, out DateTime _);
            }

            if (_EarlyCancels.Count >= _MaxEarlyCancels) return;
            _EarlyCancels[idKey] = now;
        }

        /// <summary>
        /// Returns the in-flight request that carries the progress token, or null.
        /// </summary>
        internal McpInFlightRequest? FindByProgressToken(string progressTokenJson)
        {
            return _InFlight.Values.FirstOrDefault(request =>
                request.IsActive && request.ProgressToken.HasValue && StringComparer.Ordinal.Equals(request.ProgressToken.Value.GetRawText(), progressTokenJson));
        }

        /// <summary>
        /// Cancels every in-flight request, for example when the connection closes.
        /// </summary>
        // Starts periodic pings to the client (stream transports, after initialize).
        internal void StartPinging(int intervalMs, int timeoutMs, int failureThreshold, Action<string> log)
        {
            if (Push == null || !CanPingClient) return;
            McpPinger? pinger = McpPinger.Start(intervalMs, token => PingAsync(timeoutMs, token), log, () => Terminate?.Invoke(), failureThreshold);
            Interlocked.Exchange(ref _Pinger, pinger)?.Dispose();
        }

        // Sends one ping request to the client and waits for its response.
        internal async Task<bool> PingAsync(int timeoutMs, CancellationToken token)
        {
            Func<string, CancellationToken, Task>? push = Push;
            if (push == null) return false;

            string id = "voltaic-ping-" + Guid.NewGuid().ToString("N");
            string idKey = JsonSerializer.Serialize(id);
            TaskCompletionSource<bool> answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _PendingPings[idKey] = answered;
            try
            {
                await push("{\"jsonrpc\":\"2.0\",\"id\":" + idKey + ",\"method\":\"ping\"}", token).ConfigureAwait(false);
                Task finished = await Task.WhenAny(answered.Task, Task.Delay(timeoutMs, token)).ConfigureAwait(false);
                if (finished != answered.Task && !token.IsCancellationRequested)
                {
                    // The ping timed out: tell the client to stop working on it (the cancellation utility asks the
                    // sender to cancel a request it no longer waits for). The connection may stay open when
                    // PingFailureThreshold allows further failures.
                    try
                    {
                        await push("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":" + idKey + ",\"reason\":\"ping timed out\"}}", token).ConfigureAwait(false);
                    }
                    catch (Exception sendError) when (sendError is IOException || sendError is ObjectDisposedException || sendError is InvalidOperationException || sendError is OperationCanceledException)
                    {
                    }
                }

                // Only a result answers a ping; an error response or no response in time is a failure.
                return finished == answered.Task && answered.Task.Result;
            }
            finally
            {
                _PendingPings.TryRemove(idKey, out TaskCompletionSource<bool>? _);
            }
        }

        // Completes a ping this server sent; returns false when the response answers something else.
        internal bool TryCompletePing(string idKey, bool succeeded)
        {
            if (!_PendingPings.TryRemove(idKey, out TaskCompletionSource<bool>? answered)) return false;
            answered.TrySetResult(succeeded);
            return true;
        }

        // Cancelled when the session ends, so work tied to it (such as an open GET stream) stops.
        internal CancellationToken Closed => _Closed.Token;

        internal void CancelAll()
        {
            try
            {
                _Closed.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Interlocked.Exchange(ref _Pinger, null)?.Dispose();
            foreach (McpInFlightRequest request in _InFlight.Values)
            {
                request.Cancel();
            }
        }
    }
}
