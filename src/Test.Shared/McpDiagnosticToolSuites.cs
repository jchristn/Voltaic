namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the v2.0 split between MCP protocol methods, which every server always registers, and the
    /// optional diagnostic tools, which are off by default. Also covers the protocol <c>ping</c> result
    /// shape, <c>UnregisterTool</c>, and the rule that tools are reachable only through <c>tools/call</c>.
    /// </summary>
    public static class McpDiagnosticToolSuites
    {
        /// <summary>
        /// Builds the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public static TestSuiteDescriptor ProtocolMethodsAndDiagnosticTools()
        {
            const string suiteId = "Mcp.DiagnosticTools";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Protocol Methods and Diagnostic Tools",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "HttpDefaultListsOnlyApplicationTools", "By default McpHttpServer lists only application tools and still speaks MCP", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterAppTool).ConfigureAwait(false);

                        RpcResult initialize = await fixture.PostMcpAsync("initialize", new { protocolVersion = McpProtocol.LatestProtocolVersion, capabilities = new { }, clientInfo = new { name = "t", version = "1" } }, 1, null, ct).ConfigureAwait(false);
                        RpcResult tools = await fixture.PostMcpAsync("tools/list", new { }, 2, initialize.SessionId, ct).ConfigureAwait(false);
                        RpcResult ping = await fixture.PostMcpAsync("ping", null, 3, initialize.SessionId, ct).ConfigureAwait(false);
                        RpcResult discover = await fixture.PostMcpAsync("server/discover", new { }, 4, initialize.SessionId, ct).ConfigureAwait(false);

                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, initialize.Result.Get("protocolVersion").String());
                        TestAssert.Equal("app-tool", String.Join(",", ToolNames(tools.Result)), "tools/list must contain only the application's tool.");
                        TestAssert.True(ping.Result.IsObject && ping.Result.Length == 0, "Protocol ping must return an empty object.");
                        TestAssert.True(discover.Result.Has("supportedVersions"), "server/discover must still be served.");
                    }),

                    Case(suiteId, "HttpDiagnosticToolsOptIn", "includeDiagnosticTools publishes echo and getTime but never getSessions or a ping tool", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterAppTool, includeDiagnosticTools: true).ConfigureAwait(false);

                        RpcResult tools = await fixture.PostMcpAsync("tools/list", new { }, 1, null, ct).ConfigureAwait(false);
                        RpcResult echo = await fixture.PostMcpAsync("tools/call", new { name = "echo", arguments = new { message = "hi" } }, 2, null, ct).ConfigureAwait(false);
                        RpcResult getTime = await fixture.PostMcpAsync("tools/call", new { name = "getTime", arguments = new { } }, 3, null, ct).ConfigureAwait(false);
                        List<string> names = ToolNames(tools.Result);

                        TestAssert.True(names.Contains("echo") && names.Contains("getTime") && names.Contains("app-tool"), "echo, getTime, and the application tool are listed.");
                        TestAssert.False(names.Contains("getSessions"), "getSessions was removed in v2.0.");
                        TestAssert.False(names.Contains("ping"), "ping is a protocol method, not a tool.");
                        TestAssert.Equal("hi", echo.Result.Get("content")[0].Get("text").String());
                        TestAssert.False(String.IsNullOrEmpty(getTime.Result.Get("content")[0].Get("text").String()), "getTime returns the time.");
                    }),

                    Case(suiteId, "HttpDiagnosticToolsOffCallsFail", "With diagnostic tools off, tools/call and bare calls for them fail", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        foreach (string name in new[] { "echo", "getTime", "getSessions" })
                        {
                            RpcResult call = await fixture.PostMcpAsync("tools/call", new { name = name, arguments = new { } }, 1, null, ct).ConfigureAwait(false);
                            TestAssert.Equal(-32602, call.Error.Get("code").Int(), $"tools/call {name} must report the tool as not found.");

                            RpcResult bare = await fixture.PostMcpAsync(name, new { }, 2, null, ct).ConfigureAwait(false);
                            TestAssert.Equal(-32601, bare.Error.Get("code").Int(), $"The bare method {name} must not exist.");
                        }
                    }),

                    Case(suiteId, "HttpToolsAreNotBareMethods", "Application tools are reachable only through tools/call, so schema validation cannot be skipped", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterAppTool).ConfigureAwait(false);

                        RpcResult bare = await fixture.PostMcpAsync("app-tool", new { }, 1, null, ct).ConfigureAwait(false);
                        RpcResult invalid = await fixture.PostMcpAsync("tools/call", new { name = "app-tool", arguments = new { } }, 2, null, ct).ConfigureAwait(false);
                        RpcResult valid = await fixture.PostMcpAsync("tools/call", new { name = "app-tool", arguments = new { value = "x" } }, 3, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32601, bare.Error.Get("code").Int(), "A tool must not be callable as a bare JSON-RPC method.");
                        AssertToolInputError(invalid, "tools/call must enforce the input schema.");
                        TestAssert.Equal("app:x", valid.Result.Get("content")[0].Get("text").String());
                    }),

                    Case(suiteId, "HttpApplicationPingToolDoesNotReplaceProtocolPing", "An application tool named ping does not replace the protocol ping", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterTool("ping", "Application ping tool", new { type = "object" }, _ => "app-pong");
                        }).ConfigureAwait(false);

                        RpcResult protocolPing = await fixture.PostMcpAsync("ping", null, 1, null, ct).ConfigureAwait(false);
                        RpcResult toolPing = await fixture.PostMcpAsync("tools/call", new { name = "ping", arguments = new { } }, 2, null, ct).ConfigureAwait(false);
                        RpcResult tools = await fixture.PostMcpAsync("tools/list", new { }, 3, null, ct).ConfigureAwait(false);

                        TestAssert.True(protocolPing.Result.IsObject && protocolPing.Result.Length == 0, "Protocol ping must still return an empty object.");
                        TestAssert.Equal("app-pong", toolPing.Result.Get("content")[0].Get("text").String());
                        TestAssert.True(ToolNames(tools.Result).Contains("ping"), "The application ping tool is listed.");
                    }),

                    Case(suiteId, "HttpApplicationPingToolGetsNoAuthBypass", "Neither the protocol ping nor an application tool named ping is served without authentication", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterTool("ping", "Application ping tool", new { type = "object" }, _ => "app-pong");
                            server.AuthenticationHandler = _ => Task.FromResult(new AuthenticationResult { IsAuthenticated = false, StatusCode = 401, ErrorMessage = "auth required" });
                        }).ConfigureAwait(false);

                        RpcResult protocolPing = await fixture.PostJsonRpcAsync("/mcp/", "ping", null, 1, null, ct).ConfigureAwait(false);
                        RpcResult toolPing = await fixture.PostJsonRpcAsync("/mcp/", "tools/call", new { name = "ping", arguments = new { } }, 2, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Unauthorized, protocolPing.StatusCode, "Protocol ping must be authenticated (MCP authorization requires 401 for every request without valid credentials).");
                        TestAssert.Equal(HttpStatusCode.Unauthorized, toolPing.StatusCode, "tools/call for a tool named ping must be authenticated.");
                    }),

                    Case(suiteId, "StatelessPingCarriesResultType", "ping does not exist in 2026-07-28: a stateless ping gets 404 -32601, while a handshake-era ping returns {}", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterAppTool).ConfigureAwait(false);

                        RpcResult stateless = await McpHttpTestRequests.SendStatelessAsync(fixture, "ping", 1, null, null, null, ct).ConfigureAwait(false);
                        RpcResult handshake = await fixture.PostMcpAsync("ping", null, 2, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.NotFound, stateless.StatusCode, $"Body: {stateless.Body}");
                        TestAssert.Equal(-32601, stateless.Error.Get("code").Int(), "The 2026-07-28 revision removed ping.");
                        TestAssert.True(handshake.Result.IsObject && handshake.Result.Length == 0, "A handshake-era ping returns {}.");
                    }),

                    Case(suiteId, "HttpClientAcceptsLegacyPong", "McpHttpClient connects to and pings a server that answers ping with \"pong\" as Voltaic 1.x did", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("ping", _ => "pong");
                        }).ConfigureAwait(false);

                        using McpHttpClient streamable = new McpHttpClient();
                        bool streamableConnected = await streamable.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        await streamable.PingAsync(token: ct).ConfigureAwait(false);

                        using McpHttpClient legacy = new McpHttpClient();
                        bool legacyConnected = await legacy.ConnectAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        await legacy.PingAsync(token: ct).ConfigureAwait(false);

                        TestAssert.True(streamableConnected, "Streamable connect must accept a \"pong\" ping result.");
                        TestAssert.True(legacyConnected, "Legacy connect must accept a \"pong\" ping result.");
                    }),

                    Case(suiteId, "HttpClientPingSurfacesErrors", "McpHttpClient.PingAsync throws when ping returns a JSON-RPC error", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("ping", new Func<RpcParameters?, object>(_ => throw McpProtocolException.MethodNotFound("ping disabled")));
                        }).ConfigureAwait(false);

                        using McpHttpClient client = new McpHttpClient();
                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);

                        TestAssert.True(connected, "Connect uses initialize, so a failing ping does not block it.");
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(token: ct), "PingAsync must surface the JSON-RPC error.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "HttpClientConnectFailsWhenInitializeFails", "McpHttpClient connect fails, and no session is kept, when initialize returns a JSON-RPC error", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("initialize", new Func<RpcParameters?, object>(_ => throw McpProtocolException.InvalidParams("initialize rejected")));
                        }).ConfigureAwait(false);

                        using McpHttpClient streamable = new McpHttpClient();
                        bool streamableConnected = await streamable.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        using McpHttpClient legacy = new McpHttpClient();
                        bool legacyConnected = await legacy.ConnectAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);

                        TestAssert.False(streamableConnected, "Streamable connect must fail when initialize fails.");
                        TestAssert.False(legacyConnected, "Legacy connect must fail when initialize fails.");
                        TestAssert.True(streamable.SessionId == null && legacy.SessionId == null, "No session is kept after a failed handshake.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "A failed initialize registers no session.");
                    }),

                    Case(suiteId, "HttpUnregisterTool", "UnregisterTool removes a tool from tools/list and tools/call", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            RegisterAppTool(server);
                            server.RegisterTool("other-tool", "Other tool", new { type = "object" }, _ => "other");
                        }).ConfigureAwait(false);

                        bool removed = fixture.Server.UnregisterTool("app-tool");
                        bool removedAgain = fixture.Server.UnregisterTool("app-tool");
                        RpcResult tools = await fixture.PostMcpAsync("tools/list", new { }, 1, null, ct).ConfigureAwait(false);
                        RpcResult call = await fixture.PostMcpAsync("tools/call", new { name = "app-tool", arguments = new { value = "x" } }, 2, null, ct).ConfigureAwait(false);
                        RpcResult other = await fixture.PostMcpAsync("tools/call", new { name = "other-tool", arguments = new { } }, 3, null, ct).ConfigureAwait(false);

                        TestAssert.True(removed, "UnregisterTool returns true for a registered tool.");
                        TestAssert.False(removedAgain, "UnregisterTool returns false for an unknown tool.");
                        TestAssert.Equal("other-tool", String.Join(",", ToolNames(tools.Result)), "Only the remaining tool is listed.");
                        TestAssert.Equal(-32602, call.Error.Get("code").Int(), "tools/call for a removed tool reports it as not found (a protocol error).");
                        TestAssert.Equal("other", other.Result.Get("content")[0].Get("text").String());
                        TestAssert.Throws<ArgumentNullException>(() => fixture.Server.UnregisterTool(null!), "A null name is rejected.");
                        TestAssert.Throws<ArgumentNullException>(() => fixture.Server.UnregisterTool(String.Empty), "An empty name is rejected.");
                    }),

                    Case(suiteId, "UnregisterToolOnEveryServer", "UnregisterTool works on the stdio, TCP, and WebSocket servers", ct =>
                    {
                        using McpServer stdio = new McpServer();
                        using McpTcpServer tcp = new McpTcpServer(IPAddress.Loopback, 0);
                        using McpWebsocketsServer websocket = new McpWebsocketsServer("localhost", 0);

                        stdio.RegisterTool("t", "t", new { type = "object" }, _ => "t");
                        tcp.RegisterTool("t", "t", new { type = "object" }, _ => "t");
                        websocket.RegisterTool("t", "t", new { type = "object" }, _ => "t");

                        TestAssert.True(stdio.UnregisterTool("t"), "stdio removes a registered tool.");
                        TestAssert.True(tcp.UnregisterTool("t"), "TCP removes a registered tool.");
                        TestAssert.True(websocket.UnregisterTool("t"), "WebSocket removes a registered tool.");
                        TestAssert.False(stdio.UnregisterTool("t"), "stdio returns false for an unknown tool.");
                        TestAssert.False(tcp.UnregisterTool("t"), "TCP returns false for an unknown tool.");
                        TestAssert.False(websocket.UnregisterTool("t"), "WebSocket returns false for an unknown tool.");
                        TestAssert.Throws<ArgumentNullException>(() => stdio.UnregisterTool(null!), "stdio rejects a null name.");
                        TestAssert.Throws<ArgumentNullException>(() => tcp.UnregisterTool(String.Empty), "TCP rejects an empty name.");
                        TestAssert.Throws<ArgumentNullException>(() => websocket.UnregisterTool(null!), "WebSocket rejects a null name.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "TcpDefaultAndOptIn", "McpTcpServer lists only application tools by default, answers ping with {}, and publishes diagnostics on request", async ct =>
                    {
                        await using (TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, RegisterAppTool).ConfigureAwait(false))
                        {
                            using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                            JsonProbe tools = JsonProbe.From(await client.CallAsync<object?>("tools/list", new { }, token: ct).ConfigureAwait(false));
                            JsonProbe ping = JsonProbe.From(await client.CallAsync<object?>("ping", token: ct).ConfigureAwait(false));

                            TestAssert.Equal("app-tool", String.Join(",", ToolNames(tools)), "Only the application tool is listed.");
                            TestAssert.True(ping.IsObject && ping.Length == 0, "Protocol ping returns an empty object.");
                            await ExpectRpcErrorAsync(() => client.CallAsync<object?>("app-tool", new { value = "x" }, token: ct), -32601, "A tool is not a bare method.").ConfigureAwait(false);
                            await ExpectRpcErrorAsync(() => client.CallAsync<object?>("getClients", token: ct), -32601, "getClients was removed in v2.0.").ConfigureAwait(false);
                        }

                        await using (TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, RegisterAppTool, includeDiagnosticTools: true).ConfigureAwait(false))
                        {
                            using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                            JsonProbe tools = JsonProbe.From(await client.CallAsync<object?>("tools/list", new { }, token: ct).ConfigureAwait(false));
                            List<string> names = ToolNames(tools);

                            TestAssert.True(names.Contains("echo") && names.Contains("getTime"), "Diagnostic tools are listed when enabled.");
                            TestAssert.False(names.Contains("getClients") || names.Contains("ping"), "getClients and a ping tool are never listed.");
                        }
                    }),

                    Case(suiteId, "WebSocketDiagnosticsAreTools", "McpWebsocketsServer diagnostics are opt-in tools rather than bare methods", async ct =>
                    {
                        await using (WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false))
                        {
                            using McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                            JsonProbe tools = JsonProbe.From(await client.CallAsync<object?>("tools/list", new { }, token: ct).ConfigureAwait(false));

                            TestAssert.Equal(0, tools.Get("tools").Length, "No tools are listed by default.");
                            await ExpectRpcErrorAsync(() => client.CallAsync<object?>("getClients", token: ct), -32601, "getClients was removed in v2.0.").ConfigureAwait(false);
                            await ExpectRpcErrorAsync(() => client.CallAsync<object?>("echo", new { message = "x" }, token: ct), -32601, "echo is not a bare method.").ConfigureAwait(false);
                        }

                        await using (WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, null, includeDiagnosticTools: true).ConfigureAwait(false))
                        {
                            using McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                            JsonProbe tools = JsonProbe.From(await client.CallAsync<object?>("tools/list", new { }, token: ct).ConfigureAwait(false));
                            JsonProbe echo = JsonProbe.From(await client.CallAsync<object?>("tools/call", new { name = "echo", arguments = new { message = "ws" } }, token: ct).ConfigureAwait(false));
                            List<string> names = ToolNames(tools);

                            TestAssert.True(names.Contains("echo") && names.Contains("getTime"), "Diagnostic tools are listed when enabled.");
                            TestAssert.Equal("ws", echo.Get("content")[0].Get("text").String());
                        }
                    }),

                    Case(suiteId, "JsonRpcServerDiagnosticsOptIn", "JsonRpcServer registers diagnostic methods only on request and never getClients", async ct =>
                    {
                        await using (TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, null, includeDiagnosticMethods: false).ConfigureAwait(false))
                        {
                            using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                            await ExpectRpcErrorAsync(() => client.CallAsync<object?>("ping", token: ct), -32601, "ping is not registered by default.").ConfigureAwait(false);
                            await ExpectRpcErrorAsync(() => client.CallAsync<object?>("echo", new { message = "x" }, token: ct), -32601, "echo is not registered by default.").ConfigureAwait(false);
                        }

                        await using (TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, null, includeDiagnosticMethods: true).ConfigureAwait(false))
                        {
                            using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                            string pong = await client.CallAsync<string>("ping", token: ct).ConfigureAwait(false);
                            double sum = await client.CallAsync<double>("add", new { a = 2, b = 3 }, token: ct).ConfigureAwait(false);

                            TestAssert.Equal("pong", pong);
                            TestAssert.Equal(5.0, sum);
                            await ExpectRpcErrorAsync(() => client.CallAsync<object?>("getClients", token: ct), -32601, "getClients was removed in v2.0.").ConfigureAwait(false);
                        }
                    }),
                });
        }

        /// <summary>
        /// Builds the schema-validation suite descriptor covering <c>additionalProperties</c> and
        /// <c>patternProperties</c>, enforced from v2.0.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public static TestSuiteDescriptor SchemaValidation()
        {
            const string suiteId = "Mcp.SchemaValidation";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Tool Input Schema Validation",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "AdditionalPropertiesFalseRejectsUndeclared", "additionalProperties: false rejects an undeclared argument", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterSchemaTools).ConfigureAwait(false);

                        RpcResult rejected = await CallAsync(fixture, "closed", new { dataBase64 = "AA==", data = "oops" }, ct).ConfigureAwait(false);
                        RpcResult accepted = await CallAsync(fixture, "closed", new { dataBase64 = "AA==" }, ct).ConfigureAwait(false);

                        AssertToolInputError(rejected, "An undeclared argument is rejected.");
                        TestAssert.True(rejected.Result.Get("content")[0].Get("text").String()!.Contains("unexpected property 'data'", StringComparison.Ordinal), "The error names the undeclared property.");
                        TestAssert.Equal("ok", accepted.Result.Get("content")[0].Get("text").String());
                    }),

                    Case(suiteId, "AdditionalPropertiesSchemaValidatesExtras", "An additionalProperties schema validates undeclared arguments", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterSchemaTools).ConfigureAwait(false);

                        RpcResult accepted = await CallAsync(fixture, "typed-extras", new { id = 1, label = "x" }, ct).ConfigureAwait(false);
                        RpcResult rejected = await CallAsync(fixture, "typed-extras", new { id = 1, label = 5 }, ct).ConfigureAwait(false);

                        TestAssert.Equal("ok", accepted.Result.Get("content")[0].Get("text").String());
                        AssertToolInputError(rejected, "An extra argument of the wrong type is rejected.");
                    }),

                    Case(suiteId, "PatternPropertiesWithAdditionalFalse", "patternProperties names are allowed and validated when additionalProperties is false", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterSchemaTools).ConfigureAwait(false);

                        RpcResult accepted = await CallAsync(fixture, "patterned", new Dictionary<string, object> { { "x-trace", "abc" } }, ct).ConfigureAwait(false);
                        RpcResult wrongType = await CallAsync(fixture, "patterned", new Dictionary<string, object> { { "x-trace", 7 } }, ct).ConfigureAwait(false);
                        RpcResult unmatched = await CallAsync(fixture, "patterned", new Dictionary<string, object> { { "trace", "abc" } }, ct).ConfigureAwait(false);

                        TestAssert.Equal("ok", accepted.Result.Get("content")[0].Get("text").String());
                        AssertToolInputError(wrongType, "A pattern-matched argument is validated against its schema.");
                        AssertToolInputError(unmatched, "A name matching no pattern is rejected.");
                    }),

                    Case(suiteId, "AdditionalPropertiesAbsentOrTrueAllowsExtras", "Schemas without additionalProperties, or with true, still accept extra arguments", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterSchemaTools).ConfigureAwait(false);

                        RpcResult open = await CallAsync(fixture, "open", new { id = 1, extra = "fine" }, ct).ConfigureAwait(false);
                        RpcResult explicitOpen = await CallAsync(fixture, "explicit-open", new { id = 1, extra = "fine" }, ct).ConfigureAwait(false);

                        TestAssert.Equal("ok", open.Result.Get("content")[0].Get("text").String());
                        TestAssert.Equal("ok", explicitOpen.Result.Get("content")[0].Get("text").String());
                    }),

                    Case(suiteId, "NestedAdditionalPropertiesFalse", "additionalProperties: false is enforced on nested objects", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterSchemaTools).ConfigureAwait(false);

                        RpcResult accepted = await CallAsync(fixture, "nested", new { options = new { mode = "fast" } }, ct).ConfigureAwait(false);
                        RpcResult rejected = await CallAsync(fixture, "nested", new { options = new { mode = "fast", speed = 3 } }, ct).ConfigureAwait(false);

                        TestAssert.Equal("ok", accepted.Result.Get("content")[0].Get("text").String());
                        AssertToolInputError(rejected, "An undeclared nested argument is rejected.");
                        TestAssert.True(rejected.Result.Get("content")[0].Get("text").String()!.Contains("options", StringComparison.Ordinal), "The error names the nested path.");
                    }),
                });
        }

        private static void RegisterAppTool(McpHttpServer server)
        {
            server.RegisterTool("app-tool", "Application tool", AppToolSchema(), args => "app:" + (args?.GetString("value") ?? String.Empty));
        }

        private static void RegisterAppTool(McpTcpServer server)
        {
            server.RegisterTool("app-tool", "Application tool", AppToolSchema(), args => "app:" + (args?.GetString("value") ?? String.Empty));
        }

        private static object AppToolSchema()
        {
            return new
            {
                type = "object",
                properties = new { value = new { type = "string" } },
                required = new[] { "value" }
            };
        }

        private static void RegisterSchemaTools(McpHttpServer server)
        {
            server.RegisterTool("closed", "Closed schema", new
            {
                type = "object",
                properties = new { dataBase64 = new { type = "string" } },
                required = new[] { "dataBase64" },
                additionalProperties = false
            }, _ => "ok");

            server.RegisterTool("typed-extras", "Typed extras", new
            {
                type = "object",
                properties = new { id = new { type = "integer" } },
                additionalProperties = new { type = "string" }
            }, _ => "ok");

            server.RegisterTool("patterned", "Pattern properties", new Dictionary<string, object>
            {
                { "type", "object" },
                { "patternProperties", new Dictionary<string, object> { { "^x-", new { type = "string" } } } },
                { "additionalProperties", false }
            }, _ => "ok");

            server.RegisterTool("open", "Open schema", new
            {
                type = "object",
                properties = new { id = new { type = "integer" } }
            }, _ => "ok");

            server.RegisterTool("explicit-open", "Explicitly open schema", new
            {
                type = "object",
                properties = new { id = new { type = "integer" } },
                additionalProperties = true
            }, _ => "ok");

            server.RegisterTool("nested", "Nested closed schema", new
            {
                type = "object",
                properties = new
                {
                    options = new
                    {
                        type = "object",
                        properties = new { mode = new { type = "string" } },
                        additionalProperties = false
                    }
                }
            }, _ => "ok");
        }

        private static Task<RpcResult> CallAsync(HttpMcpTestServerFixture fixture, string tool, object arguments, CancellationToken token)
        {
            return fixture.PostMcpAsync("tools/call", new { name = tool, arguments = arguments }, 1, null, token);
        }

        private static List<string> ToolNames(JsonProbe result)
        {
            return result.Get("tools").EnumerateArray().Select(tool => tool.Get("name").String() ?? String.Empty).ToList();
        }

        private static async Task ExpectRpcErrorAsync(Func<Task> action, int code, string message)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TestAssert.True(ex.Message.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), $"{message} Expected RPC error {code}, got: {ex.Message}");
                return;
            }

            TestAssert.True(false, $"{message} Expected RPC error {code}, but the call succeeded.");
        }

        /// <summary>
        /// Asserts the MCP rule for invalid tool arguments: a tool result with isError true whose text explains the
        /// problem (a tool execution error the model can act on), not a JSON-RPC protocol error.
        /// </summary>
        private static void AssertToolInputError(RpcResult response, string message)
        {
            TestAssert.False(response.Root.Has("error"), $"{message} Input validation is a tool execution error, not a protocol error. Body: {response.Body}");
            TestAssert.True(response.Result.Get("isError").Bool(), $"{message} The result has isError true.");
            TestAssert.True(response.Result.Get("content")[0].Get("text").String()!.Contains("arguments"), $"{message} The text explains the argument problem.");
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "server", "diagnostics" });
        }
    }
}
