namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Collections.Generic;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// Provides a WebSocket-based MCP (Model Context Protocol) client implementation.
    /// Supports bidirectional communication with MCP servers over WebSocket connections.
    /// Requests the server sends to the client are always answered: <c>ping</c> with an empty result (<c>{}</c>), as
    /// the MCP specification requires of both parties, other methods by handlers registered with
    /// <see cref="RegisterRequestHandler"/>, and anything else with <c>-32601</c> (method not found). Messages are
    /// sent one at a time, as <see cref="ClientWebSocket"/> requires.
    /// </summary>
    public class McpWebsocketsClient : IDisposable
    {
        /// <summary>
        /// Gets or sets the maximum message size in bytes that can be received.
        /// Default is 1 MB (1048576 bytes). Minimum is 4096 bytes.
        /// </summary>
        public int MaxMessageSize
        {
            get => _MaxMessageSize;
            set
            {
                if (value < 4096) throw new ArgumentOutOfRangeException(nameof(value), "Maximum message size must be at least 4096 bytes");
                _MaxMessageSize = value;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the client is currently connected to a server.
        /// </summary>
        public bool IsConnected => _IsConnected && _WebSocket?.State == WebSocketState.Open;

        /// <summary>
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

        /// <summary>
        /// Occurs when a notification (request without an ID) is received from the server.
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

        private ClientWebSocket? _WebSocket;
        private readonly ConcurrentDictionary<object, ClientPendingRequest> _PendingRequests;
        private CancellationTokenSource? _TokenSource;
        private Task? _ReceiveTask;
        private int _RequestIdCounter = 0;
        private bool _IsConnected = false;
        private bool _IsDisposed = false;
        private int _MaxMessageSize = 1048576; // 1 MB
        private string? _Endpoint;
        private DateTime _ConnectedUtc;
        private readonly Dictionary<string, string> _RequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _RequestHeadersLock = new object();
        private string _ProtocolVersion = McpProtocol.LatestProtocolVersion;
        private string _ClientName = "Voltaic.Mcp.WebsocketsClient";
        private string _ClientVersion = "1.0.0";
        private JsonElement? _InitializeResult;
        private readonly ClientRequestDispatcher _RequestDispatcher = new ClientRequestDispatcher { AnswersPing = true };
        private int _PingIntervalMs = 30000;
        private int _PingTimeoutMs = 10000;
        private int _PingFailureThreshold = 1;
        private McpPinger? _Pinger;
        private readonly SemaphoreSlim _SendLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="McpWebsocketsClient"/> class.
        /// </summary>
        public McpWebsocketsClient()
        {
            _PendingRequests = new ConcurrentDictionary<object, ClientPendingRequest>();
            _RequestDispatcher.Log = LogMessage;
        }

        /// <summary>
        /// Sets a header sent on the WebSocket upgrade request, for example <c>Authorization: Bearer &lt;token&gt;</c>
        /// or an API-key header such as <c>X-API-Key</c>, which an authenticated
        /// <see cref="McpWebsocketsServer"/> checks with its <see cref="McpWebsocketsServer.AuthenticationHandler"/>.
        /// Header names are case-insensitive; setting the same name again replaces the value, and a null or empty
        /// <paramref name="value"/> removes the header. Headers apply to the next <see cref="ConnectAsync"/>; an
        /// open connection is not affected. Thread-safe.
        /// </summary>
        /// <param name="name">The header name. Must not be null or empty.</param>
        /// <param name="value">The header value, or null or empty to remove the header.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null or empty.</exception>
        public void SetRequestHeader(string name, string? value)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            lock (_RequestHeadersLock)
            {
                if (String.IsNullOrEmpty(value)) _RequestHeaders.Remove(name);
                else _RequestHeaders[name] = value!;
            }
        }

        /// <summary>
        /// Asynchronously connects to a WebSocket MCP server at the specified URL.
        /// </summary>
        /// <param name="url">The WebSocket URL to connect to (e.g., "ws://localhost:8080/mcp" or "wss://example.com/mcp").</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the connection was successful; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException">Thrown when url is null or empty.</exception>
        public async Task<bool> ConnectAsync(string url, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));

            try
            {
                Disconnect();

                _WebSocket = new ClientWebSocket();
                _WebSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                lock (_RequestHeadersLock)
                {
                    foreach (KeyValuePair<string, string> header in _RequestHeaders)
                    {
                        _WebSocket.Options.SetRequestHeader(header.Key, header.Value);
                    }
                }

                Uri uri = new Uri(url);
                await _WebSocket.ConnectAsync(uri, token).ConfigureAwait(false);

                _TokenSource = new CancellationTokenSource();
                _ReceiveTask = Task.Run(() => ReceiveLoop(_TokenSource.Token));

                _IsConnected = true;
                _Endpoint = url;
                _ConnectedUtc = DateTime.UtcNow;
                LogMessage($"Connected to {url}");
                RaiseConnected();

                if (AutoInitialize)
                {
                    try
                    {
                        await InitializeAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"MCP initialize failed: {ex.Message}");
                        Disconnect();
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Connection failed: {ex.Message}");
                _IsConnected = false;
                return false;
            }
        }

        /// <summary>
        /// Sends the MCP <c>ping</c> request and completes when the server answers with any successful result.
        /// Voltaic 2.x servers and other specification-conformant servers answer with an empty object
        /// (<c>{}</c>); Voltaic 1.x servers answered with the string <c>"pong"</c>. Both are accepted, so use
        /// this method rather than <c>CallAsync&lt;string&gt;("ping")</c> to check connectivity.
        /// </summary>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Default is 30000.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that completes when the server has answered the ping.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client is not connected.</exception>
        /// <exception cref="Exception">Thrown when the server answers the ping with a JSON-RPC error.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled or times out.</exception>
        public async Task PingAsync(int timeoutMs = 30000, CancellationToken token = default)
        {
            await CallAsync<object?>("ping", null, timeoutMs, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously invokes a remote method and returns the result as the specified type.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the result into.</typeparam>
        /// <param name="method">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method. Can be null.</param>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Default is 30000 (30 seconds).</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the method result.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client is not connected.</exception>
        /// <exception cref="Exception">Thrown when the remote method returns an error.</exception>
        public async Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("WebSocket client is not connected");

            int id = Interlocked.Increment(ref _RequestIdCounter);
            JsonRpcRequest request = new JsonRpcRequest
            {
                Method = method,
                Params = parameters,
                Id = id
            };

            TaskCompletionSource<JsonRpcResponse> tcs = new TaskCompletionSource<JsonRpcResponse>();
            ClientPendingRequest pendingRequest = new ClientPendingRequest(id, request, tcs);
            _PendingRequests[id] = pendingRequest;

            try
            {
                await SendRequestAsync(request, token).ConfigureAwait(false);
                RaiseRequestSent(pendingRequest);

                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(timeoutMs);
                    cts.Token.Register(() => tcs.TrySetCanceled());
                    JsonRpcResponse response;
                    try
                    {
                        response = await tcs.Task.ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        SendCancellation(method, id, token.IsCancellationRequested);
                        throw;
                    }

                    if (response.Error != null)
                    {
                        throw new Exception($"RPC Error {response.Error.Code}: {response.Error.Message}");
                    }

                    if (response.Result != null)
                    {
                        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(response.Result))!;
                    }

                    if (response.Result == null)
                    {
                        return default(T)!;
                    }

                    return (T)Convert.ChangeType(response.Result, typeof(T));
                }
            }
            finally
            {
                _PendingRequests.TryRemove(id, out ClientPendingRequest? _);
            }
        }

        /// <summary>
        /// Asynchronously sends a notification to the server.
        /// Notifications are fire-and-forget and do not expect a response.
        /// </summary>
        /// <param name="method">The name of the notification method.</param>
        /// <param name="parameters">The parameters to pass with the notification. Can be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client is not connected.</exception>
        public async Task NotifyAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("WebSocket client is not connected");

            JsonRpcRequest notification = new JsonRpcRequest
            {
                Method = method,
                Params = parameters
                // No Id for notifications
            };

            await SendRequestAsync(notification, token).ConfigureAwait(false);
            RaiseRequestSent(new RequestSentEventArgs(notification));
        }

        /// <summary>
        /// Gets or sets the MCP protocol version requested in <c>initialize</c>; after the handshake it holds the version
        /// the server negotiated. Must be a handshake-era revision (<c>2024-11-05</c> through <c>2025-11-25</c>), since a
        /// client must request a version it can negotiate. Default is <see cref="McpProtocol.LatestProtocolVersion"/>.
        /// Setting null or whitespace restores the default.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the value is not a handshake-era protocol version.</exception>
        public string ProtocolVersion
        {
            get => _ProtocolVersion;
            set
            {
                string version = String.IsNullOrWhiteSpace(value) ? McpProtocol.LatestProtocolVersion : value;
                if (!McpProtocol.IsHandshakeVersion(version))
                {
                    throw new ArgumentException($"'{version}' is not a handshake-era MCP protocol version that initialize can negotiate.", nameof(value));
                }

                _ProtocolVersion = version;
            }
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
        /// Gets or sets the client name reported in <c>initialize</c>. Default is <c>Voltaic.Mcp.WebsocketsClient</c>.
        /// </summary>
        public string ClientName
        {
            get => _ClientName;
            set => _ClientName = String.IsNullOrWhiteSpace(value) ? "Voltaic.Mcp.WebsocketsClient" : value;
        }

        /// <summary>
        /// Gets or sets the client version reported in <c>initialize</c>. Default is <c>1.0.0</c>.
        /// </summary>
        public string ClientVersion
        {
            get => _ClientVersion;
            set => _ClientVersion = String.IsNullOrWhiteSpace(value) ? "1.0.0" : value;
        }

        /// <summary>
        /// Gets or sets whether connecting performs the MCP <c>initialize</c> handshake automatically, as the
        /// specification requires before any other request. Default is true. Set false to send <c>initialize</c>
        /// yourself (for example with <see cref="InitializeAsync"/>).
        /// </summary>
        public bool AutoInitialize { get; set; } = true;

        /// <summary>
        /// Gets additional client capabilities to declare in <c>initialize</c>, merged with the capabilities implied by
        /// registered request handlers (<c>roots</c>, <c>sampling</c>, <c>elicitation</c>). Never null. Not thread-safe;
        /// change it before connecting.
        /// </summary>
        public Dictionary<string, object?> ClientCapabilities { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the server's <c>initialize</c> result (protocol version, capabilities, server info, instructions), or
        /// null before the handshake completed.
        /// </summary>
        public JsonElement? InitializeResult => _InitializeResult;

        /// <summary>
        /// Performs the MCP <c>initialize</c> handshake: sends <c>initialize</c> with <see cref="ProtocolVersion"/>,
        /// <see cref="ClientName"/>, <see cref="ClientVersion"/>, and the client capabilities, stores the negotiated
        /// version in <see cref="ProtocolVersion"/>, and sends <c>notifications/initialized</c>. Connecting calls this
        /// automatically unless <see cref="AutoInitialize"/> is false.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the handshake is done.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client is not connected, or the server rejects initialize or chooses a version this client cannot use.</exception>
        public async Task InitializeAsync(CancellationToken token = default)
        {
            McpInitializeOutcome outcome = await McpClientHandshake.RunAsync(
                async (parameters, ct) => McpClientHandshake.ToElement(await CallAsync<object?>("initialize", parameters, 30000, ct).ConfigureAwait(false)),
                ct => NotifyAsync("notifications/initialized", null, ct),
                _ProtocolVersion,
                _ClientName,
                _ClientVersion,
                McpClientHandshake.CapabilitiesFor(_RequestDispatcher, ClientCapabilities, _ProtocolVersion),
                token).ConfigureAwait(false);

            _ProtocolVersion = outcome.ProtocolVersion;
            _InitializeResult = outcome.Result;

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

        /// <summary>
        /// Registers the handler for requests the server sends to this client with the given method name (for example
        /// <c>roots/list</c>, <c>sampling/createMessage</c>, or <c>elicitation/create</c>), replacing any previous
        /// handler for that method. The handler receives the request parameters (null when the request has none) and
        /// a token that is cancelled when the client disconnects; its return value is sent as the result (null is sent
        /// as an empty object). A handler that throws <see cref="McpProtocolException"/> sends that error; any other
        /// exception sends <c>-32603</c> without its message. Requests for methods without a handler are answered with
        /// <c>-32601</c>. <c>ping</c> is always answered by the client itself. Handlers run concurrently with each
        /// other and with the receive loop. Thread-safe.
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
        /// Disconnects from the server and cancels all pending requests.
        /// </summary>
        public void Disconnect()
        {
            _Pinger?.Dispose();
            _Pinger = null;
            if (_IsConnected)
            {
                _IsConnected = false;
                _TokenSource?.Cancel();

                // Clear pending requests
                foreach (ClientPendingRequest pending in _PendingRequests.Values)
                {
                    pending.TaskCompletionSource.TrySetCanceled();
                }
                _PendingRequests.Clear();

                if (_WebSocket?.State == WebSocketState.Open)
                {
                    try
                    {
                        _WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None)
                            .GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Ignore close errors
                    }
                }

                LogMessage("Disconnected");
                RaiseDisconnected("Client disconnected");
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="McpWebsocketsClient"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="McpWebsocketsClient"/> and optionally releases the managed resources.
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
                    _TokenSource?.Dispose();
                    _WebSocket?.Dispose();
                    _SendLock.Dispose();
                }
            }
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] buffer = new byte[_MaxMessageSize];
            StringBuilder messageBuilder = new StringBuilder();
            // Decodes across frames, so a multi-byte UTF-8 character split between reads is preserved.
            Decoder decoder = new UTF8Encoding(false).GetDecoder();

            try
            {
                while (!token.IsCancellationRequested && _WebSocket != null)
                {
                    WebSocketReceiveResult result = await _WebSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        LogMessage("Server closed connection");
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        char[] chars = new char[decoder.GetCharCount(buffer, 0, result.Count, result.EndOfMessage)];
                        int decoded = decoder.GetChars(buffer, 0, result.Count, chars, 0, result.EndOfMessage);
                        messageBuilder.Append(chars, 0, decoded);

                        if (result.EndOfMessage)
                        {
                            string message = messageBuilder.ToString();
                            messageBuilder.Clear();

                            ProcessResponse(message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    LogMessage($"Receive error: {ex.Message}");
                }
            }
            finally
            {
                _IsConnected = false;
            }
        }

        private async Task SendRequestAsync(JsonRpcRequest request, CancellationToken token = default)
        {
            await SendJsonAsync(JsonSerializer.Serialize(request), token).ConfigureAwait(false);
        }

        private async Task SendJsonAsync(string json, CancellationToken token)
        {
            if (_WebSocket == null || _WebSocket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not open");

            byte[] buffer = Encoding.UTF8.GetBytes(json);

            // ClientWebSocket allows only one send at a time.
            await _SendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _WebSocket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    token).ConfigureAwait(false);
            }
            finally
            {
                _SendLock.Release();
            }

            LogMessage($"Sent: {json}");
        }

        // A JSON-RPC batch from the server: responses and notifications are processed one by one, and the requests are
        // answered together in one array.
        private void ProcessBatch(string batchJson)
        {
            List<JsonRpcRequest> requests = new List<JsonRpcRequest>();
            try
            {
                using (JsonDocument document = JsonDocument.Parse(batchJson))
                {
                    foreach (JsonElement element in document.RootElement.EnumerateArray())
                    {
                        string raw = element.GetRawText();
                        JsonRpcRequest? request = ClientRequestDispatcher.ParseRequest(raw);
                        if (request != null) requests.Add(request);
                        else ProcessResponse(raw);
                    }
                }
            }
            catch (JsonException ex)
            {
                LogMessage($"Ignoring a malformed batch: {ex.Message}");
                return;
            }

            if (requests.Count == 0) return;
            _ = Task.Run(async () =>
            {
                CancellationToken token = _TokenSource?.Token ?? CancellationToken.None;
                try
                {
                    JsonRpcResponse?[] answered = await Task.WhenAll(requests.Select(request => _RequestDispatcher.DispatchAsync(request, token))).ConfigureAwait(false);
                    // Requests the server cancelled get no response; a batch with nothing left is not answered.
                    List<JsonRpcResponse> responses = answered.Where(response => response != null).Select(response => response!).ToList();
                    if (responses.Count > 0) await SendJsonAsync(JsonSerializer.Serialize(responses), token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogMessage($"Could not answer a batch of server requests: {ex.Message}");
                }
            });
        }

        // Tells the server to stop working on a request the client gave up on (timeout or cancellation), as MCP asks;
        // initialize is never cancelled.
        private void SendCancellation(string method, int id, bool cancelledByCaller)
        {
            if (method == "initialize") return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await NotifyAsync("notifications/cancelled", new McpCancelledNotification
                    {
                        RequestId = id,
                        Reason = cancelledByCaller ? "The request was cancelled by the client." : "The request timed out."
                    }).ConfigureAwait(false);
                }
                catch
                {
                    // The connection may be closed.
                }
            });
        }

        private async Task AnswerServerRequestAsync(JsonRpcRequest request)
        {
            CancellationToken token = _TokenSource?.Token ?? CancellationToken.None;
            try
            {
                JsonRpcResponse? response = await _RequestDispatcher.DispatchAsync(request, token).ConfigureAwait(false);
                if (response == null) return;
                await SendJsonAsync(JsonSerializer.Serialize(response), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMessage($"Could not answer server request {request.Method}: {ex.Message}");
            }
        }

        private void ProcessResponse(string responseString)
        {
            try
            {
                if (responseString.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    ProcessBatch(responseString);
                    return;
                }

                // A request from the server (method and id) must be answered, never mistaken for a response.
                JsonRpcRequest? serverRequest = ClientRequestDispatcher.ParseRequest(responseString);
                if (serverRequest != null)
                {
                    LogMessage($"Received: {responseString}");
                    _ = Task.Run(() => AnswerServerRequestAsync(serverRequest));
                    return;
                }

                LogMessage($"Received: {responseString}");

                // Try to parse as response first
                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseString);
                if (response != null && response.Id != null)
                {
                    // Extract the numeric id value when the response id is a JSON number.
                    object lookupKey = response.Id;
                    if (Int32.TryParse(JsonSerializer.Serialize(response.Id), out int numericId))
                    {
                        lookupKey = numericId;
                    }

                    if (_PendingRequests.TryRemove(lookupKey, out ClientPendingRequest? pendingRequest))
                    {
                        RaiseResponseReceived(pendingRequest, response);
                        pendingRequest.TaskCompletionSource.SetResult(response);
                    }
                }
                else
                {
                    // Try to parse as notification (request without ID)
                    JsonRpcRequest? notification = JsonSerializer.Deserialize<JsonRpcRequest>(responseString);
                    if (notification != null && notification.Id == null)
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
            }
            catch (Exception ex)
            {
                LogMessage($"Error processing response: {ex.Message}");
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
            if (Connected != null && _Endpoint != null)
            {
                ClientConnectedEventArgs eventArgs = new ClientConnectedEventArgs(_Endpoint, ClientConnectionTypeEnum.Websockets);
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
            if (Disconnected != null && _Endpoint != null)
            {
                ClientDisconnectedEventArgs eventArgs = new ClientDisconnectedEventArgs(_ConnectedUtc, _Endpoint, ClientConnectionTypeEnum.Websockets, reason);
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

        private void RaiseRequestSent(ClientPendingRequest pendingRequest)
        {
            if (RequestSent != null)
            {
                RequestSentEventArgs eventArgs = new RequestSentEventArgs(pendingRequest);
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

        private void RaiseResponseReceived(ClientPendingRequest pendingRequest, JsonRpcResponse response)
        {
            if (ResponseReceived != null)
            {
                ResponseReceivedEventArgs eventArgs = new ResponseReceivedEventArgs(pendingRequest, response);
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
    }
}
