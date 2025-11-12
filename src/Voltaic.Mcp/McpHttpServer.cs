namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.JsonRpc;

    /// <summary>
    /// Provides an HTTP-based MCP (Model Context Protocol) server implementation.
    /// Supports JSON-RPC 2.0 over HTTP POST with Server-Sent Events (SSE) for server-to-client notifications.
    /// </summary>
    public class McpHttpServer : IDisposable
    {
        /// <summary>
        /// Gets or sets the session timeout in seconds.
        /// Sessions that have been inactive for longer than this will be expired.
        /// Default is 300 seconds (5 minutes). Minimum is 10 seconds.
        /// </summary>
        public int SessionTimeoutSeconds
        {
            get => _SessionTimeoutSeconds;
            set
            {
                if (value < 10) throw new ArgumentOutOfRangeException(nameof(value), "Session timeout must be at least 10 seconds");
                _SessionTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Gets or sets whether CORS (Cross-Origin Resource Sharing) is enabled.
        /// When enabled, the server will accept requests from any origin.
        /// Default is true to support browser-based clients.
        /// </summary>
        public bool EnableCors
        {
            get => _EnableCors;
            set => _EnableCors = value;
        }

        /// <summary>
        /// Headers to use when CORS support is enabled.
        /// </summary>
        public Dictionary<string, string> CorsHeaders
        {
            get => _CorsHeaders;
            set => _CorsHeaders = (value != null ? value : new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase));
        }

        /// <summary>
        /// Gets the cancellation token source for the server.
        /// </summary>
        public CancellationTokenSource? TokenSource
        {
            get => _TokenSource;
        }

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
        /// Default is "Voltaic.Mcp.HttpServer".
        /// </summary>
        public string ServerName
        {
            get => _ServerName;
            set => _ServerName = value ?? "Voltaic.Mcp.HttpServer";
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
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

        private readonly string _Hostname;
        private readonly int _Port;
        private readonly string _RpcPath;
        private readonly string _EventsPath;
        private HttpListener? _Listener;
        private CancellationTokenSource? _TokenSource;
        private readonly ConcurrentDictionary<string, NotificationQueue> _Sessions;
        private readonly Dictionary<string, Func<JsonElement?, object>> _Methods;
        private readonly List<ToolDefinition> _Tools;
        private Task? _CleanupTask;
        private int _SessionTimeoutSeconds = 300; // 5 minutes
        private bool _EnableCors = true;
        private Dictionary<string, string> _CorsHeaders = new Dictionary<string, string>
        {
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "POST, GET, OPTIONS" },
            { "Access-Control-Allow-Headers", "*"},
            { "Access-Control-Expose-Headers", "X-Session-Id"},
            { "Access-Control-Max-Age", "86400" }
        };
        private volatile bool _IsStopping = false;
        private string _ProtocolVersion = "2025-03-26";
        private string _ServerName = "Voltaic.Mcp.HttpServer";
        private string _ServerVersion = "1.0.0";

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHttpServer"/> class.
        /// </summary>
        /// <param name="hostname">The hostname to listen on.  Use * for any hostname (requires admin or root privileges).</param>
        /// <param name="port">The port number to listen on. Must be between 0 and 65535.</param>
        /// <param name="rpcPath">The URL path for JSON-RPC requests. Default is "/rpc".</param>
        /// <param name="eventsPath">The URL path for Server-Sent Events connections. Default is "/events".</param>
        /// <param name="includeDefaultMethods">True to include default MCP methods such as echo, ping, getTime, and getSessions.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the port is invalid.</exception>
        public McpHttpServer(string hostname, int port, string rpcPath = "/rpc", string eventsPath = "/events", bool includeDefaultMethods = true)
        {
            if (String.IsNullOrEmpty(hostname)) throw new ArgumentNullException(nameof(hostname));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Hostname = hostname;
            _Port = port;
            _RpcPath = String.IsNullOrEmpty(rpcPath) ? "/rpc" : rpcPath;
            _EventsPath = String.IsNullOrEmpty(eventsPath) ? "/events" : eventsPath;
            _Sessions = new ConcurrentDictionary<string, NotificationQueue>();
            _Methods = new Dictionary<string, Func<JsonElement?, object>>();
            _Tools = new List<ToolDefinition>();

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
        /// Registers a tool with metadata for MCP protocol tool discovery.
        /// This registers both the method handler and the tool definition for tools/list.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="handler">The function that handles the tool invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(description)) throw new ArgumentNullException(nameof(description));
            if (inputSchema == null) throw new ArgumentNullException(nameof(inputSchema));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            // Register the method handler
            RegisterMethod(name, handler);

            // Register the tool metadata
            _Tools.Add(new ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema
            });
        }

        /// <summary>
        /// Starts the HTTP server and begins listening for requests asynchronously.
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
                _Listener.Prefixes.Add($"http://{_Hostname}:{_Port}{_RpcPath}/");
                _Listener.Prefixes.Add($"http://{_Hostname}:{_Port}{_EventsPath}/");
                _Listener.Start();
                _TokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);

                // Start session cleanup task
                _CleanupTask = Task.Run(() => CleanupSessionsLoop(_TokenSource.Token));

                LogMessage($"HTTP server started on port {_Port}");
                LogMessage($"RPC endpoint: {_RpcPath}");
                LogMessage($"SSE endpoint: {_EventsPath}");

                while (!_TokenSource.Token.IsCancellationRequested)
                {
                    HttpListenerContext? context = await AcceptContextAsync(_TokenSource.Token).ConfigureAwait(false);
                    if (context != null)
                    {
                        _ = Task.Run(() => HandleRequestAsync(context, _TokenSource.Token));
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcasts a notification to a specific session.
        /// The notification will be queued and delivered via the SSE connection if active.
        /// </summary>
        /// <param name="sessionId">The session ID to send the notification to.</param>
        /// <param name="method">The name of the notification method.</param>
        /// <param name="parameters">The parameters to pass with the notification. Can be null.</param>
        /// <returns>True if the notification was queued; false if the session does not exist.</returns>
        public bool SendNotificationToSession(string sessionId, string method, object? parameters = null)
        {
            if (_Sessions.TryGetValue(sessionId, out NotificationQueue? queue))
            {
                JsonRpcRequest notification = new JsonRpcRequest
                {
                    Method = method,
                    Params = parameters
                };
                queue.Enqueue(notification);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Broadcasts a notification to all active sessions.
        /// Notifications are queued and delivered via SSE connections.
        /// </summary>
        /// <param name="method">The name of the notification method.</param>
        /// <param name="parameters">The parameters to pass with the notification. Can be null.</param>
        public void BroadcastNotification(string method, object? parameters = null)
        {
            JsonRpcRequest notification = new JsonRpcRequest
            {
                Method = method,
                Params = parameters
            };

            foreach (NotificationQueue queue in _Sessions.Values)
            {
                queue.Enqueue(notification);
            }

            LogMessage($"Broadcast notification: {method}");
        }

        /// <summary>
        /// Gets a list of all active session IDs.
        /// </summary>
        /// <returns>A list of session IDs.</returns>
        public List<string> GetActiveSessions()
        {
            return _Sessions.Keys.ToList();
        }

        /// <summary>
        /// Removes a session and its notification queue.
        /// </summary>
        /// <param name="sessionId">The session ID to remove.</param>
        /// <returns>True if the session was found and removed; otherwise, false.</returns>
        public bool RemoveSession(string sessionId)
        {
            if (_Sessions.TryRemove(sessionId, out NotificationQueue? queue))
            {
                queue.Dispose();
                LogMessage($"Removed session: {sessionId}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Stops the server and closes all active sessions.
        /// </summary>
        public void Stop()
        {
            if (_IsStopping) return;
            _IsStopping = true;

            _TokenSource?.Cancel();

            foreach (NotificationQueue queue in _Sessions.Values)
            {
                queue.Dispose();
            }
            _Sessions.Clear();

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
        /// Releases all resources used by the <see cref="McpHttpServer"/>.
        /// </summary>
        public void Dispose()
        {
            Stop();
            _TokenSource?.Dispose();
            _Listener?.Close();
        }

        /// <summary>
        /// Registers the built-in MCP methods including protocol methods (initialize, tools/list) and utility tools.
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

            RegisterMethod("tools/list", (args) =>
            {
                // Return all registered tools
                List<object> toolsList = new List<object>();
                foreach (ToolDefinition tool in _Tools)
                {
                    toolsList.Add(new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        inputSchema = tool.InputSchema
                    });
                }

                return new
                {
                    tools = toolsList
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

            // Handle the initialized notification (no response needed, but we should handle it)
            RegisterMethod("notifications/initialized", (args) =>
            {
                LogMessage("Received initialized notification from client");
                return new { }; // Return empty object, though response won't be sent for notifications
            });

            // Register built-in tools with proper MCP tool metadata
            RegisterTool("ping",
                "Returns 'pong' to verify server connectivity",
                new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                },
                (_) => "pong");

            RegisterTool("echo",
                "Echoes back the provided message",
                new
                {
                    type = "object",
                    properties = new
                    {
                        message = new
                        {
                            type = "string",
                            description = "The message to echo back"
                        }
                    },
                    required = new[] { "message" }
                },
                (args) =>
                {
                    if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                        return messageProp.GetString() ?? "empty";
                    return "empty";
                });

            RegisterTool("getTime",
                "Returns the current UTC time in ISO format",
                new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                },
                (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

            RegisterTool("getSessions",
                "Returns a list of all active session IDs",
                new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                },
                (_) => GetActiveSessions());
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

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "";

                // Handle CORS preflight
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    HandleCorsPreflightRequest(context);
                    return;
                }

                if (path.StartsWith(_RpcPath))
                {
                    await HandleRpcRequestAsync(context, token).ConfigureAwait(false);
                }
                else if (path.StartsWith(_EventsPath))
                {
                    await HandleSseRequestAsync(context, token).ConfigureAwait(false);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error handling request: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch
                {
                    // Ignore errors while sending error response
                }
            }
        }

        private void HandleCorsPreflightRequest(HttpListenerContext context)
        {
            if (_EnableCors)
            {
                foreach (KeyValuePair<string, string> kvp in _CorsHeaders)
                    context.Response.AddHeader(kvp.Key, kvp.Value);
            }

            context.Response.StatusCode = 204;
            context.Response.Close();
        }

        private async Task HandleRpcRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }

            // Get or create session
            string sessionId = context.Request.Headers["X-Session-Id"] ?? Guid.NewGuid().ToString();
            NotificationQueue queue = _Sessions.GetOrAdd(sessionId, (id) => new NotificationQueue(id));

            // Read request body
            string requestBody;
            using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                requestBody = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            LogMessage($"RPC request from session {sessionId}: {requestBody}");

            // Process JSON-RPC request
            JsonRpcResponse response = ProcessRpcRequest(requestBody);

            // Send response
            if (_EnableCors)
            {
                foreach (KeyValuePair<string, string> kvp in _CorsHeaders)
                    context.Response.AddHeader(kvp.Key, kvp.Value);
            }

            context.Response.AddHeader("X-Session-Id", sessionId);
            context.Response.ContentType = "application/json";

            string responseJson = JsonSerializer.Serialize(response);
            byte[] buffer = Encoding.UTF8.GetBytes(responseJson);

            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            context.Response.Close();

            LogMessage($"RPC response to session {sessionId}: {responseJson}");
        }

        private async Task HandleSseRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            if (context.Request.HttpMethod != "GET")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }

            string sessionId = context.Request.Headers["X-Session-Id"] ?? context.Request.QueryString["session"] ?? "";
            if (String.IsNullOrEmpty(sessionId) || !_Sessions.TryGetValue(sessionId, out NotificationQueue? queue))
            {
                context.Response.StatusCode = 400;
                byte[] errorBytes = Encoding.UTF8.GetBytes("Missing or invalid session ID");
                await context.Response.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length, token).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            // Set up SSE headers
            if (_EnableCors)
            {
                context.Response.AddHeader("Access-Control-Allow-Origin", "*");
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.AddHeader("Cache-Control", "no-cache");
            context.Response.AddHeader("Connection", "keep-alive");
            context.Response.SendChunked = true;

            LogMessage($"SSE connection established for session {sessionId}");

            try
            {
                // Keep connection alive and send notifications
                while (!token.IsCancellationRequested)
                {
                    using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                        try
                        {
                            JsonRpcRequest? notification = await queue.DequeueAsync(timeoutCts.Token).ConfigureAwait(false);

                            if (notification != null)
                            {
                                string json = JsonSerializer.Serialize(notification);
                                string sseMessage = $"data: {json}\n\n";
                                byte[] buffer = Encoding.UTF8.GetBytes(sseMessage);

                                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                                await context.Response.OutputStream.FlushAsync(token).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Send keep-alive comment
                            byte[] keepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");
                            await context.Response.OutputStream.WriteAsync(keepAlive, 0, keepAlive.Length, token).ConfigureAwait(false);
                            await context.Response.OutputStream.FlushAsync(token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"SSE error for session {sessionId}: {ex.Message}");
            }
            finally
            {
                context.Response.Close();
                LogMessage($"SSE connection closed for session {sessionId}");
            }
        }

        private JsonRpcResponse ProcessRpcRequest(string requestString)
        {
            try
            {
                JsonRpcRequest? request = JsonSerializer.Deserialize<JsonRpcRequest>(requestString);
                if (request == null)
                {
                    return new JsonRpcResponse
                    {
                        Error = JsonRpcError.InvalidRequest(),
                        Id = null
                    };
                }

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
                        return new JsonRpcResponse
                        {
                            Result = result,
                            Id = request.Id
                        };
                    }
                    catch (Exception ex)
                    {
                        return new JsonRpcResponse
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
                    return new JsonRpcResponse
                    {
                        Error = JsonRpcError.MethodNotFound(),
                        Id = request.Id
                    };
                }
            }
            catch (JsonException)
            {
                return new JsonRpcResponse
                {
                    Error = JsonRpcError.ParseError(),
                    Id = null
                };
            }
        }

        private async Task CleanupSessionsLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);

                    DateTime expirationTime = DateTime.UtcNow.AddSeconds(-_SessionTimeoutSeconds);

                    List<string> expiredSessions = _Sessions
                        .Where(kvp => kvp.Value.LastActivity < expirationTime)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (string sessionId in expiredSessions)
                    {
                        RemoveSession(sessionId);
                        LogMessage($"Expired session: {sessionId}");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage($"Session cleanup error: {ex.Message}");
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
