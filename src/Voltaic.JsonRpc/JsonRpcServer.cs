namespace Voltaic.JsonRpc
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
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

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
        private readonly Dictionary<string, Func<JsonElement?, object>> _Methods;
        private int _ClientIdCounter = 0;
        private string _DefaultContentType = "application/json; charset=utf-8";

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcServer"/> class.
        /// </summary>
        /// <param name="ip">IP address to listen on.</param>
        /// <param name="port">The port number to listen on.</param>
        /// <param name="includeDefaultMethods">True to include default methods such as echo, ping, getTime, add, and getClients.</param>
        public JsonRpcServer(IPAddress ip, int port, bool includeDefaultMethods = true)
        {
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Ip = ip;
            _Port = port;
            _Clients = new ConcurrentDictionary<string, ClientConnection>();
            _Methods = new Dictionary<string, Func<JsonElement?, object>>();

            if (includeDefaultMethods) RegisterBuiltInMethods();
        }

        /// <summary>
        /// Registers a custom RPC method with the specified handler.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The function that handles the method invocation.</param>
        public void RegisterMethod(string name, Func<JsonElement?, object> handler)
        {
            _Methods[name] = handler;
        }

        /// <summary>
        /// Attempts to invoke a registered method by name with the given parameters.
        /// </summary>
        /// <param name="methodName">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method.</param>
        /// <param name="result">The result of the method invocation if successful.</param>
        /// <returns>True if the method was found and invoked successfully; otherwise, false.</returns>
        protected bool TryInvokeMethod(string methodName, JsonElement? parameters, out object? result)
        {
            result = null;
            if (_Methods.ContainsKey(methodName))
            {
                result = _Methods[methodName](parameters);
                return true;
            }
            return false;
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
            Stop();
            _TokenSource?.Dispose();
            _Listener?.Stop();
        }

        /// <summary>
        /// Registers the built-in RPC methods: ping, echo, getTime, add, and getClients.
        /// This method is virtual to allow derived classes to customize the set of built-in methods.
        /// </summary>
        protected virtual void RegisterBuiltInMethods()
        {
            // Register built-in RPC _Methods

            RegisterMethod("echo", (args) =>
            {
                if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                    return messageProp.GetString() ?? "empty";
                return "empty";
            });
            RegisterMethod("getTime", (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            RegisterMethod("add", (args) =>
            {
                double a = 0, b = 0;
                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("a", out JsonElement aProp))
                        a = aProp.GetDouble();
                    if (args.Value.TryGetProperty("b", out JsonElement bProp))
                        b = bProp.GetDouble();
                }
                return a + b;
            });
            RegisterMethod("getClients", (_) => GetConnectedClients());
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

            try
            {
                _Clients.TryAdd(clientId, client);
                LogMessage($"Client connected: {clientId} from {tcpClient.Client.RemoteEndPoint}");

                byte[] buffer = MessageFraming.CreateBuffer();
                NetworkStream stream = tcpClient.GetStream();
                int bufferOffset = 0;
                int bufferCount = 0;

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
                    await ProcessRequestAsync(client, message, token).ConfigureAwait(false);

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
                client.Dispose();
                LogMessage($"Client disconnected: {clientId}");
            }
        }

        private async Task ProcessRequestAsync(ClientConnection client, string requestString, CancellationToken token = default)
        {
            try
            {
                LogMessage($"Received from {client.Id}: {requestString}");

                JsonRpcRequest? request = JsonSerializer.Deserialize<JsonRpcRequest>(requestString);
                if (request == null)
                {
                    await SendResponseAsync(client, new JsonRpcResponse
                    {
                        Error = JsonRpcError.InvalidRequest(),
                        Id = null
                    }, token).ConfigureAwait(false);
                    return;
                }

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
                    await SendResponseAsync(client, response, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error processing request: {ex.Message}");
                await SendResponseAsync(client, new JsonRpcResponse
                {
                    Error = JsonRpcError.ParseError(),
                    Id = null
                }, token).ConfigureAwait(false);
            }
        }

        private async Task SendResponseAsync(ClientConnection client, JsonRpcResponse response, CancellationToken token = default)
        {
            try
            {
                string json = JsonSerializer.Serialize(response);
                await MessageFraming.WriteMessageAsync(client.Stream, json, _DefaultContentType, token).ConfigureAwait(false);
                LogMessage($"Sent to {client.Id}: {json}");
            }
            catch (Exception ex)
            {
                LogMessage($"Error sending response to {client.Id}: {ex.Message}");
            }
        }

        private async Task SendToClientAsync(ClientConnection client, string message, CancellationToken token = default)
        {
            try
            {
                await MessageFraming.WriteMessageAsync(client.Stream, message, _DefaultContentType, token).ConfigureAwait(false);
            }
            catch
            {
                // Client might be disconnected
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

#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
    }
}