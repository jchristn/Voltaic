namespace Voltaic
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic;

    /// <summary>
    /// Provides a WebSocket-based MCP (Model Context Protocol) server implementation.
    /// Supports bidirectional communication over WebSocket connections with full JSON-RPC 2.0 support.
    /// </summary>
    public class McpWebsocketsServer : IDisposable
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
        /// Gets or sets the WebSocket keep-alive interval in seconds.
        /// Default is 30 seconds. Set to 0 to disable keep-alive.
        /// </summary>
        public int KeepAliveIntervalSeconds
        {
            get => _KeepAliveIntervalSeconds;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Keep-alive interval cannot be negative");
                _KeepAliveIntervalSeconds = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of notifications that can be queued per client connection.
        /// When the limit is reached, oldest notifications are discarded.
        /// Default is 100 notifications. Minimum is 1.
        /// This value is applied to new client connections when they are established.
        /// </summary>
        public int MaxQueueSize
        {
            get => _MaxQueueSize;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Max queue size must be at least 1");
                _MaxQueueSize = value;
            }
        }

        /// <summary>
        /// Gets the cancellation token source for the server.
        /// </summary>
        public CancellationTokenSource? TokenSource
        {
            get => _TokenSource;
        }

        /// <summary>
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

        /// <summary>
        /// Occurs when a client connects to the server.
        /// </summary>
        public event EventHandler<ClientConnection>? ClientConnected;

        /// <summary>
        /// Occurs when a client disconnects from the server.
        /// </summary>
        public event EventHandler<ClientConnection>? ClientDisconnected;

        /// <summary>
        /// Occurs when a JSON-RPC request is received from a client.
        /// </summary>
        public event EventHandler<JsonRpcRequestEventArgs>? RequestReceived;

        /// <summary>
        /// Occurs when a JSON-RPC response is sent to a client.
        /// </summary>
        public event EventHandler<JsonRpcResponseEventArgs>? ResponseSent;

        private readonly string _Hostname;
        private readonly int _Port;
        private readonly string _Path;
        private HttpListener? _Listener;
        private CancellationTokenSource? _TokenSource;
        private readonly ConcurrentDictionary<string, ClientConnection> _Clients;
        private readonly Dictionary<string, Func<JsonElement?, object>> _Methods;
        private int _ClientIdCounter = 0;
        private int _MaxMessageSize = 1048576; // 1 MB
        private int _KeepAliveIntervalSeconds = 30;
        private int _MaxQueueSize = 100;
        private volatile bool _IsStopping = false;
        private string _ProtocolVersion = "2025-03-26";
        private string _ServerName = "Voltaic.Mcp.WebSocketsServer";
        private string _ServerVersion = "1.0.0";

        /// <summary>
        /// Gets or sets the MCP protocol version.
        /// Default is "2025-03-26".
        /// </summary>
        public string ProtocolVersion
        {
            get => _ProtocolVersion;
            set => _ProtocolVersion = value ?? "2025-03-26";
        }

        /// <summary>
        /// Gets or sets the server name for MCP serverInfo.
        /// Default is "Voltaic.Mcp.WebSocketsServer".
        /// </summary>
        public string ServerName
        {
            get => _ServerName;
            set => _ServerName = value ?? "Voltaic.Mcp.WebSocketsServer";
        }

        /// <summary>
        /// Gets or sets the server version for MCP serverInfo.
        /// Default is "1.0.0".
        /// </summary>
        public string ServerVersion
        {
            get => _ServerVersion;
            set => _ServerVersion = value ?? "1.0.0";
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpWebsocketsServer"/> class.
        /// </summary>
        /// <param name="hostname">The hostname to listen on.  Use * for any hostname (requires admin or root privileges).</param>
        /// <param name="port">The port number to listen on. Must be between 0 and 65535.</param>
        /// <param name="path">The URL path for WebSocket connections. Default is "/mcp".</param>
        /// <param name="includeDefaultMethods">True to include default MCP methods such as echo, ping, getTime, and getClients.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the port is invalid.</exception>
        public McpWebsocketsServer(string hostname, int port, string path = "/mcp", bool includeDefaultMethods = true)
        {
            if (String.IsNullOrEmpty(hostname)) throw new ArgumentNullException(nameof(hostname));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Hostname = hostname;
            _Port = port;
            _Path = String.IsNullOrEmpty(path) ? "/mcp" : path;
            _Clients = new ConcurrentDictionary<string, ClientConnection>();
            _Methods = new Dictionary<string, Func<JsonElement?, object>>();

            if (includeDefaultMethods) RegisterBuiltInMethods();
        }

        /// <summary>
        /// Registers a custom RPC method with the specified handler.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The function that handles the method invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<JsonElement?, object> handler)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Methods[name] = handler;
        }

        /// <summary>
        /// Starts the WebSocket server and begins listening for client connections asynchronously.
        /// This method will continue running until Stop() is called or the cancellation token is triggered.
        /// </summary>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the server is already running.</exception>
        public async Task StartAsync(CancellationToken token = default)
        {
            try
            {
                _Listener = new HttpListener();
                _Listener.Prefixes.Add($"http://{_Hostname}:{_Port}{_Path}/");
                _Listener.Start();
                _TokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);

                LogMessage($"WebSocket server started on port {_Port} at path {_Path}");

                while (!_TokenSource.Token.IsCancellationRequested)
                {
                    HttpListenerContext? context = await AcceptContextAsync(_TokenSource.Token).ConfigureAwait(false);
                    if (context != null)
                    {
                        _ = Task.Run(() => HandleClientAsync(context, _TokenSource.Token));
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Asynchronously broadcasts a notification to all connected clients.
        /// Notifications are fire-and-forget JSON-RPC requests without an ID.
        /// </summary>
        /// <param name="method">The name of the notification method.</param>
        /// <param name="parameters">The parameters to pass with the notification. Can be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public async Task BroadcastNotificationAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            JsonRpcRequest notification = new JsonRpcRequest
            {
                Method = method,
                Params = parameters
            };

            string json = JsonSerializer.Serialize(notification);

            List<Task> tasks = new List<Task>();
            foreach (ClientConnection client in _Clients.Values)
            {
                tasks.Add(SendToClientAsync(client, json, token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Kicks a client by disconnecting them from the server.
        /// </summary>
        /// <param name="clientId">The ID of the client to kick.</param>
        /// <returns>True if the client was found and kicked; otherwise, false.</returns>
        public bool KickClient(string clientId)
        {
            if (_Clients.TryRemove(clientId, out ClientConnection? client))
            {
                client.Dispose();
                LogMessage($"Kicked client: {clientId}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets a list of all currently connected client IDs.
        /// </summary>
        /// <returns>A list of client IDs.</returns>
        public List<string> GetConnectedClients()
        {
            return _Clients.Keys.ToList();
        }

        /// <summary>
        /// Stops the server and disconnects all clients gracefully.
        /// </summary>
        public void Stop()
        {
            if (_IsStopping) return;
            _IsStopping = true;

            _TokenSource?.Cancel();

            foreach (ClientConnection client in _Clients.Values)
            {
                client.Dispose();
            }
            _Clients.Clear();

            try
            {
                if (_Listener != null && _Listener.IsListening)
                {
                    _Listener.Stop();
                }
            }
            catch
            {
                // Ignore errors during stop
            }

            LogMessage("Server stopped");
        }

        /// <summary>
        /// Releases all resources used by the <see cref="McpWebsocketsServer"/>.
        /// </summary>
        public void Dispose()
        {
            Stop();
            _TokenSource?.Dispose();
            _Listener?.Close();
        }

        /// <summary>
        /// Registers the built-in MCP methods: initialize, ping, echo, getTime, and getClients.
        /// This method is virtual to allow derived classes to customize the set of built-in methods.
        /// </summary>
        protected virtual void RegisterBuiltInMethods()
        {
            // MCP Protocol Methods
            RegisterMethod("initialize", (args) =>
            {
                // Read the client's requested protocol version from params
                string ClientProtocolVersion = _ProtocolVersion;
                if (args.HasValue && args.Value.TryGetProperty("protocolVersion", out JsonElement protocolVersionProp))
                {
                    ClientProtocolVersion = protocolVersionProp.GetString() ?? _ProtocolVersion;
                }

                // The initialize method returns server capabilities and info
                return new
                {
                    protocolVersion = ClientProtocolVersion,
                    capabilities = new
                    {
                        tools = new
                        {
                            listChanged = true
                        }
                    },
                    serverInfo = new
                    {
                        name = _ServerName,
                        version = _ServerVersion
                    }
                };
            });

            RegisterMethod("tools/call", (args) =>
            {
                // MCP tools/call handler - invokes a tool by name with arguments
                if (!args.HasValue)
                {
                    throw new ArgumentException("tools/call requires params with 'name' and 'arguments'");
                }

                // Extract tool name from params.name
                if (!args.Value.TryGetProperty("name", out JsonElement nameElement))
                {
                    throw new ArgumentException("tools/call requires 'name' parameter");
                }

                string toolName = nameElement.GetString() ?? throw new ArgumentException("Tool name cannot be null");

                // Extract arguments from params.arguments
                JsonElement? toolArguments = null;
                if (args.Value.TryGetProperty("arguments", out JsonElement argsElement))
                {
                    toolArguments = argsElement;
                }

                // Look up and invoke the tool
                if (!_Methods.ContainsKey(toolName))
                {
                    throw new ArgumentException($"Tool '{toolName}' not found");
                }

                object result = _Methods[toolName](toolArguments);

                // Return result in MCP format with content array
                return new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = result.ToString()
                        }
                    }
                };
            });

            RegisterMethod("ping", (_) => "pong");
            RegisterMethod("echo", (args) =>
            {
                if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                    return messageProp.GetString() ?? "empty";
                return "empty";
            });
            RegisterMethod("getTime", (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            RegisterMethod("getClients", (_) => GetConnectedClients());
        }

        private async Task<HttpListenerContext?> AcceptContextAsync(CancellationToken token)
        {
            try
            {
                if (_IsStopping || _Listener == null || !_Listener.IsListening)
                {
                    return null;
                }

                return await _Listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (HttpListenerException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task HandleClientAsync(HttpListenerContext context, CancellationToken token)
        {
            string clientId = $"client_{Interlocked.Increment(ref _ClientIdCounter)}";
            ClientConnection? client = null;

            try
            {
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    LogMessage($"Rejected non-WebSocket request from {context.Request.RemoteEndPoint}");
                    return;
                }

                HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                WebSocket webSocket = webSocketContext.WebSocket;

                client = new ClientConnection(clientId, webSocket);
                client.MaxQueueSize = _MaxQueueSize;
                _Clients.TryAdd(clientId, client);

                LogMessage($"Client connected: {clientId} from {context.Request.RemoteEndPoint}");
                RaiseClientConnected(client);

                await ReceiveLoopAsync(client, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMessage($"Client {clientId} error: {ex.Message}");
            }
            finally
            {
                if (client != null)
                {
                    _Clients.TryRemove(clientId, out ClientConnection? _);
                    RaiseClientDisconnected(client);
                    client.Dispose();
                    LogMessage($"Client disconnected: {clientId}");
                }
            }
        }
        
        private async Task ReceiveLoopAsync(ClientConnection client, CancellationToken token)
        {
            byte[] buffer = new byte[_MaxMessageSize];
            StringBuilder messageBuilder = new StringBuilder();

            try
            {
                if (client.WebSocket != null)
                {
                    while (client.WebSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result = await client.WebSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), token).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await client.WebSocket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Closing",
                                token).ConfigureAwait(false);
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

                                await ProcessRequestAsync(client, message, token).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    LogMessage($"Receive error for {client.SessionId}: {ex.Message}");
                }
            }
        }

        private async Task ProcessRequestAsync(ClientConnection client, string requestString, CancellationToken token = default)
        {
            ServerPendingRequest? pendingRequest = null;

            try
            {
                LogMessage($"Received from {client.SessionId}: {requestString}");

                JsonRpcRequest? request = JsonSerializer.Deserialize<JsonRpcRequest>(requestString);
                if (request == null)
                {
                    JsonRpcResponse invalidResponse = new JsonRpcResponse
                    {
                        Error = JsonRpcError.InvalidRequest(),
                        Id = null
                    };
                    await SendResponseAsync(client, null, invalidResponse, token).ConfigureAwait(false);
                    return;
                }

                pendingRequest = new ServerPendingRequest(request.Id, client, request);
                RaiseRequestReceived(pendingRequest);

                JsonRpcResponse response;

                if (_Methods.ContainsKey(request.Method))
                {
                    try
                    {
                        JsonElement? paramsElement = null;
                        if (request.Params is JsonElement jsonElement)
                        {
                            paramsElement = jsonElement;
                        }

                        object result = _Methods[request.Method](paramsElement);
                        response = new JsonRpcResponse
                        {
                            Result = result,
                            Id = request.Id
                        };
                    }
                    catch (Exception ex)
                    {
                        response = new JsonRpcResponse
                        {
                            Error = new JsonRpcError
                            {
                                Code = -32603,
                                Message = "Internal error",
                                Data = ex.Message
                            },
                            Id = request.Id
                        };
                    }
                }
                else
                {
                    response = new JsonRpcResponse
                    {
                        Error = JsonRpcError.MethodNotFound(),
                        Id = request.Id
                    };
                }

                // Only send response if request has an id (not a notification)
                if (request.Id != null)
                {
                    await SendResponseAsync(client, pendingRequest, response, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error processing request: {ex.Message}");
                JsonRpcResponse errorResponse = new JsonRpcResponse
                {
                    Error = JsonRpcError.ParseError(),
                    Id = null
                };
                await SendResponseAsync(client, null, errorResponse, token).ConfigureAwait(false);
            }
        }

        private async Task SendResponseAsync(ClientConnection client, ServerPendingRequest? pendingRequest, JsonRpcResponse response, CancellationToken token = default)
        {
            try
            {
                string json = JsonSerializer.Serialize(response);
                await SendToClientAsync(client, json, token).ConfigureAwait(false);
                LogMessage($"Sent to {client.SessionId}: {json}");

                if (pendingRequest != null)
                {
                    RaiseResponseSent(pendingRequest, response);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error sending response to {client.SessionId}: {ex.Message}");
            }
        }

        private async Task SendToClientAsync(ClientConnection client, string message, CancellationToken token = default)
        {
            try
            {
                if (client.WebSocket != null && client.WebSocket.State == WebSocketState.Open)
                {
                    byte[] buffer = Encoding.UTF8.GetBytes(message);
                    await client.WebSocket.SendAsync(
                        new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Client might be disconnected
            }
        }

        private void RaiseClientConnected(ClientConnection client)
        {
            // Invoke each handler individually to ensure exception isolation
            if (ClientConnected != null)
            {
                foreach (Delegate handler in ClientConnected.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<ClientConnection>)handler)(this, client);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseClientDisconnected(ClientConnection client)
        {
            // Invoke each handler individually to ensure exception isolation
            if (ClientDisconnected != null)
            {
                foreach (Delegate handler in ClientDisconnected.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<ClientConnection>)handler)(this, client);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseRequestReceived(ServerPendingRequest pendingRequest)
        {
            // Invoke each handler individually to ensure exception isolation
            if (RequestReceived != null)
            {
                JsonRpcRequestEventArgs eventArgs = new JsonRpcRequestEventArgs(pendingRequest);
                foreach (Delegate handler in RequestReceived.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<JsonRpcRequestEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseResponseSent(ServerPendingRequest pendingRequest, JsonRpcResponse response)
        {
            // Invoke each handler individually to ensure exception isolation
            if (ResponseSent != null)
            {
                JsonRpcResponseEventArgs eventArgs = new JsonRpcResponseEventArgs(pendingRequest, response);
                foreach (Delegate handler in ResponseSent.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<JsonRpcResponseEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
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
