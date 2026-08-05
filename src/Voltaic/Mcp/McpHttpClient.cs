namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides an HTTP-based MCP (Model Context Protocol) client implementation.
    /// Supports JSON-RPC 2.0 over HTTP with Server-Sent Events (SSE) for server-to-client notifications.
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
        /// Gets or sets the MCP protocol version header sent after a session is established.
        /// Default is <see cref="McpProtocol.LatestProtocolVersion"/>.
        /// </summary>
        public string ProtocolVersion
        {
            get => _ProtocolVersion;
            set => _ProtocolVersion = String.IsNullOrWhiteSpace(value) ? McpProtocol.LatestProtocolVersion : value;
        }

        /// <summary>
        /// Gets or sets the client name reported in the <c>_meta</c> client info of stateless
        /// (2026-07-28) requests. Default is <c>Voltaic.Mcp.HttpClient</c>.
        /// </summary>
        public string ClientName
        {
            get => _ClientName;
            set => _ClientName = String.IsNullOrWhiteSpace(value) ? "Voltaic.Mcp.HttpClient" : value;
        }

        /// <summary>
        /// Gets or sets the client version reported in the <c>_meta</c> client info of stateless
        /// (2026-07-28) requests. Default is <c>1.0.0</c>.
        /// </summary>
        public string ClientVersion
        {
            get => _ClientVersion;
            set => _ClientVersion = String.IsNullOrWhiteSpace(value) ? "1.0.0" : value;
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
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

        /// <summary>
        /// Occurs when a notification (request without an ID) is received from the server via SSE.
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

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHttpClient"/> class.
        /// </summary>
        public McpHttpClient()
        {
            _HttpClient = new HttpClient();
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
        /// Asynchronously connects to an HTTP MCP server at the specified base URL.
        /// This method will make an initial request to establish a session.
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
                // Make initial ping request to establish session
                await CallAsync<string>("ping", null, _RequestTimeoutMs, token).ConfigureAwait(false);

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
                // Make initial ping request to establish session
                await CallAsync<string>("ping", null, _RequestTimeoutMs, token).ConfigureAwait(false);

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
            if (_IsSseConnected)
            {
                _IsSseConnected = false;
                _SseTokenSource?.Cancel();
                LogMessage("SSE connection stopped");
            }
        }

        /// <summary>
        /// Asynchronously invokes a remote method and returns the result as the specified type.
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
            if (_HttpClient == null || String.IsNullOrEmpty(_RpcUrl))
                throw new InvalidOperationException("Client not initialized. Call ConnectAsync first.");

            if (timeoutMs == 0) timeoutMs = _RequestTimeoutMs;

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

            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(timeoutMs);

                HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _RpcUrl);
                httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                if (!String.IsNullOrEmpty(SessionId))
                {
                    httpRequest.Headers.Add(McpProtocol.SessionIdHeader, SessionId);
                    httpRequest.Headers.Add(McpProtocol.ProtocolVersionHeader, _ProtocolVersion);
                }

                httpRequest.Headers.Accept.ParseAdd("application/json");
                httpRequest.Headers.Accept.ParseAdd("text/event-stream");

                HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
                httpResponse.EnsureSuccessStatusCode();

                // Extract session ID from response
                if (httpResponse.Headers.TryGetValues(McpProtocol.SessionIdHeader, out System.Collections.Generic.IEnumerable<string>? sessionHeaders) ||
                    httpResponse.Headers.TryGetValues(McpProtocol.LegacySessionIdHeader, out sessionHeaders))
                {
                    foreach (string sessionHeader in sessionHeaders)
                    {
                        SessionId = sessionHeader;
                        break;
                    }
                }

                string responseJson = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                LogMessage($"Received response: {responseJson}");

                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);

                if (response != null)
                {
                    RaiseResponseReceived(new ResponseReceivedEventArgs(request, response, sentUtc));
                }

                response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);
                if (response == null)
                {
                    throw new Exception("Invalid response from server");
                }

                if (response.Error != null)
                {
                    throw new Exception($"RPC Error {response.Error.Code}: {response.Error.Message}");
                }

                if (response.Result is JsonElement jsonElement)
                {
                    return JsonSerializer.Deserialize<T>(jsonElement.GetRawText())!;
                }

                if (response.Result == null)
                {
                    return default(T)!;
                }

                return (T)Convert.ChangeType(response.Result, typeof(T));
            }
        }

        /// <summary>
        /// Asynchronously invokes a remote method and returns the raw JSON-RPC response.
        /// </summary>
        /// <param name="method">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method. Can be null.</param>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Default is the value of RequestTimeoutMs property.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the JSON-RPC response.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized.</exception>
        /// <exception cref="Exception">Thrown when the HTTP response body cannot be parsed as JSON-RPC.</exception>
        public async Task<JsonRpcResponse> CallAsync(string method, object? parameters = null, int timeoutMs = 0, CancellationToken token = default)
        {
            if (_HttpClient == null || String.IsNullOrEmpty(_RpcUrl))
                throw new InvalidOperationException("Client not initialized. Call ConnectAsync first.");

            if (timeoutMs == 0) timeoutMs = _RequestTimeoutMs;

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

            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(timeoutMs);

                using HttpRequestMessage httpRequest = CreatePostRequest(requestJson);
                HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
                httpResponse.EnsureSuccessStatusCode();
                CaptureSessionId(httpResponse);

                string responseJson = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                LogMessage($"Received response: {responseJson}");

                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);
                if (response == null)
                {
                    throw new Exception("Invalid response from server");
                }

                RaiseResponseReceived(new ResponseReceivedEventArgs(request, response, sentUtc));
                return response;
            }
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
                _ProtocolVersion = String.IsNullOrWhiteSpace(protocolVersion) ? McpProtocol.NewestProtocolVersion : protocolVersion!;

                McpDiscoverResult discover = await DiscoverAsync(token).ConfigureAwait(false);

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
        /// the <c>MCP-Protocol-Version</c>, <c>Mcp-Method</c>, and (when supplied) <c>Mcp-Name</c>
        /// routing headers. The raw response is returned, including error responses.
        /// </summary>
        /// <param name="method">The JSON-RPC method. Must not be null or empty.</param>
        /// <param name="parameters">The request parameters as a field map, or null.</param>
        /// <param name="name">The routing name for the <c>Mcp-Name</c> header (the tool name or resource URI), or null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The JSON-RPC response.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized.</exception>
        public async Task<JsonRpcResponse> SendStatelessAsync(string method, IReadOnlyDictionary<string, object?>? parameters, string? name, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(method)) throw new ArgumentNullException(nameof(method));
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
                cts.CancelAfter(_RequestTimeoutMs);

                using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _RpcUrl);
                httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                httpRequest.Headers.Accept.ParseAdd("application/json");
                httpRequest.Headers.Accept.ParseAdd("text/event-stream");
                httpRequest.Headers.Add(McpProtocol.ProtocolVersionHeader, _ProtocolVersion);
                httpRequest.Headers.Add(McpProtocol.MethodHeader, method);
                if (!String.IsNullOrEmpty(name))
                {
                    httpRequest.Headers.Add(McpProtocol.NameHeader, EncodeMcpNameHeader(name!));
                }

                HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
                string responseJson = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                LogMessage($"Received stateless response ({(int)httpResponse.StatusCode}): {responseJson}");

                if (String.IsNullOrEmpty(responseJson))
                {
                    throw new McpProtocolException(-32603, $"Empty stateless response (HTTP {(int)httpResponse.StatusCode}).");
                }

                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);
                if (response == null)
                {
                    throw new McpProtocolException(-32603, "Invalid stateless response from server.");
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
        /// arrives or <paramref name="maxInputRounds"/> is exhausted.
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
                Params = parameters
            };

            string requestJson = JsonSerializer.Serialize(request);
            LogMessage($"Sending notification: {requestJson}");
            RaiseRequestSent(new RequestSentEventArgs(request));

            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(timeoutMs);

                using HttpRequestMessage httpRequest = CreatePostRequest(requestJson);
                HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
                httpResponse.EnsureSuccessStatusCode();
                CaptureSessionId(httpResponse);
            }
        }

        /// <summary>
        /// Disconnects from the server and stops the SSE connection if active.
        /// </summary>
        public void Disconnect()
        {
            if (!String.IsNullOrEmpty(SessionId))
            {
                StopSse();
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
                    _HttpClient?.Dispose();
                }
            }
        }

        private async Task SseLoop(CancellationToken token)
        {
            try
            {
                if (String.IsNullOrEmpty(_EventsUrl) || String.IsNullOrEmpty(SessionId))
                {
                    LogMessage("Cannot start SSE: missing URL or session ID");
                    return;
                }

                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, _EventsUrl);
                request.Headers.Add(McpProtocol.SessionIdHeader, SessionId);
                request.Headers.Add(McpProtocol.ProtocolVersionHeader, _ProtocolVersion);
                request.Headers.Add("Accept", "text/event-stream");

                HttpResponseMessage response = await _HttpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                _IsSseConnected = true;

                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string? line;
                    StringBuilder dataBuilder = new StringBuilder();

                    while (!token.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (line.StartsWith("data: "))
                        {
                            dataBuilder.Append(line.Substring(6));
                        }
                        else if (String.IsNullOrEmpty(line) && dataBuilder.Length > 0)
                        {
                            // End of message
                            string data = dataBuilder.ToString();
                            dataBuilder.Clear();

                            ProcessNotification(data);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    LogMessage($"SSE error: {ex.Message}");
                }
            }
            finally
            {
                _IsSseConnected = false;
            }
        }

        private Dictionary<string, object?> BuildStatelessParams(IReadOnlyDictionary<string, object?>? parameters)
        {
            Dictionary<string, object?> result = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (parameters != null)
            {
                foreach (KeyValuePair<string, object?> entry in parameters)
                {
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
                { McpProtocol.MetaClientCapabilitiesKey, new Dictionary<string, object?>(StringComparer.Ordinal) }
            };

            result["_meta"] = meta;
            return result;
        }

        private static string EncodeMcpNameHeader(string value)
        {
            bool safe = value.Length > 0;
            foreach (char character in value)
            {
                if (character < 0x20 || character > 0x7E)
                {
                    safe = false;
                    break;
                }
            }

            if (safe && !value.StartsWith("=?base64?", StringComparison.Ordinal))
            {
                return value;
            }

            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return $"=?base64?{encoded}?=";
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

        private HttpRequestMessage CreatePostRequest(string requestJson)
        {
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _RpcUrl);
            httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            httpRequest.Headers.Accept.ParseAdd("application/json");
            httpRequest.Headers.Accept.ParseAdd("text/event-stream");

            if (!String.IsNullOrEmpty(SessionId))
            {
                httpRequest.Headers.Add(McpProtocol.SessionIdHeader, SessionId);
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
