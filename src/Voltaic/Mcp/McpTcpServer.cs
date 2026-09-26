namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides a TCP-based MCP (Model Context Protocol) server implementation for handling remote procedure calls over a network.
    /// This class extends JsonRpcServer with MCP-specific defaults and semantics.
    /// </summary>
    public class McpTcpServer : JsonRpcServer
    {
        private readonly McpEndpoint _Endpoint;
        private readonly McpMessageProcessor _Processor;

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
        /// Default is "Voltaic.Mcp.TcpServer".
        /// </summary>
        public string ServerName
        {
            get => _Endpoint.ServerName;
            set => _Endpoint.ServerName = value ?? "Voltaic.Mcp.TcpServer";
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
        /// Initializes a new instance of the <see cref="McpTcpServer"/> class.
        /// </summary>
        /// <param name="ip">The IP address to listen on.</param>
        /// <param name="port">The port number to listen on.</param>
        /// <param name="includeDiagnosticTools">
        /// True to also publish the diagnostic tools <c>echo</c> and <c>getTime</c> in <c>tools/list</c>.
        /// Default is false, so the server publishes only the tools the application registers. The MCP protocol
        /// methods (<c>initialize</c>, <c>ping</c>, <c>tools/*</c>, <c>resources/*</c>, <c>prompts/*</c>, and so on)
        /// are always registered regardless of this value.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when ip is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the port is invalid.</exception>
        public McpTcpServer(IPAddress ip, int port, bool includeDiagnosticTools = false)
            : base(ip, port, false)
        {
            _Endpoint = new McpEndpoint("Voltaic.Mcp.TcpServer");
            _Endpoint.ErrorLog = WriteLog;
            // Registration changes are announced to initialized clients (notifications/{kind}/list_changed).
            _Endpoint.ListChanged = kind =>
            {
                if (!_Endpoint.SupportsListChangedNotifications) return;
                _ = McpServerNotifications.ListChangedAsync(Sessions(), "notifications/" + kind + "/list_changed", CancellationToken.None);
            };
            _Processor = new McpMessageProcessor(_Endpoint, Methods, WriteLog)
            {
                RequestReceived = (request, session) => { if (session.Owner is ClientConnection client) RaiseRequestReceivedFor(client, request); },
                ResponseProduced = (request, response, session) => { if (session.Owner is ClientConnection client) RaiseResponseSentFor(client, request, response); }
            };

            RegisterProtocolMethods();
            if (includeDiagnosticTools) RegisterDiagnosticTools();
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
        /// Registers a tool whose handler additionally receives the authenticated caller for the current
        /// request via the ambient <see cref="RpcCallContext.Current"/>. The base TCP transport performs no
        /// authentication, so the context is null unless a derived transport populates it.
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

        /// <summary>
        /// Registers the MCP diagnostic tools in place of the plain JSON-RPC diagnostic methods of
        /// <see cref="JsonRpcServer"/>, so a derived class that calls this method gets MCP tools rather than
        /// bare methods. The constructor calls <see cref="RegisterDiagnosticTools"/> directly.
        /// </summary>
        protected override void RegisterDiagnosticMethods()
        {
            RegisterDiagnosticTools();
        }
    
        // MCP over TCP accepts newline-delimited JSON (the stdio framing) as well as Content-Length framing.
        private protected override bool AcceptNewlineFraming => true;

        private protected override void OnClientConnected(ClientConnection client)
        {
            McpSessionState session = new McpSessionState { Owner = client, CanPingClient = true };
            session.Push = (json, token) => WriteToClientAsync(client, json, token);
            client.ProtocolState = session;
        }

        private protected override void OnClientDisconnected(ClientConnection client)
        {
            (client.ProtocolState as McpSessionState)?.CancelAll();
        }

        // Every message goes through the shared MCP processor: batches, initialization order, concurrent requests,
        // cancellation, and per-version serialization.
        private protected override Task ProcessMessageAsync(ClientConnection client, string message, CancellationToken token)
        {
            McpSessionState session = client.ProtocolState as McpSessionState ?? new McpSessionState { Owner = client };
            return _Processor.ProcessAsync(message, session, (json, ct) => WriteToClientAsync(client, json, ct), token);
        }

        /// <summary>
        /// Asynchronously broadcasts a notification to connected clients that completed <c>initialize</c> (MCP sends
        /// nothing but pings before it). A client whose negotiated revision does not define <paramref name="method"/>
        /// is skipped, and the notification is reduced to what each client's revision defines.
        /// </summary>
        /// <param name="method">The name of the notification method. Must not be null or whitespace.</param>
        /// <param name="parameters">The parameters to pass with the notification. Can be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="method"/> is null or whitespace.</exception>
        public override Task BroadcastNotificationAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            return McpServerNotifications.BroadcastAsync(Sessions(), method, parameters, token);
        }

        private IEnumerable<McpSessionState> Sessions()
        {
            return ConnectedClients.Select(client => client.ProtocolState).OfType<McpSessionState>().ToList();
        }
}
}
