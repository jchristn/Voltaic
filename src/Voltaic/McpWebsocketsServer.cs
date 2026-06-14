namespace Voltaic.Mcp
{
    using Voltaic.Core;
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
        private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<object>>> _Methods;
        private readonly McpEndpoint _Endpoint;
        private int _ClientIdCounter = 0;
        private int _MaxMessageSize = 1048576; // 1 MB
        private int _KeepAliveIntervalSeconds = 30;
        private int _MaxQueueSize = 100;
        private volatile bool _IsStopping = false;
        private bool _IsDisposed = false;
        /// <summary>
        /// Gets or sets the MCP protocol version.
        /// Default is <see cref="McpProtocol.LatestProtocolVersion"/>.
        /// </summary>
        public string ProtocolVersion
        {
            get => _Endpoint.ProtocolVersion;
            set => _Endpoint.ProtocolVersion = value ?? McpProtocol.LatestProtocolVersion;
        }

        /// <summary>
        /// Gets or sets the server name for MCP serverInfo.
        /// Default is "Voltaic.Mcp.WebSocketsServer".
        /// </summary>
        public string ServerName
        {
            get => _Endpoint.ServerName;
            set => _Endpoint.ServerName = value ?? "Voltaic.Mcp.WebSocketsServer";
        }

        /// <summary>
        /// Gets or sets the server version for MCP serverInfo.
        /// Default is "1.0.0".
        /// </summary>
        public string ServerVersion
        {
            get => _Endpoint.ServerVersion;
            set => _Endpoint.ServerVersion = value ?? "1.0.0";
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
            _Methods = new Dictionary<string, Func<JsonElement?, CancellationToken, Task<object>>>();
            _Endpoint = new McpEndpoint("Voltaic.Mcp.WebSocketsServer");

            if (includeDefaultMethods) RegisterBuiltInMethods();
        }

        /// <summary>
        /// Registers a custom RPC method with the specified synchronous handler.
        /// The handler is wrapped internally to support async invocation.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The function that handles the method invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<JsonElement?, object> handler)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Methods[name] = (args, _) => Task.FromResult(handler(args));
        }

        /// <summary>
        /// Registers a custom RPC method with the specified asynchronous handler.
        /// Use this overload when the handler needs to perform asynchronous operations such as
        /// database queries, HTTP calls, or file I/O.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<JsonElement?, Task<object>> handler)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
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
        public void RegisterMethod(string name, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Methods[name] = handler;
        }

        /// <summary>
        /// Registers a tool with metadata for MCP tool discovery using a synchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using a synchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="outputSchema">Output JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a synchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<JsonElement?, object> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, (args, _) => Task.FromResult(handler(args)));
            RegisterMethod(definition.Name, handler);
        }

        /// <summary>
        /// Registers a tool with metadata for MCP tool discovery using an asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using an asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="outputSchema">Output JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using an asynchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<JsonElement?, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, (args, _) => handler(args));
            RegisterMethod(definition.Name, handler);
        }

        /// <summary>
        /// Registers a tool with metadata for MCP tool discovery using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="outputSchema">Output JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, handler);
            RegisterMethod(definition.Name, handler);
        }

        /// <summary>
        /// Registers a static MCP resource with a synchronous read handler.
        /// </summary>
        /// <param name="uri">Resource URI.</param>
        /// <param name="name">Resource name.</param>
        /// <param name="mimeType">Resource MIME type.</param>
        /// <param name="readHandler">Read handler.</param>
        public void RegisterResource(string uri, string name, string mimeType, Func<McpReadResourceResult> readHandler)
        {
            if (readHandler == null) throw new ArgumentNullException(nameof(readHandler));
            RegisterResource(CreateResource(uri, name, mimeType), (_, _) => Task.FromResult(readHandler()));
        }

        /// <summary>
        /// Registers a static MCP resource with a cancellable read handler.
        /// </summary>
        /// <param name="resource">Resource metadata.</param>
        /// <param name="readHandler">Read handler that receives the requested URI.</param>
        public void RegisterResource(McpResource resource, Func<string, CancellationToken, Task<McpReadResourceResult>> readHandler)
        {
            _Endpoint.RegisterResource(resource, readHandler);
        }

        /// <summary>
        /// Registers a dynamic MCP resource template with a synchronous read handler.
        /// </summary>
        /// <param name="uriTemplate">URI template.</param>
        /// <param name="name">Template name.</param>
        /// <param name="mimeType">Resource MIME type.</param>
        /// <param name="readHandler">Read handler that receives the matched URI.</param>
        public void RegisterResourceTemplate(string uriTemplate, string name, string mimeType, Func<string, McpReadResourceResult> readHandler)
        {
            if (readHandler == null) throw new ArgumentNullException(nameof(readHandler));
            RegisterResourceTemplate(CreateResourceTemplate(uriTemplate, name, mimeType), (uri, _, _) => Task.FromResult(readHandler(uri)));
        }

        /// <summary>
        /// Registers a dynamic MCP resource template with a cancellable read handler.
        /// </summary>
        /// <param name="template">Template metadata.</param>
        /// <param name="readHandler">Read handler that receives the matched URI and template variables.</param>
        public void RegisterResourceTemplate(
            McpResourceTemplate template,
            Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<McpReadResourceResult>> readHandler)
        {
            _Endpoint.RegisterResourceTemplate(template, readHandler);
        }

        /// <summary>
        /// Registers an MCP prompt with a synchronous handler.
        /// </summary>
        /// <param name="name">Prompt name.</param>
        /// <param name="description">Prompt description.</param>
        /// <param name="arguments">Prompt arguments.</param>
        /// <param name="handler">Prompt handler.</param>
        public void RegisterPrompt(string name, string description, IEnumerable<McpPromptArgument>? arguments, Func<JsonElement?, McpGetPromptResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterPrompt(CreatePrompt(name, description, arguments), (args, _) => Task.FromResult(handler(args)));
        }

        /// <summary>
        /// Registers an MCP prompt with a cancellable handler.
        /// </summary>
        /// <param name="prompt">Prompt metadata.</param>
        /// <param name="handler">Prompt handler.</param>
        public void RegisterPrompt(McpPrompt prompt, Func<JsonElement?, CancellationToken, Task<McpGetPromptResult>> handler)
        {
            _Endpoint.RegisterPrompt(prompt, handler);
        }

        /// <summary>
        /// Registers a completion provider for prompt arguments or resource template variables.
        /// </summary>
        /// <param name="referenceType">Reference type, usually <c>ref/prompt</c> or <c>ref/resource</c>.</param>
        /// <param name="referenceId">Prompt name or resource URI template. Use null to match any reference of the type.</param>
        /// <param name="argumentName">Argument name. Use null to match any argument on the reference.</param>
        /// <param name="handler">Completion handler.</param>
        public void RegisterCompletionProvider(
            string referenceType,
            string? referenceId,
            string? argumentName,
            Func<McpCompleteRequest, CancellationToken, Task<McpCompleteResult>> handler)
        {
            _Endpoint.RegisterCompletionProvider(referenceType, referenceId, argumentName, handler);
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
        /// Notifies connected clients that the tool list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyToolsChangedAsync(CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/tools/list_changed", null, token);
        }

        /// <summary>
        /// Notifies connected clients that the resource list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyResourcesChangedAsync(CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/resources/list_changed", null, token);
        }

        /// <summary>
        /// Notifies connected clients that a resource was updated.
        /// </summary>
        /// <param name="uri">Updated resource URI.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyResourceUpdatedAsync(string uri, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(uri)) throw new ArgumentNullException(nameof(uri));
            return BroadcastNotificationAsync("notifications/resources/updated", new { uri }, token);
        }

        /// <summary>
        /// Notifies connected clients that the prompt list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyPromptsChangedAsync(CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/prompts/list_changed", null, token);
        }

        /// <summary>
        /// Notifies connected WebSocket clients about request progress.
        /// </summary>
        /// <param name="progressToken">Progress token from request metadata.</param>
        /// <param name="progress">Current progress value.</param>
        /// <param name="total">Optional total progress value.</param>
        /// <param name="message">Optional human-readable progress text.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyProgressAsync(object progressToken, double progress, double? total = null, string? message = null, CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/progress", new McpProgressNotification
            {
                ProgressToken = progressToken,
                Progress = progress,
                Total = total,
                Message = message
            }, token);
        }

        /// <summary>
        /// Notifies connected WebSocket clients that a request was cancelled.
        /// </summary>
        /// <param name="requestId">Cancelled request ID.</param>
        /// <param name="reason">Optional cancellation reason.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyCancelledAsync(object requestId, string? reason = null, CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/cancelled", new McpCancelledNotification
            {
                RequestId = requestId,
                Reason = reason
            }, token);
        }

        /// <summary>
        /// Sends an MCP log message notification to connected WebSocket clients.
        /// </summary>
        /// <param name="level">Syslog-style level.</param>
        /// <param name="data">JSON-serializable log data.</param>
        /// <param name="logger">Optional logger name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyLogMessageAsync(string level, object? data, string? logger = null, CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/message", new McpLogMessageNotification
            {
                Level = level,
                Logger = logger,
                Data = data
            }, token);
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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="McpWebsocketsServer"/> and optionally releases the managed resources.
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
                    _Endpoint.Clear();
                    _TokenSource?.Dispose();
                    ((IDisposable?)_Listener)?.Dispose();
                }
            }
        }

        private static ToolDefinition CreateToolDefinition(string name, string description, object inputSchema, object? outputSchema)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(description)) throw new ArgumentNullException(nameof(description));
            if (inputSchema == null) throw new ArgumentNullException(nameof(inputSchema));

            return new ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema,
                OutputSchema = outputSchema
            };
        }

        private static McpResource CreateResource(string uri, string name, string mimeType)
        {
            if (String.IsNullOrEmpty(uri)) throw new ArgumentNullException(nameof(uri));
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            return new McpResource
            {
                Uri = uri,
                Name = name,
                MimeType = mimeType
            };
        }

        private static McpResourceTemplate CreateResourceTemplate(string uriTemplate, string name, string mimeType)
        {
            if (String.IsNullOrEmpty(uriTemplate)) throw new ArgumentNullException(nameof(uriTemplate));
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            return new McpResourceTemplate
            {
                UriTemplate = uriTemplate,
                Name = name,
                MimeType = mimeType
            };
        }

        private static McpPrompt CreatePrompt(string name, string description, IEnumerable<McpPromptArgument>? arguments)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            return new McpPrompt
            {
                Name = name,
                Description = description,
                Arguments = arguments?.ToList()
            };
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
                return _Endpoint.Initialize(args);
            });

            RegisterMethod("tools/list", (args) => _Endpoint.ListTools(args));
            RegisterMethod("tools/call", _Endpoint.CallToolAsync);
            RegisterMethod("resources/list", (args) => _Endpoint.ListResources(args));
            RegisterMethod("resources/templates/list", (args) => _Endpoint.ListResourceTemplates(args));
            RegisterMethod("resources/read", _Endpoint.ReadResourceAsync);
            RegisterMethod("resources/subscribe", (args) => _Endpoint.SubscribeResource(args));
            RegisterMethod("resources/unsubscribe", (args) => _Endpoint.UnsubscribeResource(args));
            RegisterMethod("prompts/list", (args) => _Endpoint.ListPrompts(args));
            RegisterMethod("prompts/get", _Endpoint.GetPromptAsync);
            RegisterMethod("completion/complete", _Endpoint.CompleteAsync);
            RegisterMethod("logging/setLevel", (args) => _Endpoint.SetLogLevel(args));
            RegisterMethod("notifications/cancelled", (args) => _Endpoint.Cancelled(args));
            RegisterMethod("notifications/initialized", (args) => _Endpoint.Initialized(args));

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

                        object result = await _Methods[request.Method](paramsElement, token).ConfigureAwait(false);
                        response = new JsonRpcResponse
                        {
                            Result = result,
                            Id = request.Id
                        };
                    }
                    catch (Exception ex)
                    {
                        JsonRpcError error = ex is McpProtocolException protocolException
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
