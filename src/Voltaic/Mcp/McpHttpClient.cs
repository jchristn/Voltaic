namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// Provides an HTTP-based MCP (Model Context Protocol) client implementation.
    /// Supports JSON-RPC 2.0 over HTTP with Server-Sent Events (SSE) for server-to-client messages.
    /// <para>
    /// Requests the server sends to the client over a session (on the GET stream or on a POST response stream) are
    /// always answered by POSTing the JSON-RPC response back to the endpoint: <c>ping</c> with an empty result
    /// (<c>{}</c>), as the MCP specification requires of both parties, other methods by handlers registered with
    /// <see cref="RegisterRequestHandler"/>, and anything else with <c>-32601</c> (method not found). The stateless
    /// 2026-07-28 revision forbids server requests on streams (servers use Multi Round-Trip Requests instead), so
    /// they are logged and ignored in stateless mode.
    /// </para>
    /// <para>
    /// SSE streams are parsed per the event stream rules: event IDs are tracked, empty priming events are skipped,
    /// and a stream the server closes is reopened (when <see cref="AutoReconnectSse"/> is true) with
    /// <c>Last-Event-ID</c> after the server's <c>retry</c> interval or <see cref="SseReconnectDelayMs"/>. A POST
    /// response stream that ends before its response is resumed the same way with a GET request.
    /// </para>
    /// </summary>
    public class McpHttpClient : IDisposable
    {
        /// <summary>
        /// Gets the session ID assigned by the server.
        /// This ID is used to correlate requests and receive notifications via SSE.
        /// </summary>
        public string? SessionId { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the client is currently connected to a server.
        /// A client is considered connected if it has a valid session ID.
        /// </summary>
        public bool IsConnected => !String.IsNullOrEmpty(SessionId);

        /// <summary>
        /// Gets a value indicating whether the SSE (Server-Sent Events) connection is active.
        /// </summary>
        public bool IsSseConnected => _IsSseConnected;

        /// <summary>
        /// Gets or sets the MCP protocol version. <see cref="ConnectAsync"/> and <see cref="ConnectStreamableAsync"/>
        /// request this version in <c>initialize</c> and replace it with the version the server negotiates;
        /// it is then sent in the <c>MCP-Protocol-Version</c> header of every request in the session.
        /// Default is <see cref="McpProtocol.LatestProtocolVersion"/>. Setting null or whitespace restores the default.
        /// </summary>
        public string ProtocolVersion
        {
            get => _ProtocolVersion;
            set => _ProtocolVersion = String.IsNullOrWhiteSpace(value) ? McpProtocol.LatestProtocolVersion : value;
        }

        /// <summary>
        /// Gets or sets the client name reported in the <c>clientInfo</c> of the <c>initialize</c> handshake
        /// and in the <c>_meta</c> client info of stateless (2026-07-28) requests.
        /// Default is <c>Voltaic.Mcp.HttpClient</c>.
        /// </summary>
        public string ClientName
        {
            get => _ClientName;
            set => _ClientName = String.IsNullOrWhiteSpace(value) ? "Voltaic.Mcp.HttpClient" : value;
        }

        /// <summary>
        /// Gets or sets the client version reported in the <c>clientInfo</c> of the <c>initialize</c> handshake
        /// and in the <c>_meta</c> client info of stateless (2026-07-28) requests. Default is <c>1.0.0</c>.
        /// </summary>
        public string ClientVersion
        {
            get => _ClientVersion;
            set => _ClientVersion = String.IsNullOrWhiteSpace(value) ? "1.0.0" : value;
        }

        /// <summary>
        /// Gets or sets how often the client pings the server after <c>initialize</c>, in milliseconds, to check that
        /// the connection is healthy (MCP ping utility); a ping that is not answered within <see cref="PingTimeoutMs"/>
        /// is logged. Default is 30000. 0 disables pinging. Maximum is 3600000. Takes effect at the next
        /// <c>initialize</c>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 3600000.</exception>
        public int PingIntervalMs
        {
            get => _PingIntervalMs;
            set
            {
                if (value < 0 || value > 3600000) throw new ArgumentOutOfRangeException(nameof(value), "PingIntervalMs must be between 0 and 3600000.");
                _PingIntervalMs = value;
            }
        }

        /// <summary>
        /// Gets or sets how long the client waits for the server to answer its periodic ping, in milliseconds. Default
        /// is 10000. Minimum is 100; maximum is 600000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 100 to 600000.</exception>
        public int PingTimeoutMs
        {
            get => _PingTimeoutMs;
            set
            {
                if (value < 100 || value > 600000) throw new ArgumentOutOfRangeException(nameof(value), "PingTimeoutMs must be between 100 and 600000.");
                _PingTimeoutMs = value;
            }
        }

        /// <summary>
        /// Gets or sets how many consecutive pings the server may leave unanswered before the client treats the
        /// connection as failed and disconnects (MCP: ping timeouts are connection failures). Default is 1. 0 only logs
        /// unanswered pings. Maximum is 100.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 100.</exception>
        public int PingFailureThreshold
        {
            get => _PingFailureThreshold;
            set
            {
                if (value < 0 || value > 100) throw new ArgumentOutOfRangeException(nameof(value), "PingFailureThreshold must be between 0 and 100.");
                _PingFailureThreshold = value;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the client is operating in the stateless (2026-07-28)
        /// mode established by <see cref="ConnectStatelessAsync"/>.
        /// </summary>
        public bool IsStateless => _Stateless;

        /// <summary>
        /// Gets or sets the request timeout in milliseconds.
        /// Default is 30000 (30 seconds). Minimum is 1000 (1 second).
        /// </summary>
        public int RequestTimeoutMs
        {
            get => _RequestTimeoutMs;
            set
            {
                if (value < 1000) throw new ArgumentOutOfRangeException(nameof(value), "Request timeout must be at least 1000ms");
                _RequestTimeoutMs = value;
            }
        }

        /// <summary>
        /// Gets or sets whether SSE streams the server closes, or that fail after opening, are reopened with
        /// <c>Last-Event-ID</c>, as the MCP Streamable HTTP transport (2025-03-26 to 2025-11-25) expects clients to do.
        /// Default is true. When false, the GET stream ends when the server closes it and an interrupted POST response
        /// stream is not resumed.
        /// </summary>
        public bool AutoReconnectSse { get; set; } = true;

        /// <summary>
        /// Gets or sets the delay in milliseconds before reopening an SSE stream when the server has not sent a
        /// <c>retry</c> value. A server-supplied <c>retry</c> value always takes precedence. Default is 1000.
        /// Minimum is 0; maximum is 600000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 600000.</exception>
        public int SseReconnectDelayMs
        {
            get => _SseReconnectDelayMs;
            set
            {
                if (value < 0 || value > 600000) throw new ArgumentOutOfRangeException(nameof(value), "SSE reconnect delay must be between 0 and 600000 ms.");
                _SseReconnectDelayMs = value;
            }
        }

        /// <summary>
        /// Gets or sets how many consecutive failed attempts to open an SSE stream are made before giving up. A
        /// successful connection resets the count. An HTTP 4xx answer (for example 404 for an expired session or 405
        /// for a server without a GET stream) stops reconnecting immediately. Default is 5. Minimum is 0.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set below 0.</exception>
        public int SseMaxReconnectAttempts
        {
            get => _SseMaxReconnectAttempts;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "SSE reconnect attempts must be 0 or more.");
                _SseMaxReconnectAttempts = value;
            }
        }

        /// <summary>
        /// Gets additional client capabilities to declare in <c>initialize</c> and in the <c>clientCapabilities</c> of
        /// stateless requests, merged with the capabilities implied by registered request handlers (<c>roots</c>,
        /// <c>sampling</c>, <c>elicitation</c>). For example, add <c>elicitation</c> when
        /// <see cref="CallToolStatelessAsync"/> answers elicitation input requests through its callback. Never null.
        /// Change it before connecting.
        /// </summary>
        public Dictionary<string, object?> ClientCapabilities { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

        /// <summary>
        /// Occurs when a notification (a message with a method and no ID) is received from the server via SSE.
        /// </summary>
        public event EventHandler<JsonRpcRequest>? NotificationReceived;

        /// <summary>
        /// Occurs when the client successfully connects to a server.
        /// </summary>
        public event EventHandler<ClientConnectedEventArgs>? Connected;

        /// <summary>
        /// Occurs when the client disconnects from the server.
        /// </summary>
        public event EventHandler<ClientDisconnectedEventArgs>? Disconnected;

        /// <summary>
        /// Occurs when a request is sent to the server.
        /// </summary>
        public event EventHandler<RequestSentEventArgs>? RequestSent;

        /// <summary>
        /// Occurs when a response is received from the server.
        /// </summary>
        public event EventHandler<ResponseReceivedEventArgs>? ResponseReceived;

        private HttpClient? _HttpClient;
        private readonly Dictionary<string, string> _RequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string? _BaseUrl;
        private string? _RpcUrl;
        private string? _EventsUrl;
        private CancellationTokenSource? _SseTokenSource;
        private Task? _SseTask;
        private bool _IsSseConnected = false;
        private bool _Stateless = false;
        private string _ClientName = "Voltaic.Mcp.HttpClient";
        private string _ClientVersion = "1.0.0";
        private string _ProtocolVersion = McpProtocol.LatestProtocolVersion;

        private int _RequestTimeoutMs = 30000;
        private bool _IsDisposed = false;
        private DateTime _ConnectedUtc;
        private int _SseReconnectDelayMs = 1000;
        private int _SseMaxReconnectAttempts = 5;
        private string? _SseLastEventId;
        private int? _SseRetryMs;
        private bool _HandshakeComplete;
        private bool _Streamable;
        private readonly SemaphoreSlim _RecoverLock = new SemaphoreSlim(1, 1);
        private bool _AutoNegotiate = true;
        private int _PingIntervalMs = 30000;
        private int _PingTimeoutMs = 10000;
        private int _PingFailureThreshold = 1;
        private McpPinger? _Pinger;
        private bool _SseWanted;
        private readonly ClientRequestDispatcher _RequestDispatcher = new ClientRequestDispatcher { AnswersPing = true };
        private CancellationTokenSource _ServerRequestTokenSource = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, List<McpHeaderParameter>> _ToolHeaderParameters = new ConcurrentDictionary<string, List<McpHeaderParameter>>(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHttpClient"/> class.
        /// </summary>
        public McpHttpClient()
        {
            _HttpClient = new HttpClient();
            _RequestDispatcher.Log = LogMessage;
        }

        /// <summary>
        /// Sets a default request header that is applied to every HTTP request this client sends — the
        /// JSON-RPC POST requests and the SSE GET stream, including the initial connection handshake.
        /// Use this to attach authentication such as an <c>Authorization: Bearer &lt;token&gt;</c> header
        /// or a custom API-key header (for example <c>X-API-Key</c>). Passing a null or empty
        /// <paramref name="value"/> removes any header previously set under <paramref name="name"/>.
        /// Header names are treated case-insensitively; setting the same name again replaces the previous
        /// value. Call this before <see cref="ConnectStreamableAsync"/> or <see cref="ConnectAsync"/> for
        /// the header to be present on the handshake request.
        /// </summary>
        /// <param name="name">The header name. Must not be null or empty.</param>
        /// <param name="value">The header value, or null or empty to remove the header.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null or empty.</exception>
        public void SetRequestHeader(string name, string? value)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            if (String.IsNullOrEmpty(value)) _RequestHeaders.Remove(name);
            else _RequestHeaders[name] = value!;

            if (_HttpClient != null)
            {
                _HttpClient.DefaultRequestHeaders.Remove(name);
                if (!String.IsNullOrEmpty(value))
                    _HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            }
        }

        /// <summary>
        /// Registers the handler for requests the server sends to this client with the given method name, replacing any
        /// previous handler for that method. Registering <c>roots/list</c>, <c>sampling/createMessage</c>, or
        /// <c>elicitation/create</c> before connecting also declares the matching client capability (<c>roots</c>,
        /// <c>sampling</c>, <c>elicitation</c>) in <c>initialize</c>, so servers know they may send those requests. The
        /// handler receives the request parameters (null when the request has none) and a token that is cancelled when
        /// the client disconnects; its return value is sent as the result (null is sent as an empty object). A handler
        /// that throws <see cref="McpProtocolException"/> sends that error; any other exception sends <c>-32603</c>
        /// without its message. Requests for methods without a handler are answered with <c>-32601</c>. <c>ping</c> is
        /// always answered by the client itself. Handlers run concurrently. Thread-safe.
        /// Capabilities are negotiated when connecting (and sent with every stateless request), so register handlers
        /// before calling <see cref="ConnectStreamableAsync"/> or <see cref="ConnectAsync"/>; a handler registered
        /// later still answers requests, but a conforming server will not send them.
        /// </summary>
        /// <param name="method">The method name, compared case-sensitively. Must not be null, empty, or whitespace, and must not be <c>ping</c>.</param>
        /// <param name="handler">The handler. Must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is null, empty, or whitespace, or <paramref name="handler"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="method"/> is <c>ping</c>.</exception>
        public void RegisterRequestHandler(string method, Func<RpcParameters?, CancellationToken, Task<object?>> handler)
        {
            _RequestDispatcher.Register(method, handler);
        }

        /// <summary>
        /// Removes the handler registered with <see cref="RegisterRequestHandler"/> for a method. Later requests for
        /// that method are answered with <c>-32601</c>. Thread-safe.
        /// </summary>
        /// <param name="method">The method name. Must not be null, empty, or whitespace.</param>
        /// <returns>True when a handler was removed; false when none was registered.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is null, empty, or whitespace.</exception>
        public bool UnregisterRequestHandler(string method)
        {
            return _RequestDispatcher.Unregister(method);
        }

        /// <summary>
        /// Asynchronously connects to an HTTP MCP server at the specified base URL using the JSON-RPC
        /// endpoint for requests and the SSE endpoint for notifications.
        /// Performs the MCP handshake: sends <c>initialize</c> (requesting <see cref="ProtocolVersion"/> and
        /// reporting <see cref="ClientName"/> and <see cref="ClientVersion"/>), stores the session ID and the
        /// negotiated protocol version the server returns, then sends <c>notifications/initialized</c>.
        /// </summary>
        /// <param name="baseUrl">The base URL of the server (e.g., "http://localhost:8080").</param>
        /// <param name="rpcPath">The RPC endpoint path. Default is "/rpc".</param>
        /// <param name="eventsPath">The SSE events endpoint path. Default is "/events".</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the connection was successful; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException">Thrown when baseUrl is null or empty.</exception>
        public async Task<bool> ConnectAsync(string baseUrl, string rpcPath = "/rpc", string eventsPath = "/events", CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));

            try
            {
                Disconnect();

                _BaseUrl = baseUrl.TrimEnd('/');
                _RpcUrl = $"{_BaseUrl}{rpcPath}";
                _EventsUrl = $"{_BaseUrl}{eventsPath}";
                _Streamable = false;
                await PerformHandshakeAsync(token).ConfigureAwait(false);

                _ConnectedUtc = DateTime.UtcNow;
                LogMessage($"Connected to {baseUrl}");
                RaiseConnected();
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Connection failed: {ex.Message}");
                SessionId = null;
                return false;
            }
        }

        /// <summary>
        /// Asynchronously connects to an HTTP MCP server using the Streamable HTTP transport.
        /// Uses a single endpoint path for both RPC (POST) and SSE (GET), with Mcp-Session-Id headers.
        /// Performs the MCP handshake: sends <c>initialize</c> (requesting <see cref="ProtocolVersion"/> and
        /// reporting <see cref="ClientName"/> and <see cref="ClientVersion"/>), stores the session ID and the
        /// negotiated protocol version the server returns, then sends <c>notifications/initialized</c>.
        /// This establishes the RPC/session side of the transport; call <see cref="StartSseAsync(System.Threading.CancellationToken)"/> to start the SSE stream used for notifications.
        /// </summary>
        /// <param name="baseUrl">The base URL of the server (e.g., "http://localhost:7891").</param>
        /// <param name="mcpPath">The MCP endpoint path. Default is "/mcp".</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the connection was successful; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException">Thrown when baseUrl is null or empty.</exception>
        public async Task<bool> ConnectStreamableAsync(string baseUrl, string mcpPath = "/mcp", CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));

            try
            {
                Disconnect();

                _BaseUrl = baseUrl.TrimEnd('/');
                _RpcUrl = $"{_BaseUrl}{mcpPath}";
                _EventsUrl = $"{_BaseUrl}{mcpPath}";
                _Streamable = true;
                await PerformHandshakeAsync(token).ConfigureAwait(false);

                _ConnectedUtc = DateTime.UtcNow;
                LogMessage($"Connected to {baseUrl} via Streamable HTTP");
                RaiseConnected();
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Streamable HTTP connection failed: {ex.Message}");
                SessionId = null;
                return false;
            }
        }

        /// <summary>
        /// Asynchronously starts the Server-Sent Events (SSE) connection to receive notifications from the server.
        /// A session must be established before calling this method.
        /// When using Streamable HTTP, this opens the GET side of the shared /mcp endpoint.
        /// </summary>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the SSE connection was established; otherwise, false.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no session has been established.</exception>
        public async Task<bool> StartSseAsync(CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(SessionId))
                throw new InvalidOperationException("No session established. Call ConnectAsync or ConnectStreamableAsync first.");

            try
            {
                StopSse();

                _SseLastEventId = null;
                _SseRetryMs = null;
                _SseWanted = true;
                _SseTokenSource = new CancellationTokenSource();
                _SseTask = Task.Run(() => SseLoop(_SseTokenSource.Token));

                // Give SSE connection a moment to establish
                await Task.Delay(100, token).ConfigureAwait(false);

                LogMessage("SSE connection started");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to start SSE: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the Server-Sent Events (SSE) connection.
        /// </summary>
        public void StopSse()
        {
            _SseWanted = false;
            CancellationTokenSource? source = _SseTokenSource;
            if (source != null && !source.IsCancellationRequested)
            {
                source.Cancel();
                LogMessage("SSE connection stopped");
            }

            _IsSseConnected = false;
        }

        /// <summary>
        /// Asynchronously invokes a remote method and returns the result as the specified type. When a request that
        /// carried a session gets HTTP 404 (the session expired or was terminated), the client starts a new session with
        /// <c>initialize</c> and retries once, as the Streamable HTTP transport requires. A call that times out or is
        /// cancelled sends <c>notifications/cancelled</c>.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the result into.</typeparam>
        /// <param name="method">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method. Can be null.</param>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Default is the value of RequestTimeoutMs property.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the method result.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized.</exception>
        /// <exception cref="Exception">Thrown when the remote method returns an error.</exception>
        public async Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 0, CancellationToken token = default)
        {
            JsonRpcResponse response = await CallAsync(method, parameters, timeoutMs, token).ConfigureAwait(false);
            if (response.Error != null)
            {
                throw new Exception($"RPC Error {response.Error.Code}: {response.Error.Message}");
            }

            if (response.Result == null)
            {
                return default(T)!;
            }

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(response.Result))!;
        }

        /// <summary>
        /// Sends the MCP <c>ping</c> request and completes when the server answers with any successful result.
        /// Voltaic 2.x servers and other specification-conformant servers answer with an empty object
        /// (<c>{}</c>); Voltaic 1.x servers answered with the string <c>"pong"</c>. Both are accepted, so use
        /// this method rather than <c>CallAsync&lt;string&gt;("ping")</c> to check connectivity.
        /// </summary>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Zero (the default) uses <see cref="RequestTimeoutMs"/>.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that completes when the server has answered the ping.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized, or when the server answers the ping with a JSON-RPC error.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled or times out.</exception>
        public async Task PingAsync(int timeoutMs = 0, CancellationToken token = default)
        {
            JsonRpcResponse response = await CallAsync("ping", null, timeoutMs, token).ConfigureAwait(false);
            if (response.Error != null)
            {
                throw new InvalidOperationException($"Ping failed with RPC error {response.Error.Code}: {response.Error.Message}");
            }
        }

        /// <summary>
        /// Asynchronously invokes a remote method and returns the raw JSON-RPC response. A 404 on a session request
        /// starts a new session and retries once; a call that times out or is cancelled sends
        /// <c>notifications/cancelled</c>.
        /// </summary>
        /// <param name="method">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method. Can be null.</param>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Default is the value of RequestTimeoutMs property.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the JSON-RPC response.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized.</exception>
        /// <exception cref="Exception">Thrown when the HTTP response body cannot be parsed as JSON-RPC.</exception>
        public Task<JsonRpcResponse> CallAsync(string method, object? parameters = null, int timeoutMs = 0, CancellationToken token = default)
        {
            // In stateless mode (2026-07-28) every request carries its own _meta and routing headers.
            if (_Stateless) return SendStatelessAsync(method, ToParameterMap(parameters), null, timeoutMs, token);
            return ExchangeAsync(method, parameters, timeoutMs, token);
        }

        // Converts request parameters to the field map stateless requests use; MCP params are always an object.
        private static IReadOnlyDictionary<string, object?>? ToParameterMap(object? parameters)
        {
            if (parameters == null) return null;
            if (parameters is IReadOnlyDictionary<string, object?> map) return map;
            JsonElement element = parameters is JsonElement json ? json : JsonSerializer.SerializeToElement(parameters);
            if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("MCP request parameters must be a JSON object.", nameof(parameters));
            Dictionary<string, object?> fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject()) fields[property.Name] = property.Value.Clone();
            return fields;
        }

        private async Task<JsonRpcResponse> ExchangeAsync(string method, object? parameters, int timeoutMs, CancellationToken token)
        {
            if (_HttpClient == null || String.IsNullOrEmpty(_RpcUrl))
                throw new InvalidOperationException("Client not initialized. Call ConnectAsync first.");

            if (timeoutMs == 0) timeoutMs = _RequestTimeoutMs;

            for (int attempt = 0; ; attempt++)
            {
                JsonRpcRequest request = new JsonRpcRequest
                {
                    Method = method,
                    Params = parameters,
                    Id = Guid.NewGuid().ToString()
                };

                DateTime sentUtc = DateTime.UtcNow;
                string requestJson = JsonSerializer.Serialize(request);
                LogMessage($"Sending request: {requestJson}");
                RaiseRequestSent(new RequestSentEventArgs(request));
                bool hadSession = !String.IsNullOrEmpty(SessionId);
                string? sentSessionId = SessionId;

                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(timeoutMs);
                    try
                    {
                        using HttpRequestMessage httpRequest = CreatePostRequest(requestJson);
                        HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                        // 404 to a request that carried a session: the session is gone, so start a new one (MUST).
                        if ((int)httpResponse.StatusCode == 404 && hadSession && attempt == 0 && method != "initialize" && _Streamable)
                        {
                            httpResponse.Dispose();
                            LogMessage($"Retrying {method} on a new session");
                            if (await RecoverSessionAsync(sentSessionId, token).ConfigureAwait(false)) continue;
                            throw McpProtocolException.SessionNotFound();
                        }

                        httpResponse.EnsureSuccessStatusCode();
                        CaptureSessionId(httpResponse);

                        string responseJson = await ReadResponseBodyAsync(httpResponse, request.Id, cts.Token).ConfigureAwait(false);
                        LogMessage($"Received response: {responseJson}");

                        JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);
                        if (response == null)
                        {
                            throw new Exception("Invalid response from server");
                        }

                        RaiseResponseReceived(new ResponseReceivedEventArgs(request, response, sentUtc));
                        return response;
                    }
                    catch (McpProtocolException lost) when (lost.Code == -32001 && hadSession && attempt == 0 && method != "initialize" && _Streamable)
                    {
                        // A resumed response stream found the session gone: start a new session and send the request again.
                        LogMessage($"Retrying {method} on a new session after its response stream was lost");
                        if (!await RecoverSessionAsync(sentSessionId, token).ConfigureAwait(false)) throw;
                    }
                    catch (OperationCanceledException) when (method != "initialize" && method != "notifications/cancelled")
                    {
                        SendCancellation(request.Id, token.IsCancellationRequested);
                        throw;
                    }
                }
            }
        }

        // Tells the server to stop working on a request the client gave up on (handshake-era sessions; on 2026-07-28
        // closing the response stream is the cancellation signal).
        private void SendCancellation(object? requestId, bool cancelledByCaller)
        {
            if (_Stateless || requestId == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await NotifyAsync("notifications/cancelled", new McpCancelledNotification
                    {
                        RequestId = requestId,
                        Reason = cancelledByCaller ? "The request was cancelled by the client." : "The request timed out."
                    }).ConfigureAwait(false);
                }
                catch
                {
                    // The session may be gone.
                }
            });
        }

        /// <summary>
        /// Connects to a server using the stateless Streamable HTTP transport (2026-07-28). No session
        /// is established; each request carries its own <c>_meta</c> and routing headers. This calls
        /// <see cref="DiscoverAsync"/> to verify reachability and, when <paramref name="autoNegotiate"/>
        /// is true, selects a mutually supported protocol version.
        /// </summary>
        /// <param name="baseUrl">The base URL of the server. Must not be null or empty.</param>
        /// <param name="mcpPath">The MCP endpoint path. Default is <c>/mcp</c>.</param>
        /// <param name="protocolVersion">The preferred protocol version. Default is <see cref="McpProtocol.NewestProtocolVersion"/>.</param>
        /// <param name="autoNegotiate">When true, switches to a server-supported version if the preferred one is not offered. Default is true.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>True if discovery succeeded; otherwise false.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseUrl"/> is null or empty.</exception>
        public async Task<bool> ConnectStatelessAsync(string baseUrl, string mcpPath = "/mcp", string? protocolVersion = null, bool autoNegotiate = true, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));

            try
            {
                Disconnect();

                _BaseUrl = baseUrl.TrimEnd('/');
                _RpcUrl = $"{_BaseUrl}{mcpPath}";
                _EventsUrl = _RpcUrl;
                _Stateless = true;
                _AutoNegotiate = autoNegotiate;
                _ProtocolVersion = String.IsNullOrWhiteSpace(protocolVersion) ? McpProtocol.NewestProtocolVersion : protocolVersion!;

                McpDiscoverResult discover;
                try
                {
                    discover = await DiscoverAsync(token).ConfigureAwait(false);
                }
                catch (McpProtocolException unsupported) when (unsupported.Code == -32022 && autoNegotiate)
                {
                    // The server lists what it supports; pick a stateless-era version both sides implement and retry.
                    string? fallback = PickSupportedStatelessVersion(unsupported.ErrorData);
                    if (fallback == null) throw;
                    LogMessage($"The server does not support {_ProtocolVersion}; retrying with {fallback}");
                    _ProtocolVersion = fallback;
                    discover = await DiscoverAsync(token).ConfigureAwait(false);
                }

                if (autoNegotiate && discover.SupportedVersions != null && discover.SupportedVersions.Count > 0
                    && !discover.SupportedVersions.Contains(_ProtocolVersion))
                {
                    foreach (string candidate in discover.SupportedVersions)
                    {
                        if (McpProtocol.IsSupportedVersion(candidate) && McpProtocol.GetEra(candidate) == McpProtocolEra.Stateless)
                        {
                            _ProtocolVersion = candidate;
                            break;
                        }
                    }
                }

                _ConnectedUtc = DateTime.UtcNow;
                LogMessage($"Connected to {baseUrl} via stateless Streamable HTTP ({_ProtocolVersion})");
                RaiseConnected();
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Stateless connection failed: {ex.Message}");
                _Stateless = false;
                return false;
            }
        }

        /// <summary>
        /// Calls the <c>server/discover</c> RPC and returns the parsed discovery result (2026-07-28).
        /// </summary>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The discovery result.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized.</exception>
        /// <exception cref="McpProtocolException">Thrown when the server returns a JSON-RPC error.</exception>
        public async Task<McpDiscoverResult> DiscoverAsync(CancellationToken token = default)
        {
            JsonRpcResponse response = await SendStatelessAsync("server/discover", null, null, token).ConfigureAwait(false);
            if (response.Error != null)
            {
                throw new McpProtocolException(response.Error.Code, response.Error.Message ?? "server/discover failed.", response.Error.Data);
            }

            McpDiscoverResult? discover = DeserializeResult<McpDiscoverResult>(response.Result);
            if (discover != null)
            {
                return discover;
            }

            throw new McpProtocolException(-32603, "Invalid server/discover response.");
        }

        /// <summary>
        /// Sends a single stateless (2026-07-28) JSON-RPC request. The client injects the required
        /// <c>_meta</c> (protocol version, client info, client capabilities) into the params and sets
        /// the <c>MCP-Protocol-Version</c>, <c>Mcp-Method</c>, and <c>Mcp-Name</c> routing headers. <c>Mcp-Name</c>
        /// is <paramref name="name"/> when supplied, otherwise <c>params.name</c> or <c>params.uri</c> for
        /// <c>tools/call</c>, <c>prompts/get</c>, and <c>resources/read</c>; values that are not plain header-safe
        /// ASCII are sent in the <c>=?base64?...?=</c> form.
        /// <para>
        /// Tool parameters that a tool definition annotates with <c>x-mcp-header</c> are mirrored into
        /// <c>Mcp-Param-{Name}</c> headers on <c>tools/call</c>, using the definitions from the most recent
        /// <c>tools/list</c>. Tool definitions whose annotations are invalid are removed from <c>tools/list</c> results,
        /// and a warning naming the tool and the reason is written to <see cref="Log"/>. When a <c>tools/call</c> is
        /// rejected with <c>HeaderMismatch</c> (<c>-32020</c>), the client lists the tools again and retries once.
        /// </para>
        /// The raw response is returned, including error responses.
        /// </summary>
        /// <param name="method">The JSON-RPC method. Must not be null or empty.</param>
        /// <param name="parameters">The request parameters as a field map, or null.</param>
        /// <param name="name">The routing name for the <c>Mcp-Name</c> header (the tool name or resource URI), or null to take it from the parameters.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The JSON-RPC response.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is null or empty.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized.</exception>
        public Task<JsonRpcResponse> SendStatelessAsync(string method, IReadOnlyDictionary<string, object?>? parameters, string? name, CancellationToken token = default)
        {
            return SendStatelessAsync(method, parameters, name, 0, token);
        }

        private async Task<JsonRpcResponse> SendStatelessAsync(string method, IReadOnlyDictionary<string, object?>? parameters, string? name, int timeoutMs, CancellationToken token)
        {
            if (String.IsNullOrEmpty(method)) throw new ArgumentNullException(nameof(method));

            JsonRpcResponse response = await SendStatelessCoreAsync(method, parameters, name, timeoutMs, token).ConfigureAwait(false);
            if (response.Error != null && response.Error.Code == -32022 && _AutoNegotiate)
            {
                // The server no longer supports the version in use: switch to one it lists and send the request again.
                string? fallback = PickSupportedStatelessVersion(response.Error.Data);
                if (fallback != null && !StringComparer.Ordinal.Equals(fallback, _ProtocolVersion))
                {
                    LogMessage($"The server does not support {_ProtocolVersion}; retrying {method} with {fallback}");
                    _ProtocolVersion = fallback;
                    response = await SendStatelessCoreAsync(method, parameters, name, timeoutMs, token).ConfigureAwait(false);
                }
            }

            if (StringComparer.Ordinal.Equals(method, "tools/call") && response.Error != null && response.Error.Code == -32020)
            {
                LogMessage($"tools/call was rejected with HeaderMismatch ({response.Error.Message}); refreshing tool definitions and retrying once");
                await RefreshToolDefinitionsAsync(token).ConfigureAwait(false);
                response = await SendStatelessCoreAsync(method, parameters, name, timeoutMs, token).ConfigureAwait(false);
            }

            return response;
        }

        private async Task<JsonRpcResponse> SendStatelessCoreAsync(string method, IReadOnlyDictionary<string, object?>? parameters, string? name, int timeoutMs, CancellationToken token, bool reissued = false)
        {
            if (_HttpClient == null || String.IsNullOrEmpty(_RpcUrl))
                throw new InvalidOperationException("Client not initialized. Call ConnectStatelessAsync first.");

            Dictionary<string, object?> paramsObject = BuildStatelessParams(parameters);
            JsonRpcRequest request = new JsonRpcRequest
            {
                Method = method,
                Params = paramsObject,
                Id = Guid.NewGuid().ToString()
            };

            DateTime sentUtc = DateTime.UtcNow;
            string requestJson = JsonSerializer.Serialize(request);
            LogMessage($"Sending stateless request: {requestJson}");
            RaiseRequestSent(new RequestSentEventArgs(request));

            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(timeoutMs > 0 ? timeoutMs : _RequestTimeoutMs);

                using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _RpcUrl);
                httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                httpRequest.Headers.Accept.ParseAdd("application/json");
                httpRequest.Headers.Accept.ParseAdd("text/event-stream");
                httpRequest.Headers.Add(McpProtocol.ProtocolVersionHeader, _ProtocolVersion);
                httpRequest.Headers.Add(McpProtocol.MethodHeader, method);
                string? routingName = name ?? GetRoutingName(method, parameters);
                if (!String.IsNullOrEmpty(routingName))
                {
                    httpRequest.Headers.TryAddWithoutValidation(McpProtocol.NameHeader, McpHeaderParameters.Encode(routingName!));
                }

                if (StringComparer.Ordinal.Equals(method, "tools/call") && !String.IsNullOrEmpty(routingName))
                {
                    AddParamHeaders(httpRequest, routingName!, parameters);
                }

                HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                string responseJson;
                try
                {
                    responseJson = await ReadResponseBodyAsync(httpResponse, request.Id, cts.Token).ConfigureAwait(false);
                }
                catch (Exception broken) when (!reissued && !cts.IsCancellationRequested && (broken is IOException || broken is HttpRequestException))
                {
                    // The connection broke before the response arrived; 2026-07-28 clients must re-issue with a new ID.
                    LogMessage($"The response for {method} broke off ({broken.Message}); re-issuing the request");
                    return await SendStatelessCoreAsync(method, parameters, name, timeoutMs, token, true).ConfigureAwait(false);
                }

                LogMessage($"Received stateless response ({(int)httpResponse.StatusCode}): {responseJson}");

                if (String.IsNullOrEmpty(responseJson))
                {
                    // A broken response stream loses the request; 2026-07-28 clients must re-issue it with a new ID.
                    if (!reissued && StringComparer.OrdinalIgnoreCase.Equals(httpResponse.Content.Headers.ContentType?.MediaType, "text/event-stream"))
                    {
                        LogMessage($"The response stream for {method} ended without a response; re-issuing the request");
                        return await SendStatelessCoreAsync(method, parameters, name, timeoutMs, token, true).ConfigureAwait(false);
                    }

                    throw new McpProtocolException(-32603, $"Empty stateless response (HTTP {(int)httpResponse.StatusCode}).");
                }

                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);
                if (response == null)
                {
                    throw new McpProtocolException(-32603, "Invalid stateless response from server.");
                }

                // resultType values this client does not recognize make the result invalid (a missing one is "complete",
                // for servers that predate the field).
                string? resultType = ReadResultType(response.Result);
                if (response.Error == null && resultType != null && resultType != McpResult.ResultTypeComplete && resultType != McpResult.ResultTypeInputRequired)
                {
                    LogMessage($"Rejecting a {method} result with unrecognized resultType '{resultType}'");
                    response = new JsonRpcResponse
                    {
                        Id = response.Id,
                        Error = new JsonRpcError { Code = -32603, Message = $"The server returned an unrecognized resultType '{resultType}'." }
                    };
                }

                if (StringComparer.Ordinal.Equals(method, "tools/list") && response.Error == null)
                {
                    response.Result = FilterToolDefinitions(response.Result);
                }

                RaiseResponseReceived(new ResponseReceivedEventArgs(request, response, sentUtc));
                return response;
            }
        }

        /// <summary>
        /// Sends a stateless request and deserializes the successful result into <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="method">The JSON-RPC method.</param>
        /// <param name="parameters">The request parameters as a field map, or null.</param>
        /// <param name="name">The routing name for the <c>Mcp-Name</c> header, or null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The deserialized result.</returns>
        /// <exception cref="McpProtocolException">Thrown when the server returns a JSON-RPC error.</exception>
        public async Task<T> CallStatelessAsync<T>(string method, IReadOnlyDictionary<string, object?>? parameters = null, string? name = null, CancellationToken token = default)
        {
            JsonRpcResponse response = await SendStatelessAsync(method, parameters, name, token).ConfigureAwait(false);
            if (response.Error != null)
            {
                throw new McpProtocolException(response.Error.Code, response.Error.Message ?? "Stateless request failed.", response.Error.Data);
            }

            return DeserializeResult<T>(response.Result)!;
        }

        /// <summary>
        /// Calls a tool over the stateless transport, following the Multi Round-Trip Requests (MRTR)
        /// pattern. When the server responds with an input-required result and
        /// <paramref name="provideInputResponses"/> is supplied, the client gathers the responses and
        /// retries the original call — echoing the server's <c>requestState</c> — until a final result
        /// arrives or <paramref name="maxInputRounds"/> is exhausted. A server may request only the kinds of input the
        /// client declares, so add the matching entries (for example <c>elicitation</c>) to
        /// <see cref="ClientCapabilities"/> before calling; otherwise the server answers <c>-32021</c>.
        /// </summary>
        /// <param name="name">The tool name. Must not be null or empty.</param>
        /// <param name="arguments">The tool arguments, or null.</param>
        /// <param name="provideInputResponses">Callback that returns the responses for an input-required result, or null to return the input-required result without retrying.</param>
        /// <param name="maxInputRounds">The maximum number of input rounds to satisfy. Default is 3. Minimum meaningful value is 1.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The final (or last) JSON-RPC response.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null or empty.</exception>
        public async Task<JsonRpcResponse> CallToolStatelessAsync(
            string name,
            object? arguments = null,
            Func<McpInputRequiredResult, IReadOnlyDictionary<string, object?>>? provideInputResponses = null,
            int maxInputRounds = 3,
            CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            IReadOnlyDictionary<string, object?>? pendingResponses = null;
            string? requestState = null;
            int round = 0;

            while (true)
            {
                Dictionary<string, object?> toolParams = new Dictionary<string, object?>(StringComparer.Ordinal) { { "name", name } };
                if (arguments != null)
                {
                    toolParams["arguments"] = arguments;
                }

                if (pendingResponses != null)
                {
                    toolParams["inputResponses"] = pendingResponses;
                }

                if (requestState != null)
                {
                    toolParams["requestState"] = requestState;
                }

                JsonRpcResponse response = await SendStatelessAsync("tools/call", toolParams, name, token).ConfigureAwait(false);
                if (response.Error != null)
                {
                    return response;
                }

                McpInputRequiredResult? inputRequired = TryParseInputRequired(response.Result);
                if (inputRequired == null)
                {
                    return response;
                }

                round++;
                if (provideInputResponses == null || round > maxInputRounds)
                {
                    return response;
                }

                pendingResponses = provideInputResponses(inputRequired);
                requestState = inputRequired.RequestState;
            }
        }

        /// <summary>
        /// Asynchronously sends a JSON-RPC notification to the server.
        /// </summary>
        /// <param name="method">The notification method.</param>
        /// <param name="parameters">The notification parameters. Can be null.</param>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for HTTP acceptance. Default is the value of RequestTimeoutMs property.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized.</exception>
        public async Task NotifyAsync(string method, object? parameters = null, int timeoutMs = 0, CancellationToken token = default)
        {
            if (_HttpClient == null || String.IsNullOrEmpty(_RpcUrl))
                throw new InvalidOperationException("Client not initialized. Call ConnectAsync first.");

            if (timeoutMs == 0) timeoutMs = _RequestTimeoutMs;

            JsonRpcRequest request = new JsonRpcRequest
            {
                Method = method,
                // In stateless mode (2026-07-28) a notification carries the protocol version and client identity too.
                Params = _Stateless ? BuildStatelessParams(ToParameterMap(parameters)) : parameters
            };

            string requestJson = JsonSerializer.Serialize(request);
            LogMessage($"Sending notification: {requestJson}");
            RaiseRequestSent(new RequestSentEventArgs(request));

            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(timeoutMs);

                string? sentSessionId = SessionId;
                using HttpRequestMessage httpRequest = CreatePostRequest(requestJson);
                if (_Stateless) httpRequest.Headers.TryAddWithoutValidation(McpProtocol.MethodHeader, method);
                HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
                if ((int)httpResponse.StatusCode == 404 && !String.IsNullOrEmpty(sentSessionId) && _Streamable)
                {
                    // The session is gone: start a new one. A notification about the old session (initialized or a
                    // cancellation) means nothing to the new one; anything else is sent again.
                    httpResponse.Dispose();
                    bool recovered = await RecoverSessionAsync(sentSessionId, token).ConfigureAwait(false);
                    if (!recovered) throw McpProtocolException.SessionNotFound();
                    if (method == "notifications/initialized" || method == "notifications/cancelled") return;

                    using HttpRequestMessage retry = CreatePostRequest(requestJson);
                    httpResponse = await _HttpClient.SendAsync(retry, cts.Token).ConfigureAwait(false);
                }

                httpResponse.EnsureSuccessStatusCode();
                CaptureSessionId(httpResponse);
            }
        }

        /// <summary>
        /// Disconnects from the server and stops the SSE connection if active.
        /// </summary>
        public void Disconnect()
        {
            _Pinger?.Dispose();
            _Pinger = null;
            CancellationTokenSource previousRequests = Interlocked.Exchange(ref _ServerRequestTokenSource, new CancellationTokenSource());
            previousRequests.Cancel();
            previousRequests.Dispose();

            _HandshakeComplete = false;
            _Stateless = false;
            if (!String.IsNullOrEmpty(SessionId))
            {
                StopSse();
                TryDeleteSession();
                RaiseDisconnected("Client disconnected");
                SessionId = null;
                LogMessage("Disconnected");
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="McpHttpClient"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="McpHttpClient"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_IsDisposed)
            {
                _IsDisposed = true;

                if (disposing)
                {
                    Disconnect();
                    _SseTokenSource?.Dispose();
                    _ServerRequestTokenSource.Dispose();
                    _HttpClient?.Dispose();
                }
            }
        }

        private async Task SseLoop(CancellationToken token)
        {
            int consecutiveFailures = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (String.IsNullOrEmpty(_EventsUrl) || String.IsNullOrEmpty(SessionId))
                    {
                        LogMessage("Cannot start SSE: missing URL or session ID");
                        return;
                    }

                    bool opened = false;
                    try
                    {
                        using HttpRequestMessage request = CreateStreamRequest(_SseLastEventId);
                        using HttpResponseMessage response = await _HttpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        int status = (int)response.StatusCode;
                        if (status == 404 && _Streamable && !String.IsNullOrEmpty(SessionId))
                        {
                            // The session is gone: start a new one, which reopens the stream on it.
                            string? lost = SessionId;
                            LogMessage("SSE stream found the session gone; starting a new session");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await RecoverSessionAsync(lost, CancellationToken.None).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    LogMessage($"Could not start a new session: {ex.Message}");
                                }
                            });
                            return;
                        }

                        if (status >= 400 && status < 500)
                        {
                            // 405 (no GET stream) or another client error: retrying cannot help.
                            LogMessage($"SSE stream rejected with HTTP {status}; not reconnecting");
                            return;
                        }

                        response.EnsureSuccessStatusCode();
                        opened = true;
                        consecutiveFailures = 0;
                        _IsSseConnected = true;

                        using (Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            SseEventReader events = new SseEventReader(reader, _SseLastEventId);
                            try
                            {
                                SseEvent? sseEvent;
                                while ((sseEvent = await events.ReadEventAsync(token).ConfigureAwait(false)) != null)
                                {
                                    _SseLastEventId = events.LastEventId;
                                    HandleStreamMessage(sseEvent.Data);
                                }
                            }
                            finally
                            {
                                _SseLastEventId = events.LastEventId;
                                if (events.RetryMs.HasValue) _SseRetryMs = events.RetryMs;
                            }
                        }

                        LogMessage("SSE stream closed by the server");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"SSE error: {ex.Message}");
                    }
                    finally
                    {
                        _IsSseConnected = false;
                    }

                    if (!AutoReconnectSse) return;
                    if (!opened && ++consecutiveFailures > _SseMaxReconnectAttempts)
                    {
                        LogMessage($"SSE reconnection abandoned after {consecutiveFailures - 1} attempts");
                        return;
                    }

                    await Task.Delay(_SseRetryMs ?? _SseReconnectDelayMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }

        // Ends the Streamable HTTP session on the server (clients SHOULD send DELETE when they no longer need it).
        // A 405 means the server does not allow clients to end sessions; either way the local session is dropped.
        private void TryDeleteSession()
        {
            if (!_Streamable || _HttpClient == null || String.IsNullOrEmpty(_RpcUrl) || String.IsNullOrEmpty(SessionId)) return;
            string sessionId = SessionId!;
            string url = _RpcUrl!;
            HttpClient client = _HttpClient;
            string version = _ProtocolVersion;
            try
            {
                Task.Run(async () =>
                {
                    using CancellationTokenSource timeout = new CancellationTokenSource(2000);
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, url);
                    request.Headers.Add(McpProtocol.SessionIdHeader, sessionId);
                    request.Headers.Add(McpProtocol.ProtocolVersionHeader, version);
                    using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    LogMessage($"Session {sessionId} ended with DELETE (HTTP {(int)response.StatusCode})");
                }).Wait(2500);
            }
            catch (Exception ex)
            {
                LogMessage($"Could not end session {sessionId}: {ex.Message}");
            }
        }

        private async Task PerformHandshakeAsync(CancellationToken token)
        {
            _HandshakeComplete = false;
            Dictionary<string, object?> parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                { "protocolVersion", _ProtocolVersion },
                { "capabilities", BuildClientCapabilities() },
                { "clientInfo", new Dictionary<string, object?>(StringComparer.Ordinal) { { "name", _ClientName }, { "version", _ClientVersion } } }
            };

            JsonRpcResponse response = await CallAsync("initialize", parameters, _RequestTimeoutMs, token).ConfigureAwait(false);
            if (response.Error != null)
            {
                throw new InvalidOperationException($"initialize failed with RPC error {response.Error.Code}: {response.Error.Message}");
            }

            string? negotiatedVersion = response.Result == null
                ? null
                : new RpcParameters(JsonSerializer.Serialize(response.Result)).GetString("protocolVersion");
            if (!String.IsNullOrEmpty(negotiatedVersion))
            {
                if (!McpProtocol.IsHandshakeVersion(negotiatedVersion!))
                {
                    throw new InvalidOperationException($"The server negotiated MCP protocol version '{negotiatedVersion}', which this client cannot use for a session.");
                }

                _ProtocolVersion = negotiatedVersion!;
            }

            _HandshakeComplete = true;
            await NotifyAsync("notifications/initialized", null, _RequestTimeoutMs, token).ConfigureAwait(false);

            // Check the connection's health periodically (MCP ping utility).
            _Pinger?.Dispose();
            _Pinger = McpPinger.Start(_PingIntervalMs, async ct =>
            {
                try
                {
                    await PingAsync(_PingTimeoutMs, ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    return false;
                }
            }, LogMessage, () => Task.Run(() => Disconnect()), _PingFailureThreshold);
        }

        // Declares the client capabilities whose requests have handlers, so servers know they may send them, limited to
        // those the requested protocol version defines.
        private Dictionary<string, object?> BuildClientCapabilities()
        {
            return McpClientHandshake.CapabilitiesFor(_RequestDispatcher, ClientCapabilities, _ProtocolVersion);
        }

        private Dictionary<string, object?> BuildStatelessParams(IReadOnlyDictionary<string, object?>? parameters)
        {
            Dictionary<string, object?> result = new Dictionary<string, object?>(StringComparer.Ordinal);
            Dictionary<string, object?> callerMeta = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (parameters != null)
            {
                foreach (KeyValuePair<string, object?> entry in parameters)
                {
                    if (entry.Key == "_meta" && entry.Value != null)
                    {
                        JsonElement metaElement = entry.Value is JsonElement json ? json : JsonSerializer.SerializeToElement(entry.Value);
                        if (metaElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty property in metaElement.EnumerateObject()) callerMeta[property.Name] = property.Value.Clone();
                        }

                        continue;
                    }

                    result[entry.Key] = entry.Value;
                }
            }

            Dictionary<string, object?> clientInfo = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                { "name", _ClientName },
                { "version", _ClientVersion }
            };

            Dictionary<string, object?> meta = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                { McpProtocol.MetaProtocolVersionKey, _ProtocolVersion },
                { McpProtocol.MetaClientInfoKey, clientInfo },
                { McpProtocol.MetaClientCapabilitiesKey, BuildClientCapabilities() }
            };

            foreach (KeyValuePair<string, object?> entry in callerMeta)
            {
                if (!meta.ContainsKey(entry.Key)) meta[entry.Key] = entry.Value;
            }

            result["_meta"] = meta;
            return result;
        }

        // The Mcp-Name source for tools/call and prompts/get is params.name; for resources/read it is params.uri.
        private static string? GetRoutingName(string method, IReadOnlyDictionary<string, object?>? parameters)
        {
            if (parameters == null) return null;
            string key = StringComparer.Ordinal.Equals(method, "resources/read") ? "uri" : "name";
            if (!StringComparer.Ordinal.Equals(method, "tools/call") && !StringComparer.Ordinal.Equals(method, "prompts/get") && !StringComparer.Ordinal.Equals(method, "resources/read")) return null;
            if (!parameters.TryGetValue(key, out object? value) || value == null) return null;
            if (value is string text) return text;
            JsonElement element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value);
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        // Mirrors the arguments of x-mcp-header parameters into Mcp-Param-{Name} headers (2026-07-28). A parameter
        // without a value in the arguments gets no header.
        private void AddParamHeaders(HttpRequestMessage httpRequest, string toolName, IReadOnlyDictionary<string, object?>? parameters)
        {
            if (!_ToolHeaderParameters.TryGetValue(toolName, out List<McpHeaderParameter>? headerParameters) || headerParameters.Count == 0) return;
            if (parameters == null || !parameters.TryGetValue("arguments", out object? argumentObject) || argumentObject == null) return;

            JsonElement arguments = argumentObject is JsonElement element ? element : JsonSerializer.SerializeToElement(argumentObject);
            foreach (McpHeaderParameter parameter in headerParameters)
            {
                if (!McpHeaderParameters.TryGetValue(arguments, parameter.Path, out JsonElement value)) continue;
                string? headerValue = McpHeaderParameters.FormatValue(value, parameter.Type);
                if (headerValue != null) httpRequest.Headers.TryAddWithoutValidation(parameter.HeaderName, headerValue);
            }
        }

        // Removes tool definitions with invalid x-mcp-header annotations from a tools/list result, as clients must, and
        // records the header parameters of the valid ones for later tools/call requests.
        private object? FilterToolDefinitions(object? result)
        {
            if (result == null) return null;

            JsonElement root = result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tools", out JsonElement tools) || tools.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            List<JsonElement> kept = new List<JsonElement>();
            bool removedAny = false;
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                string? toolName = tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                JsonElement inputSchema = tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("inputSchema", out JsonElement schemaElement) ? schemaElement : default;

                List<McpHeaderParameter>? headerParameters = McpHeaderParameters.TryExtract(inputSchema, out string? error);
                if (headerParameters == null)
                {
                    LogMessage($"Warning: tool '{toolName}' was removed from tools/list because its x-mcp-header annotations are invalid: {error}");
                    if (toolName != null) _ToolHeaderParameters.TryRemove(toolName, out List<McpHeaderParameter>? _);
                    removedAny = true;
                    continue;
                }

                if (toolName != null) _ToolHeaderParameters[toolName] = headerParameters;
                kept.Add(tool);
            }

            if (!removedAny) return result;

            Dictionary<string, JsonElement> rebuilt = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                rebuilt[property.Name] = property.NameEquals("tools") ? JsonSerializer.SerializeToElement(kept) : property.Value;
            }

            return JsonSerializer.SerializeToElement(rebuilt);
        }

        // Lists every page of tools so the x-mcp-header definitions are current.
        private async Task RefreshToolDefinitionsAsync(CancellationToken token)
        {
            string? cursor = null;
            for (int page = 0; page < 100; page++)
            {
                Dictionary<string, object?>? parameters = cursor == null ? null : new Dictionary<string, object?>(StringComparer.Ordinal) { { "cursor", cursor } };
                JsonRpcResponse listed = await SendStatelessCoreAsync("tools/list", parameters, null, 0, token).ConfigureAwait(false);
                if (listed.Error != null || listed.Result == null) return;

                JsonElement result = listed.Result is JsonElement element ? element : JsonSerializer.SerializeToElement(listed.Result);
                cursor = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("nextCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString()
                    : null;
                if (String.IsNullOrEmpty(cursor)) return;
            }
        }

        private static string? PickSupportedStatelessVersion(object? errorData)
        {
            if (errorData == null) return null;
            JsonElement data = errorData is JsonElement json ? json : JsonSerializer.SerializeToElement(errorData);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("supported", out JsonElement supported) || supported.ValueKind != JsonValueKind.Array) return null;

            foreach (JsonElement candidate in supported.EnumerateArray())
            {
                string? version = candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;
                if (version != null && McpProtocol.IsSupportedVersion(version) && McpProtocol.GetEra(version) == McpProtocolEra.Stateless) return version;
            }

            return null;
        }

        private static string? ReadResultType(object? result)
        {
            if (result == null) return null;
            JsonElement element = result is JsonElement json ? json : JsonSerializer.SerializeToElement(result);
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("resultType", out JsonElement value)) return null;

            // A resultType that is not a string is unrecognized too; its raw JSON is returned so it is rejected.
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }

        private static T? DeserializeResult<T>(object? result)
        {
            if (result == null)
            {
                return default;
            }

            string raw = JsonSerializer.Serialize(result);
            return JsonSerializer.Deserialize<T>(raw);
        }

        private static McpInputRequiredResult? TryParseInputRequired(object? result)
        {
            if (result == null)
            {
                return null;
            }

            string raw = JsonSerializer.Serialize(result);
            ResultTypeProbe? probe = JsonSerializer.Deserialize<ResultTypeProbe>(raw);
            if (probe != null && StringComparer.Ordinal.Equals(probe.ResultType, "input_required"))
            {
                return JsonSerializer.Deserialize<McpInputRequiredResult>(raw);
            }

            return null;
        }

        /// <summary>
        /// Returns the JSON-RPC response body for a request. Streamable HTTP servers may answer a POST with either
        /// <c>application/json</c> or an SSE stream (<c>text/event-stream</c>) that carries notifications, server
        /// requests, and then the response; the specification requires clients to support both. For a stream, events
        /// are read as they arrive: notifications are raised through <see cref="NotificationReceived"/>, server
        /// requests are answered, and reading stops at the response whose id matches <paramref name="requestId"/>.
        /// When the stream ends early after an event ID, it is resumed with GET and <c>Last-Event-ID</c> (handshake
        /// era only). Returns an empty string when no response arrives.
        /// </summary>
        private async Task<string> ReadResponseBodyAsync(HttpResponseMessage httpResponse, object? requestId, CancellationToken token)
        {
            string? mediaType = httpResponse.Content.Headers.ContentType?.MediaType;
            if (!StringComparer.OrdinalIgnoreCase.Equals(mediaType, "text/event-stream"))
            {
                return await httpResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }

            string requestIdJson = JsonSerializer.Serialize(requestId);
            string? lastEventId;
            int? retryMs;
            using (Stream stream = await httpResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                SseEventReader events = new SseEventReader(reader);
                try
                {
                    SseEvent? sseEvent;
                    while ((sseEvent = await events.ReadEventAsync(token).ConfigureAwait(false)) != null)
                    {
                        string? response = HandleStreamedMessage(sseEvent.Data, requestIdJson);
                        if (response != null) return response;
                    }
                }
                catch (IOException ex)
                {
                    LogMessage($"Response stream interrupted: {ex.Message}");
                }
                catch (HttpRequestException ex)
                {
                    LogMessage($"Response stream interrupted: {ex.Message}");
                }

                lastEventId = events.LastEventId;
                retryMs = events.RetryMs;
            }

            // Resumption exists only in the handshake era (2025-03-26 to 2025-11-25); 2026-07-28 has none.
            if (!_Stateless && AutoReconnectSse && !String.IsNullOrEmpty(lastEventId) && !String.IsNullOrEmpty(SessionId) && !String.IsNullOrEmpty(_EventsUrl))
            {
                return await ResumeResponseStreamAsync(lastEventId!, retryMs, requestIdJson, token).ConfigureAwait(false);
            }

            return String.Empty;
        }

        // Reopens an interrupted POST response stream with GET and Last-Event-ID until the response arrives.
        private async Task<string> ResumeResponseStreamAsync(string lastEventId, int? retryMs, string requestIdJson, CancellationToken token)
        {
            for (int attempt = 1; attempt <= Math.Max(1, _SseMaxReconnectAttempts); attempt++)
            {
                await Task.Delay(retryMs ?? _SseReconnectDelayMs, token).ConfigureAwait(false);
                LogMessage($"Resuming response stream after event {lastEventId} (attempt {attempt})");

                try
                {
                    using HttpRequestMessage request = CreateStreamRequest(lastEventId);
                    using HttpResponseMessage response = await _HttpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if ((int)response.StatusCode == 404 && _Streamable)
                    {
                        LogMessage("Response stream resumption found the session gone");
                        throw McpProtocolException.SessionNotFound();
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        LogMessage($"Response stream resumption rejected with HTTP {(int)response.StatusCode}");
                        return String.Empty;
                    }

                    using (Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        SseEventReader events = new SseEventReader(reader, lastEventId);
                        try
                        {
                            SseEvent? sseEvent;
                            while ((sseEvent = await events.ReadEventAsync(token).ConfigureAwait(false)) != null)
                            {
                                string? result = HandleStreamedMessage(sseEvent.Data, requestIdJson);
                                if (result != null) return result;
                            }
                        }
                        finally
                        {
                            lastEventId = events.LastEventId ?? lastEventId;
                            retryMs = events.RetryMs ?? retryMs;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException || ex is HttpRequestException)
                {
                    LogMessage($"Response stream resumption failed: {ex.Message}");
                }
            }

            return String.Empty;
        }

        private string? HandleStreamedMessage(string payload, string requestIdJson)
        {
            if (String.IsNullOrWhiteSpace(payload)) return null;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(payload))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;

                    bool hasId = root.TryGetProperty("id", out JsonElement id) && id.ValueKind != JsonValueKind.Null;
                    bool hasMethod = root.TryGetProperty("method", out JsonElement _);
                    if (hasMethod)
                    {
                        HandleStreamMessage(payload);
                        return null;
                    }

                    if (hasId && StringComparer.Ordinal.Equals(id.GetRawText(), requestIdJson))
                    {
                        return payload;
                    }
                }
            }
            catch (JsonException)
            {
                LogMessage($"Ignoring a malformed SSE event: {payload}");
            }

            return null;
        }

        // Routes a message that arrived on an SSE stream: server requests are answered, notifications are raised.
        // Empty events (such as a priming event that only carries an event ID) are skipped.
        private void HandleStreamMessage(string payload)
        {
            if (String.IsNullOrWhiteSpace(payload)) return;

            JsonRpcRequest? serverRequest = ClientRequestDispatcher.ParseRequest(payload);
            if (serverRequest != null)
            {
                if (_Stateless)
                {
                    LogMessage($"Ignoring a server request on a stateless stream (2026-07-28 servers must use input requests instead): {payload}");
                    return;
                }

                LogMessage($"Received server request: {payload}");
                _ = Task.Run(() => AnswerServerRequestAsync(serverRequest));
                return;
            }

            ProcessNotification(payload);
        }

        // Answers a server request by POSTing the JSON-RPC response to the endpoint on the same session.
        private async Task AnswerServerRequestAsync(JsonRpcRequest request)
        {
            CancellationToken token = _ServerRequestTokenSource.Token;
            try
            {
                JsonRpcResponse? response = await _RequestDispatcher.DispatchAsync(request, token).ConfigureAwait(false);
                if (response == null) return;
                string responseJson = JsonSerializer.Serialize(response);
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(_RequestTimeoutMs);
                    string? sentSessionId = SessionId;
                    using HttpRequestMessage post = CreatePostRequest(responseJson);
                    using HttpResponseMessage reply = await _HttpClient!.SendAsync(post, cts.Token).ConfigureAwait(false);
                    LogMessage($"Answered server request {request.Method} (HTTP {(int)reply.StatusCode}): {responseJson}");
                    if ((int)reply.StatusCode == 404 && _Streamable && !String.IsNullOrEmpty(sentSessionId))
                    {
                        // The answer belonged to a session that no longer exists; start a new one.
                        await RecoverSessionAsync(sentSessionId, token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Could not answer server request {request.Method}: {ex.Message}");
            }
        }

        // A 404 to a request that carried a session means the server ended the session; Streamable HTTP clients must
        // start a new one. Concurrent callers that saw the same lost session share one new session. Returns true when
        // a session is available afterwards.
        private async Task<bool> RecoverSessionAsync(string? lostSessionId, CancellationToken token)
        {
            if (!_Streamable || String.IsNullOrEmpty(lostSessionId)) return false;
            await _RecoverLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!StringComparer.Ordinal.Equals(SessionId, lostSessionId)) return !String.IsNullOrEmpty(SessionId);

                LogMessage($"Session {lostSessionId} was not found; starting a new session");
                SessionId = null;
                _HandshakeComplete = false;
                _SseLastEventId = null;
                await PerformHandshakeAsync(token).ConfigureAwait(false);
                if (_SseWanted) await StartSseAsync(token).ConfigureAwait(false);
                return !String.IsNullOrEmpty(SessionId) || _HandshakeComplete;
            }
            finally
            {
                _RecoverLock.Release();
            }
        }

        private HttpRequestMessage CreateStreamRequest(string? lastEventId)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, _EventsUrl);
            if (!String.IsNullOrEmpty(SessionId)) request.Headers.Add(McpProtocol.SessionIdHeader, SessionId);
            request.Headers.Add(McpProtocol.ProtocolVersionHeader, _ProtocolVersion);
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (!String.IsNullOrEmpty(lastEventId)) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
            return request;
        }

        private HttpRequestMessage CreatePostRequest(string requestJson)
        {
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _RpcUrl);
            httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            httpRequest.Headers.Accept.ParseAdd("application/json");
            httpRequest.Headers.Accept.ParseAdd("text/event-stream");

            if (!String.IsNullOrEmpty(SessionId))
            {
                httpRequest.Headers.Add(McpProtocol.SessionIdHeader, SessionId);
            }

            // After initialize, every request carries the negotiated version, with or without a session; in stateless
            // mode every POST carries the version in use.
            if (!String.IsNullOrEmpty(SessionId) || _HandshakeComplete || _Stateless)
            {
                httpRequest.Headers.Add(McpProtocol.ProtocolVersionHeader, _ProtocolVersion);
            }

            return httpRequest;
        }

        private void CaptureSessionId(HttpResponseMessage httpResponse)
        {
            if (httpResponse.Headers.TryGetValues(McpProtocol.SessionIdHeader, out System.Collections.Generic.IEnumerable<string>? sessionHeaders) ||
                httpResponse.Headers.TryGetValues(McpProtocol.LegacySessionIdHeader, out sessionHeaders))
            {
                foreach (string sessionHeader in sessionHeaders)
                {
                    SessionId = sessionHeader;
                    break;
                }
            }
        }

        private void ProcessNotification(string data)
        {
            try
            {
                LogMessage($"Received notification: {data}");

                JsonRpcRequest? notification = JsonSerializer.Deserialize<JsonRpcRequest>(data);
                if (notification != null)
                {
                    // A cancellation of a request the server sent stops its handler; the notification is still raised.
                    _RequestDispatcher.TryHandleCancellation(notification);

                    // Invoke each handler individually to ensure exception isolation
                    if (NotificationReceived != null)
                    {
                        foreach (Delegate handler in NotificationReceived.GetInvocationList())
                        {
                            try
                            {
                                ((EventHandler<JsonRpcRequest>)handler)(this, notification);
                            }
                            catch (Exception ex)
                            {
                                LogMessage($"Error in NotificationReceived handler: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error processing notification: {ex.Message}");
            }
        }

        private void LogMessage(string message)
        {
            string formattedMessage = $"[{DateTime.UtcNow:HH:mm:ss.fffZ}] {message}";

            // Invoke each handler individually to ensure exception isolation
            if (Log != null)
            {
                foreach (Delegate handler in Log.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<string>)handler)(this, formattedMessage);
                    }
                    catch
                    {
                        // Swallow exceptions in log handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseConnected()
        {
            if (Connected != null && _BaseUrl != null)
            {
                ClientConnectedEventArgs eventArgs = new ClientConnectedEventArgs(_BaseUrl, ClientConnectionTypeEnum.Http);
                foreach (Delegate handler in Connected.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<ClientConnectedEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseDisconnected(string reason)
        {
            if (Disconnected != null && _BaseUrl != null)
            {
                ClientDisconnectedEventArgs eventArgs = new ClientDisconnectedEventArgs(_ConnectedUtc, _BaseUrl, ClientConnectionTypeEnum.Http, reason);
                foreach (Delegate handler in Disconnected.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<ClientDisconnectedEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseRequestSent(RequestSentEventArgs eventArgs)
        {
            if (RequestSent != null)
            {
                foreach (Delegate handler in RequestSent.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<RequestSentEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseResponseReceived(ResponseReceivedEventArgs eventArgs)
        {
            if (ResponseReceived != null)
            {
                foreach (Delegate handler in ResponseReceived.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<ResponseReceivedEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private sealed class ResultTypeProbe
        {
            [JsonPropertyName("resultType")]
            public string? ResultType { get; set; }
        }
    }
}
