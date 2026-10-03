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
        // Requests read from the connection but not yet started (they start on the thread pool), by ID, with whether a
        // cancellation already arrived for them. Reserved in message order, so a cancellation that follows its request
        // always finds it, even when the ID was used before. A cancellation for any other ID is ignored (MCP: unknown
        // request IDs), so it can never affect a later request.
        private readonly Dictionary<string, bool> _Reserved = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly object _RequestLock = new object();
        // Pings this server sent to the client, by request ID JSON, completed by the client's response.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _PendingPings = new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);
        private McpPinger? _Pinger;
        private readonly CancellationTokenSource _Closed = new CancellationTokenSource();
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
            lock (_RequestLock)
            {
                if (_InFlight.TryAdd(idKey, request))
                {
                    bool cancelledWhileReserved = _Reserved.TryGetValue(idKey, out bool reservedCancel) && reservedCancel;
                    _Reserved.Remove(idKey);
                    if (method != "initialize" && cancelledWhileReserved) request.Cancel();
                    return request;
                }
            }

            request.Dispose();
            return null;
        }

        // True when a cancellation arrived for a reserved request that never started (it was rejected before running):
        // nothing may be sent for it.
        internal bool WasCancelledBeforeStart(string idKey)
        {
            lock (_RequestLock)
            {
                return _Reserved.TryGetValue(idKey, out bool cancelled) && cancelled;
            }
        }

        /// <summary>
        /// Reserves a request ID when the request is read, before it starts on the thread pool, so a cancellation read
        /// after it applies to it, also when the ID was used before (allowed once its earlier request was answered, and
        /// on 2026-07-28).
        /// </summary>
        internal void ReserveRequest(string idKey)
        {
            lock (_RequestLock)
            {
                if (_InFlight.ContainsKey(idKey)) return;
                _Reserved[idKey] = false;
            }
        }

        private readonly McpTokenBucket _LogBucket = new McpTokenBucket();

        // Identifies the client for McpRateLimits: unique per session unless the transport names a shared client (for
        // example the remote address of sessionless HTTP requests).
        internal string RateLimitKey { get; set; } = Guid.NewGuid().ToString("N");

        // The log message rate limit, set from McpRateLimits for every request the session makes.
        internal int LogRatePerSecond { get; set; } = 200;

        // The endpoint's per-client limiter, so the log limit follows RateLimitKey (shared by every request of a client),
        // not the session object; set by the processor for every request.
        internal McpRateLimiter? Limiter { get; set; }

        // The live rate limit settings, so a change applies at once, even to sessions that make no request.
        internal McpRateLimits? Limits { get; set; }

        // Takes one log message from the client's rate limit; false when over it.
        internal bool TryConsumeLog()
        {
            int rate = Limits?.LogMessagesPerSecond ?? LogRatePerSecond;
            McpRateLimiter? limiter = Limiter;
            if (limiter != null) return limiter.TryAcquire(RateLimitKey, "logs", rate);
            if (rate <= 0) return true;
            bool allowed = _LogBucket.TryTake(rate);
            Voltaic.Core.VoltaicInstruments.RateLimitDecision("logs", allowed);
            return allowed;
        }

        // True while any request of this session is running (a running request keeps the session alive).
        internal bool HasRequestsInFlight => !_InFlight.IsEmpty;

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
            lock (_RequestLock)
            {
                if (_InFlight.TryGetValue(idKey, out McpInFlightRequest? request))
                {
                    if (request.Method == "initialize") return false;
                    request.Cancel();
                    return true;
                }

                if (_Reserved.ContainsKey(idKey))
                {
                    _Reserved[idKey] = true;
                    return true;
                }

                // Unknown or already answered: ignored, as MCP asks of invalid cancellations.
                return false;
            }
        }

        // Records that a request with this ID was answered (or rejected); unless the ID is in flight again.
        internal void RecordFinished(string idKey)
        {
            lock (_RequestLock)
            {
                _Reserved.Remove(idKey);
            }
        }

        /// <summary>
        /// Returns the in-flight request that carries the progress token, or null.
        /// </summary>
        internal McpInFlightRequest? FindByProgressToken(string progressTokenJson)
        {
            return _InFlight.Values.FirstOrDefault(request =>
                request.IsActive && request.ProgressToken.HasValue && StringComparer.Ordinal.Equals(request.ProgressToken.Value.GetRawText(), progressTokenJson));
        }

        // Starts periodic pings to the client (stream transports, after initialize).
        internal void StartPinging(int intervalMs, int timeoutMs, int failureThreshold, Action<string> log)
        {
            if (Push == null || !CanPingClient) return;
            McpPinger? pinger = McpPinger.Start(intervalMs, token => PingAsync(timeoutMs, token), log, () => Terminate?.Invoke(), failureThreshold, "server");
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

        // Cancels every in-flight request, for example when the connection closes.
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
