namespace Voltaic.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides a TCP-based JSON-RPC 2.0 server implementation for handling remote procedure calls.
    /// </summary>
    public class JsonRpcServer : IDisposable
    {
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.

        /// <summary>
        /// Cancellation token source.
        /// </summary>
        public CancellationTokenSource? TokenSource
        {
            get => _TokenSource;
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

        /// <summary>
        /// Gets or sets the default Content-Type header value used when sending messages.
        /// This will default to application/json; charset=utf-8 if the supplied value is null.
        /// </summary>
        public string DefaultContentType
        {
            get => _DefaultContentType;
            set => _DefaultContentType = value ?? "application/json; charset=utf-8";
        }

        private TcpListener? _Listener;
        private readonly IPAddress _Ip;
        private readonly int _Port;
        private CancellationTokenSource? _TokenSource;
        private readonly ConcurrentDictionary<string, ClientConnection> _Clients;
        private readonly Dictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>> _Methods;
        private int _ClientIdCounter = 0;
        private string _DefaultContentType = "application/json; charset=utf-8";
        private int _MaxQueueSize = 100;
        private bool _IsDisposed = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcServer"/> class.
        /// </summary>
        /// <param name="ip">IP address to listen on.</param>
        /// <param name="port">The port number to listen on.</param>
        /// <param name="includeDiagnosticMethods">
        /// True to register the diagnostic methods <c>ping</c>, <c>echo</c>, <c>getTime</c>, and <c>add</c>.
        /// Default is false, so the server exposes only the methods the application registers.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when ip is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the port is invalid.</exception>
        public JsonRpcServer(IPAddress ip, int port, bool includeDiagnosticMethods = false)
        {
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Ip = ip;
            _Port = port;
            _Clients = new ConcurrentDictionary<string, ClientConnection>();
            _Methods = new Dictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>>();

            if (includeDiagnosticMethods) RegisterDiagnosticMethods();
        }

        /// <summary>
        /// Registers a custom RPC method with the specified synchronous handler.
        /// The handler is wrapped internally to support async invocation.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The function that handles the method invocation.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<RpcParameters?, object> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Methods[name] = (args, _) => Task.FromResult(handler(args));
        }

        /// <summary>
        /// Registers a custom RPC method with the specified asynchronous handler.
        /// Use this overload when the handler needs to perform asynchronous operations such as
        /// database queries, HTTP calls, or file I/O.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<RpcParameters?, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Methods[name] = (args, _) => handler(args);
        }

        /// <summary>
        /// Registers a custom RPC method with the specified asynchronous handler that accepts a cancellation token.
        /// Use this overload when the handler needs to perform cancellable asynchronous operations.
        /// The cancellation token provided to the handler is the same token used by the server's connection processing.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation with cancellation support.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<RpcParameters?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Methods[name] = handler;
        }

        /// <summary>
        /// Registers a custom RPC method whose handler additionally receives the authenticated caller for
        /// the current request. The context is the ambient <see cref="RpcCallContext.Current"/> captured at
        /// invocation time; it is null when the transport did not authenticate the request (the base
        /// TCP JSON-RPC transport performs no authentication, so it is always null there).
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation with caller context and cancellation support.</param>
        /// <exception cref="ArgumentNullException">Thrown when handler is null.</exception>
        public void RegisterMethod(string name, Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Methods[name] = (RpcParameters? args, CancellationToken token) => handler(args, RpcCallContext.Current, token);
        }

        /// <summary>
        /// Attempts to invoke a registered method by name with the given parameters asynchronously.
        /// </summary>
        /// <param name="methodName">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method.</param>
        /// <param name="token">Cancellation token to pass to the method handler.</param>
        /// <returns>An <see cref="RpcMethodInvocation"/> indicating whether the method was found and the result of invocation.</returns>
        protected async Task<RpcMethodInvocation> TryInvokeMethodAsync(string methodName, RpcParameters? parameters, CancellationToken token = default)
        {
            if (_Methods.ContainsKey(methodName))
            {
                object result = await _Methods[methodName](parameters, token).ConfigureAwait(false);
                return new RpcMethodInvocation(true, result);
            }
            return new RpcMethodInvocation(false, null);
        }

        /// <summary>
        /// Starts the server and begins listening for client connections asynchronously.
        /// This method will continue running until Stop() is called.
        /// </summary>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public async Task StartAsync(CancellationToken token = default)
        {
            try
            {
                _Listener = new TcpListener(_Ip, _Port);
                _Listener.Start();
                _TokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);

                LogMessage($"Server started on port {_Port}");

                while (!_TokenSource.Token.IsCancellationRequested)
                {
                    TcpClient? tcpClient = await AcceptClientAsync(_TokenSource.Token).ConfigureAwait(false);
                    if (tcpClient != null)
                    {
                        _ = Task.Run(() => HandleClientAsync(tcpClient, _TokenSource.Token));
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Asynchronously broadcasts a notification to all connected _Clients.
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
            if (_Clients.TryRemove(clientId, out ClientConnection client))
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
        /// Stops the server and disconnects all _Clients.
        /// </summary>
        public void Stop()
        {
            _TokenSource?.Cancel();

            foreach (ClientConnection client in _Clients.Values)
            {
                client.Dispose();
            }
            _Clients.Clear();

            _Listener?.Stop();
            LogMessage("Server stopped");
        }

        /// <summary>
        /// Releases all resources used by the <see cref="JsonRpcServer"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="JsonRpcServer"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_IsDisposed)
            {
                _IsDisposed = true;

                if (disposing)
                {
                    Stop();
                    _TokenSource?.Dispose();

                    try
                    {
                        ((IDisposable?)_Listener)?.Dispose();
                    }
                    catch
                    {
                        // Ignore errors during listener disposal
                    }
                }
            }
        }

        /// <summary>
        /// Registers the diagnostic RPC methods: <c>ping</c>, <c>echo</c>, <c>getTime</c>, and <c>add</c>.
        /// Called by the constructor only when <c>includeDiagnosticMethods</c> is true. This method is virtual
        /// to allow derived classes to customize the diagnostic set.
        /// </summary>
        protected virtual void RegisterDiagnosticMethods()
        {
            RegisterMethod("echo", (args) =>
            {
                RpcEchoArguments? echo = args?.Deserialize<RpcEchoArguments>();
                return echo?.Message ?? "empty";
            });
            RegisterMethod("getTime", (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            RegisterMethod("add", (args) =>
            {
                RpcAddArguments? add = args?.Deserialize<RpcAddArguments>();
                return add == null ? 0d : add.A + add.B;
            });
            RegisterMethod("ping", (_) => "pong");
        }

        private async Task<TcpClient?> AcceptClientAsync(CancellationToken token)
        {
            try
            {
                if (_Listener != null)
                {
                    using (token.Register(_Listener.Stop))
                    {
                        return await _Listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken token)
        {
            string clientId = $"client_{Interlocked.Increment(ref _ClientIdCounter)}";
            ClientConnection client = new ClientConnection(clientId, tcpClient);
            client.MaxQueueSize = _MaxQueueSize;

            try
            {
                _Clients.TryAdd(clientId, client);
                LogMessage($"Client connected: {clientId} from {tcpClient.Client.RemoteEndPoint}");
                OnClientConnected(client);
                RaiseClientConnected(client);

                byte[] buffer = MessageFraming.CreateBuffer();
                NetworkStream stream = tcpClient.GetStream();
                int bufferOffset = 0;
                int bufferCount = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (bufferCount == 0) return;

                // Newline-delimited JSON (the stdio framing, which MCP asks custom stream transports to reuse) starts
                // with '{' or '['; anything else must be Content-Length framing.
                int first = 0;
                while (first < bufferCount && (buffer[first] == (byte)' ' || buffer[first] == (byte)'\t' || buffer[first] == (byte)'\r' || buffer[first] == (byte)'\n')) first++;
                if (AcceptNewlineFraming && first < bufferCount && (buffer[first] == (byte)'{' || buffer[first] == (byte)'['))
                {
                    client.NewlineFraming = true;
                    await ReadNewlineMessagesAsync(client, stream, buffer, bufferCount, token).ConfigureAwait(false);
                    return;
                }

                while (!token.IsCancellationRequested && tcpClient.Connected)
                {
                    // Read a complete message using LSP-style framing
                    (string? message, byte[] newBuffer, int newOffset, int newCount) = await MessageFraming.ReadMessageAsync(
                        stream, buffer, bufferOffset, bufferCount, token).ConfigureAwait(false);

                    // Update buffer reference in case it was resized
                    buffer = newBuffer;

                    if (message == null)
                    {
                        // Check if stream was closed (newCount == 0) or just need more data
                        if (newCount == 0 && bufferCount == 0)
                        {
                            break;
                        }

                        // Update buffer state and continue reading
                        bufferOffset = newOffset;
                        bufferCount = newCount;
                        continue;
                    }

                    // Process the complete message
                    await ProcessMessageAsync(client, message, token).ConfigureAwait(false);

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
                LogMessage($"Client {clientId} error: {ex.Message}");
            }
            finally
            {
                _Clients.TryRemove(clientId, out _);
                OnClientDisconnected(client);
                RaiseClientDisconnected(client);
                client.Dispose();
                LogMessage($"Client disconnected: {clientId}");
            }
        }

        // Reads newline-delimited messages. A line that is not JSON closes the connection, so a
        // browser's cross-protocol HTTP request (whose first line is "POST / HTTP/1.1") never reaches a handler.
        private async Task ReadNewlineMessagesAsync(ClientConnection client, NetworkStream stream, byte[] initial, int initialCount, CancellationToken token)
        {
            List<byte> pending = new List<byte>(initialCount);
            for (int i = 0; i < initialCount; i++) pending.Add(initial[i]);
            byte[] chunk = new byte[8192];

            while (!token.IsCancellationRequested)
            {
                int newline;
                while ((newline = pending.IndexOf((byte)'\n')) >= 0)
                {
                    string line = Encoding.UTF8.GetString(pending.GetRange(0, newline).ToArray()).TrimEnd('\r');
                    pending.RemoveRange(0, newline + 1);
                    if (String.IsNullOrWhiteSpace(line)) continue;

                    string trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal) && !IsJsonValue(trimmed))
                    {
                        LogMessage($"Closing {client.SessionId}: a line is not a JSON message.");
                        return;
                    }

                    await ProcessMessageAsync(client, line, token).ConfigureAwait(false);
                }

                if (pending.Count > MaxNewlineMessageBytes)
                {
                    LogMessage($"Closing {client.SessionId}: a message exceeds {MaxNewlineMessageBytes} bytes.");
                    return;
                }

                int read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                if (read == 0) return;
                for (int i = 0; i < read; i++) pending.Add(chunk[i]);
            }
        }

        // True when the line is some other JSON value (a string or number), which gets an Invalid Request reply.
        private static bool IsJsonValue(string line)
        {
            try
            {
                using (JsonDocument.Parse(line))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // The registered method handlers, for derived servers that dispatch messages themselves.
        private protected IDictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>> Methods => _Methods;

        // The connected clients.
        private protected IEnumerable<ClientConnection> ConnectedClients => _Clients.Values;

        // Derived servers may accept newline-delimited JSON in addition to Content-Length framing.
        private protected virtual bool AcceptNewlineFraming => false;

        // Largest newline-delimited message accepted.
        private protected virtual int MaxNewlineMessageBytes => 16 * 1024 * 1024;

        // Called for every complete message. The default handles one JSON-RPC request at a time.
        private protected virtual Task ProcessMessageAsync(ClientConnection client, string message, CancellationToken token)
        {
            return ProcessRequestAsync(client, message, token);
        }

        // Called when a client connects, before any message is read.
        private protected virtual void OnClientConnected(ClientConnection client)
        {
        }

        // Called when a client disconnects.
        private protected virtual void OnClientDisconnected(ClientConnection client)
        {
        }

        // Writes one message in the connection's framing; writes to a connection never interleave.
        private protected async Task WriteToClientAsync(ClientConnection client, string json, CancellationToken token)
        {
            if (client.Stream == null) return;

            await client.WriteLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (client.NewlineFraming)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
                    await client.Stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                    await client.Stream.FlushAsync(token).ConfigureAwait(false);
                }
                else
                {
                    await MessageFraming.WriteMessageAsync(client.Stream, json, _DefaultContentType, token).ConfigureAwait(false);
                }
            }
            finally
            {
                client.WriteLock.Release();
            }
        }

        // Raises RequestReceived for a request a derived server dispatched itself.
        private protected void RaiseRequestReceivedFor(ClientConnection client, JsonRpcRequest request)
        {
            RaiseRequestReceived(new ServerPendingRequest(request.Id, client, request));
        }

        // Raises ResponseSent for a response a derived server produced itself.
        private protected void RaiseResponseSentFor(ClientConnection client, JsonRpcRequest? request, JsonRpcResponse response)
        {
            if (request == null) return;
            RaiseResponseSent(new ServerPendingRequest(request.Id, client, request), response);
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
                        RpcParameters? paramsElement = request.Params == null ? null : RpcParameters.FromObject(request.Params);

                        object result = await _Methods[request.Method](paramsElement, token).ConfigureAwait(false);
                        response = new JsonRpcResponse
                        {
                            Result = result,
                            Id = request.Id
                        };
                    }
                    catch (Exception ex)
                    {
                        JsonRpcError error = ex is IJsonRpcErrorProvider protocolException
                            ? protocolException.ToJsonRpcError()
                            : new JsonRpcError
                            {
                                Code = -32603,
                                Message = "Internal error",
                                Data = ex.Message
                            };

                        response = new JsonRpcResponse
                        {
                            Error = error,
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
                await WriteToClientAsync(client, json, token).ConfigureAwait(false);
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
                await WriteToClientAsync(client, message, token).ConfigureAwait(false);
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

        // Lets derived servers write to the Log event.
        private protected void WriteLog(string message)
        {
            LogMessage(message);
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

#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
    }
}
