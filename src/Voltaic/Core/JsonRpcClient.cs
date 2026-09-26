namespace Voltaic.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides a TCP-based JSON-RPC 2.0 client implementation for making remote procedure calls.
    /// Requests the server sends to the client (messages with both <c>method</c> and <c>id</c>) are always
    /// answered, as JSON-RPC 2.0 requires: by a handler registered with <see cref="RegisterRequestHandler"/>, or
    /// with <c>-32601</c> (method not found). Messages are written one at a time, so concurrent calls and
    /// responses never interleave on the stream.
    /// </summary>
    public class JsonRpcClient : IDisposable
    {
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.

        /// <summary>
        /// Underlying TCP client.
        /// </summary>
        public TcpClient? TcpClient
        {
            get => _TcpClient;
        }

        /// <summary>
        /// Cancellation token source.
        /// </summary>
        public CancellationTokenSource? TokenSource
        {
            get => _TokenSource;
        }

        /// <summary>
        /// Gets a value indicating whether the client is currently connected to a server.
        /// </summary>
        public bool IsConnected => _IsConnected && _TcpClient?.Connected == true;

        /// <summary>
        /// Gets or sets the default Content-Type header value used when sending messages.
        /// This will default to application/json; charset=utf-8 if the supplied value is null.
        /// </summary>
        public string DefaultContentType
        {
            get => _DefaultContentType;
            set => _DefaultContentType = value ?? "application/json; charset=utf-8";
        }

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

        private TcpClient? _TcpClient;
        private NetworkStream? _Stream;
        private readonly ConcurrentDictionary<object, ClientPendingRequest> _PendingRequests;
        private CancellationTokenSource? _TokenSource;
        private Task? _ReceiveTask;
        private int _RequestIdCounter = 0;
        private bool _IsConnected = false;
        private bool _IsDisposed = false;
        private string _DefaultContentType = "application/json; charset=utf-8";
        private string? _Endpoint;
        private DateTime _ConnectedUtc;
        private readonly ClientRequestDispatcher _RequestDispatcher = new ClientRequestDispatcher();
        private readonly SemaphoreSlim _SendLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcClient"/> class.
        /// </summary>
        public JsonRpcClient()
        {
            _PendingRequests = new ConcurrentDictionary<object, ClientPendingRequest>();
        }

        /// <summary>
        /// Asynchronously connects to a JSON-RPC server at the specified host and port.
        /// </summary>
        /// <param name="host">The hostname or IP address of the server.</param>
        /// <param name="port">The port number to connect to.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the connection was successful; otherwise, false.</returns>
        public async Task<bool> ConnectAsync(string host, int port, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            try
            {
                Disconnect();

                _TcpClient = new TcpClient();
                await _TcpClient.ConnectAsync(host, port).ConfigureAwait(false);
                _Stream = _TcpClient.GetStream();

                _TokenSource = new CancellationTokenSource();
                _ReceiveTask = Task.Run(() => ReceiveLoop(_TokenSource.Token));

                _IsConnected = true;
                _Endpoint = $"{host}:{port}";
                _ConnectedUtc = DateTime.UtcNow;
                LogMessage($"Connected to {host}:{port}");
                RaiseConnected();

                if (!await OnConnectedAsync(token).ConfigureAwait(false))
                {
                    Disconnect();
                    return false;
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
                throw new InvalidOperationException("Client is not connected");

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
                        OnCallAbandoned(method, id, token.IsCancellationRequested);
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
                _PendingRequests.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// Asynchronously invokes a remote method and returns the result as an object.
        /// </summary>
        /// <param name="method">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method. Can be null.</param>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Default is 30000 (30 seconds).</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the method result.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client is not connected.</exception>
        /// <exception cref="Exception">Thrown when the remote method returns an error.</exception>
        public async Task<object?> CallAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)
        {
            return await CallAsync<object>(method, parameters, timeoutMs, token).ConfigureAwait(false);
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
                throw new InvalidOperationException("Client is not connected");

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
        /// Registers the handler for requests the server sends to this client with the given method name,
        /// replacing any previous handler for that method. The handler receives the request parameters (null
        /// when the request has none) and a token that is cancelled when the client disconnects; its return value
        /// is sent as the result (null is sent as an empty object). A handler that throws an exception implementing
        /// <see cref="IJsonRpcErrorProvider"/> (such as <c>McpProtocolException</c>) sends that error; any other
        /// exception sends <c>-32603</c> without its message. Requests for methods without a handler are answered
        /// with <c>-32601</c>. Handlers run concurrently with each other and with the receive loop. Thread-safe.
        /// </summary>
        /// <param name="method">The method name, compared case-sensitively. Must not be null, empty, or whitespace.</param>
        /// <param name="handler">The handler. Must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is null, empty, or whitespace, or <paramref name="handler"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="method"/> is <c>ping</c> on an MCP client, which answers <c>ping</c> itself.</exception>
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

                LogMessage("Disconnected");
                RaiseDisconnected("Client disconnected");
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="JsonRpcClient"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="JsonRpcClient"/> and optionally releases the managed resources.
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
                    _Stream?.Dispose();
                    _TcpClient?.Dispose();
                    _SendLock.Dispose();
                }
            }
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            if (NewlineFraming && _Stream != null)
            {
                await ReceiveLinesAsync(_Stream, token).ConfigureAwait(false);
                return;
            }

            byte[] buffer = MessageFraming.CreateBuffer();
            int bufferOffset = 0;
            int bufferCount = 0;

            try
            {
                while (!token.IsCancellationRequested && _Stream != null)
                {
                    // Read a complete message using LSP-style framing
                    (string? message, byte[] newBuffer, int newOffset, int newCount) = await MessageFraming.ReadMessageAsync(
                        _Stream, buffer, bufferOffset, bufferCount, token);

                    // Update buffer reference in case it was resized
                    buffer = newBuffer;

                    if (message == null)
                    {
                        // Check if _Stream was closed (newCount == 0) or just need more data
                        if (newCount == 0 && bufferCount == 0)
                        {
                            LogMessage("Server disconnected");
                            break;
                        }

                        // Update buffer state and continue reading
                        bufferOffset = newOffset;
                        bufferCount = newCount;
                        continue;
                    }

                    // Process the complete message
                    ProcessResponse(message);

                    // Update buffer state for any remaining data
                    bufferOffset = newOffset;
                    bufferCount = newCount;

                    // If there's data remaining in buffer, immediately try to process it
                    // (handles multiple messages in single read)
                    if (bufferCount > 0)
                    {
                        continue;
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

        // Reads newline-delimited messages (the MCP stdio framing).
        private async Task ReceiveLinesAsync(NetworkStream stream, CancellationToken token)
        {
            try
            {
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, leaveOpen: true))
                {
                    while (!token.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                        if (line == null)
                        {
                            LogMessage("Server disconnected");
                            break;
                        }

                        if (!String.IsNullOrWhiteSpace(line)) ProcessResponse(line);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) LogMessage($"Receive error: {ex.Message}");
            }
            finally
            {
                _IsConnected = false;
            }
        }

        // True to send and receive newline-delimited JSON instead of Content-Length framing. Set before connecting.
        private protected bool NewlineFraming { get; set; }

        // The dispatcher that answers server requests, for derived clients that declare capabilities from it.
        private protected ClientRequestDispatcher RequestDispatcher => _RequestDispatcher;

        // Called after the connection opened; return false to disconnect and fail ConnectAsync.
        private protected virtual Task<bool> OnConnectedAsync(CancellationToken token)
        {
            return Task.FromResult(true);
        }

        // Called when a call gave up (timeout or cancellation) before its response arrived.
        private protected virtual void OnCallAbandoned(string method, int id, bool cancelledByCaller)
        {
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

        // Makes an MCP client answer ping itself (MCP requires every party to answer ping) and reserves the name.
        private protected void AnswerPingRequests()
        {
            _RequestDispatcher.AnswersPing = true;
        }

        private async Task SendRequestAsync(JsonRpcRequest request, CancellationToken token = default)
        {
            await SendJsonAsync(JsonSerializer.Serialize(request), token).ConfigureAwait(false);
        }

        private async Task SendJsonAsync(string json, CancellationToken token)
        {
            if (_Stream == null)
                throw new InvalidOperationException("Stream is not initialized");

            await _SendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (NewlineFraming)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
                    await _Stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                    await _Stream.FlushAsync(token).ConfigureAwait(false);
                }
                else
                {
                    await MessageFraming.WriteMessageAsync(_Stream, json, _DefaultContentType, token).ConfigureAwait(false);
                }
            }
            finally
            {
                _SendLock.Release();
            }

            LogMessage($"Sent: {json}");
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
                LogMessage($"Received: {responseString}");

                if (responseString.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    ProcessBatch(responseString);
                    return;
                }

                // A request from the server (method and id) must be answered, never mistaken for a response.
                JsonRpcRequest? serverRequest = ClientRequestDispatcher.ParseRequest(responseString);
                if (serverRequest != null)
                {
                    _ = Task.Run(() => AnswerServerRequestAsync(serverRequest));
                    return;
                }

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
                ClientConnectedEventArgs eventArgs = new ClientConnectedEventArgs(_Endpoint, ClientConnectionTypeEnum.Tcp);
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
                ClientDisconnectedEventArgs eventArgs = new ClientDisconnectedEventArgs(_ConnectedUtc, _Endpoint, ClientConnectionTypeEnum.Tcp, reason);
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

#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
    }
}