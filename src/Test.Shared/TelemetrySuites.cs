namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.A2A;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Proves Voltaic's built-in telemetry: every instrumented area emits its metrics and spans to an in-memory
    /// collector subscribed to the <c>Voltaic</c> meter and activity source, failure paths carry their outcome and
    /// <c>error.type</c>, W3C trace context crosses each transport, and nothing breaks when no one listens, when telemetry
    /// is switched off, or when a listener throws.
    /// </summary>
    public static class TelemetrySuites
    {
        private const string Protocol = VoltaicTelemetryNames.AttrProtocol;
        private const string Transport = VoltaicTelemetryNames.AttrTransport;
        private const string Method = VoltaicTelemetryNames.AttrRpcMethod;
        private const string Outcome = VoltaicTelemetryNames.AttrOutcome;
        private const string ErrorType = VoltaicTelemetryNames.AttrErrorType;
        private const string Reason = VoltaicTelemetryNames.AttrReason;

        private static readonly object _EchoSchema = new
        {
            type = "object",
            properties = new { text = new { type = "string" } },
            required = new[] { "text" }
        };

        /// <summary>
        /// Cases.
        /// </summary>
        public static TestSuiteDescriptor Cases()
        {
            const string suiteId = "Telemetry";
            return new TestSuiteDescriptor(
                suiteId,
                "Built-in metrics and traces",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "NamesAndBuildInfo", "The meter and activity source are named Voltaic and voltaic.build.info reports the version", async ct =>
                    {
                        TestAssert.Equal("Voltaic", VoltaicTelemetryNames.MeterName);
                        TestAssert.Equal("Voltaic", VoltaicTelemetryNames.ActivitySourceName);
                        TestAssert.False(String.IsNullOrEmpty(VoltaicTelemetry.Version), "The version is known.");
                        TestAssert.True(VoltaicTelemetry.Enabled && VoltaicTelemetry.PropagateTraceContext, "Telemetry and propagation are on by default.");

                        // A Voltaic type must be loaded for the meter to exist.
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct).ConfigureAwait(false);
                        using TelemetryCapture capture = new TelemetryCapture();
                        capture.Observe();
                        CapturedMeasurement? info = capture.Find(VoltaicTelemetryNames.BuildInfo).FirstOrDefault();
                        TestAssert.NotNull(info, "voltaic.build.info is observed.");
                        TestAssert.Equal(VoltaicTelemetry.Version, info!.Tag(VoltaicTelemetryNames.AttrVersion)?.ToString(), "It is labeled with the version.");
                        TestAssert.Equal(1.0, info.Value, "Its value is 1.");
                    }),

                    Case(suiteId, "JsonRpcTcpServerAndClient", "A JSON-RPC call records client and server durations, connect, and connection lifecycle, with client and server spans", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server => server.RegisterMethod("telemetry.echo", args => "ok"), false).ConfigureAwait(false);
                        using (JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false))
                        {
                            TestAssert.Equal("ok", await client.CallAsync<string>("telemetry.echo", null, 5000, ct).ConfigureAwait(false));
                        }

                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.RpcServerDuration, ct, Protocol, "jsonrpc", Transport, "tcp", Method, "telemetry.echo", Outcome, "success").ConfigureAwait(false)).Count > 0, "The server records the request.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcClientDuration, Protocol, "jsonrpc", Method, "telemetry.echo", Outcome, "success").Count > 0, "The client records the call.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ClientConnectDuration, Protocol, "jsonrpc", Transport, "tcp", Outcome, "success").Count > 0, "The connect is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ServerSessionsOpened, Protocol, "jsonrpc", Transport, "tcp").Count > 0, "The connection is counted as opened.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcServerActiveRequests).Any(m => m.Value == 1) && capture.Find(VoltaicTelemetryNames.RpcServerActiveRequests).Any(m => m.Value == -1), "The in-flight count rises and falls.");
                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.ServerSessionsClosed, ct, Protocol, "jsonrpc", Reason, "client_closed").ConfigureAwait(false)).Count > 0, "The disconnect is recorded with its reason.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ServerSessionDuration, Protocol, "jsonrpc").Count > 0, "The connection lifetime is recorded.");

                        TestAssert.True(capture.Spans.Any(s => s.DisplayName == "telemetry.echo" && s.Kind == ActivityKind.Client), "The client span is named after the method.");
                        TestAssert.True(capture.Spans.Any(s => s.DisplayName == "telemetry.echo" && s.Kind == ActivityKind.Server && s.Status == ActivityStatusCode.Ok), "The server span is named after the method and succeeds.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcServerDuration).All(m => m.Unit == "s"), "Durations are in seconds.");
                    }),

                    Case(suiteId, "JsonRpcFailurePaths", "An unknown method is an error with error.type -32601 under the bounded _OTHER label; a refused connect is a failed connect", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, null, false).ConfigureAwait(false);
                        using (JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false))
                        {
                            await AssertThrowsAsync(() => client.CallAsync<string>("no.such.method-" + Guid.NewGuid().ToString("N"), null, 5000, ct)).ConfigureAwait(false);
                        }

                        IReadOnlyList<CapturedMeasurement> server = await capture.WaitForAsync(VoltaicTelemetryNames.RpcServerDuration, ct, Protocol, "jsonrpc", Method, "_OTHER", Outcome, "error", ErrorType, "-32601").ConfigureAwait(false);
                        TestAssert.True(server.Count > 0, "The server records an unknown method as _OTHER with -32601.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcClientDuration, Protocol, "jsonrpc", Outcome, "error", ErrorType, "-32601").Count > 0, "The client records the error code.");
                        TestAssert.True(capture.Spans.Any(s => s.Kind == ActivityKind.Server && s.DisplayName == "_OTHER" && s.Status == ActivityStatusCode.Error), "The server span is an error.");

                        int closedPort = TestPorts.GetFreePort();
                        using JsonRpcClient refused = new JsonRpcClient();
                        TestAssert.False(await refused.ConnectAsync("127.0.0.1", closedPort, ct).ConfigureAwait(false), "Nothing listens on the port.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ClientConnectDuration, Protocol, "jsonrpc", Outcome, "error").Count > 0, "The failed connect is recorded.");
                    }),

                    Case(suiteId, "McpToolCallStagesAndPropagation", "An MCP tool call over TCP records the tool, its stages, the schema check, and one trace from the client span to the server span", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, server => server.RegisterTool("telemetry_echo", "Echoes text.", _EchoSchema, args => "echo:" + args?.GetString("text"))).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        await client.CallAsync<JsonElement>("tools/call", new { name = "telemetry_echo", arguments = new { text = "hi" } }, 5000, ct).ConfigureAwait(false);

                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.McpToolDuration, ct, VoltaicTelemetryNames.AttrToolName, "telemetry_echo", Outcome, "success").ConfigureAwait(false)).Count > 0, "The tool call is recorded by tool and outcome.");
                        foreach (string stage in new[] { "validate_input", "execute" })
                        {
                            TestAssert.True(capture.Find(VoltaicTelemetryNames.McpToolStageDuration, VoltaicTelemetryNames.AttrToolName, "telemetry_echo", VoltaicTelemetryNames.AttrStage, stage).Count > 0, $"The {stage} stage is recorded.");
                            TestAssert.True(capture.Spans.Any(s => s.DisplayName == "stage:" + stage), $"The stage:{stage} span exists.");
                        }

                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpSchemaValidations, VoltaicTelemetryNames.AttrKind, "input", Outcome, "valid").Count > 0, "The input validation is counted.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcServerDuration, Protocol, "mcp", Transport, "tcp", Method, "tools/call", Outcome, "success").Count > 0, "The MCP request is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcServerDuration, Protocol, "mcp", Method, "initialize").Count > 0, "The handshake is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ClientConnectDuration, Protocol, "mcp", Transport, "tcp", Outcome, "success").Count > 0, "The MCP connect (with initialize) is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ServerSessionsOpened, Protocol, "mcp", Transport, "tcp").Count > 0, "The MCP connection is counted.");

                        Activity? clientSpan = capture.Spans.FirstOrDefault(s => s.Kind == ActivityKind.Client && s.DisplayName == "tools/call");
                        Activity? serverSpan = await capture.WaitForSpanAsync(s => s.Kind == ActivityKind.Server && s.DisplayName == "tools/call telemetry_echo", ct).ConfigureAwait(false);
                        TestAssert.NotNull(clientSpan, "The client span exists.");
                        TestAssert.NotNull(serverSpan, "The server span is named after the tool.");
                        TestAssert.Equal(clientSpan!.TraceId, serverSpan!.TraceId, "params._meta.traceparent carries the trace across TCP.");
                        TestAssert.Equal(clientSpan.SpanId, serverSpan.ParentSpanId, "The server span is the client span's child.");
                        TestAssert.Equal("telemetry_echo", serverSpan.GetTagItem(VoltaicTelemetryNames.AttrToolName)?.ToString(), "The server span carries gen_ai.tool.name.");
                    }),

                    Case(suiteId, "McpToolFailurePaths", "Tool failures are classified: invalid arguments, handler exceptions, tool errors, unknown tools, and rate limits", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, server =>
                        {
                            server.RegisterTool("telemetry_echo", "Echoes text.", _EchoSchema, args => "echo");
                            server.RegisterTool("telemetry_throw", "Throws.", new { type = "object" }, args => throw new InvalidOperationException("secret-value"));
                            server.RegisterTool("telemetry_limited", "Limited.", new { type = "object" }, args => "ok");
                        }).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        await client.CallAsync<JsonElement>("tools/call", new { name = "telemetry_echo", arguments = new { } }, 5000, ct).ConfigureAwait(false);
                        await client.CallAsync<JsonElement>("tools/call", new { name = "telemetry_throw", arguments = new { } }, 5000, ct).ConfigureAwait(false);
                        await AssertThrowsAsync(() => client.CallAsync<JsonElement>("tools/call", new { name = "telemetry_missing", arguments = new { } }, 5000, ct)).ConfigureAwait(false);

                        // The limit is live, so it applies from the next call on.
                        ((McpTcpServer)fixture.Server).RateLimits.ToolCallsPerSecond = 1;
                        for (int i = 0; i < 3; i++)
                        {
                            await client.CallAsync<JsonElement>("tools/call", new { name = "telemetry_limited", arguments = new { } }, 5000, ct).ConfigureAwait(false);
                        }

                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.McpToolDuration, ct, VoltaicTelemetryNames.AttrToolName, "telemetry_echo", Outcome, "invalid_arguments").ConfigureAwait(false)).Count > 0, "Arguments that fail the schema are invalid_arguments.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpSchemaValidations, VoltaicTelemetryNames.AttrKind, "input", Outcome, "invalid").Count > 0, "The failed validation is counted.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpToolDuration, VoltaicTelemetryNames.AttrToolName, "telemetry_throw", Outcome, "handler_exception", ErrorType, "InvalidOperationException").Count > 0, "A throwing handler is handler_exception with its type.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpToolStageDuration, VoltaicTelemetryNames.AttrStage, "execute", Outcome, "error").Count > 0, "The execute stage is an error.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpToolDuration, VoltaicTelemetryNames.AttrToolName, "_OTHER", Outcome, "protocol_error", ErrorType, "-32602").Count > 0, "An unknown tool is protocol_error under _OTHER.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpRateLimitDecisions, VoltaicTelemetryNames.AttrKind, "tools", Outcome, "rejected").Count > 0, "The rate limiter's refusal is counted.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpToolDuration, VoltaicTelemetryNames.AttrToolName, "telemetry_limited", Outcome, "rate_limited").Count > 0, "A refused call is rate_limited.");

                        Activity? execute = capture.Spans.FirstOrDefault(s => s.DisplayName == "stage:execute" && s.Status == ActivityStatusCode.Error);
                        TestAssert.NotNull(execute, "The failed execute span is an error.");
                        TestAssert.True(execute!.Events.Any(e => e.Name == "exception"), "It carries an exception event.");
                        TestAssert.False(capture.Spans.Any(s => s.Events.Any(e => e.Tags.Any(t => t.Value?.ToString()?.Contains("secret-value") == true)) || s.TagObjects.Any(t => t.Value?.ToString()?.Contains("secret-value") == true)), "Exception messages never reach spans.");
                    }),

                    Case(suiteId, "McpHttpServerAndClient", "MCP Streamable HTTP records the HTTP layer by route, sessions, the session limit, and one trace from client to server", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.RegisterTool("telemetry_echo", "Echoes text.", _EchoSchema, args => "echo")).ConfigureAwait(false);
                        using (McpHttpClient client = new McpHttpClient())
                        {
                            TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                            await client.CallAsync<JsonElement>("tools/call", new { name = "telemetry_echo", arguments = new { text = "x" } }, 5000, ct).ConfigureAwait(false);

                            capture.Observe();
                            CapturedMeasurement? limit = capture.Find(VoltaicTelemetryNames.ServerSessionsLimit).LastOrDefault();
                            TestAssert.True(limit != null && limit.Value >= fixture.Server.MaxSessions, "The session capacity gauge reports MaxSessions.");
                            client.Disconnect();
                        }

                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.HttpServerRequestDuration, ct, Protocol, "mcp", VoltaicTelemetryNames.AttrHttpMethod, "POST", VoltaicTelemetryNames.AttrHttpRoute, "/mcp", VoltaicTelemetryNames.AttrHttpStatusCode, "200").ConfigureAwait(false)).Count > 0, "The HTTP layer is recorded by route template and status.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ServerSessionsOpened, Protocol, "mcp", Transport, "http").Count > 0, "The session is counted as opened.");
                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.ServerSessionsClosed, ct, Protocol, "mcp", Transport, "http", Reason, "deleted").ConfigureAwait(false)).Count > 0, "DELETE closes the session with reason deleted.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcClientDuration, Protocol, "mcp", Transport, "http", Method, "tools/call", Outcome, "success").Count > 0, "The HTTP client records the call.");

                        Activity? clientSpan = capture.Spans.FirstOrDefault(s => s.Kind == ActivityKind.Client && s.DisplayName == "tools/call");
                        Activity? httpSpan = capture.Spans.FirstOrDefault(s => s.Kind == ActivityKind.Server && s.DisplayName == "POST /mcp" && clientSpan != null && s.TraceId == clientSpan.TraceId);
                        Activity? mcpSpan = capture.Spans.FirstOrDefault(s => s.DisplayName == "tools/call telemetry_echo");
                        TestAssert.NotNull(clientSpan, "The client span exists.");
                        TestAssert.NotNull(httpSpan, "The traceparent header joins the HTTP server span to the client's trace.");
                        TestAssert.True(mcpSpan != null && mcpSpan.ParentSpanId == httpSpan!.SpanId, "The MCP server span nests under the HTTP span.");
                    }),

                    Case(suiteId, "McpHttpRejections", "Refused origins, unknown sessions, and missing sessions are counted by reason", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string ping = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

                        using (HttpRequestMessage evil = new HttpRequestMessage(HttpMethod.Post, fixture.BaseUrl + "/mcp"))
                        {
                            evil.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
                            evil.Content = new StringContent(ping, Encoding.UTF8, "application/json");
                            using HttpResponseMessage response = await fixture.Client.SendAsync(evil, ct).ConfigureAwait(false);
                            TestAssert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                        }

                        using (HttpRequestMessage unknown = new HttpRequestMessage(HttpMethod.Post, fixture.BaseUrl + "/mcp"))
                        {
                            unknown.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, "no-such-session");
                            unknown.Headers.Accept.ParseAdd("application/json");
                            unknown.Headers.Accept.ParseAdd("text/event-stream");
                            unknown.Content = new StringContent(ping, Encoding.UTF8, "application/json");
                            using HttpResponseMessage response = await fixture.Client.SendAsync(unknown, ct).ConfigureAwait(false);
                            TestAssert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                        }

                        using (HttpRequestMessage sessionless = new HttpRequestMessage(HttpMethod.Post, fixture.BaseUrl + "/mcp"))
                        {
                            sessionless.Headers.Accept.ParseAdd("application/json");
                            sessionless.Headers.Accept.ParseAdd("text/event-stream");
                            sessionless.Content = new StringContent(ping, Encoding.UTF8, "application/json");
                            using HttpResponseMessage response = await fixture.Client.SendAsync(sessionless, ct).ConfigureAwait(false);
                            TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                        }

                        TestAssert.True(capture.Find(VoltaicTelemetryNames.HttpAccessDenials, Protocol, "mcp", Reason, "origin").Count > 0, "The origin refusal is counted.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ServerSessionsRejected, Protocol, "mcp", Reason, "not_found").Count > 0, "The unknown session is counted.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ServerSessionsRejected, Protocol, "mcp", Reason, "session_required").Count > 0, "The missing session is counted.");
                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.HttpServerRequestDuration, ct, VoltaicTelemetryNames.AttrHttpStatusCode, "403").ConfigureAwait(false)).Count > 0, "The refused request is still timed with its status.");
                    }),

                    Case(suiteId, "McpWebSocketNotificationsAndPings", "WebSocket MCP records sessions and requests; list_changed notifications and keep-alive pings are counted", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server => server.RegisterTool("telemetry_echo", "Echoes text.", _EchoSchema, args => "echo")).ConfigureAwait(false);
                        using (McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false))
                        {
                            await client.CallAsync<JsonElement>("tools/list", null, 5000, ct).ConfigureAwait(false);
                        }

                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.RpcServerDuration, ct, Protocol, "mcp", Transport, "websocket", Method, "tools/list").ConfigureAwait(false)).Count > 0, "The WebSocket request is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ServerSessionsOpened, Protocol, "mcp", Transport, "websocket").Count > 0, "The WebSocket connection is counted.");
                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.ServerSessionsClosed, ct, Protocol, "mcp", Transport, "websocket").ConfigureAwait(false)).Count > 0, "Its close is counted.");

                        await using TcpJsonRpcFixture tcp = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        McpTcpClient pinging = new McpTcpClient { PingIntervalMs = 100 };
                        using (pinging)
                        {
                            TestAssert.True(await pinging.ConnectAsync("127.0.0.1", tcp.Port, ct).ConfigureAwait(false), "The client connects.");
                            await ((McpTcpServer)tcp.Server).NotifyToolsChangedAsync(ct).ConfigureAwait(false);
                            TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.McpPings, ct, VoltaicTelemetryNames.AttrRole, "client", Outcome, "answered").ConfigureAwait(false)).Count > 0, "The client's keep-alive pings are counted.");
                        }

                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpNotificationsSent, Method, "notifications/tools/list_changed", Outcome, "sent").Count > 0, "The list_changed notification is counted.");
                    }),

                    Case(suiteId, "StdioLaunchFailure", "A stdio server process that cannot start is a failed process start and a failed connect", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        using McpClient client = new McpClient();
                        bool launched = await client.LaunchServerAsync("/nonexistent/voltaic-telemetry-" + Guid.NewGuid().ToString("N"), Array.Empty<string>(), ct).ConfigureAwait(false);
                        TestAssert.False(launched, "The executable does not exist.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.McpClientProcessStarts, Outcome, "error").Count > 0, "The failed start is counted.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.ClientConnectDuration, Protocol, "mcp", Transport, "stdio", Outcome, "error").Count > 0, "The failed connect is recorded.");
                        TestAssert.True(capture.Spans.Any(s => s.DisplayName == VoltaicTelemetryNames.SpanMcpLaunchServer && s.Status == ActivityStatusCode.Error), "The launch span is an error.");
                    }),

                    Case(suiteId, "ConnectionQueue", "Queued notifications move the queue depth and a full queue counts its drops", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        using (ClientConnection connection = new ClientConnection("telemetry-queue", ClientConnectionTypeEnum.Http) { MaxQueueSize = 1 })
                        {
                            for (int i = 0; i < 3; i++) connection.Enqueue(new JsonRpcRequest { Method = "notifications/message" });
                            TestAssert.Equal(1, connection.Count(), "The queue holds one notification.");
                        }

                        double depth = capture.Find(VoltaicTelemetryNames.ConnectionQueueDepth, Transport, "http").Sum(m => m.Value);
                        TestAssert.Equal(0.0, depth, "Enqueues, drops, and disposal balance to zero.");
                        TestAssert.Equal(2, capture.Find(VoltaicTelemetryNames.ConnectionQueueDropped, Transport, "http").Count, "Two notifications were dropped.");
                        await Task.CompletedTask.ConfigureAwait(false);
                    }),

                    Case(suiteId, "A2AJsonRpcAgentAndTasks", "An A2A message records the HTTP layer, the JSON-RPC binding, the agent stage, task transitions, and one trace", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using A2ATelemetryFixture fixture = await A2ATelemetryFixture.StartAsync(ct, new CompletingAgent()).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);
                        SendMessageResponse response = await client.SendMessageAsync(MessageRequest("hello"), ct).ConfigureAwait(false);
                        TestAssert.NotNull(response.Task, "The agent returns a task.");

                        await AssertThrowsAsync(() => client.GetTaskAsync(new GetTaskRequest { Id = "missing-" + Guid.NewGuid().ToString("N") }, ct)).ConfigureAwait(false);

                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.RpcServerDuration, ct, Protocol, "a2a", Transport, "jsonrpc", Method, A2AProtocol.SendMessage, Outcome, "success").ConfigureAwait(false)).Count > 0, "The binding records SendMessage.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcServerDuration, Protocol, "a2a", Method, A2AProtocol.GetTask, Outcome, "error", ErrorType, ((int)A2AErrorCode.TaskNotFound).ToString()).Count > 0, "An unknown task is an error with its A2A code.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.A2AAgentDuration, VoltaicTelemetryNames.AttrKind, "blocking", Outcome, "success").Count > 0, "The agent stage is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.A2ATaskTransitions).Count > 0, "Task transitions are counted.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.HttpServerRequestDuration, Protocol, "a2a", VoltaicTelemetryNames.AttrHttpRoute, "/a2a").Count > 0, "The HTTP layer is recorded by route.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcClientDuration, Protocol, "a2a", Method, A2AProtocol.SendMessage, Outcome, "success").Count > 0, "The client records the call.");

                        Activity? clientSpan = capture.Spans.FirstOrDefault(s => s.Kind == ActivityKind.Client && s.DisplayName == A2AProtocol.SendMessage);
                        Activity? agentSpan = capture.Spans.FirstOrDefault(s => s.DisplayName == VoltaicTelemetryNames.SpanA2AAgentExecute);
                        TestAssert.True(clientSpan != null && agentSpan != null && agentSpan.TraceId == clientSpan.TraceId, "The agent span is in the client's trace.");
                    }),

                    Case(suiteId, "A2AFailingAgentAndRest", "A throwing agent is a failed agent stage; HTTP+JSON requests are recorded under route templates", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        await using A2ATelemetryFixture fixture = await A2ATelemetryFixture.StartAsync(ct, new FailingAgent()).ConfigureAwait(false);
                        using A2AHttpJsonClient rest = new A2AHttpJsonClient(fixture.BaseUrl, fixture.Client);
                        await AssertThrowsAsync(() => rest.SendMessageAsync(MessageRequest("boom"), ct)).ConfigureAwait(false);
                        await AssertThrowsAsync(() => rest.GetTaskAsync(new GetTaskRequest { Id = "missing" }, ct)).ConfigureAwait(false);

                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.A2AAgentDuration, ct, Outcome, "error", ErrorType, "InvalidOperationException").ConfigureAwait(false)).Count > 0, "The failed agent stage is recorded with its type.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcServerDuration, Protocol, "a2a", Transport, "http_json", Method, A2AProtocol.GetTask, Outcome, "error").Count > 0, "The REST binding records the failed GetTask.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.HttpServerRequestDuration, VoltaicTelemetryNames.AttrHttpRoute, "/tasks/{id}").Count > 0, "The task route is a template, never the raw path.");
                        TestAssert.False(capture.Find(VoltaicTelemetryNames.HttpServerRequestDuration).Any(m => m.Tag(VoltaicTelemetryNames.AttrHttpRoute)?.ToString()?.Contains("missing") == true), "IDs never reach metric labels.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcClientDuration, Protocol, "a2a", Transport, "http_json", Method, A2AProtocol.GetTask, Outcome, "error").Count > 0, "The REST client records the failure.");
                    }),

                    Case(suiteId, "A2APushPipeline", "Push notifications record queued and deliver stages, attempts, deliveries, the last success, URL refusals, and carry traceparent", async ct =>
                    {
                        using TelemetryCapture capture = new TelemetryCapture();
                        using WebhookReceiver receiver = new WebhookReceiver();
                        await using (A2ATelemetryFixture fixture = await A2ATelemetryFixture.StartAsync(ct, new CompletingAgent(), server => server.PushNotificationUrlValidator = uri => uri.IsLoopback).ConfigureAwait(false))
                        {
                            using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);
                            SendMessageRequest request = MessageRequest("push");
                            request.Configuration = new SendMessageConfiguration { PushNotificationConfig = new PushNotificationConfig { Url = receiver.Url } };
                            await client.SendMessageAsync(request, ct).ConfigureAwait(false);
                            TestAssert.True(await receiver.WaitForAsync(1, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false), "The webhook receives an event.");
                            TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.A2APushDeliveries, ct, Outcome, "success").ConfigureAwait(false)).Count > 0, "The delivery is counted.");
                        }

                        TestAssert.True(capture.Find(VoltaicTelemetryNames.A2APushStageDuration, VoltaicTelemetryNames.AttrStage, "queued").Count > 0, "The queued stage is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.A2APushStageDuration, VoltaicTelemetryNames.AttrStage, "deliver").Count > 0, "The deliver stage is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.A2APushAttemptDuration, Outcome, "success").Count > 0, "The attempt is recorded.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.A2APushPending).Any(m => m.Value == 1) && capture.Find(VoltaicTelemetryNames.A2APushPending).Any(m => m.Value == -1), "The pending count rises and falls.");
                        TestAssert.True(receiver.Requests.Any(item => !String.IsNullOrEmpty(item.Headers["traceparent"])), "The webhook request carries traceparent.");
                        TestAssert.True(capture.Spans.Any(s => s.DisplayName == VoltaicTelemetryNames.SpanA2APushDeliver && s.Kind == ActivityKind.Consumer), "The deliver span exists.");
                        capture.Observe();
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.A2APushLastSuccess).Any(m => m.Value > 0), "The last-success gauge is set.");

                        await using (A2ATelemetryFixture strict = await A2ATelemetryFixture.StartAsync(ct, new CompletingAgent()).ConfigureAwait(false))
                        {
                            using A2AClient client = new A2AClient(strict.EndpointUrl, strict.Client);
                            SendMessageRequest request = MessageRequest("ssrf");
                            request.Configuration = new SendMessageConfiguration { PushNotificationConfig = new PushNotificationConfig { Url = "http://127.0.0.1:9/hook" } };
                            await AssertThrowsAsync(() => client.SendMessageAsync(request, ct)).ConfigureAwait(false);
                        }

                        TestAssert.True(capture.Find(VoltaicTelemetryNames.A2APushUrlRejections, Reason, "address_policy").Count > 0, "The loopback webhook is refused by the address policy.");
                    }),

                    Case(suiteId, "A2AGrpcWithWatson", "The gRPC binding records per-method operations nested under Watson's own server span", async ct =>
                    {
                        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
                        using TelemetryCapture capture = new TelemetryCapture(includeWatson: true);
                        int port = TestPorts.GetFreePort();
                        string baseUrl = $"http://localhost:{port}";
                        using A2AGrpcServer server = new A2AGrpcServer("localhost", port, A2ATelemetryFixture.Card(baseUrl), new CompletingAgent());
                        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        await server.StartAsync(stop.Token).ConfigureAwait(false);
                        try
                        {
                            using HttpClient http = new HttpClient(new SocketsHttpHandler());
                            using A2AGrpcClient client = new A2AGrpcClient(baseUrl, http);
                            SendMessageResponse response = await RetryAsync(() => client.SendMessageAsync(MessageRequest("grpc"), ct), ct).ConfigureAwait(false);
                            TestAssert.NotNull(response.Task, "The gRPC call returns a task.");
                        }
                        finally
                        {
                            server.Stop();
                            stop.Cancel();
                        }

                        TestAssert.True((await capture.WaitForAsync(VoltaicTelemetryNames.RpcServerDuration, ct, Protocol, "a2a", Transport, "grpc", Method, A2AProtocol.SendMessage, Outcome, "success").ConfigureAwait(false)).Count > 0, "The gRPC binding records SendMessage.");
                        TestAssert.True(capture.Find(VoltaicTelemetryNames.RpcClientDuration, Protocol, "a2a", Transport, "grpc", Method, A2AProtocol.SendMessage, Outcome, "success").Count > 0, "The gRPC client records the call.");
                        Activity? binding = capture.Spans.FirstOrDefault(s => s.DisplayName == "a2a " + A2AProtocol.SendMessage);
                        Activity? watson = capture.Spans.FirstOrDefault(s => s.Source.Name == "Watson" && binding != null && s.SpanId == binding.ParentSpanId);
                        TestAssert.NotNull(watson, "Watson's server span (telemetry on) is the parent of the binding span.");
                    }),

                    Case(suiteId, "NoListenerDisabledAndThrowingListener", "Nothing breaks with no listener, nothing is recorded when Enabled is false, and a throwing listener never reaches the caller", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, server => server.RegisterTool("telemetry_echo", "Echoes text.", _EchoSchema, args => "echo")).ConfigureAwait(false);
                        using (JsonRpcClient quiet = await fixture.ConnectClientAsync(ct).ConfigureAwait(false))
                        {
                            await quiet.CallAsync<JsonElement>("tools/call", new { name = "telemetry_echo", arguments = new { text = "x" } }, 5000, ct).ConfigureAwait(false);
                        }

                        using (TelemetryCapture capture = new TelemetryCapture())
                        {
                            VoltaicTelemetry.Enabled = false;
                            try
                            {
                                using JsonRpcClient off = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                                await off.CallAsync<JsonElement>("tools/call", new { name = "telemetry_echo", arguments = new { text = "x" } }, 5000, ct).ConfigureAwait(false);
                                capture.Observe();
                            }
                            finally
                            {
                                VoltaicTelemetry.Enabled = true;
                            }

                            await Task.Delay(100, ct).ConfigureAwait(false);
                            TestAssert.Equal(0, capture.Find(VoltaicTelemetryNames.McpToolDuration).Count + capture.Find(VoltaicTelemetryNames.RpcClientDuration).Count + capture.Find(VoltaicTelemetryNames.BuildInfo).Count, "Nothing is recorded while telemetry is off.");
                            TestAssert.False(capture.Spans.Any(s => s.DisplayName.StartsWith("tools/call", StringComparison.Ordinal)), "No span is started while telemetry is off.");
                        }

                        using (TelemetryCapture throwing = new TelemetryCapture(throwFor: _ => true))
                        {
                            using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                            JsonElement result = await client.CallAsync<JsonElement>("tools/call", new { name = "telemetry_echo", arguments = new { text = "x" } }, 5000, ct).ConfigureAwait(false);
                            TestAssert.True(result.TryGetProperty("content", out JsonElement _), "The call succeeds although every measurement callback throws.");
                        }
                    }),
                });
        }

        private static SendMessageRequest MessageRequest(string text)
        {
            return new SendMessageRequest
            {
                Message = new Message
                {
                    Role = Role.User,
                    MessageId = Guid.NewGuid().ToString("N"),
                    Parts = new List<Part> { Part.FromText(text) }
                }
            };
        }

        private static async Task AssertThrowsAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            throw new InvalidOperationException("Expected the call to fail.");
        }

        private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try
                {
                    return await action().ConfigureAwait(false);
                }
                catch (HttpRequestException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "telemetry" });
        }

        private sealed class CompletingAgent : IA2AAgentHandler
        {
            public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(token: token).ConfigureAwait(false);
                await updater.StartAsync(token: token).ConfigureAwait(false);
                await updater.CompleteAsync(new Message
                {
                    Role = Role.Agent,
                    MessageId = Guid.NewGuid().ToString("N"),
                    TaskId = context.TaskId,
                    ContextId = context.ContextId,
                    Parts = new List<Part> { Part.FromText("done") }
                }, token).ConfigureAwait(false);
            }
        }

        private sealed class FailingAgent : IA2AAgentHandler
        {
            public Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                throw new InvalidOperationException("agent failed");
            }
        }

        private sealed class A2ATelemetryFixture : IAsyncDisposable
        {
            private readonly A2AHttpServer _Server;
            private readonly CancellationTokenSource _TokenSource;

            private A2ATelemetryFixture(A2AHttpServer server, int port, CancellationTokenSource tokenSource)
            {
                _Server = server;
                _TokenSource = tokenSource;
                Port = port;
                Client = new HttpClient();
            }

            public int Port { get; }

            public HttpClient Client { get; }

            public string BaseUrl => $"http://localhost:{Port}";

            public string EndpointUrl => $"{BaseUrl}/a2a";

            public static AgentCard Card(string url)
            {
                return new AgentCard
                {
                    Name = "Telemetry Agent",
                    Description = "Exercises Voltaic telemetry.",
                    Version = "1.0.0",
                    SupportedInterfaces = new List<AgentInterface>
                    {
                        new AgentInterface { Url = url, ProtocolBinding = "JSONRPC", ProtocolVersion = A2AProtocol.ProtocolVersion },
                        new AgentInterface { Url = url, ProtocolBinding = "GRPC", ProtocolVersion = A2AProtocol.ProtocolVersion }
                    },
                    Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true }
                };
            }

            public static async Task<A2ATelemetryFixture> StartAsync(CancellationToken token, IA2AAgentHandler handler, Action<A2AHttpServer>? configure = null)
            {
                int port = TestPorts.GetFreePort();
                A2AHttpServer server = new A2AHttpServer("localhost", port, Card($"http://localhost:{port}/a2a"), handler);
                configure?.Invoke(server);
                CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                await server.StartAsync(tokenSource.Token).ConfigureAwait(false);
                A2ATelemetryFixture fixture = new A2ATelemetryFixture(server, port, tokenSource);
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        using HttpResponseMessage response = await fixture.Client.GetAsync(fixture.BaseUrl + A2AProtocol.AgentCardPath, token).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode) break;
                    }
                    catch (HttpRequestException)
                    {
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                return fixture;
            }

            public async ValueTask DisposeAsync()
            {
                _Server.Stop();
                _TokenSource.Cancel();
                Client.Dispose();
                await Task.Delay(50).ConfigureAwait(false);
                _TokenSource.Dispose();
                _Server.Dispose();
            }
        }
    }
}
