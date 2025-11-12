namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.JsonRpc;

    /// <summary>
    /// Provides a WebSocket-based MCP (Model Context Protocol) client implementation.
    /// Supports bidirectional communication with MCP servers over WebSocket connections.
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

        private ClientWebSocket? _WebSocket;
        private readonly ConcurrentDictionary<object, TaskCompletionSource<JsonRpcResponse>> _PendingRequests;
        private CancellationTokenSource? _TokenSource;
        private Task? _ReceiveTask;
        private int _RequestIdCounter = 0;
        private bool _IsConnected = false;
        private int _MaxMessageSize = 1048576; // 1 MB

        /// <summary>
        /// Initializes a new instance of the <see cref="McpWebsocketsClient"/> class.
        /// </summary>
        public McpWebsocketsClient()
        {
            _PendingRequests = new ConcurrentDictionary<object, TaskCompletionSource<JsonRpcResponse>>();
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

                Uri uri = new Uri(url);
                await _WebSocket.ConnectAsync(uri, token).ConfigureAwait(false);

                _TokenSource = new CancellationTokenSource();
                _ReceiveTask = Task.Run(() => ReceiveLoop(_TokenSource.Token));

                _IsConnected = true;
                LogMessage($"Connected to {url}");
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
                throw new InvalidOperationException("WebSocket client is not connected");

            int id = Interlocked.Increment(ref _RequestIdCounter);
            JsonRpcRequest request = new JsonRpcRequest
            {
                Method = method,
                Params = parameters,
                Id = id
            };

            TaskCompletionSource<JsonRpcResponse> tcs = new TaskCompletionSource<JsonRpcResponse>();
            _PendingRequests[id] = tcs;

            try
            {
                await SendRequestAsync(request, token).ConfigureAwait(false);

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
                _PendingRequests.TryRemove(id, out TaskCompletionSource<JsonRpcResponse>? _);
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
                foreach (TaskCompletionSource<JsonRpcResponse> pending in _PendingRequests.Values)
                {
                    pending.TrySetCanceled();
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

                _WebSocket?.Dispose();
                LogMessage("Disconnected");
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="McpWebsocketsClient"/>.
        /// </summary>
        public void Dispose()
        {
            Disconnect();
            _TokenSource?.Dispose();
            _WebSocket?.Dispose();
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] buffer = new byte[_MaxMessageSize];
            StringBuilder messageBuilder = new StringBuilder();

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
                        string chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(chunk);

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
            if (_WebSocket == null || _WebSocket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not open");

            string json = JsonSerializer.Serialize(request);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            await _WebSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                token).ConfigureAwait(false);

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

                    if (_PendingRequests.TryRemove(lookupKey, out TaskCompletionSource<JsonRpcResponse>? tcs))
                    {
                        tcs.SetResult(response);
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
    }
}
