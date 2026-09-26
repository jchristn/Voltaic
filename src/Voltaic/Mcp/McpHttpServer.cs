namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

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
        /// Gets or sets how long a stateless (<c>2026-07-28</c>) request may run before its response turns into an SSE
        /// stream that carries a keep-alive comment every interval. A keep-alive that cannot be written means the client
        /// closed the stream, which that revision defines as cancellation, so the handler's token is cancelled. Default
        /// is 2000. Minimum is 0 (no keep-alives, so a disconnect is noticed only when the response is written); maximum
        /// is 600000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 600000.</exception>
        public int ResponseKeepAliveMs
        {
            get => _ResponseKeepAliveMs;
            set
            {
                if (value < 0 || value > 600000) throw new ArgumentOutOfRangeException(nameof(value), "Response keep-alive interval must be between 0 and 600000 ms.");
                _ResponseKeepAliveMs = value;
            }
        }

        /// <summary>
        /// Gets or sets how many recent events of each <c>GET</c> SSE stream on the MCP endpoint are kept for replay.
        /// Every event on that stream carries an ID of the form <c>{streamId}-{sequence}</c>, and each stream starts
        /// with a priming event (an ID and an empty <c>data</c> field) as MCP 2025-11-25 recommends. A client that
        /// reconnects with <c>Last-Event-ID</c> receives the retained events after that ID, on the same stream; IDs from
        /// another session or an unknown stream start a new stream. The last 8 streams of each session are kept.
        /// Default is 100. Minimum is 0 (IDs are still sent, but nothing is replayed); maximum is 10000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 10000.</exception>
        public int SseReplayBufferSize
        {
            get => _SseReplayBufferSize;
            set
            {
                if (value < 0 || value > 10000) throw new ArgumentOutOfRangeException(nameof(value), "SSE replay buffer size must be between 0 and 10000.");
                _SseReplayBufferSize = value;
            }
        }

        /// <summary>
        /// Gets or sets the reconnection delay in milliseconds sent in the <c>retry</c> field when a <c>GET</c> SSE stream
        /// opens on the MCP endpoint. Clients wait this long before reconnecting after the stream closes.
        /// Default is 1000. Minimum is 0; maximum is 600000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 600000.</exception>
        public int SseRetryIntervalMs
        {
            get => _SseRetryIntervalMs;
            set
            {
                if (value < 0 || value > 600000) throw new ArgumentOutOfRangeException(nameof(value), "SSE retry interval must be between 0 and 600000 ms.");
                _SseRetryIntervalMs = value;
            }
        }

        /// <summary>
        /// Obsolete; always true. Since v2.1.4 handshake-era sessions are created only by a successful
        /// <c>initialize</c>, as the MCP Streamable HTTP specification requires, and this setting has no effect:
        /// <list type="bullet">
        /// <item><description>A session is created, <see cref="ClientConnected"/> is raised, and the <c>MCP-Session-Id</c> header is returned only when <c>initialize</c> succeeds. A rejected <c>initialize</c> creates nothing.</description></item>
        /// <item><description>A POST to the MCP endpoint without <c>MCP-Session-Id</c> is rejected with HTTP 400, except <c>initialize</c>.</description></item>
        /// <item><description>A POST to the JSON-RPC endpoint without <c>MCP-Session-Id</c> is served on a temporary connection that is never registered, and no session header is returned.</description></item>
        /// </list>
        /// Session IDs are always generated by the server, a request carrying an <c>MCP-Session-Id</c> the server does
        /// not know (never issued, expired, or terminated) is rejected with HTTP 404, and a failed request never
        /// creates a session. The stateless <c>2026-07-28</c> revision never uses sessions. Clients that opened a
        /// session with <c>ping</c> (<see cref="McpHttpClient"/> from Voltaic 2.0.0 and earlier) must be upgraded.
        /// Setting this property is ignored.
        /// </summary>
        [Obsolete("Sessions are created only by initialize, as the MCP specification requires. This property always returns true and setting it has no effect.")]
        public bool RequireInitializedSessions
        {
            get => true;
            set { }
        }

        /// <summary>
        /// Gets or sets whether CORS (Cross-Origin Resource Sharing) headers are sent.
        /// When enabled, a request from a browser origin that <see cref="OriginPolicy"/> allows receives
        /// <c>Access-Control-Allow-Origin</c> set to that origin (never <c>*</c>), <c>Vary: Origin</c>, and the
        /// <see cref="CorsHeaders"/>. Requests without an <c>Origin</c> header receive no CORS headers.
        /// This setting never widens access: origins the policy rejects get HTTP 403 whether CORS is on or off.
        /// Default is true.
        /// </summary>
        public bool EnableCors
        {
            get => _EnableCors;
            set => _EnableCors = value;
        }

        /// <summary>
        /// Additional CORS headers sent to allowed browser origins when <see cref="EnableCors"/> is true.
        /// Defaults: <c>Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS</c>, an explicit
        /// <c>Access-Control-Allow-Headers</c> list (including <c>Authorization</c> and the MCP headers; on a preflight
        /// that names the headers it wants, those names are allowed instead, so custom headers such as
        /// <c>X-API-Key</c> or <c>Mcp-Param-*</c> work),
        /// <c>Access-Control-Expose-Headers: Mcp-Session-Id, MCP-Protocol-Version, WWW-Authenticate</c> (so browser
        /// clients can read the session and the OAuth challenge), and
        /// <c>Access-Control-Max-Age: 86400</c>. An <c>Access-Control-Allow-Origin</c> or <c>Vary</c> entry is
        /// ignored, because the allowed origin is always echoed; use <see cref="OriginPolicy"/> to choose origins.
        /// Setting null stores an empty dictionary.
        /// </summary>
        public Dictionary<string, string> CorsHeaders
        {
            get => _CorsHeaders;
            set => _CorsHeaders = (value != null ? value : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets or sets the policy that decides which browser origins may call the server. The MCP
        /// specification requires servers to validate <c>Origin</c> on every request to prevent DNS rebinding
        /// and cross-site calls. Requests whose origin the policy rejects receive HTTP 403 with no CORS headers,
        /// before routing, preflight handling, or authentication. The default allows requests without an
        /// <c>Origin</c> header and loopback origins; add browser origins to <see cref="OriginPolicy.AllowedOrigins"/>.
        /// Setting null restores the default policy.
        /// </summary>
        public OriginPolicy OriginPolicy
        {
            get => _OriginPolicy;
            set => _OriginPolicy = value ?? new OriginPolicy();
        }

        /// <summary>
        /// Gets or sets the OAuth 2.0 Protected Resource Metadata (RFC 9728) document the server publishes.
        /// The MCP authorization specification requires servers that use OAuth to publish it so clients can find
        /// the authorization server. When set, a GET to <see cref="McpProtocol.ProtectedResourceMetadataPath"/>
        /// (<c>/.well-known/oauth-protected-resource</c>) or to that path followed by the MCP endpoint path (for
        /// example <c>/.well-known/oauth-protected-resource/mcp</c>) returns the document as JSON, without calling
        /// <see cref="AuthenticationHandler"/>; the loopback and origin checks still apply. Point clients at it
        /// from a 401 with <see cref="AuthenticationResult.BearerChallenge(string?, string?, string?, string?)"/>. Default is null, which serves
        /// nothing at those paths (HTTP 404).
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the value has an empty <see cref="McpProtectedResourceMetadata.Resource"/> or no <see cref="McpProtectedResourceMetadata.AuthorizationServers"/>.</exception>
        public McpProtectedResourceMetadata? ProtectedResourceMetadata
        {
            get => _ProtectedResourceMetadata;
            set
            {
                if (value != null)
                {
                    if (String.IsNullOrWhiteSpace(value.Resource))
                        throw new ArgumentException("Protected resource metadata requires a resource identifier.", nameof(value));
                    if (value.AuthorizationServers.Count == 0 || value.AuthorizationServers.Any(String.IsNullOrWhiteSpace))
                        throw new ArgumentException("Protected resource metadata requires at least one authorization server, and entries must not be empty.", nameof(value));
                }

                _ProtectedResourceMetadata = value;
            }
        }

        /// <summary>
        /// Gets or sets whether only loopback clients are served. When true, a request whose remote address is
        /// not loopback (<c>127.0.0.0/8</c>, <c>::1</c>, or an IPv4-mapped loopback address) receives HTTP 403.
        /// Default is true when the server is constructed with a loopback host name (<c>localhost</c>,
        /// <c>127.x.x.x</c>, or <c>::1</c>) and false otherwise. This matters on Windows, where
        /// <see cref="HttpListener"/> (http.sys) listens on every interface for a <c>localhost</c> prefix and
        /// routes by the <c>Host</c> header, which a remote client can spoof. Bind to <c>+</c>, <c>*</c>, or a
        /// specific address to serve remote clients, or set this to false explicitly.
        /// </summary>
        public bool RestrictToLoopbackClients
        {
            get => _RestrictToLoopbackClients;
            set => _RestrictToLoopbackClients = value;
        }

        /// <summary>
        /// Gets the cancellation token source for the server.
        /// </summary>
        public CancellationTokenSource? TokenSource
        {
            get => _TokenSource;
        }

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
        /// Default is "Voltaic.Mcp.HttpServer".
        /// </summary>
        public string ServerName
        {
            get => _Endpoint.ServerName;
            set => _Endpoint.ServerName = value ?? "Voltaic.Mcp.HttpServer";
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
        /// Gets or sets whether the server advertises the <c>io.modelcontextprotocol/tasks</c>
        /// extension in its capabilities (2026-07-28+). Default is false. Voltaic does not implement the
        /// <c>tasks/*</c> methods: set this to true only when the application registers them itself with
        /// <c>RegisterMethod</c>, because a server must not advertise a capability it does not support.
        /// </summary>
        public bool AdvertiseTasksExtension
        {
            get => _Endpoint.AdvertiseTasksExtension;
            set => _Endpoint.AdvertiseTasksExtension = value;
        }

        /// <summary>
        /// Gets or sets the cache lifetime in milliseconds applied to <c>tools/list</c>,
        /// <c>resources/list</c>, <c>resources/templates/list</c>, and <c>prompts/list</c> results
        /// (2026-07-28+). Null (the default) sends 0 (do not cache). Minimum is 0.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a negative value.</exception>
        public long? ListCacheTtlMs
        {
            get => _Endpoint.ListCacheTtlMs;
            set
            {
                if (value.HasValue && value.Value < 0) throw new ArgumentOutOfRangeException(nameof(value), "ttlMs must be zero or greater.");
                _Endpoint.ListCacheTtlMs = value;
            }
        }

        /// <summary>
        /// Gets or sets the cache scope (for example <c>public</c> or <c>private</c>) applied to
        /// list results (2026-07-28+). Null (the default) omits caching guidance.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when set to anything other than null, "public", or "private".</exception>
        public string? ListCacheScope
        {
            get => _Endpoint.ListCacheScope;
            set
            {
                if (value != null && value != "public" && value != "private") throw new ArgumentException("cacheScope must be \"public\" or \"private\".", nameof(value));
                _Endpoint.ListCacheScope = value;
            }
        }

        /// <summary>
        /// Gets or sets an optional asynchronous authentication handler.
        /// When set, incoming HTTP requests that pass the loopback check (<see cref="RestrictToLoopbackClients"/>)
        /// and origin check (<see cref="OriginPolicy"/>) are passed through this handler before processing.
        /// If the handler returns an <see cref="AuthenticationResult"/> with <see cref="AuthenticationResult.IsAuthenticated"/> set to false,
        /// the server immediately returns the result's status code, <see cref="AuthenticationResult.Headers"/>
        /// (for example <c>WWW-Authenticate</c>), and error message without processing the request.
        /// When null (the default), no authentication is performed; requests are still subject to the loopback
        /// and origin checks.
        /// The handler receives the full <see cref="HttpListenerRequest"/> so it can inspect headers, query strings,
        /// client certificates, or any other request property needed for authentication.
        /// The handler is not called for the health check endpoint (<c>/</c>), CORS preflight (<c>OPTIONS</c>), and
        /// <see cref="ProtectedResourceMetadata"/> at <see cref="McpProtocol.ProtectedResourceMetadataPath"/>, so
        /// clients can check connectivity and discover the authorization server. Every other request, including
        /// <c>ping</c>, must authenticate, as MCP authorization requires (since v2.1.4; earlier versions answered
        /// a <c>ping</c> that failed authentication).
        /// Authenticated requests run through exactly the same MCP protocol pipeline as unauthenticated
        /// ones (version resolution, stateless 2026-07-28 routing, batching rules, and session tracking),
        /// so setting a handler never changes protocol behavior.
        /// </summary>
        public Func<HttpListenerRequest, Task<AuthenticationResult>>? AuthenticationHandler
        {
            get => _AuthenticationHandler;
            set => _AuthenticationHandler = value;
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
        private readonly string _RpcPath;
        private readonly string _EventsPath;
        private readonly string _McpPath;
        private HttpListener? _Listener;
        private CancellationTokenSource? _TokenSource;
        private readonly ConcurrentDictionary<string, ClientConnection> _Sessions;
        private readonly ConcurrentDictionary<string, byte> _TerminatedSessions;
        private readonly ConcurrentDictionary<string, string> _SessionVersions = new ConcurrentDictionary<string, string>();
        private readonly Dictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>> _Methods;
        private readonly McpEndpoint _Endpoint;
        private readonly McpMessageProcessor _Processor;
        private int _ResponseKeepAliveMs = 2000;
        private Task? _CleanupTask;
        private int _SessionTimeoutSeconds = 300; // 5 minutes
        private int _MaxQueueSize = 100;
        private int _SseReplayBufferSize = 100;
        private int _SseRetryIntervalMs = 1000;
        private readonly ConcurrentDictionary<string, SseSessionStreams> _SseStreams = new ConcurrentDictionary<string, SseSessionStreams>(StringComparer.Ordinal);
        private const int MaxSseStreamsPerSession = 8;
        private bool _EnableCors = true;
        private Dictionary<string, string> _CorsHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS" },
            { "Access-Control-Allow-Headers", HttpAccessGuard.DefaultAllowHeaders },
            { "Access-Control-Expose-Headers", "Mcp-Session-Id, MCP-Protocol-Version, WWW-Authenticate" },
            { "Access-Control-Max-Age", "86400" }
        };
        private OriginPolicy _OriginPolicy = new OriginPolicy();
        private bool _RestrictToLoopbackClients;
        private McpProtectedResourceMetadata? _ProtectedResourceMetadata;
        private Func<HttpListenerRequest, Task<AuthenticationResult>>? _AuthenticationHandler;
        private volatile bool _IsStopping = false;
        private bool _IsDisposed = false;
        private static readonly TimeSpan _SseHeartbeatInterval = TimeSpan.FromSeconds(30);
        private static readonly byte[] _SseConnectedPrelude = Encoding.UTF8.GetBytes(": connected\n\n");
        private static readonly byte[] _SseKeepAliveComment = Encoding.UTF8.GetBytes(": keep-alive\n\n");

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHttpServer"/> class.
        /// </summary>
        /// <param name="hostname">The hostname to listen on. Use <c>*</c> or <c>+</c> for all interfaces (requires admin or root privileges). A loopback name (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) serves loopback clients only; see <see cref="RestrictToLoopbackClients"/>.</param>
        /// <param name="port">The port number to listen on. Must be between 0 and 65535.</param>
        /// <param name="rpcPath">The URL path for JSON-RPC requests. Default is "/rpc".</param>
        /// <param name="eventsPath">The URL path for Server-Sent Events connections. Default is "/events".</param>
        /// <param name="includeDiagnosticTools">
        /// True to also publish the diagnostic tools <c>echo</c> and <c>getTime</c> in <c>tools/list</c>.
        /// Default is false, so the server publishes only the tools the application registers. The MCP protocol
        /// methods (<c>initialize</c>, <c>ping</c>, <c>tools/*</c>, <c>resources/*</c>, <c>prompts/*</c>, and so on)
        /// are always registered regardless of this value.
        /// </param>
        /// <param name="mcpPath">The URL path for the MCP Streamable HTTP endpoint. Default is "/mcp". Set to null to disable.</param>
        /// <exception cref="ArgumentNullException">Thrown when the hostname is null or empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the port is invalid.</exception>
        public McpHttpServer(string hostname, int port, string rpcPath = "/rpc", string eventsPath = "/events", bool includeDiagnosticTools = false, string? mcpPath = "/mcp")
        {
            if (String.IsNullOrEmpty(hostname)) throw new ArgumentNullException(nameof(hostname));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Hostname = hostname;
            _Port = port;
            _RestrictToLoopbackClients = LoopbackAddresses.IsLoopbackHostname(hostname);
            _RpcPath = String.IsNullOrEmpty(rpcPath) ? "/rpc" : rpcPath;
            _EventsPath = String.IsNullOrEmpty(eventsPath) ? "/events" : eventsPath;
            _McpPath = mcpPath ?? "";
            _Sessions = new ConcurrentDictionary<string, ClientConnection>();
            _TerminatedSessions = new ConcurrentDictionary<string, byte>();
            _Methods = new Dictionary<string, Func<RpcParameters?, CancellationToken, Task<object>>>();
            _Endpoint = new McpEndpoint("Voltaic.Mcp.HttpServer");
            _Endpoint.ErrorLog = LogMessage;
            _Processor = new McpMessageProcessor(_Endpoint, _Methods, LogMessage)
            {
                RequestReceived = (request, session) =>
                {
                    if (session.Owner is ClientConnection client) RaiseRequestReceived(new ServerPendingRequest(request.Id, client, request));
                },
                ResponseProduced = (request, response, session) =>
                {
                    if (session.Owner is ClientConnection client) RaiseResponseSent(client, request == null ? null : new ServerPendingRequest(request.Id, client, request), response);
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
        /// <remarks>
        /// When a request is served under the stateless 2026-07-28 revision, a result that derives from
        /// <see cref="McpResult"/> (for example <see cref="McpEmptyResult"/> or <see cref="McpToolCallResult"/>)
        /// receives the <c>resultType</c> that revision requires, and cacheable results receive
        /// <c>ttlMs</c> and <c>cacheScope</c>, unless the handler already set them. Any other result object is
        /// serialized unmodified, so a custom method that must serve stateless clients should return an
        /// <see cref="McpResult"/> subclass.
        /// </remarks>
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
        /// <remarks>
        /// When a request is served under the stateless 2026-07-28 revision, a result that derives from
        /// <see cref="McpResult"/> (for example <see cref="McpEmptyResult"/> or <see cref="McpToolCallResult"/>)
        /// receives the <c>resultType</c> that revision requires, and cacheable results receive
        /// <c>ttlMs</c> and <c>cacheScope</c>, unless the handler already set them. Any other result object is
        /// serialized unmodified, so a custom method that must serve stateless clients should return an
        /// <see cref="McpResult"/> subclass.
        /// </remarks>
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
        /// The cancellation token provided to the handler is the same token used by the server's request processing.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation with cancellation support.</param>
        /// <remarks>
        /// When a request is served under the stateless 2026-07-28 revision, a result that derives from
        /// <see cref="McpResult"/> (for example <see cref="McpEmptyResult"/> or <see cref="McpToolCallResult"/>)
        /// receives the <c>resultType</c> that revision requires, and cacheable results receive
        /// <c>ttlMs</c> and <c>cacheScope</c>, unless the handler already set them. Any other result object is
        /// serialized unmodified, so a custom method that must serve stateless clients should return an
        /// <see cref="McpResult"/> subclass.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<RpcParameters?, CancellationToken, Task<object>> handler)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Methods[name] = handler;
        }

        /// <summary>
        /// Registers a tool with metadata for MCP protocol tool discovery using a synchronous handler.
        /// The tool is listed by <c>tools/list</c> and invoked only through <c>tools/call</c>, which validates
        /// arguments against the input schema. It is not callable as a bare JSON-RPC method.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="handler">The function that handles the tool invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
        public void RegisterTool(string name, string description, object inputSchema, Func<RpcParameters?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using a synchronous handler.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="outputSchema">The JSON schema object defining structured output, or null.</param>
        /// <param name="handler">The function that handles the tool invocation.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<RpcParameters?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a synchronous handler.
        /// </summary>
        /// <param name="definition">The tool definition.</param>
        /// <param name="handler">The function that handles the tool invocation.</param>
        public void RegisterTool(ToolDefinition definition, Func<RpcParameters?, object> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Endpoint.RegisterTool(definition, (args, _) => Task.FromResult(handler(args)));
        }

        /// <summary>
        /// Registers a tool with metadata for MCP protocol tool discovery using an asynchronous handler.
        /// The tool is listed by <c>tools/list</c> and invoked only through <c>tools/call</c>, which validates
        /// arguments against the input schema. It is not callable as a bare JSON-RPC method.
        /// Use this overload when the handler needs to perform asynchronous operations such as
        /// database queries, HTTP calls, or file I/O.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="handler">The async function that handles the tool invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
        public void RegisterTool(string name, string description, object inputSchema, Func<RpcParameters?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using an asynchronous handler.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="outputSchema">The JSON schema object defining structured output, or null.</param>
        /// <param name="handler">The async function that handles the tool invocation.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<RpcParameters?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using an asynchronous handler.
        /// </summary>
        /// <param name="definition">The tool definition.</param>
        /// <param name="handler">The async function that handles the tool invocation.</param>
        public void RegisterTool(ToolDefinition definition, Func<RpcParameters?, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Endpoint.RegisterTool(definition, (args, _) => handler(args));
        }

        /// <summary>
        /// Registers a tool with metadata for MCP protocol tool discovery using an asynchronous handler that accepts a cancellation token.
        /// The tool is listed by <c>tools/list</c> and invoked only through <c>tools/call</c>, which validates
        /// arguments against the input schema. It is not callable as a bare JSON-RPC method.
        /// Use this overload when the handler needs to perform cancellable asynchronous operations.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="handler">The async function that handles the tool invocation with cancellation support.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
        public void RegisterTool(string name, string description, object inputSchema, Func<RpcParameters?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="outputSchema">The JSON schema object defining structured output, or null.</param>
        /// <param name="handler">The async function that handles the tool invocation with cancellation support.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<RpcParameters?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="definition">The tool definition.</param>
        /// <param name="handler">The async function that handles the tool invocation with cancellation support.</param>
        public void RegisterTool(ToolDefinition definition, Func<RpcParameters?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Endpoint.RegisterTool(definition, handler);
        }

        /// <summary>
        /// Removes a previously registered tool so it no longer appears in <c>tools/list</c> and
        /// <c>tools/call</c> for it returns a "not found" error. Clients are not notified automatically;
        /// call <see cref="NotifyToolsChanged"/> afterwards to send <c>notifications/tools/list_changed</c>.
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
        /// the current request. The context is the ambient <see cref="RpcCallContext.Current"/> captured at
        /// invocation time; it is null when the request was not authenticated (for example, when no
        /// <see cref="AuthenticationHandler"/> is configured).
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation with caller context and cancellation support.</param>
        /// <remarks>
        /// When a request is served under the stateless 2026-07-28 revision, a result that derives from
        /// <see cref="McpResult"/> (for example <see cref="McpEmptyResult"/> or <see cref="McpToolCallResult"/>)
        /// receives the <c>resultType</c> that revision requires, and cacheable results receive
        /// <c>ttlMs</c> and <c>cacheScope</c>, unless the handler already set them. Any other result object is
        /// serialized unmodified, so a custom method that must serve stateless clients should return an
        /// <see cref="McpResult"/> subclass.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            RegisterMethod(name, (RpcParameters? args, CancellationToken token) => handler(args, RpcCallContext.Current, token));
        }

        /// <summary>
        /// Registers a tool whose handler additionally receives the authenticated caller for the current
        /// request. The context is the ambient <see cref="RpcCallContext.Current"/> captured at invocation
        /// time; it is null when the request was not authenticated.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="handler">The async function that handles the tool invocation with caller context and cancellation support.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
        public void RegisterTool(string name, string description, object inputSchema, Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata whose handler additionally receives the
        /// authenticated caller for the current request.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="outputSchema">The JSON schema object defining structured output, or null.</param>
        /// <param name="handler">The async function that handles the tool invocation with caller context and cancellation support.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition whose handler additionally receives the
        /// authenticated caller for the current request.
        /// </summary>
        /// <param name="definition">The tool definition.</param>
        /// <param name="handler">The async function that handles the tool invocation with caller context and cancellation support.</param>
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
                _Listener.Prefixes.Add($"http://{_Hostname}:{_Port}/");
                _Listener.Prefixes.Add($"http://{_Hostname}:{_Port}{_RpcPath}/");
                _Listener.Prefixes.Add($"http://{_Hostname}:{_Port}{_EventsPath}/");
                if (!String.IsNullOrEmpty(_McpPath))
                    _Listener.Prefixes.Add($"http://{_Hostname}:{_Port}{_McpPath}/");
                _Listener.Start();
                _TokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);

                // Start session cleanup task
                _CleanupTask = Task.Run(() => CleanupSessionsLoop(_TokenSource.Token));

                LogMessage($"HTTP server started on port {_Port}");
                LogMessage($"RPC endpoint: {_RpcPath}");
                LogMessage($"SSE endpoint: {_EventsPath}");
                if (!String.IsNullOrEmpty(_McpPath))
                    LogMessage($"MCP Streamable HTTP endpoint: {_McpPath}");

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
            if (_Sessions.TryGetValue(sessionId, out ClientConnection? connection))
            {
                JsonRpcRequest notification = new JsonRpcRequest
                {
                    Method = method,
                    Params = parameters
                };
                connection.Enqueue(notification);
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

            foreach (ClientConnection connection in _Sessions.Values)
            {
                connection.Enqueue(notification);
            }

            LogMessage($"Broadcast notification: {method}");
        }

        /// <summary>
        /// Notifies sessions that completed <c>initialize</c> that the tool list changed. Delivered on their GET stream.
        /// </summary>
        public void NotifyToolsChanged()
        {
            McpServerNotifications.ListChangedAsync(Sessions(), "notifications/tools/list_changed", CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Notifies sessions that completed <c>initialize</c> that the resource list changed.
        /// </summary>
        public void NotifyResourcesChanged()
        {
            McpServerNotifications.ListChangedAsync(Sessions(), "notifications/resources/list_changed", CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Sends <c>notifications/resources/updated</c> to the sessions subscribed to <paramref name="uri"/> with
        /// <c>resources/subscribe</c>.
        /// </summary>
        /// <param name="uri">Updated resource URI. Must not be null or empty.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="uri"/> is null or empty.</exception>
        public void NotifyResourceUpdated(string uri)
        {
            McpServerNotifications.ResourceUpdatedAsync(Sessions(), uri, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Notifies sessions that completed <c>initialize</c> that the prompt list changed.
        /// </summary>
        public void NotifyPromptsChanged()
        {
            McpServerNotifications.ListChangedAsync(Sessions(), "notifications/prompts/list_changed", CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Sends <c>notifications/progress</c> for the in-flight request of a session that carries
        /// <paramref name="progressToken"/>, on that request's response stream (the response becomes an SSE stream).
        /// Tool handlers can use <see cref="McpToolCallContext.ReportProgressAsync"/> instead.
        /// </summary>
        /// <param name="sessionId">Target session ID.</param>
        /// <param name="progressToken">Progress token from the request's <c>_meta</c>. Must not be null.</param>
        /// <param name="progress">Current progress value. Must increase with every notification.</param>
        /// <param name="total">Optional total progress value.</param>
        /// <param name="message">Optional human-readable progress text.</param>
        /// <returns>True when the session has an active request with that token and the notification was sent.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="progress"/> does not increase.</exception>
        public bool NotifyProgress(string sessionId, object progressToken, double progress, double? total = null, string? message = null)
        {
            if (!_Sessions.TryGetValue(sessionId, out ClientConnection? connection)) return false;
            return McpServerNotifications.ProgressAsync(new[] { StateOf(connection) }, progressToken, progress, total, message, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Obsolete and does nothing. <c>notifications/cancelled</c> may only reference a request the sender issued, and
        /// an MCP server issues no requests to clients, so the server has nothing it may cancel.
        /// </summary>
        /// <param name="sessionId">Ignored.</param>
        /// <param name="requestId">Ignored.</param>
        /// <param name="reason">Ignored.</param>
        /// <returns>Always false.</returns>
        [Obsolete("A server may only cancel requests it sent, and MCP servers send none. This method does nothing.")]
        public bool NotifyCancelled(string sessionId, object requestId, string? reason = null)
        {
            return false;
        }

        /// <summary>
        /// Queues a <c>notifications/message</c> log entry for a session when <paramref name="level"/> meets the level
        /// the session set with <c>logging/setLevel</c> (everything when none was set). To log about a specific tool
        /// call, use <see cref="McpToolCallContext.LogAsync"/>.
        /// </summary>
        /// <param name="sessionId">Target session ID.</param>
        /// <param name="level">One of debug, info, notice, warning, error, critical, alert, emergency.</param>
        /// <param name="data">JSON-serializable log data.</param>
        /// <param name="logger">Optional logger name.</param>
        /// <returns>True if the session was found.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="level"/> is not an MCP log level.</exception>
        public bool NotifyLogMessage(string sessionId, string level, object? data, string? logger = null)
        {
            if (!McpLogLevels.IsValid(level)) throw new ArgumentException($"'{level}' is not an MCP log level.", nameof(level));
            if (!_Sessions.TryGetValue(sessionId, out ClientConnection? connection)) return false;
            McpServerNotifications.LogAsync(new[] { StateOf(connection) }, level, data, logger, CancellationToken.None).GetAwaiter().GetResult();
            return true;
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
        /// Gets a list of all currently connected client IDs.
        /// </summary>
        /// <returns>A list of client IDs.</returns>
        public List<string> GetConnectedClients()
        {
            return _Sessions.Keys.ToList();
        }

        /// <summary>
        /// Kicks a client by disconnecting them from the server.
        /// </summary>
        /// <param name="clientId">The ID of the client to kick.</param>
        /// <returns>True if the client was found and kicked; otherwise, false.</returns>
        public bool KickClient(string clientId)
        {
            if (_Sessions.TryRemove(clientId, out ClientConnection? connection))
            {
                _SessionVersions.TryRemove(clientId, out string? _);
                RaiseClientDisconnected(connection);
                connection.Dispose();
                LogMessage($"Kicked client: {clientId}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes a session and its notification queue.
        /// </summary>
        /// <param name="sessionId">The session ID to remove.</param>
        /// <returns>True if the session was found and removed; otherwise, false.</returns>
        public bool RemoveSession(string sessionId)
        {
            if (_Sessions.TryRemove(sessionId, out ClientConnection? connection))
            {
                _SessionVersions.TryRemove(sessionId, out string? _);
                (connection.ProtocolState as McpSessionState)?.CancelAll();
                _SseStreams.TryRemove(sessionId, out SseSessionStreams? _);
                RaiseClientDisconnected(connection);
                connection.Dispose();
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

            foreach (ClientConnection connection in _Sessions.Values)
            {
                connection.Dispose();
            }
            _Sessions.Clear();
            _SessionVersions.Clear();

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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="McpHttpServer"/> and optionally releases the managed resources.
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

        private static void ValidateToolDefinition(ToolDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (String.IsNullOrEmpty(definition.Name)) throw new ArgumentException("Tool definition must include a name.", nameof(definition));
            if (String.IsNullOrEmpty(definition.Description)) throw new ArgumentException("Tool definition must include a description.", nameof(definition));
            if (definition.InputSchema == null) throw new ArgumentException("Tool definition must include an input schema.", nameof(definition));
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
        /// Registers the MCP protocol methods: <c>initialize</c>, <c>ping</c>, <c>server/discover</c>,
        /// <c>tools/*</c>, <c>resources/*</c>, <c>prompts/*</c>, <c>completion/complete</c>,
        /// <c>logging/setLevel</c>, and the client notification handlers. Called once by the constructor,
        /// regardless of <c>includeDiagnosticTools</c>. A derived class that overrides this method must call
        /// the base implementation, or the server will not speak MCP.
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
            RegisterMethod("notifications/initialized", (args) =>
            {
                LogMessage("Received initialized notification from client");
                return _Endpoint.Initialized(args);
            });
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

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "";

                // Carries the authenticated caller (Principal + Claims) into method/tool handlers via
                // an ambient AsyncLocal. Null unless an AuthenticationHandler authenticated this request.
                RpcCallContext? callContext = null;

                // A server bound to a loopback name serves loopback clients only (http.sys routes by Host
                // header, so a remote client could otherwise reach it by spoofing Host: localhost).
                if (!HttpAccessGuard.IsRemoteAllowed(context.Request, _RestrictToLoopbackClients))
                {
                    LogMessage($"Rejected remote client {context.Request.RemoteEndPoint}: the server accepts loopback clients only");
                    await HttpAccessGuard.RejectAsync(context, 403, "Remote connections are not allowed.", token).ConfigureAwait(false);
                    return;
                }

                // Origin validation (MCP Streamable HTTP security requirement), before preflight and auth.
                string? origin = HttpAccessGuard.GetOrigin(context.Request);
                if (!_OriginPolicy.IsAllowed(origin))
                {
                    LogMessage($"Rejected request from disallowed origin '{origin}' for {path}");
                    await HttpAccessGuard.RejectAsync(context, 403, "Origin not allowed.", token).ConfigureAwait(false);
                    return;
                }

                // Handle CORS preflight
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    HandleCorsPreflightRequest(context);
                    return;
                }

                // Health check endpoint is always unauthenticated
                if (path == "/")
                {
                    await HandleHealthCheckAsync(context, token).ConfigureAwait(false);
                    return;
                }

                // Protected resource metadata (RFC 9728) must be readable without a token.
                if (IsProtectedResourceMetadataPath(path))
                {
                    await HandleProtectedResourceMetadataAsync(context, token).ConfigureAwait(false);
                    return;
                }

                // Every other request must authenticate, including ping: MCP authorization requires a 401 for a
                // missing or invalid token on every request (2025-03-26 and later).
                string? requestBody = null;
                if (_AuthenticationHandler != null)
                {
                    // Buffer a POST body first, so a handler that reads the request stream (for example to verify a
                    // signature) does not leave the dispatcher with an empty body.
                    if (context.Request.HttpMethod == "POST" && context.Request.HasEntityBody)
                    {
                        requestBody = await ReadRequestBodyAsync(context).ConfigureAwait(false);
                    }

                    AuthenticationResult authResult = await _AuthenticationHandler(context.Request).ConfigureAwait(false);
                    if (!authResult.IsAuthenticated)
                    {
                        await HttpAccessGuard.WriteAuthenticationFailureAsync(context, authResult, _EnableCors, _CorsHeaders, token, ResourceMetadataUrl(context)).ConfigureAwait(false);
                        LogMessage($"Authentication failed for {path}: {authResult.ErrorMessage ?? "no details"}");
                        return;
                    }

                    // Make the authenticated caller ambient for the remainder of this request.
                    callContext = new RpcCallContext(authResult.Principal, authResult.Claims);
                }

                // Push the ambient caller context (a no-op that restores null when unauthenticated) so it
                // flows into every method/tool handler dispatched below and is cleared when the request ends.
                using (RpcCallContext.Push(callContext))
                {
                    if (!String.IsNullOrEmpty(_McpPath) && IsEndpointPath(path, _McpPath))
                    {
                        await HandleMcpRequestAsync(context, requestBody, token).ConfigureAwait(false);
                    }
                    else if (IsEndpointPath(path, _RpcPath))
                    {
                        await HandleRpcRequestAsync(context, requestBody, token).ConfigureAwait(false);
                    }
                    else if (IsEndpointPath(path, _EventsPath))
                    {
                        await HandleSseRequestAsync(context, token).ConfigureAwait(false);
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                    }
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

        // An endpoint matches its exact path, with or without a trailing slash (not every path that starts with it).
        private static bool IsEndpointPath(string path, string endpoint)
        {
            string trimmed = endpoint.TrimEnd('/');
            return StringComparer.Ordinal.Equals(path, trimmed) || StringComparer.Ordinal.Equals(path, trimmed + "/");
        }

        /// <summary>
        /// Extracts the session ID from the Mcp-Session-Id header (or its legacy spelling).
        /// </summary>
        private string? GetSessionId(HttpListenerContext context)
        {
            return context.Request.Headers[McpProtocol.SessionIdHeader]
                ?? context.Request.Headers[McpProtocol.LegacySessionIdHeader];
        }

        /// <summary>
        /// Extracts the session ID for a GET (SSE) request. Besides the headers, the session query string
        /// parameter is accepted, because browser EventSource clients cannot set request headers. POST and
        /// DELETE never read the query string, so session IDs stay out of URLs and access logs there.
        /// </summary>
        private string? GetStreamSessionId(HttpListenerContext context)
        {
            return GetSessionId(context) ?? context.Request.QueryString["session"];
        }

        /// <summary>
        /// Returns the live session for an ID the server issued, marking it active. False when the ID was
        /// never issued, has expired, or was terminated, or when the session belongs to a different
        /// authenticated principal than the current request (a session is bound to the principal that
        /// created it, so another caller cannot adopt it).
        /// </summary>
        private bool TryGetActiveSession(string sessionId, out ClientConnection? connection)
        {
            connection = null;
            if (_TerminatedSessions.ContainsKey(sessionId)) return false;
            if (!_Sessions.TryGetValue(sessionId, out ClientConnection? found)) return false;

            string? owner = found.Caller?.Principal;
            string? caller = RpcCallContext.Current?.Principal;
            if (!StringComparer.Ordinal.Equals(owner, caller))
            {
                LogMessage($"Rejected session {sessionId}: it belongs to a different principal");
                return false;
            }

            found.MarkActivity();
            connection = found;
            return true;
        }

        /// <summary>
        /// Creates a connection that is not registered as a session. It becomes a session only if
        /// <see cref="RegisterSession"/> is called after the request succeeds.
        /// </summary>
        private ClientConnection CreateProvisionalConnection(bool requireInitialize = true)
        {
            ClientConnection connection = new ClientConnection(Guid.NewGuid().ToString());
            connection.MaxQueueSize = _MaxQueueSize;
            McpSessionState state = new McpSessionState(requireInitialize) { Owner = connection };
            state.Push = (json, token) =>
            {
                JsonRpcRequest? notification = JsonSerializer.Deserialize<JsonRpcRequest>(json);
                if (notification != null) connection.Enqueue(notification);
                return Task.CompletedTask;
            };
            connection.ProtocolState = state;
            return connection;
        }

        private static McpSessionState StateOf(ClientConnection connection)
        {
            return connection.ProtocolState as McpSessionState ?? new McpSessionState(false) { Owner = connection };
        }

        private IEnumerable<McpSessionState> Sessions()
        {
            return _Sessions.Values.Select(connection => connection.ProtocolState).OfType<McpSessionState>().ToList();
        }

        // Dispatches one JSON-RPC message (not a batch) through the shared MCP processor and writes the response:
        // JSON, or an SSE stream when the handler sends related notifications or a keep-alive is needed.
        private async Task DispatchSingleAsync(
            HttpListenerContext context,
            string requestBody,
            ClientConnection connection,
            bool hasSession,
            bool mayIssueSession,
            string? statelessVersion,
            CancellationToken token)
        {
            JsonElement root;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(requestBody))
                {
                    root = document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                if (mayIssueSession) connection.Dispose();
                await WriteJsonRpcErrorAsync(context, 400, null, new McpProtocolException(-32700, "Parse error"), token).ConfigureAwait(false);
                return;
            }

            McpEnvelope envelope = McpEnvelope.Parse(root);
            if (envelope.Kind == McpEnvelopeKind.Response)
            {
                AcceptClientResponse(context, connection, mayIssueSession, hasSession);
                return;
            }

            McpSessionState state = StateOf(connection);
            bool sessionHeader = hasSession;

            // A session's POST response stream is resumable (MCP 2025-03-26 and later): its events carry IDs and are
            // logged, and from 2025-11-25 it starts with a priming event. Stateless requests have no session to
            // resume on, and a replay buffer of 0 turns resumability off.
            Func<SseStreamLog>? openLog = null;
            bool prime = false;
            if (hasSession && statelessVersion == null && _SseReplayBufferSize > 0)
            {
                string streamSessionId = connection.SessionId;
                openLog = () => _SseStreams.GetOrAdd(streamSessionId, _ => new SseSessionStreams(MaxSseStreamsPerSession)).OpenRequestStream(_SseReplayBufferSize);
                string? negotiated = state.NegotiatedVersion;
                prime = negotiated != null && String.CompareOrdinal(negotiated, McpProtocol.ProtocolVersion20251125) >= 0;
            }

            McpHttpResponseWriter writer = new McpHttpResponseWriter(context, response =>
            {
                HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);
                if (sessionHeader) SetSessionIdHeaders(response, connection.SessionId);
            }, openLog, prime, _SseRetryIntervalMs);

            using CancellationTokenSource requestTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            using CancellationTokenSource keepAliveStop = new CancellationTokenSource();
            Task keepAlive = statelessVersion != null && envelope.Kind == McpEnvelopeKind.Request && _ResponseKeepAliveMs > 0
                ? RunKeepAliveAsync(writer, requestTokenSource, keepAliveStop.Token)
                : Task.CompletedTask;

            McpHandledResponse? handled;
            try
            {
                handled = await _Processor.HandleAsync(
                    envelope,
                    state,
                    statelessVersion,
                    true,
                    (notification, ct) => writer.NotifyAsync(McpMessageProcessor.SerializeNotification(notification, state), ct),
                    requestTokenSource.Token).ConfigureAwait(false);
            }
            finally
            {
                keepAliveStop.Cancel();
                await keepAlive.ConfigureAwait(false);
            }

            if (mayIssueSession)
            {
                if (envelope.Method == "initialize" && handled != null && handled.Response.Error == null)
                {
                    RegisterSession(connection);
                    sessionHeader = true;
                }
                else
                {
                    connection.Dispose();
                }
            }

            if (sessionHeader && state.NegotiatedVersion != null) _SessionVersions[connection.SessionId] = state.NegotiatedVersion;

            if (handled == null)
            {
                if (envelope.Kind == McpEnvelopeKind.Request)
                {
                    // The client cancelled the request: no response is sent, and the stream simply ends.
                    await writer.EndWithoutResponseAsync(token).ConfigureAwait(false);
                }
                else
                {
                    await writer.CompleteAsync(null, 202, token).ConfigureAwait(false);
                    LogMessage($"MCP notification accepted: {envelope.Method}");
                }

                return;
            }

            if (handled.InsufficientScope != null && !writer.IsStreaming)
            {
                await WriteInsufficientScopeAsync(context, envelope.ResponseId, handled.InsufficientScope, handled.Response.Error?.Message, sessionHeader ? connection.SessionId : null, token).ConfigureAwait(false);
                return;
            }

            string responseJson = _Processor.SerializeResponse(handled.Response, handled.StatelessVersion, envelope.Method, state);
            await writer.CompleteAsync(responseJson, StatusFor(handled.Response.Error, handled.StatelessVersion != null), token).ConfigureAwait(false);
            LogMessage($"MCP response{(sessionHeader ? " to session " + connection.SessionId : "")}: {responseJson}");
        }

        // Keeps a stateless response alive while the handler runs. A keep-alive that cannot be written means the client
        // closed the stream, which 2026-07-28 defines as cancellation of the request.
        private async Task RunKeepAliveAsync(McpHttpResponseWriter writer, CancellationTokenSource request, CancellationToken stop)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(_ResponseKeepAliveMs, stop).ConfigureAwait(false);
                    if (!await writer.KeepAliveAsync(stop).ConfigureAwait(false))
                    {
                        LogMessage("The client closed the response stream; cancelling the request.");
                        request.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        // HTTP status for a JSON-RPC response. Stateless-era requests: an unknown method is 404, and a header mismatch,
        // missing client capability, or unsupported version is 400. Everything else travels with 200.
        private static int StatusFor(JsonRpcError? error, bool stateless)
        {
            if (error == null || !stateless) return 200;
            if (error.Code == -32601) return 404;
            if (error.Code == -32020 || error.Code == -32021 || error.Code == -32022) return 400;
            return 200;
        }

        // 403 with an RFC 6750 insufficient_scope challenge, for a handler that threw McpInsufficientScopeException.
        private async Task WriteInsufficientScopeAsync(HttpListenerContext context, object? id, string scope, string? message, string? sessionId, CancellationToken token)
        {
            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);
            if (sessionId != null) SetSessionIdHeaders(context.Response, sessionId);
            string challenge = "Bearer error=\"insufficient_scope\", scope=\"" + scope.Replace("\"", "") + "\"";
            string? metadataUrl = ResourceMetadataUrl(context);
            if (metadataUrl != null) challenge += ", resource_metadata=\"" + metadataUrl + "\"";
            context.Response.AddHeader("WWW-Authenticate", challenge);

            JsonRpcResponse response = new JsonRpcResponse { Id = id, Error = new JsonRpcError { Code = McpInsufficientScopeException.ErrorCode, Message = message ?? "Insufficient scope" } };
            byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
            context.Response.Close();
        }

        // The absolute URL of the protected resource metadata, when it is configured.
        private string? ResourceMetadataUrl(HttpListenerContext context)
        {
            if (_ProtectedResourceMetadata == null || context.Request.Url == null) return null;
            return context.Request.Url.GetLeftPart(UriPartial.Authority) + McpProtocol.ProtectedResourceMetadataPath;
        }

        // A request served without a session earns one only when it is a successful initialize.
        private static bool ShouldIssueSession(JsonRpcRequest? request, JsonRpcResponse response)
        {
            if (request == null || request.Id == null || response.Error != null || response.Result == null) return false;
            return IsInitializeRequest(request);
        }

        private static bool IsInitializeRequest(JsonRpcRequest? request)
        {
            return request != null && StringComparer.Ordinal.Equals(request.Method, "initialize");
        }

        /// <summary>
        /// Registers a provisional connection as a session and raises <see cref="ClientConnected"/>.
        /// </summary>
        private void RegisterSession(ClientConnection connection)
        {
            connection.Caller = RpcCallContext.Current;
            if (_Sessions.TryAdd(connection.SessionId, connection))
            {
                RaiseClientConnected(connection);
                LogMessage($"MCP session created: {connection.SessionId}");
            }
        }

        /// <summary>
        /// Records the protocol version a successful initialize negotiated for a session, so later
        /// batching and header policy can be keyed on it.
        /// </summary>
        private void RecordNegotiatedVersion(string sessionId, JsonRpcRequest? request, JsonRpcResponse response)
        {
            if (!IsInitializeRequest(request) || response.Result == null) return;

            string? negotiatedVersion = new RpcParameters(JsonSerializer.Serialize(response.Result)).GetString("protocolVersion");
            if (!String.IsNullOrEmpty(negotiatedVersion))
            {
                _SessionVersions[sessionId] = negotiatedVersion!;
            }
        }

        /// <summary>
        /// Sets the Mcp-Session-Id header on the response.
        /// </summary>
        private void SetSessionIdHeaders(HttpListenerResponse response, string sessionId)
        {
            response.AddHeader(McpProtocol.SessionIdHeader, sessionId);
        }

        /// <summary>
        /// Handles requests on the MCP Streamable HTTP endpoint (/mcp by default).
        /// POST: JSON-RPC requests (same as /rpc, but with Mcp-Session-Id header).
        /// GET: SSE stream for server-to-client notifications (same as /events).
        /// DELETE: Session termination.
        /// This provides compatibility with the MCP Streamable HTTP transport specification.
        /// </summary>
        private async Task HandleMcpRequestAsync(HttpListenerContext context, string? preReadBody, CancellationToken token)
        {
            string method = context.Request.HttpMethod;

            if (method == "POST")
            {
                if (!RequestAccepts(context, "application/json") || !RequestAccepts(context, "text/event-stream"))
                {
                    await SendTextResponseAsync(context, 406, "POST /mcp requires an Accept header that permits application/json and text/event-stream", token).ConfigureAwait(false);
                    return;
                }

                // Streamable HTTP bodies are JSON. Rejecting other media types also closes the preflight-free
                // text/plain path a browser page could otherwise use. A request with no Content-Type is accepted
                // only without an Origin header (a non-browser client), because a browser can send a body with no
                // Content-Type (a Blob) without a CORS preflight.
                string? contentType = context.Request.ContentType;
                bool nonBrowserWithoutContentType = String.IsNullOrWhiteSpace(contentType) && HttpAccessGuard.GetOrigin(context.Request) == null;
                if (!IsJsonContentType(contentType) && !nonBrowserWithoutContentType)
                {
                    await SendTextResponseAsync(context, 415, "POST /mcp requires Content-Type: application/json", token).ConfigureAwait(false);
                    return;
                }

                // Read the request body once; both the stateless and handshake paths reuse it. The
                // authentication step may already have read it.
                string requestBody = preReadBody ?? await ReadRequestBodyAsync(context).ConfigureAwait(false);

                JsonRpcRequest? incomingRequest = null;
                try
                {
                    incomingRequest = JsonSerializer.Deserialize<JsonRpcRequest>(requestBody);
                }
                catch (JsonException)
                {
                }

                if (await TryHandleStatelessPostAsync(context, incomingRequest, requestBody, token).ConfigureAwait(false))
                {
                    return;
                }

                if (!ValidateProtocolVersionHeader(context))
                {
                    await SendTextResponseAsync(context, 400, "Unsupported MCP-Protocol-Version", token).ConfigureAwait(false);
                    return;
                }

                // Resolve the session (handshake era). Session IDs are only ever generated by the server:
                // an ID it does not know is rejected, never adopted, and a sessionless request gets a
                // provisional connection that becomes a session only if the request earns one.
                string? requestedSessionId = GetSessionId(context);
                bool isBatch = IsBatchBody(requestBody);
                ClientConnection? connection;
                bool isProvisional = false;

                if (!String.IsNullOrEmpty(requestedSessionId))
                {
                    if (!TryGetActiveSession(requestedSessionId!, out connection) || connection == null)
                    {
                        await WriteJsonRpcErrorAsync(context, 404, isBatch ? null : incomingRequest?.Id, McpProtocolException.SessionNotFound(), token).ConfigureAwait(false);
                        return;
                    }
                }
                else if (!isBatch && IsInitializeRequest(incomingRequest))
                {
                    connection = CreateProvisionalConnection();
                    isProvisional = true;
                }
                else
                {
                    await WriteJsonRpcErrorAsync(context, 400, isBatch ? null : incomingRequest?.Id, McpProtocolException.SessionRequired(), token).ConfigureAwait(false);
                    return;
                }

                string sessionId = connection.SessionId;
                bool hasSession = !isProvisional;

                LogMessage(isProvisional
                    ? $"MCP request without a session: {requestBody}"
                    : $"MCP request from session {sessionId}: {requestBody}");

                // JSON-RPC batching is version-gated: permitted through 2025-03-26 and removed from
                // 2025-06-18 onward. The policy is keyed on the session's negotiated version.
                if (isBatch)
                {
                    string batchVersion = ResolveHandshakeVersion(sessionId, context);
                    McpProtocolVersionInfo? batchInfo = McpProtocol.GetVersionInfo(batchVersion);
                    if (batchInfo == null || !batchInfo.SupportsBatching)
                    {
                        await WriteJsonRpcErrorAsync(context, 400, null,
                            new McpProtocolException(-32600, $"JSON-RPC batching is not supported in protocol version '{batchVersion}'."), token).ConfigureAwait(false);
                        return;
                    }

                    await DispatchBatchAsync(context, requestBody, connection, token).ConfigureAwait(false);
                    return;
                }

                await DispatchSingleAsync(context, requestBody, connection, hasSession, isProvisional, null, token).ConfigureAwait(false);
            }
            else if (method == "GET")
            {
                if (!ValidateProtocolVersionHeader(context))
                {
                    await SendTextResponseAsync(context, 400, "Unsupported MCP-Protocol-Version", token).ConfigureAwait(false);
                    return;
                }

                if (!RequestAccepts(context, "text/event-stream"))
                {
                    await SendTextResponseAsync(context, 406, "GET /mcp requires Accept: text/event-stream", token).ConfigureAwait(false);
                    return;
                }

                // SSE stream for server-to-client notifications. The session ID travels in the MCP-Session-Id header,
                // as the specification requires (never the query string, which ends up in URLs and logs).
                string? sessionId = GetSessionId(context);
                if (String.IsNullOrEmpty(sessionId))
                {
                    await WriteJsonRpcErrorAsync(context, 400, null, McpProtocolException.SessionRequired(), token).ConfigureAwait(false);
                    return;
                }

                if (!TryGetActiveSession(sessionId, out ClientConnection? connection) || connection == null)
                {
                    await WriteJsonRpcErrorAsync(context, 404, null, McpProtocolException.SessionNotFound(), token).ConfigureAwait(false);
                    return;
                }

                // Set up SSE headers
                HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);

                context.Response.ContentType = "text/event-stream";
                context.Response.AddHeader("Cache-Control", "no-cache");
                context.Response.AddHeader("Connection", "keep-alive");
                context.Response.AddHeader("X-Accel-Buffering", "no");
                context.Response.SendChunked = true;

                // Resumability (2025-03-26 and later): a Last-Event-ID naming one of this session's streams resumes
                // that stream with the events the client missed; anything else opens a new stream.
                SseSessionStreams sessionStreams = _SseStreams.GetOrAdd(sessionId, _ => new SseSessionStreams(MaxSseStreamsPerSession));
                SseStreamLog streamLog = sessionStreams.Open(context.Request.Headers["Last-Event-ID"], _SseReplayBufferSize, out long resumeAfter);

                LogMessage(resumeAfter >= 0
                    ? $"MCP SSE stream {streamLog.StreamId} resumed after event {resumeAfter} for session {sessionId}"
                    : $"MCP SSE connection established for session {sessionId} (stream {streamLog.StreamId})");

                using CancellationTokenSource streamTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                CancellationToken streamToken = streamTokenSource.Token;
                streamLog.Claim(streamTokenSource);
                try
                {
                    // lastSent is the newest event this connection has written; anything newer in the stream log
                    // (missed events on resume, or an event a former writer took after the client moved) is sent first.
                    long lastSent;
                    if (resumeAfter >= 0)
                    {
                        await WriteSseAsync(context.Response, $"retry: {_SseRetryIntervalMs}\n\n", token).ConfigureAwait(false);
                        lastSent = resumeAfter;
                    }
                    else
                    {
                        // Priming event: an event ID and an empty data field, so the client can reconnect with
                        // Last-Event-ID, plus the reconnection delay (MCP 2025-11-25).
                        string primingId = streamLog.CurrentEventId;
                        await WriteSseAsync(context.Response, $"id: {primingId}\nretry: {_SseRetryIntervalMs}\ndata:\n\n", token).ConfigureAwait(false);
                        SseStreamLog.TryParseEventId(primingId, out string _, out lastSent);
                    }

                    while (!streamToken.IsCancellationRequested)
                    {
                        foreach (SseLoggedEvent pending in streamLog.After(lastSent))
                        {
                            await WriteSseAsync(context.Response, $"id: {pending.EventId}\ndata: {pending.Data}\n\n", token).ConfigureAwait(false);
                            lastSent = pending.Sequence;
                        }

                        if (streamLog.IsRequestStream)
                        {
                            // A resumed POST response stream carries only that request's messages and ends after its
                            // response; the session's other notifications stay on the GET stream.
                            if (streamLog.IsComplete && streamLog.After(lastSent).Count == 0) break;
                            CancellationToken appended = streamLog.BeginWait(lastSent);
                            using (CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(streamToken, appended))
                            {
                                try
                                {
                                    await Task.Delay(_SseHeartbeatInterval, waitCts.Token).ConfigureAwait(false);
                                    await SendSseKeepAliveAsync(context.Response, token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (!streamToken.IsCancellationRequested)
                                {
                                }
                            }

                            continue;
                        }

                        CancellationToken wake = streamLog.BeginWait(lastSent);
                        using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(streamToken, wake))
                        {
                            timeoutCts.CancelAfter(_SseHeartbeatInterval);

                            try
                            {
                                JsonRpcRequest? notification = await connection.DequeueAsync(timeoutCts.Token).ConfigureAwait(false);

                                if (notification != null)
                                {
                                    // Logged before writing, so a message lost to a broken connection can be replayed.
                                    SseLoggedEvent logged = streamLog.Append(JsonSerializer.Serialize(notification), streamTokenSource);
                                    if (streamToken.IsCancellationRequested) break;
                                    await WriteSseAsync(context.Response, $"id: {logged.EventId}\ndata: {logged.Data}\n\n", token).ConfigureAwait(false);
                                    lastSent = Math.Max(lastSent, logged.Sequence);
                                }
                            }
                            catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (OperationCanceledException) when (wake.IsCancellationRequested)
                            {
                                // Newer events exist in the log; send them at the top of the loop.
                            }
                            catch (OperationCanceledException)
                            {
                                // An open stream is activity: keep the session from idle expiry.
                                connection.MarkActivity();
                                await SendSseKeepAliveAsync(context.Response, token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"MCP SSE error for session {sessionId}: {ex.Message}");
                }
                finally
                {
                    streamLog.Release(streamTokenSource);
                    context.Response.Close();
                    LogMessage($"MCP SSE connection closed for session {sessionId}");
                }
            }
            else if (method == "DELETE")
            {
                if (!ValidateProtocolVersionHeader(context))
                {
                    await SendTextResponseAsync(context, 400, "Unsupported MCP-Protocol-Version", token).ConfigureAwait(false);
                    return;
                }

                // Session termination
                string? sessionId = GetSessionId(context);
                if (String.IsNullOrEmpty(sessionId))
                {
                    await WriteJsonRpcErrorAsync(context, 400, null, McpProtocolException.SessionRequired(), token).ConfigureAwait(false);
                    return;
                }

                // Only the principal that created the session may end it.
                if (!TryGetActiveSession(sessionId, out ClientConnection? _))
                {
                    await WriteJsonRpcErrorAsync(context, 404, null, McpProtocolException.SessionNotFound(), token).ConfigureAwait(false);
                    return;
                }

                if (!String.IsNullOrEmpty(sessionId))
                {
                    RemoveSession(sessionId);
                    _TerminatedSessions[sessionId] = 0;
                    LogMessage($"MCP session terminated: {sessionId}");
                }

                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            else
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
            }
        }

        /// <summary>
        /// Resolves the protocol version of a POST and, when it resolves to the stateless (2026-07-28) era,
        /// serves it through <see cref="HandleStatelessPostAsync"/>. Both the MCP endpoint and the JSON-RPC
        /// endpoint call this, because <c>server/discover</c> advertises the stateless revision on both, so a
        /// client that chose it must get stateless results wherever it sends requests. Returns true when a
        /// response was written, including a version-resolution error; false when the request belongs to
        /// the handshake era and the caller should continue.
        /// </summary>
        private async Task<bool> TryHandleStatelessPostAsync(
            HttpListenerContext context,
            JsonRpcRequest? incomingRequest,
            string requestBody,
            CancellationToken token)
        {
            ExtractStatelessSignals(requestBody, out string? bodyName, out string? metaProtocolVersion);

            McpResolvedVersion resolved;
            try
            {
                resolved = McpVersionResolver.Resolve(
                    context.Request.Headers[McpProtocol.ProtocolVersionHeader],
                    metaProtocolVersion,
                    incomingRequest?.Method,
                    !String.IsNullOrEmpty(context.Request.Headers[McpProtocol.LegacySessionIdHeader]),
                    !String.IsNullOrEmpty(context.Request.Headers[McpProtocol.MethodHeader]) || !String.IsNullOrEmpty(context.Request.Headers[McpProtocol.NameHeader]));
            }
            catch (McpProtocolException resolveError)
            {
                // The supported list must name only versions this server accepts (the handshake cap applies).
                McpProtocolException reported = resolveError.Code == -32022
                    ? McpProtocolException.UnsupportedProtocolVersion(metaProtocolVersion ?? context.Request.Headers[McpProtocol.ProtocolVersionHeader] ?? String.Empty, _Endpoint.SupportedVersions())
                    : resolveError;
                await WriteJsonRpcErrorAsync(context, 400, incomingRequest?.Id, reported, token).ConfigureAwait(false);
                return true;
            }

            // initialize is a handshake-era request by definition: stateless-era revisions define no
            // initialize and no sessions. It always takes the handshake path, where negotiation is capped
            // at MaximumHandshakeProtocolVersion, even when a stateless-era version header accompanies it.
            bool isHandshakeRequest = incomingRequest != null && StringComparer.Ordinal.Equals(incomingRequest.Method, "initialize");

            if (resolved.Era == McpProtocolEra.Stateless && !isHandshakeRequest)
            {
                await HandleStatelessPostAsync(context, incomingRequest, requestBody, bodyName, metaProtocolVersion, resolved.Version, token).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Handles a POST to the MCP endpoint that resolved to the stateless (2026-07-28) era.
        /// Validates the required routing headers, dispatches through the shared registry without
        /// creating or echoing a session, and returns a single JSON response (or 202 for a
        /// notification). An unknown method returns HTTP 404 with a JSON-RPC method-not-found error.
        /// </summary>
        private async Task HandleStatelessPostAsync(
            HttpListenerContext context,
            JsonRpcRequest? incomingRequest,
            string requestBody,
            string? bodyName,
            string? metaProtocolVersion,
            string protocolVersion,
            CancellationToken token)
        {
            string? bodyMethod = incomingRequest?.Method;

            // The 2026-07-28 revision defines no header requirements for notification POSTs, so the routing
            // header checks below apply to requests only. A header that is present must still agree with the
            // body (the version resolver has already rejected a version mismatch).
            bool isNotification = incomingRequest != null && incomingRequest.Id == null;

            string? protocolHeader = context.Request.Headers[McpProtocol.ProtocolVersionHeader];
            if (!isNotification && String.IsNullOrWhiteSpace(protocolHeader))
            {
                await WriteJsonRpcErrorAsync(context, 400, incomingRequest?.Id,
                    McpProtocolException.HeaderMismatch($"Missing required {McpProtocol.ProtocolVersionHeader} header."), token).ConfigureAwait(false);
                return;
            }

            // params._meta must carry protocolVersion and clientCapabilities; a request missing either is malformed
            // (-32602 with 400). Notifications are exempt: this revision defines no rules for their _meta.
            if (incomingRequest != null && incomingRequest.Id != null && String.IsNullOrWhiteSpace(metaProtocolVersion))
            {
                await WriteJsonRpcErrorAsync(context, 400, incomingRequest.Id,
                    McpProtocolException.InvalidParams($"params._meta[\"{McpProtocol.MetaProtocolVersionKey}\"] is required."), token).ConfigureAwait(false);
                return;
            }

            if (incomingRequest != null && incomingRequest.Id != null)
            {
                McpProtocolException? metaError = McpMessageProcessor.CheckStatelessMeta(requestBody);
                if (metaError != null)
                {
                    await WriteJsonRpcErrorAsync(context, 400, incomingRequest.Id, metaError, token).ConfigureAwait(false);
                    return;
                }
            }

            string? methodHeader = context.Request.Headers[McpProtocol.MethodHeader];
            if (!isNotification && String.IsNullOrWhiteSpace(methodHeader))
            {
                await WriteJsonRpcErrorAsync(context, 400, incomingRequest?.Id,
                    McpProtocolException.HeaderMismatch($"Missing required {McpProtocol.MethodHeader} header."), token).ConfigureAwait(false);
                return;
            }

            if (!String.IsNullOrWhiteSpace(methodHeader) && !StringComparer.Ordinal.Equals(methodHeader, bodyMethod))
            {
                await WriteJsonRpcErrorAsync(context, 400, incomingRequest?.Id,
                    McpProtocolException.HeaderMismatch($"{McpProtocol.MethodHeader} header value '{methodHeader}' does not match body method '{bodyMethod}'."), token).ConfigureAwait(false);
                return;
            }

            if (!isNotification && RequiresMcpName(bodyMethod))
            {
                string? rawNameHeader = context.Request.Headers[McpProtocol.NameHeader];
                if (rawNameHeader != null && !McpHeaderParameters.TryDecode(rawNameHeader, out string _))
                {
                    await WriteJsonRpcErrorAsync(context, 400, incomingRequest?.Id,
                        McpProtocolException.HeaderMismatch($"{McpProtocol.NameHeader} header contains characters that are not allowed in a header value, or an invalid Base64 value."), token).ConfigureAwait(false);
                    return;
                }

                string? nameHeader = DecodeMcpNameHeader(rawNameHeader);
                if (String.IsNullOrWhiteSpace(nameHeader))
                {
                    await WriteJsonRpcErrorAsync(context, 400, incomingRequest?.Id,
                        McpProtocolException.HeaderMismatch($"Missing required {McpProtocol.NameHeader} header for '{bodyMethod}'."), token).ConfigureAwait(false);
                    return;
                }

                if (!StringComparer.Ordinal.Equals(nameHeader, bodyName))
                {
                    await WriteJsonRpcErrorAsync(context, 400, incomingRequest?.Id,
                        McpProtocolException.HeaderMismatch($"{McpProtocol.NameHeader} header value '{nameHeader}' does not match body value '{bodyName}'."), token).ConfigureAwait(false);
                    return;
                }
            }

            // Tool parameters annotated with x-mcp-header must be mirrored in matching Mcp-Param-{Name} headers.
            if (!isNotification && StringComparer.Ordinal.Equals(bodyMethod, "tools/call") && !String.IsNullOrEmpty(bodyName))
            {
                McpProtocolException? paramError = ValidateParamHeaders(context, bodyName!, requestBody);
                if (paramError != null)
                {
                    await WriteJsonRpcErrorAsync(context, 400, incomingRequest?.Id, paramError, token).ConfigureAwait(false);
                    return;
                }
            }

            LogMessage($"Stateless MCP request: {requestBody}");

            // Every stateless request runs on its own state: no session, nothing inferred from earlier requests.
            ClientConnection connection = CreateProvisionalConnection(requireInitialize: false);
            try
            {
                await DispatchSingleAsync(context, requestBody, connection, false, false, protocolVersion, token).ConfigureAwait(false);
            }
            finally
            {
                connection.Dispose();
            }
        }

        // Checks the Mcp-Param-{Name} headers of a tools/call against the arguments (2026-07-28). A value in the body
        // needs a header with the same value (after decoding the Base64 sentinel); a header needs a body value; a
        // header with characters a header value may not carry is rejected. Returns the error, or null when valid.
        private McpProtocolException? ValidateParamHeaders(HttpListenerContext context, string toolName, string requestBody)
        {
            List<McpHeaderParameter>? parameters = _Endpoint.GetToolHeaderParameters(toolName);
            if (parameters == null || parameters.Count == 0) return null;

            JsonElement arguments = default;
            bool hasArguments = false;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(requestBody))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("params", out JsonElement parameterObject)
                        && parameterObject.ValueKind == JsonValueKind.Object
                        && parameterObject.TryGetProperty("arguments", out JsonElement argumentObject))
                    {
                        arguments = argumentObject.Clone();
                        hasArguments = true;
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            foreach (McpHeaderParameter parameter in parameters)
            {
                string path = String.Join(".", parameter.Path);
                string? header = context.Request.Headers[parameter.HeaderName];
                bool hasValue = McpHeaderParameters.TryGetValue(hasArguments ? arguments : default, parameter.Path, out JsonElement value) && hasArguments;

                // The header must match the body whatever the value's type (a client mirrors it as its own type); an
                // object or array cannot be mirrored at all, so the request cannot carry a matching header.
                string matchType = parameter.Type;
                if (hasValue && !McpHeaderParameters.HasDeclaredType(value, parameter.Type))
                {
                    string? actual = McpHeaderParameters.ActualType(value);
                    if (actual == null)
                    {
                        return McpProtocolException.HeaderMismatch($"Argument '{path}' is an object or array, which cannot be mirrored in the {parameter.HeaderName} header.");
                    }

                    matchType = actual;
                }

                if (hasValue && matchType == "integer" && !McpHeaderParameters.IsSafeInteger(value))
                {
                    return McpProtocolException.HeaderMismatch($"Argument '{path}' is outside the JavaScript safe integer range, so it cannot be mirrored in {parameter.HeaderName}.");
                }

                if (header == null)
                {
                    if (hasValue) return McpProtocolException.HeaderMismatch($"Missing required {parameter.HeaderName} header for argument '{path}'.");
                    continue;
                }

                if (!McpHeaderParameters.TryDecode(header, out string decoded))
                {
                    return McpProtocolException.HeaderMismatch($"{parameter.HeaderName} header contains characters that are not allowed in a header value, or an invalid Base64 value.");
                }

                if (!hasValue)
                {
                    return McpProtocolException.HeaderMismatch($"{parameter.HeaderName} header is present but the request has no value for argument '{path}'.");
                }

                if (!McpHeaderParameters.Matches(decoded, value, matchType))
                {
                    return McpProtocolException.HeaderMismatch($"Header mismatch: {parameter.HeaderName} header value does not match the body value of argument '{path}'.");
                }
            }

            return null;
        }

        private static bool RequiresMcpName(string? method)
        {
            return StringComparer.Ordinal.Equals(method, "tools/call")
                || StringComparer.Ordinal.Equals(method, "resources/read")
                || StringComparer.Ordinal.Equals(method, "prompts/get");
        }

        private static string? DecodeMcpNameHeader(string? value)
        {
            if (String.IsNullOrEmpty(value)) return value;
            return McpHeaderParameters.TryDecode(value, out string decoded) ? decoded : null;
        }

        private static void ExtractStatelessSignals(string requestBody, out string? name, out string? protocolVersion)
        {
            name = null;
            protocolVersion = null;
            if (String.IsNullOrWhiteSpace(requestBody))
            {
                return;
            }

            try
            {
                StatelessSignalEnvelope? envelope = JsonSerializer.Deserialize<StatelessSignalEnvelope>(requestBody);
                if (envelope != null && envelope.Params != null)
                {
                    name = !String.IsNullOrEmpty(envelope.Params.Name) ? envelope.Params.Name : envelope.Params.Uri;
                    protocolVersion = envelope.Params.Meta?.ProtocolVersion;
                }
            }
            catch (JsonException)
            {
            }
        }

        private sealed class StatelessSignalEnvelope
        {
            [JsonPropertyName("params")]
            public StatelessSignalParams? Params { get; set; }
        }

        private sealed class StatelessSignalParams
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("uri")]
            public string? Uri { get; set; }

            [JsonPropertyName("_meta")]
            public StatelessSignalMeta? Meta { get; set; }
        }

        private sealed class StatelessSignalMeta
        {
            [JsonPropertyName(McpProtocol.MetaProtocolVersionKey)]
            public string? ProtocolVersion { get; set; }
        }

        private async Task WriteJsonRpcErrorAsync(HttpListenerContext context, int statusCode, object? id, McpProtocolException error, CancellationToken token)
        {
            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);

            JsonRpcResponse response = new JsonRpcResponse
            {
                Error = error.ToJsonRpcError(),
                Id = id
            };

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            byte[] buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            context.Response.Close();
        }

        private void AcceptClientResponse(HttpListenerContext context, ClientConnection connection, bool isProvisional, bool hasSession)
        {
            if (isProvisional) connection.Dispose();

            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);
            if (hasSession) SetSessionIdHeaders(context.Response, connection.SessionId);
            context.Response.StatusCode = 202;
            context.Response.Close();
            LogMessage($"Accepted a JSON-RPC response from the client{(hasSession ? " on session " + connection.SessionId : "")}; no server request was pending");
        }

        /// <summary>
        /// Returns true when the body is a single JSON-RPC response or error object (it has an id and a
        /// result or error, and no method).
        /// </summary>
        private static bool IsJsonRpcResponseMessage(string body)
        {
            if (String.IsNullOrWhiteSpace(body)) return false;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(body))
                {
                    return IsJsonRpcResponseElement(document.RootElement);
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsJsonRpcResponseElement(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object
                && !element.TryGetProperty("method", out JsonElement _)
                && element.TryGetProperty("id", out JsonElement _)
                && (element.TryGetProperty("result", out JsonElement _) || element.TryGetProperty("error", out JsonElement _));
        }

        private static bool IsBatchBody(string body)
        {
            if (String.IsNullOrEmpty(body))
            {
                return false;
            }

            foreach (char character in body)
            {
                if (Char.IsWhiteSpace(character))
                {
                    continue;
                }

                return character == '[';
            }

            return false;
        }

        private string ResolveHandshakeVersion(string sessionId, HttpListenerContext context)
        {
            if (_SessionVersions.TryGetValue(sessionId, out string? stored) && !String.IsNullOrEmpty(stored))
            {
                return stored;
            }

            string? header = context.Request.Headers[McpProtocol.ProtocolVersionHeader];
            if (!String.IsNullOrWhiteSpace(header) && McpProtocol.IsSupportedVersion(header))
            {
                return header!;
            }

            // No negotiated version and no header: the Streamable HTTP specification says to assume 2025-03-26.
            return McpProtocol.HeaderlessProtocolVersion;
        }

        // Processes a JSON-RPC batch (2025-03-26 sessions only) through the shared processor.
        private async Task DispatchBatchAsync(HttpListenerContext context, string body, ClientConnection connection, CancellationToken token)
        {
            List<JsonElement> elements;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(body))
                {
                    elements = document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
                }
            }
            catch (JsonException)
            {
                await WriteJsonRpcErrorAsync(context, 400, null, new McpProtocolException(-32700, "Parse error"), token).ConfigureAwait(false);
                return;
            }

            if (elements.Count == 0)
            {
                await WriteJsonRpcErrorAsync(context, 400, null, new McpProtocolException(-32600, "Invalid Request: an empty JSON-RPC batch is not allowed."), token).ConfigureAwait(false);
                return;
            }

            McpSessionState state = StateOf(connection);
            List<Task<string?>> pending = new List<Task<string?>>();
            foreach (JsonElement element in elements)
            {
                McpEnvelope envelope = McpEnvelope.Parse(element);
                if (envelope.Method == "initialize")
                {
                    pending.Add(Task.FromResult<string?>(JsonSerializer.Serialize(new JsonRpcResponse
                    {
                        Id = envelope.ResponseId,
                        Error = new JsonRpcError { Code = -32600, Message = "Invalid Request: initialize must not be part of a JSON-RPC batch." }
                    })));
                    continue;
                }

                pending.Add(_Processor.HandleAndSerializeAsync(envelope, state, null, true, null, token));
            }

            List<string> responses = (await Task.WhenAll(pending).ConfigureAwait(false)).Where(json => json != null).Select(json => json!).ToList();

            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);
            SetSessionIdHeaders(context.Response, connection.SessionId);

            // A batch of only notifications and responses has nothing to answer: 202 with no body.
            if (responses.Count == 0)
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }

            byte[] buffer = Encoding.UTF8.GetBytes("[" + String.Join(",", responses) + "]");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            context.Response.Close();
        }

        private void HandleCorsPreflightRequest(HttpListenerContext context)
        {
            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);

            context.Response.StatusCode = 204;
            context.Response.Close();
        }

        private bool ValidateProtocolVersionHeader(HttpListenerContext context)
        {
            string? version = context.Request.Headers[McpProtocol.ProtocolVersionHeader];
            if (String.IsNullOrWhiteSpace(version))
            {
                return true;
            }

            return McpProtocol.IsSupportedVersion(version);
        }

        private static bool IsJsonContentType(string? contentType)
        {
            if (String.IsNullOrWhiteSpace(contentType)) return false;
            string mediaType = contentType!.Split(';')[0].Trim();
            return StringComparer.OrdinalIgnoreCase.Equals(mediaType, "application/json");
        }

        /// <summary>
        /// Returns true when the Accept header permits <paramref name="mediaType"/>. A missing Accept header means
        /// any type (RFC 9110), and media ranges such as <c>*/*</c> and <c>application/*</c> match.
        /// </summary>
        private static bool RequestAccepts(HttpListenerContext context, string mediaType)
        {
            string? acceptHeader = context.Request.Headers["Accept"];
            if (String.IsNullOrWhiteSpace(acceptHeader))
            {
                return true;
            }

            string typeRange = mediaType.Split('/')[0] + "/*";
            string[] parts = acceptHeader.Split(',');
            foreach (string part in parts)
            {
                string value = part.Split(';')[0].Trim();
                if (StringComparer.OrdinalIgnoreCase.Equals(value, mediaType)
                    || value == "*/*"
                    || StringComparer.OrdinalIgnoreCase.Equals(value, typeRange))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task SendTextResponseAsync(HttpListenerContext context, int statusCode, string body, CancellationToken token)
        {
            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain";
            byte[] errorBytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = errorBytes.Length;
            await context.Response.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length, token).ConfigureAwait(false);
            context.Response.Close();
        }

        private bool IsProtectedResourceMetadataPath(string path)
        {
            string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
            if (StringComparer.OrdinalIgnoreCase.Equals(trimmed, McpProtocol.ProtectedResourceMetadataPath)) return true;

            string mcpPath = _McpPath.TrimEnd('/');
            return !String.IsNullOrEmpty(mcpPath)
                && StringComparer.OrdinalIgnoreCase.Equals(trimmed, McpProtocol.ProtectedResourceMetadataPath + mcpPath);
        }

        private async Task HandleProtectedResourceMetadataAsync(HttpListenerContext context, CancellationToken token)
        {
            McpProtectedResourceMetadata? metadata = _ProtectedResourceMetadata;
            if (metadata == null)
            {
                await SendTextResponseAsync(context, 404, "Not found", token).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod != "GET")
            {
                context.Response.AddHeader("Allow", "GET, OPTIONS");
                await SendTextResponseAsync(context, 405, "Method not allowed", token).ConfigureAwait(false);
                return;
            }

            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            byte[] buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata));
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            context.Response.Close();
        }

        private async Task HandleHealthCheckAsync(HttpListenerContext context, CancellationToken token)
        {
            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);

            context.Response.StatusCode = 200;

            if (context.Request.HttpMethod == "GET")
            {
                context.Response.ContentType = "application/json";
                byte[] buffer = Encoding.UTF8.GetBytes("{\"status\":\"Ok\"}");
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            }

            context.Response.Close();
        }

        private static async Task<string> ReadRequestBodyAsync(HttpListenerContext context)
        {
            using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private async Task HandleRpcRequestAsync(HttpListenerContext context, string? preReadBody, CancellationToken token)
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }

            // Read request body, unless the authentication step already read it
            string requestBody = preReadBody ?? await ReadRequestBodyAsync(context).ConfigureAwait(false);

            // A client that chose the stateless revision from server/discover gets stateless results here
            // too, without a session. Requests without stateless signals keep the JSON-RPC behavior below.
            JsonRpcRequest? incomingRequest = null;
            try
            {
                incomingRequest = JsonSerializer.Deserialize<JsonRpcRequest>(requestBody);
            }
            catch (JsonException)
            {
            }

            if (await TryHandleStatelessPostAsync(context, incomingRequest, requestBody, token).ConfigureAwait(false))
            {
                return;
            }

            // Resolve the session. A request with a session ID must name a live session the server issued.
            // A request without one is served on a temporary connection; it becomes a session only when
            // it is a successful initialize, so plain request/response callers never leave sessions behind.
            string? requestedSessionId = GetSessionId(context);
            ClientConnection? connection;
            bool isProvisional = false;

            if (!String.IsNullOrEmpty(requestedSessionId))
            {
                if (!TryGetActiveSession(requestedSessionId!, out connection) || connection == null)
                {
                    await WriteJsonRpcErrorAsync(context, 404, incomingRequest?.Id, McpProtocolException.SessionNotFound(), token).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                connection = CreateProvisionalConnection(requireInitialize: false);
                isProvisional = true;
            }

            string sessionId = connection.SessionId;
            bool hasSession = !isProvisional;

            LogMessage(isProvisional
                ? $"RPC request without a session: {requestBody}"
                : $"RPC request from session {sessionId}: {requestBody}");

            // Voltaic's /rpc endpoint is plain request/response: a sessionless call runs on a fresh state without the
            // initialization requirement, and only a successful initialize creates a session.
            await DispatchSingleAsync(context, requestBody, connection, hasSession, isProvisional, null, token).ConfigureAwait(false);
        }

        private async Task HandleSseRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            if (context.Request.HttpMethod != "GET")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }

            string sessionId = GetStreamSessionId(context) ?? "";
            if (String.IsNullOrEmpty(sessionId))
            {
                await SendTextResponseAsync(context, 400, "Missing session ID. Send initialize to the JSON-RPC endpoint first.", token).ConfigureAwait(false);
                return;
            }

            if (!TryGetActiveSession(sessionId, out ClientConnection? connection) || connection == null)
            {
                await SendTextResponseAsync(context, 404, "Invalid or terminated session ID.", token).ConfigureAwait(false);
                return;
            }

            // Set up SSE headers
            HttpAccessGuard.ApplyCorsHeaders(context, _EnableCors, _CorsHeaders);

            context.Response.ContentType = "text/event-stream";
            context.Response.AddHeader("Cache-Control", "no-cache");
            context.Response.AddHeader("Connection", "keep-alive");
            context.Response.SendChunked = true;

            LogMessage($"SSE connection established for session {sessionId}");

            try
            {
                await SendSsePreludeAsync(context.Response, token).ConfigureAwait(false);

                // Keep connection alive and send notifications
                while (!token.IsCancellationRequested)
                {
                    using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeoutCts.CancelAfter(_SseHeartbeatInterval);

                        try
                        {
                            JsonRpcRequest? notification = await connection.DequeueAsync(timeoutCts.Token).ConfigureAwait(false);

                            if (notification != null)
                            {
                                await SendSseNotificationAsync(context.Response, notification, token).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            // An open stream is activity: keep the session from idle expiry.
                            connection.MarkActivity();
                            await SendSseKeepAliveAsync(context.Response, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            break;
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

        private static async Task WriteSseAsync(HttpListenerResponse response, string text, CancellationToken token)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(text);
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task SendSsePreludeAsync(HttpListenerResponse response, CancellationToken token)
        {
            await response.OutputStream.WriteAsync(_SseConnectedPrelude, 0, _SseConnectedPrelude.Length, token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task SendSseNotificationAsync(HttpListenerResponse response, JsonRpcRequest notification, CancellationToken token)
        {
            string json = JsonSerializer.Serialize(notification);
            string sseMessage = $"data: {json}\n\n";
            byte[] buffer = Encoding.UTF8.GetBytes(sseMessage);

            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task SendSseKeepAliveAsync(HttpListenerResponse response, CancellationToken token)
        {
            await response.OutputStream.WriteAsync(_SseKeepAliveComment, 0, _SseKeepAliveComment.Length, token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
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

        private void RaiseResponseSent(ClientConnection connection, ServerPendingRequest? pendingRequest, JsonRpcResponse response)
        {
            // Invoke each handler individually to ensure exception isolation
            if (ResponseSent != null)
            {
                JsonRpcResponseEventArgs eventArgs;
                if (pendingRequest != null)
                {
                    eventArgs = new JsonRpcResponseEventArgs(pendingRequest, response);
                }
                else
                {
                    // Create a minimal request for error cases where we don't have a pendingRequest
                    JsonRpcRequest dummyRequest = new JsonRpcRequest { Method = "unknown", Id = response.Id };
                    eventArgs = new JsonRpcResponseEventArgs(connection, dummyRequest, response, DateTime.UtcNow);
                }

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
