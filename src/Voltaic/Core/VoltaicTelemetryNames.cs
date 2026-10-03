namespace Voltaic.Core
{
    using System;

    /// <summary>
    /// Every telemetry name Voltaic emits: the meter and activity source names a host subscribes to, the metric
    /// instrument names, the span names, and the attribute (label) keys and well-known values. These strings are a
    /// public contract: dashboards and alerts are written against them, so they change only in a major release.
    /// See <c>TELEMETRY.md</c> for the full catalog. Thread-safe (constants only).
    /// </summary>
    /// <remarks>
    /// Instrument names are dotted OpenTelemetry-style names. A Prometheus exporter rewrites them to snake case and adds
    /// unit and type suffixes, so <c>voltaic.rpc.server.duration</c> (unit <c>s</c>) becomes
    /// <c>voltaic_rpc_server_duration_seconds</c> and the counter <c>voltaic.server.sessions.opened</c> becomes
    /// <c>voltaic_server_sessions_opened_total</c>.
    /// </remarks>
    public static class VoltaicTelemetryNames
    {
        #region Sources

        /// <summary>
        /// The name of the <see cref="System.Diagnostics.Metrics.Meter"/> every Voltaic metric is recorded on: <c>Voltaic</c>.
        /// </summary>
        public const string MeterName = "Voltaic";

        /// <summary>
        /// The name of the <see cref="System.Diagnostics.ActivitySource"/> every Voltaic span is started on: <c>Voltaic</c>.
        /// </summary>
        public const string ActivitySourceName = "Voltaic";

        #endregion Sources

        #region Metrics

        /// <summary>Histogram (s): duration of one inbound JSON-RPC, MCP, or A2A request or notification, by outcome.</summary>
        public const string RpcServerDuration = "voltaic.rpc.server.duration";

        /// <summary>UpDownCounter ({request}): inbound requests currently being handled.</summary>
        public const string RpcServerActiveRequests = "voltaic.rpc.server.active_requests";

        /// <summary>Counter ({message}): inbound messages rejected before dispatch (parse errors, oversize, invalid framing).</summary>
        public const string RpcServerRejectedMessages = "voltaic.rpc.server.rejected_messages";

        /// <summary>Histogram (s): duration of one outbound JSON-RPC, MCP, or A2A call made by a Voltaic client, by outcome.</summary>
        public const string RpcClientDuration = "voltaic.rpc.client.duration";

        /// <summary>UpDownCounter ({request}): outbound calls currently waiting for their response.</summary>
        public const string RpcClientActiveRequests = "voltaic.rpc.client.active_requests";

        /// <summary>Histogram (s): duration of a client connect (and, for MCP clients, the initialize handshake), by outcome.</summary>
        public const string ClientConnectDuration = "voltaic.client.connect.duration";

        /// <summary>Counter ({connection}): client connections lost while calls may have been in flight.</summary>
        public const string ClientConnectionLosses = "voltaic.client.connection_losses";

        /// <summary>Counter ({recovery}): MCP Streamable HTTP client session recoveries after a 404, by outcome.</summary>
        public const string ClientSessionRecoveries = "voltaic.client.session_recoveries";

        /// <summary>UpDownCounter ({session}): server-side connections (TCP, stdio, WebSocket) and MCP HTTP sessions currently open.</summary>
        public const string ServerSessionsActive = "voltaic.server.sessions.active";

        /// <summary>Counter ({session}): server-side connections and sessions opened.</summary>
        public const string ServerSessionsOpened = "voltaic.server.sessions.opened";

        /// <summary>Counter ({session}): server-side connections and sessions closed, by reason.</summary>
        public const string ServerSessionsClosed = "voltaic.server.sessions.closed";

        /// <summary>Counter ({session}): session requests refused (limit reached, unknown session, missing session, wrong owner), by reason.</summary>
        public const string ServerSessionsRejected = "voltaic.server.sessions.rejected";

        /// <summary>Histogram (s): lifetime of a server-side connection or session, by close reason.</summary>
        public const string ServerSessionDuration = "voltaic.server.session.duration";

        /// <summary>ObservableGauge ({session}): configured session capacity (<c>MaxSessions</c>) summed over running MCP HTTP servers.</summary>
        public const string ServerSessionsLimit = "voltaic.server.sessions.limit";

        /// <summary>UpDownCounter ({message}): notifications queued on server-side connections and not yet delivered.</summary>
        public const string ConnectionQueueDepth = "voltaic.connection.queue.depth";

        /// <summary>Counter ({message}): queued notifications discarded because a connection queue was full.</summary>
        public const string ConnectionQueueDropped = "voltaic.connection.queue.dropped";

        /// <summary>Histogram (s): duration of one request on an HttpListener-based server (MCP HTTP, A2A HTTP), by route and status.</summary>
        public const string HttpServerRequestDuration = "voltaic.http.server.request.duration";

        /// <summary>UpDownCounter ({request}): HTTP requests currently being handled by HttpListener-based servers.</summary>
        public const string HttpServerActiveRequests = "voltaic.http.server.active_requests";

        /// <summary>Counter ({request}): HTTP and WebSocket requests refused by the access gates (loopback, origin, authentication, scope), by reason.</summary>
        public const string HttpAccessDenials = "voltaic.http.access.denials";

        /// <summary>Histogram (s): duration of one MCP <c>tools/call</c>, by tool and outcome.</summary>
        public const string McpToolDuration = "voltaic.mcp.tool.duration";

        /// <summary>Histogram (s): duration of one stage of an MCP tool call (input validation, execution, output validation).</summary>
        public const string McpToolStageDuration = "voltaic.mcp.tool.stage.duration";

        /// <summary>Counter ({validation}): JSON Schema validations of tool input and output, by outcome.</summary>
        public const string McpSchemaValidations = "voltaic.mcp.schema.validations";

        /// <summary>Counter ({decision}): MCP rate-limit decisions (tool calls, completions, log messages), by outcome.</summary>
        public const string McpRateLimitDecisions = "voltaic.mcp.rate_limit.decisions";

        /// <summary>Counter ({notification}): server-to-client MCP notifications, by method and outcome.</summary>
        public const string McpNotificationsSent = "voltaic.mcp.notifications.sent";

        /// <summary>Counter ({ping}): MCP keep-alive pings, by side and outcome.</summary>
        public const string McpPings = "voltaic.mcp.pings";

        /// <summary>Counter ({connection}): connections closed because the peer stopped answering pings.</summary>
        public const string McpPingConnectionFailures = "voltaic.mcp.ping.connection_failures";

        /// <summary>UpDownCounter ({stream}): open MCP server-sent event streams, by kind.</summary>
        public const string McpSseStreamsActive = "voltaic.mcp.sse.streams.active";

        /// <summary>Counter ({event}): SSE events replayed to a client that resumed with <c>Last-Event-ID</c>.</summary>
        public const string McpSseReplayedEvents = "voltaic.mcp.sse.replayed_events";

        /// <summary>UpDownCounter ({subscription}): open MCP <c>subscriptions/listen</c> streams.</summary>
        public const string McpSubscriptionsActive = "voltaic.mcp.subscriptions.active";

        /// <summary>Counter ({process}): MCP stdio server processes launched by <c>McpClient</c>, by outcome.</summary>
        public const string McpClientProcessStarts = "voltaic.mcp.client.process.starts";

        /// <summary>Histogram (s): duration of one A2A agent handler execution, by mode and outcome.</summary>
        public const string A2AAgentDuration = "voltaic.a2a.agent.duration";

        /// <summary>Counter ({transition}): A2A task status updates applied, by the new state.</summary>
        public const string A2ATaskTransitions = "voltaic.a2a.task.transitions";

        /// <summary>Counter ({notification}): A2A push notification deliveries, by final outcome.</summary>
        public const string A2APushDeliveries = "voltaic.a2a.push.deliveries";

        /// <summary>Histogram (s): duration of one A2A push notification stage (<c>queued</c>, <c>deliver</c>).</summary>
        public const string A2APushStageDuration = "voltaic.a2a.push.stage.duration";

        /// <summary>Histogram (s): duration of one HTTP attempt to deliver an A2A push notification, by outcome.</summary>
        public const string A2APushAttemptDuration = "voltaic.a2a.push.attempt.duration";

        /// <summary>UpDownCounter ({notification}): A2A push notifications queued or being delivered.</summary>
        public const string A2APushPending = "voltaic.a2a.push.pending";

        /// <summary>Counter ({url}): A2A push notification webhook URLs or addresses refused (SSRF protection), by reason.</summary>
        public const string A2APushUrlRejections = "voltaic.a2a.push.url_rejections";

        /// <summary>ObservableGauge (s): Unix time of the last successful A2A push notification delivery in this process.</summary>
        public const string A2APushLastSuccess = "voltaic.a2a.push.last_success";

        /// <summary>ObservableGauge (no unit): always 1, labeled with the Voltaic version and .NET runtime.</summary>
        public const string BuildInfo = "voltaic.build.info";

        #endregion Metrics

        #region Spans

        /// <summary>Span: the HTTP layer of one request on an HttpListener-based server is named <c>{method} {route}</c>; this is the route used for unmatched paths.</summary>
        public const string SpanHttpUnmatchedRoute = "_OTHER";

        /// <summary>Span name prefix for a stage of a workflow, as in <c>stage:validate_input</c>.</summary>
        public const string SpanStagePrefix = "stage:";

        /// <summary>Span: one A2A agent handler execution.</summary>
        public const string SpanA2AAgentExecute = "a2a.agent execute";

        /// <summary>Span: one A2A push notification delivery (all attempts).</summary>
        public const string SpanA2APushDeliver = "a2a.push deliver";

        /// <summary>Span: one HTTP attempt to deliver an A2A push notification.</summary>
        public const string SpanA2APushAttempt = "a2a.push POST";

        /// <summary>Span: launching an MCP stdio server process.</summary>
        public const string SpanMcpLaunchServer = "mcp.stdio launch_server";

        /// <summary>Span: an MCP Streamable HTTP client recovering a lost session.</summary>
        public const string SpanMcpSessionRecover = "mcp.http recover_session";

        /// <summary>Span name suffix for a client connect, as in <c>mcp.websocket connect</c>.</summary>
        public const string SpanConnectSuffix = " connect";

        #endregion Spans

        #region Attributes

        /// <summary>Attribute: the protocol (<c>jsonrpc</c>, <c>mcp</c>, <c>a2a</c>).</summary>
        public const string AttrProtocol = "voltaic.protocol";

        /// <summary>Attribute: the transport or binding (<c>tcp</c>, <c>stdio</c>, <c>websocket</c>, <c>http</c>, <c>jsonrpc</c>, <c>http_json</c>, <c>grpc</c>).</summary>
        public const string AttrTransport = "voltaic.transport";

        /// <summary>Attribute: the outcome of an operation (for example <c>success</c>, <c>error</c>, <c>cancelled</c>, <c>timeout</c>).</summary>
        public const string AttrOutcome = "voltaic.outcome";

        /// <summary>Attribute: why something was refused, closed, or dropped.</summary>
        public const string AttrReason = "voltaic.reason";

        /// <summary>Attribute: a bounded sub-kind (rate-limit kind, stream kind, validation target, agent mode).</summary>
        public const string AttrKind = "voltaic.kind";

        /// <summary>Attribute: the stage of a workflow or pipeline.</summary>
        public const string AttrStage = "voltaic.stage";

        /// <summary>Attribute: which side of a connection recorded a measurement (<c>server</c>, <c>client</c>).</summary>
        public const string AttrRole = "voltaic.role";

        /// <summary>Attribute: the Voltaic version (build info).</summary>
        public const string AttrVersion = "voltaic.version";

        /// <summary>Attribute: the .NET runtime description (build info).</summary>
        public const string AttrRuntime = "voltaic.runtime";

        /// <summary>Attribute (OpenTelemetry): the RPC system, always <c>jsonrpc</c> for Voltaic spans.</summary>
        public const string AttrRpcSystem = "rpc.system";

        /// <summary>Attribute (OpenTelemetry): the JSON-RPC or A2A method name; unrecognized server-side methods are reported as <c>_OTHER</c>.</summary>
        public const string AttrRpcMethod = "rpc.method";

        /// <summary>Attribute (OpenTelemetry): the JSON-RPC error code of a failed request (spans only).</summary>
        public const string AttrJsonRpcErrorCode = "rpc.jsonrpc.error_code";

        /// <summary>Attribute (OpenTelemetry): the JSON-RPC request ID (spans only).</summary>
        public const string AttrJsonRpcRequestId = "rpc.jsonrpc.request_id";

        /// <summary>Attribute (OpenTelemetry): the error class: a JSON-RPC error code, an HTTP status code, or an exception type name.</summary>
        public const string AttrErrorType = "error.type";

        /// <summary>Attribute (OpenTelemetry MCP): the MCP method name (spans only).</summary>
        public const string AttrMcpMethodName = "mcp.method.name";

        /// <summary>Attribute (OpenTelemetry MCP): the MCP protocol revision serving the request (spans only).</summary>
        public const string AttrMcpProtocolVersion = "mcp.protocol.version";

        /// <summary>Attribute (OpenTelemetry GenAI): the name of a registered MCP tool.</summary>
        public const string AttrToolName = "gen_ai.tool.name";

        /// <summary>Attribute (OpenTelemetry GenAI): the name of an MCP prompt (spans only).</summary>
        public const string AttrPromptName = "gen_ai.prompt.name";

        /// <summary>Attribute (OpenTelemetry): the HTTP request method.</summary>
        public const string AttrHttpMethod = "http.request.method";

        /// <summary>Attribute (OpenTelemetry): the HTTP route template (a configured endpoint path, never a raw path).</summary>
        public const string AttrHttpRoute = "http.route";

        /// <summary>Attribute (OpenTelemetry): the HTTP response status code.</summary>
        public const string AttrHttpStatusCode = "http.response.status_code";

        /// <summary>Attribute (OpenTelemetry): the remote host of an outbound call (spans only).</summary>
        public const string AttrServerAddress = "server.address";

        /// <summary>Attribute (OpenTelemetry): the A2A task state reached by a status update.</summary>
        public const string AttrA2ATaskState = "a2a.task.state";

        /// <summary>Attribute: the A2A task ID (spans only).</summary>
        public const string AttrA2ATaskId = "a2a.task.id";

        /// <summary>Attribute: the attempt number of a retried operation (spans only).</summary>
        public const string AttrAttempt = "voltaic.attempt";

        #endregion Attributes

        #region Values

        /// <summary>Value of <see cref="AttrRpcMethod"/> and similar labels for names Voltaic does not recognize, which keeps label cardinality bounded.</summary>
        public const string OtherValue = "_OTHER";

        /// <summary>Protocol value: plain JSON-RPC 2.0.</summary>
        public const string ProtocolJsonRpc = "jsonrpc";

        /// <summary>Protocol value: Model Context Protocol.</summary>
        public const string ProtocolMcp = "mcp";

        /// <summary>Protocol value: Agent2Agent.</summary>
        public const string ProtocolA2A = "a2a";

        /// <summary>Transport value: TCP.</summary>
        public const string TransportTcp = "tcp";

        /// <summary>Transport value: standard input and output.</summary>
        public const string TransportStdio = "stdio";

        /// <summary>Transport value: WebSocket.</summary>
        public const string TransportWebSocket = "websocket";

        /// <summary>Transport value: HTTP (MCP Streamable HTTP and the legacy <c>/rpc</c> and <c>/events</c> endpoints).</summary>
        public const string TransportHttp = "http";

        /// <summary>Transport value: the A2A JSON-RPC binding.</summary>
        public const string TransportA2AJsonRpc = "jsonrpc";

        /// <summary>Transport value: the A2A HTTP+JSON (REST) binding.</summary>
        public const string TransportA2AHttpJson = "http_json";

        /// <summary>Transport value: the A2A gRPC binding.</summary>
        public const string TransportA2AGrpc = "grpc";

        /// <summary>Outcome value: the operation succeeded.</summary>
        public const string OutcomeSuccess = "success";

        /// <summary>Outcome value: the operation failed.</summary>
        public const string OutcomeError = "error";

        /// <summary>Outcome value: the operation was cancelled by the caller or the peer.</summary>
        public const string OutcomeCancelled = "cancelled";

        /// <summary>Outcome value: the operation timed out.</summary>
        public const string OutcomeTimeout = "timeout";

        #endregion Values
    }
}
