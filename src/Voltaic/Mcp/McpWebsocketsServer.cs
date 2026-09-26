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
        /// Gets or sets an optional asynchronous authentication handler, the same delegate type as
        /// <see cref="McpHttpServer.AuthenticationHandler"/>. It is invoked with the WebSocket upgrade request, so
        /// it can check <c>Authorization</c>, API-key headers, the query string, or client certificates. When it
        /// returns a result with <see cref="AuthenticationResult.IsAuthenticated"/> false, the upgrade is refused
        /// with <see cref="AuthenticationResult.StatusCode"/>, <see cref="AuthenticationResult.Headers"/> (for
        /// example <c>WWW-Authenticate</c>), and <see cref="AuthenticationResult.ErrorMessage"/>. When it
        /// succeeds, the caller is stored in <see cref="ClientConnection.Caller"/> and is the ambient
        /// <see cref="RpcCallContext.Current"/> for every request on that socket, including the
        /// <see cref="RpcCallContext"/> handler overloads. Default is null, which accepts every upgrade that
        /// passes the origin and loopback checks.
        /// </summary>
        public Func<HttpListenerRequest, Task<AuthenticationResult>>? AuthenticationHandler
        {
            get => _AuthenticationHandler;
            set => _AuthenticationHandler = value;
        }

        /// <summary>
        /// Gets or sets the policy that decides which browser origins may open a socket. Browsers do not apply
        /// CORS to WebSockets, so this check is the defence against cross-site WebSocket hijacking. An upgrade
        /// whose <c>Origin</c> the policy rejects receives HTTP 403 before authentication. The default allows
        /// upgrades without an <c>Origin</c> header (non-browser clients) and loopback origins.
        /// Setting null restores the default policy.
        /// </summary>
        public OriginPolicy OriginPolicy
        {
            get => _OriginPolicy;
            set => _OriginPolicy = value ?? new OriginPolicy();
        }

        /// <summary>
        /// Gets or sets whether only loopback clients are served. When true, an upgrade request whose remote
        /// address is not loopback receives HTTP 403. Default is true when the server is constructed with a
        /// loopback host name (<c>localhost</c>, <c>127.x.x.x</c>, or <c>::1</c>) and false otherwise. On Windows
        /// a <c>localhost</c> prefix is served on every interface and routed by the spoofable <c>Host</c> header,
        /// so this check is what keeps such a server local.
        /// </summary>
        public bool RestrictToLoopbackClients
        {
            get => _RestrictToLoopbackClients;
            set => _RestrictToLoopbackClients = value;
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
        private readonly Dictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>> _Methods;
        private readonly McpEndpoint _Endpoint;
        private readonly McpMessageProcessor _Processor;
        private int _ClientIdCounter = 0;
        private int _MaxMessageSize = 1048576; // 1 MB
        private int _KeepAliveIntervalSeconds = 30;
        private int _MaxQueueSize = 100;
        private volatile bool _IsStopping = false;
        private bool _IsDisposed = false;
        private Func<HttpListenerRequest, Task<AuthenticationResult>>? _AuthenticationHandler;
        private OriginPolicy _OriginPolicy = new OriginPolicy();
        private bool _RestrictToLoopbackClients;

        /// <summary>
        /// Obsolete and has no effect. <c>initialize</c> must name a protocol version (a request without one gets
        /// <c>-32602</c>), so there is no version to fall back to; negotiation is capped by
        /// <see cref="MaximumHandshakeProtocolVersion"/>. The value is stored and returned for compatibility. Default is
        /// <see cref="McpProtocol.LatestProtocolVersion"/>; setting null restores the default.
        /// </summary>
        [Obsolete("initialize must name a protocol version, so this setting has no effect. Use MaximumHandshakeProtocolVersion to cap negotiation.")]
        public string ProtocolVersion
        {
            get => _Endpoint.ProtocolVersion;
            set => _Endpoint.ProtocolVersion = value ?? McpProtocol.LatestProtocolVersion;
        }

        /// <summary>
        /// Gets or sets whether the message of an exception thrown by a tool handler is shown to the client. A handler
        /// exception becomes a tool result with <c>isError</c> set to true (a tool execution error the model can read).
        /// When false (the default), the text is a generic "Tool '&lt;name&gt;' failed because of an internal error." so
        /// internal details (connection strings, paths, host names) are not sent, as the MCP security considerations
        /// require tool outputs to be sanitized; when true, the text is "Tool '&lt;name&gt;' failed: &lt;message&gt;". The
        /// message of a <see cref="McpToolException"/> is always shown, and the exception type and message are always
        /// written to the <c>Log</c> event. <see cref="McpProtocolException"/> is sent as a JSON-RPC error instead.
        /// </summary>
        public bool IncludeToolExceptionMessages
        {
            get => _Endpoint.IncludeToolExceptionMessages;
            set => _Endpoint.IncludeToolExceptionMessages = value;
        }

        /// <summary>
        /// Gets or sets the newest protocol revision the server agrees to during an <c>initialize</c>
        /// handshake. A client that requests a newer revision, or a stateless-era revision such as
        /// <c>2026-07-28</c> (which defines no <c>initialize</c> request and no sessions), is answered with
        /// this value, following the MCP rule that a server responds with a version it supports.
        /// Default is <see cref="McpProtocol.NewestHandshakeProtocolVersion"/> (currently <c>2025-11-25</c>).
        /// Lower it to pin clients to an older revision. Setting null restores the default.
        /// Set this before the server starts accepting requests; it is not synchronized with in-flight
        /// handshakes.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the value is not a supported handshake-era revision (for example <c>2026-07-28</c> or an unknown version).</exception>
        public string MaximumHandshakeProtocolVersion
        {
            get => _Endpoint.MaximumHandshakeProtocolVersion;
            set
            {
                string version = value ?? McpProtocol.NewestHandshakeProtocolVersion;
                if (!McpProtocol.IsHandshakeVersion(version))
                {
                    throw new ArgumentException(
                        $"'{version}' is not a supported handshake-era MCP protocol revision. Supported handshake-era revisions: {String.Join(", ", McpProtocol.SupportedVersions.Where(info => info.Era == McpProtocolEra.Handshake).Select(info => info.Version))}.",
                        nameof(value));
                }

                _Endpoint.MaximumHandshakeProtocolVersion = version;
            }
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
        /// Gets or sets optional natural-language instructions describing how to use the server, returned in the
        /// <c>initialize</c> result and by <c>server/discover</c> (2026-07-28). Null (the default) omits them.
        /// </summary>
        public string? ServerInstructions
        {
            get => _Endpoint.ServerInstructions;
            set => _Endpoint.ServerInstructions = value;
        }

        /// <summary>
        /// Gets or sets how many items one page of <c>tools/list</c>, <c>resources/list</c>,
        /// <c>resources/templates/list</c>, or <c>prompts/list</c> returns before a <c>nextCursor</c> is issued.
        /// Default is 100. Minimum is 1.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than 1.</exception>
        public int PageSize
        {
            get => _Endpoint.PageSize;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "PageSize must be at least 1.");
                _Endpoint.PageSize = value;
            }
        }

        /// <summary>
        /// Gets or sets how often the server pings each client that completed <c>initialize</c>, in milliseconds, to
        /// check that the connection is healthy (MCP ping utility); a ping that is not answered within
        /// <see cref="PingTimeoutMs"/> is logged. Default is 30000. 0 disables pinging. Maximum is 3600000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 3600000.</exception>
        public int PingIntervalMs
        {
            get => _Endpoint.PingIntervalMs;
            set
            {
                if (value < 0 || value > 3600000) throw new ArgumentOutOfRangeException(nameof(value), "PingIntervalMs must be between 0 and 3600000.");
                _Endpoint.PingIntervalMs = value;
            }
        }

        /// <summary>
        /// Gets or sets how long the server waits for a client to answer its ping, in milliseconds. Default is 10000.
        /// Minimum is 100; maximum is 600000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 100 to 600000.</exception>
        public int PingTimeoutMs
        {
            get => _Endpoint.PingTimeoutMs;
            set
            {
                if (value < 100 || value > 600000) throw new ArgumentOutOfRangeException(nameof(value), "PingTimeoutMs must be between 100 and 600000.");
                _Endpoint.PingTimeoutMs = value;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpWebsocketsServer"/> class.
        /// </summary>
        /// <param name="hostname">The hostname to listen on. Use <c>*</c> or <c>+</c> for all interfaces (requires admin or root privileges). A loopback name (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) serves loopback clients only; see <see cref="RestrictToLoopbackClients"/>.</param>
        /// <param name="port">The port number to listen on. Must be between 0 and 65535.</param>
        /// <param name="path">The URL path for WebSocket connections. Default is "/mcp".</param>
        /// <param name="includeDiagnosticTools">
        /// True to also publish the diagnostic tools <c>echo</c> and <c>getTime</c> in <c>tools/list</c>.
        /// Default is false, so the server publishes only the tools the application registers. The MCP protocol
        /// methods (<c>initialize</c>, <c>ping</c>, <c>tools/*</c>, <c>resources/*</c>, <c>prompts/*</c>, and so on)
        /// are always registered regardless of this value.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the hostname is null or empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the port is invalid.</exception>
        public McpWebsocketsServer(string hostname, int port, string path = "/mcp", bool includeDiagnosticTools = false)
        {
            if (String.IsNullOrEmpty(hostname)) throw new ArgumentNullException(nameof(hostname));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Hostname = hostname;
            _Port = port;
            _RestrictToLoopbackClients = LoopbackAddresses.IsLoopbackHostname(hostname);
            _Path = String.IsNullOrEmpty(path) ? "/mcp" : path;
            _Clients = new ConcurrentDictionary<string, ClientConnection>();
            _Methods = new Dictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>>();
            _Endpoint = new McpEndpoint("Voltaic.Mcp.WebSocketsServer");
            _Endpoint.ErrorLog = LogMessage;
            // Registration changes are announced to initialized clients (notifications/{kind}/list_changed).
            _Endpoint.ListChanged = kind =>
            {
                if (!_Endpoint.SupportsListChangedNotifications) return;
                _ = McpServerNotifications.ListChangedAsync(Sessions(), "notifications/" + kind + "/list_changed", CancellationToken.None);
            };
            _Processor = new McpMessageProcessor(_Endpoint, _Methods, LogMessage)
            {
                RequestReceived = (request, session) =>
                {
                    if (session.Owner is ClientConnection client) RaiseRequestReceived(new ServerPendingRequest(request.Id, client, request));
                },
                ResponseProduced = (request, response, session) =>
                {
                    if (session.Owner is ClientConnection client && request != null) RaiseResponseSent(new ServerPendingRequest(request.Id, client, request), response);
                }
            };

            RegisterProtocolMethods();
            if (includeDiagnosticTools) RegisterDiagnosticTools();
        }

        /// <summary>
        /// Registers a custom RPC method with the specified synchronous handler.
        /// The handler is wrapped internally to support async invocation.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The function that handles the method invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<RpcParameters?, object> handler)
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
        public void RegisterMethod(string name, Func<RpcParameters?, Task<object>> handler)
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
        public void RegisterMethod(string name, Func<RpcParameters?, CancellationToken, Task<object>> handler)
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
        public void RegisterTool(string name, string description, object inputSchema, Func<RpcParameters?, object> handler)
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
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<RpcParameters?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a synchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<RpcParameters?, object> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, (args, _) => Task.FromResult(handler(args)));
        }

        /// <summary>
        /// Registers a tool with metadata for MCP tool discovery using an asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<RpcParameters?, Task<object>> handler)
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
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<RpcParameters?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using an asynchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<RpcParameters?, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, (args, _) => handler(args));
        }

        /// <summary>
        /// Registers a tool with metadata for MCP tool discovery using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<RpcParameters?, CancellationToken, Task<object>> handler)
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
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<RpcParameters?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<RpcParameters?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, handler);
        }

        /// <summary>
        /// Removes a previously registered tool so it no longer appears in <c>tools/list</c> and
        /// <c>tools/call</c> for it returns a "not found" error. Clients are not notified automatically;
        /// call <see cref="NotifyToolsChangedAsync"/> afterwards to send <c>notifications/tools/list_changed</c>.
        /// Thread-safe.
        /// </summary>
        /// <param name="name">The tool name. Matched case-sensitively. Must not be null or empty.</param>
        /// <returns>True if a tool with that name was registered and has been removed; false if no such tool existed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when name is null or empty.</exception>
        public bool UnregisterTool(string name)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            return _Endpoint.UnregisterTool(name);
        }

        /// <summary>
        /// Registers a custom RPC method whose handler additionally receives the authenticated caller for
        /// the current request via the ambient <see cref="RpcCallContext.Current"/>. The base WebSocket
        /// transport performs no authentication, so the context is null unless populated by the host.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation with caller context and cancellation support.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            RegisterMethod(name, (RpcParameters? args, CancellationToken token) => handler(args, RpcCallContext.Current, token));
        }

        /// <summary>
        /// Registers a tool whose handler additionally receives the authenticated caller for the current
        /// request via the ambient <see cref="RpcCallContext.Current"/>.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler receiving caller context and cancellation support.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata whose handler additionally receives the
        /// authenticated caller for the current request.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="outputSchema">Output JSON schema.</param>
        /// <param name="handler">Tool handler receiving caller context and cancellation support.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition whose handler additionally receives the
        /// authenticated caller for the current request.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler receiving caller context and cancellation support.</param>
        /// <exception cref="ArgumentNullException">Thrown when handler is null.</exception>
        public void RegisterTool(ToolDefinition definition, Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterTool(definition, (RpcParameters? args, CancellationToken token) => handler(args, RpcCallContext.Current, token));
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
        public void RegisterPrompt(string name, string description, IEnumerable<McpPromptArgument>? arguments, Func<RpcParameters?, McpGetPromptResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterPrompt(CreatePrompt(name, description, arguments), (args, _) => Task.FromResult(handler(args)));
        }

        /// <summary>
        /// Registers an MCP prompt with a cancellable handler.
        /// </summary>
        /// <param name="prompt">Prompt metadata.</param>
        /// <param name="handler">Prompt handler.</param>
        public void RegisterPrompt(McpPrompt prompt, Func<RpcParameters?, CancellationToken, Task<McpGetPromptResult>> handler)
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
        /// Asynchronously broadcasts a notification to connected clients that completed <c>initialize</c> (MCP sends
        /// nothing but pings before it). A client whose negotiated revision does not define <paramref name="method"/>
        /// is skipped, and the notification is reduced to what each client's revision defines.
        /// </summary>
        /// <param name="method">The name of the notification method.</param>
        /// <param name="parameters">The parameters to pass with the notification. Can be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task BroadcastNotificationAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            return McpServerNotifications.BroadcastAsync(Sessions(), method, parameters, token);
        }

        /// <summary>
        /// Notifies connected clients that completed <c>initialize</c> that the tool list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyToolsChangedAsync(CancellationToken token = default)
        {
            return McpServerNotifications.ListChangedAsync(Sessions(), "notifications/tools/list_changed", token);
        }

        /// <summary>
        /// Notifies connected clients that completed <c>initialize</c> that the resource list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyResourcesChangedAsync(CancellationToken token = default)
        {
            return McpServerNotifications.ListChangedAsync(Sessions(), "notifications/resources/list_changed", token);
        }

        /// <summary>
        /// Sends <c>notifications/resources/updated</c> to the clients subscribed to <paramref name="uri"/> with
        /// <c>resources/subscribe</c>.
        /// </summary>
        /// <param name="uri">Updated resource URI. Must not be null or empty.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="uri"/> is null or empty.</exception>
        public Task NotifyResourceUpdatedAsync(string uri, CancellationToken token = default)
        {
            return McpServerNotifications.ResourceUpdatedAsync(Sessions(), uri, token);
        }

        /// <summary>
        /// Notifies connected clients that completed <c>initialize</c> that the prompt list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyPromptsChangedAsync(CancellationToken token = default)
        {
            return McpServerNotifications.ListChangedAsync(Sessions(), "notifications/prompts/list_changed", token);
        }

        /// <summary>
        /// Sends <c>notifications/progress</c> for the in-flight request that carries <paramref name="progressToken"/>.
        /// Called from a tool handler, the handler's own request is used; otherwise the notification is sent only when
        /// exactly one active request on any connection carries the token (tokens are unique only per client), and
        /// nothing is sent for a cancelled or finished request. Prefer <see cref="McpToolCallContext.ReportProgressAsync"/>,
        /// which is always scoped to the calling request.
        /// </summary>
        /// <param name="progressToken">Progress token from the request's <c>_meta</c>. Must not be null.</param>
        /// <param name="progress">Current progress value. Must increase with every notification.</param>
        /// <param name="total">Optional total progress value.</param>
        /// <param name="message">Optional human-readable progress text.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="progressToken"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="progress"/> does not increase.</exception>
        public async Task NotifyProgressAsync(object progressToken, double progress, double? total = null, string? message = null, CancellationToken token = default)
        {
            await McpServerNotifications.ProgressAsync(Sessions(), progressToken, progress, total, message, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Obsolete and does nothing. <c>notifications/cancelled</c> may only reference a request the sender issued, and
        /// an MCP server issues no requests to clients, so the server has nothing it may cancel.
        /// </summary>
        /// <param name="requestId">Ignored.</param>
        /// <param name="reason">Ignored.</param>
        /// <param name="token">Ignored.</param>
        /// <returns>A completed task.</returns>
        [Obsolete("A server may only cancel requests it sent, and MCP servers send none. This method does nothing.")]
        public Task NotifyCancelledAsync(object requestId, string? reason = null, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends a <c>notifications/message</c> log entry to connected clients that completed <c>initialize</c>, when
        /// <paramref name="level"/> meets the level each set with <c>logging/setLevel</c> (everything when none was set).
        /// To log about a specific tool call, use <see cref="McpToolCallContext.LogAsync"/>.
        /// </summary>
        /// <param name="level">One of debug, info, notice, warning, error, critical, alert, emergency.</param>
        /// <param name="data">JSON-serializable log data.</param>
        /// <param name="logger">Optional logger name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="level"/> is not an MCP log level.</exception>
        public Task NotifyLogMessageAsync(string level, object? data, string? logger = null, CancellationToken token = default)
        {
            return McpServerNotifications.LogAsync(Sessions(), level, data, logger, token);
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
        /// Registers the MCP protocol methods: <c>initialize</c>, <c>ping</c>, <c>tools/*</c>,
        /// <c>resources/*</c>, <c>prompts/*</c>, <c>completion/complete</c>, <c>logging/setLevel</c>, and the
        /// client notification handlers. Called once by the constructor, regardless of
        /// <c>includeDiagnosticTools</c>. A derived class that overrides this method must call the base
        /// implementation, or the server will not speak MCP.
        /// </summary>
        protected virtual void RegisterProtocolMethods()
        {
            RegisterMethod("initialize", (args) => _Endpoint.Initialize(args));
            RegisterMethod("ping", (args) => _Endpoint.Ping(args));
            RegisterMethod("server/discover", (args) => _Endpoint.Discover(args));
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
        }

        /// <summary>
        /// Registers the optional diagnostic tools <c>echo</c> and <c>getTime</c>. Called by the constructor
        /// only when <c>includeDiagnosticTools</c> is true. Derived classes may override this to publish a
        /// different diagnostic set.
        /// </summary>
        protected virtual void RegisterDiagnosticTools()
        {
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
                    McpEchoArguments? echo = args?.Deserialize<McpEchoArguments>();
                    return echo?.Message ?? "empty";
                });

            RegisterTool("getTime",
                "Returns the current UTC time in ISO format",
                new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                },
                (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
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
                if (!HttpAccessGuard.IsRemoteAllowed(context.Request, _RestrictToLoopbackClients))
                {
                    LogMessage($"Rejected remote client {context.Request.RemoteEndPoint}: the server accepts loopback clients only");
                    await HttpAccessGuard.RejectAsync(context, 403, "Remote connections are not allowed.", token).ConfigureAwait(false);
                    return;
                }

                string? origin = HttpAccessGuard.GetOrigin(context.Request);
                if (!_OriginPolicy.IsAllowed(origin))
                {
                    LogMessage($"Rejected WebSocket upgrade from disallowed origin '{origin}'");
                    await HttpAccessGuard.RejectAsync(context, 403, "Origin not allowed.", token).ConfigureAwait(false);
                    return;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    LogMessage($"Rejected non-WebSocket request from {context.Request.RemoteEndPoint}");
                    return;
                }

                RpcCallContext? caller = null;
                Func<HttpListenerRequest, Task<AuthenticationResult>>? authenticationHandler = _AuthenticationHandler;
                if (authenticationHandler != null)
                {
                    AuthenticationResult authResult = await authenticationHandler(context.Request).ConfigureAwait(false);
                    if (!authResult.IsAuthenticated)
                    {
                        LogMessage($"Authentication failed for WebSocket upgrade from {context.Request.RemoteEndPoint}: {authResult.ErrorMessage ?? "no details"}");
                        await HttpAccessGuard.WriteAuthenticationFailureAsync(context, authResult, false, null, token).ConfigureAwait(false);
                        return;
                    }

                    caller = new RpcCallContext(authResult.Principal, authResult.Claims);
                }

                HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                WebSocket webSocket = webSocketContext.WebSocket;

                client = new ClientConnection(clientId, webSocket);
                client.MaxQueueSize = _MaxQueueSize;
                client.Caller = caller;
                ClientConnection connected = client;
                McpSessionState session = new McpSessionState { Owner = connected, CanPingClient = true };
                session.Push = (json, ct) => SendToClientAsync(connected, json, ct);
                client.ProtocolState = session;
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
                    (client.ProtocolState as McpSessionState)?.CancelAll();
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
            // Decodes across frames, so a multi-byte UTF-8 character split between reads is preserved.
            Decoder decoder = new UTF8Encoding(false).GetDecoder();

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
                            char[] chars = new char[decoder.GetCharCount(buffer, 0, result.Count, result.EndOfMessage)];
                            int decoded = decoder.GetChars(buffer, 0, result.Count, chars, 0, result.EndOfMessage);
                            messageBuilder.Append(chars, 0, decoded);

                            if (result.EndOfMessage)
                            {
                                string message = messageBuilder.ToString();
                                messageBuilder.Clear();

                                // The authenticated caller of the upgrade is ambient for every request on the socket.
                                using (RpcCallContext.Push(client.Caller))
                                {
                                    LogMessage($"Received from {client.SessionId}: {message}");
                                    McpSessionState session = client.ProtocolState as McpSessionState ?? new McpSessionState { Owner = client };
                                    await _Processor.ProcessAsync(message, session, (json, ct) => SendToClientAsync(client, json, ct), token).ConfigureAwait(false);
                                }
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

        private async Task SendToClientAsync(ClientConnection client, string message, CancellationToken token = default)
        {
            try
            {
                if (client.WebSocket != null && client.WebSocket.State == WebSocketState.Open)
                {
                    byte[] buffer = Encoding.UTF8.GetBytes(message);

                    // A WebSocket allows one send at a time.
                    await client.WriteLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await client.WebSocket.SendAsync(
                            new ArraySegment<byte>(buffer),
                            WebSocketMessageType.Text,
                            true,
                            token).ConfigureAwait(false);
                    }
                    finally
                    {
                        client.WriteLock.Release();
                    }

                    LogMessage($"Sent to {client.SessionId}: {message}");
                }
            }
            catch
            {
                // Client might be disconnected
            }
        }

        private IEnumerable<McpSessionState> Sessions()
        {
            return _Clients.Values.Select(client => client.ProtocolState).OfType<McpSessionState>().ToList();
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
