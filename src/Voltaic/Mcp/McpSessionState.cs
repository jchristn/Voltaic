namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
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
        private string? _NegotiatedVersion;
        private JsonElement? _ClientCapabilities;
        private string? _LogLevel;
        private bool _IsInitialized;
        private bool _ClientInitialized;

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
            if (_InFlight.TryAdd(idKey, request)) return request;
            request.Dispose();
            return null;
        }

        internal void EndRequest(McpInFlightRequest request)
        {
            _InFlight.TryRemove(new KeyValuePair<string, McpInFlightRequest>(request.IdKey, request));
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

            return false;
        }

        /// <summary>
        /// Returns the in-flight request that carries the progress token, or null.
        /// </summary>
        internal McpInFlightRequest? FindByProgressToken(string progressTokenJson)
        {
            return _InFlight.Values.FirstOrDefault(request =>
                request.ProgressToken.HasValue && StringComparer.Ordinal.Equals(request.ProgressToken.Value.GetRawText(), progressTokenJson));
        }

        /// <summary>
        /// Cancels every in-flight request, for example when the connection closes.
        /// </summary>
        internal void CancelAll()
        {
            foreach (McpInFlightRequest request in _InFlight.Values)
            {
                request.Cancel();
            }
        }
    }
}
