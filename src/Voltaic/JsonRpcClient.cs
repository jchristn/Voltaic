namespace Voltaic
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides a TCP-based JSON-RPC 2.0 client implementation for making remote procedure calls.
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
        private string _DefaultContentType = "application/json; charset=utf-8";
        private string? _Endpoint;
        private DateTime _ConnectedUtc;

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
                    JsonRpcResponse response = await tcs.Task.ConfigureAwait(false);

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

                _Stream?.Dispose();
                _TcpClient?.Close();

                LogMessage("Disconnected");
                RaiseDisconnected("Client disconnected");
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="JsonRpcClient"/>.
        /// </summary>
        public void Dispose()
        {
            Disconnect();
            _TokenSource?.Dispose();
            _Stream?.Dispose();
            _TcpClient?.Dispose();
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
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

        private async Task SendRequestAsync(JsonRpcRequest request, CancellationToken token = default)
        {
            if (_Stream == null)
                throw new InvalidOperationException("Stream is not initialized");

            string json = JsonSerializer.Serialize(request);
            await MessageFraming.WriteMessageAsync(_Stream, json, _DefaultContentType, token).ConfigureAwait(false);

            LogMessage($"Sent: {json}");
        }

        private void ProcessResponse(string responseString)
        {
            try
            {
                LogMessage($"Received: {responseString}");

                // Try to parse as response first
                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseString);
                if (response != null && response.Id != null)
                {
                    // Extract the actual integer value from JsonElement if needed
                    object lookupKey = response.Id;
                    if (response.Id is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Number)
                    {
                        lookupKey = jsonElement.GetInt32();
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