namespace Test.Shared
{
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic;

    public static class McpHttpAdvancedProtocolSuites
    {
        public static TestSuiteDescriptor StreamableHttpMatrix()
        {
            const string suiteId = "McpHttp.Streamable.Matrix";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Streamable HTTP Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "HealthCheck", "HTTP health check returns JSON status", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpResponseMessage response = await fixture.Client.GetAsync($"{fixture.BaseUrl}/", ct).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.True(body.Contains("\"status\":\"Ok\""), "Health check should return status JSON.");
                    }),

                    Case(suiteId, "CorsPreflight", "OPTIONS requests return CORS preflight headers", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Options, $"{fixture.BaseUrl}/mcp/");
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                        TestAssert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out _), "CORS origin header should be present.");
                    }),

                    Case(suiteId, "CorsCanBeDisabled", "CORS headers can be disabled", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.EnableCors = false).ConfigureAwait(false);
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Options, $"{fixture.BaseUrl}/mcp/");
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                        TestAssert.False(response.Headers.TryGetValues("Access-Control-Allow-Origin", out _), "CORS origin header should be omitted.");
                    }),

                    Case(suiteId, "PostCreatesSession", "POST /mcp creates a session and returns Mcp-Session-Id", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("ping", null, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.False(String.IsNullOrEmpty(response.SessionId), "Session ID should be returned.");
                        TestAssert.True(fixture.Server.GetActiveSessions().Contains(response.SessionId!), "Server should track created session.");
                    }),

                    Case(suiteId, "ExistingSessionIsReused", "POST /mcp reuses the supplied session id", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult first = await fixture.PostMcpAsync("ping", null, 1, null, ct).ConfigureAwait(false);
                        RpcResult second = await fixture.PostMcpAsync("ping", null, 2, first.SessionId, ct).ConfigureAwait(false);

                        TestAssert.Equal(first.SessionId, second.SessionId);
                        TestAssert.Equal(1, fixture.Server.GetActiveSessions().Count);
                    }),

                    Case(suiteId, "GetMissingSession", "GET /mcp without a session returns 400", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/mcp/");
                        request.Headers.Accept.ParseAdd("text/event-stream");
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                        TestAssert.True(body.Contains("Missing session ID"), "Error should explain missing session.");
                    }),

                    Case(suiteId, "DeleteRemovesSession", "DELETE /mcp terminates an existing session", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult first = await fixture.PostMcpAsync("ping", null, 1, null, ct).ConfigureAwait(false);

                        using HttpRequestMessage delete = new HttpRequestMessage(HttpMethod.Delete, $"{fixture.BaseUrl}/mcp/");
                        delete.Headers.Add(McpProtocol.SessionIdHeader, first.SessionId);
                        using HttpResponseMessage deleteResponse = await fixture.Client.SendAsync(delete, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
                        TestAssert.False(fixture.Server.GetActiveSessions().Contains(first.SessionId!), "Session should be removed.");
                    }),

                    Case(suiteId, "UnsupportedVerb", "Unsupported /mcp verbs return 405", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, $"{fixture.BaseUrl}/mcp/");
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                    }),

                    Case(suiteId, "PostRequiresAcceptHeaders", "POST /mcp requires JSON and SSE Accept values", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult response = await fixture.PostJsonRpcAsync("/mcp/", "ping", null, 1, null, ct, addMcpAcceptHeaders: false).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
                    }),

                    Case(suiteId, "InvalidProtocolHeader", "Invalid MCP-Protocol-Version returns 400", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage request = CreateJsonRpcPost($"{fixture.BaseUrl}/mcp/", "ping", null, 1, null);
                        request.Headers.Add(McpProtocol.ProtocolVersionHeader, "1900-01-01");
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    }),

                    Case(suiteId, "NotificationAcceptedNoBody", "POST /mcp notifications return 202 with no JSON body", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage request = CreateJsonRpcPost($"{fixture.BaseUrl}/mcp/", "notifications/initialized", null, null, null);
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                        TestAssert.True(String.IsNullOrEmpty(body), "Accepted notifications should not return a JSON-RPC response body.");
                    }),

                    Case(suiteId, "TerminatedSessionReturnsNotFound", "Requests with terminated sessions return 404", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult first = await fixture.PostMcpAsync("ping", null, 1, null, ct).ConfigureAwait(false);

                        using HttpRequestMessage delete = new HttpRequestMessage(HttpMethod.Delete, $"{fixture.BaseUrl}/mcp/");
                        delete.Headers.Add(McpProtocol.SessionIdHeader, first.SessionId);
                        using HttpResponseMessage deleteResponse = await fixture.Client.SendAsync(delete, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

                        using HttpRequestMessage request = CreateJsonRpcPost($"{fixture.BaseUrl}/mcp/", "ping", null, 2, first.SessionId);
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    }),

                    Case(suiteId, "LegacyRpcEndpoint", "Legacy /rpc endpoint remains compatible", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult response = await fixture.PostRpcAsync("ping", null, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.Equal("pong", response.Result.GetString());
                        TestAssert.False(String.IsNullOrEmpty(response.SessionId), "Legacy endpoint should still create sessions.");
                    }),

                    Case(suiteId, "LegacyEventsMissingSession", "Legacy /events endpoint rejects missing sessions", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/events/");
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    }),

                    Case(suiteId, "AuthFailureBlocksNonPing", "AuthenticationHandler can block non-ping requests", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = _ => Task.FromResult(new AuthenticationResult
                            {
                                IsAuthenticated = false,
                                StatusCode = 403,
                                ErrorMessage = "denied"
                            });
                        }).ConfigureAwait(false);

                        using HttpRequestMessage request = CreateJsonRpcPost($"{fixture.BaseUrl}/mcp/", "tools/list", new { }, 1, null);
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                        TestAssert.Equal("denied", body);
                    }),

                    Case(suiteId, "AuthSuccessAllowsRequest", "AuthenticationHandler can allow requests", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = _ => Task.FromResult(new AuthenticationResult { IsAuthenticated = true, Principal = "tester" });
                        }).ConfigureAwait(false);

                        RpcResult response = await fixture.PostMcpAsync("ping", null, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.Equal("pong", response.Result.GetString());
                    }),

                    Case(suiteId, "PingBypassesAuthFailure", "Ping bypasses authentication for connectivity checks", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = _ => Task.FromResult(new AuthenticationResult
                            {
                                IsAuthenticated = false,
                                StatusCode = 401,
                                ErrorMessage = "auth required"
                            });
                        }).ConfigureAwait(false);

                        RpcResult response = await fixture.PostMcpAsync("ping", null, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.Equal("pong", response.Result.GetString());
                    }),

                    Case(suiteId, "RawSseReceivesQueuedNotification", "GET /mcp streams queued notifications over SSE", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult session = await fixture.PostMcpAsync("ping", null, 1, null, ct).ConfigureAwait(false);

                        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/mcp/");
                        request.Headers.Add(McpProtocol.SessionIdHeader, session.SessionId);
                        request.Headers.Accept.ParseAdd("text/event-stream");
                        using HttpResponseMessage response = await fixture.SendRawAsync(request, timeout.Token).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

                        using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                        using StreamReader reader = new StreamReader(stream);
                        string? prelude = await ReadLineAsync(reader, timeout.Token).ConfigureAwait(false);
                        TestAssert.Equal(": connected", prelude);

                        bool queued = fixture.Server.SendNotificationToSession(session.SessionId!, "notifications/test", new { value = 7 });
                        TestAssert.True(queued, "Notification should be queued.");
                        string data = await ReadUntilDataAsync(reader, timeout.Token).ConfigureAwait(false);
                        JsonElement notification = TestJson.ParseRoot(data);
                        TestAssert.Equal("notifications/test", notification.GetProperty("method").GetString());
                        TestAssert.Equal(7, notification.GetProperty("params").GetProperty("value").GetInt32());
                    }),
                });
        }

        public static TestSuiteDescriptor RegistryProtocolMatrix()
        {
            const string suiteId = "McpHttp.Registry.Matrix";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Registry Protocol Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "InitializeCapabilities", "initialize advertises capabilities for registered tools, resources, and prompts", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("initialize", new { protocolVersion = McpProtocol.LatestProtocolVersion }, 1, null, ct).ConfigureAwait(false);

                        JsonElement capabilities = response.Result.GetProperty("capabilities");
                        TestAssert.True(capabilities.TryGetProperty("tools", out _), "Tools capability should be advertised.");
                        TestAssert.True(capabilities.TryGetProperty("resources", out _), "Resources capability should be advertised.");
                        TestAssert.True(capabilities.TryGetProperty("prompts", out _), "Prompts capability should be advertised.");
                        TestAssert.True(capabilities.TryGetProperty("completions", out _), "Completions capability should be advertised when providers are registered.");
                        TestAssert.True(capabilities.TryGetProperty("logging", out _), "Logging capability should be advertised.");
                    }),

                    Case(suiteId, "ToolsListMetadata", "tools/list exposes full tool metadata", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/list", new { }, 1, null, ct).ConfigureAwait(false);

                        JsonElement tool = response.Result.GetProperty("tools").EnumerateArray().First(t => t.GetProperty("name").GetString() == "structured");
                        TestAssert.Equal("Structured", tool.GetProperty("title").GetString());
                        TestAssert.True(tool.TryGetProperty("outputSchema", out _), "Output schema should be present.");
                        TestAssert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Annotation should be present.");
                        TestAssert.Equal("test", tool.GetProperty("_meta").GetProperty("source").GetString());
                    }),

                    Case(suiteId, "ToolsListPagination", "tools/list paginates large registries", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterManyTools).ConfigureAwait(false);
                        RpcResult first = await fixture.PostMcpAsync("tools/list", new { }, 1, null, ct).ConfigureAwait(false);
                        RpcResult second = await fixture.PostMcpAsync("tools/list", new { cursor = first.Result.GetProperty("nextCursor").GetString() }, 2, first.SessionId, ct).ConfigureAwait(false);

                        TestAssert.Equal(100, first.Result.GetProperty("tools").GetArrayLength());
                        TestAssert.Equal("100", first.Result.GetProperty("nextCursor").GetString());
                        TestAssert.True(second.Result.GetProperty("tools").GetArrayLength() > 0, "Second page should contain remaining tools.");
                        TestAssert.False(second.Result.TryGetProperty("nextCursor", out _), "Final page should omit nextCursor.");
                    }),

                    Case(suiteId, "InvalidCursor", "List endpoints reject invalid cursors", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/list", new { cursor = "not-a-number" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                        TestAssert.True(response.Error.GetProperty("message").GetString()!.Contains("Invalid cursor"), "Error should mention cursor.");
                    }),

                    Case(suiteId, "ToolsCallStructured", "tools/call returns structured tool results", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "structured", arguments = new { a = 4, b = 6 } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(10, response.Result.GetProperty("structuredContent").GetProperty("total").GetInt32());
                        TestAssert.Equal("text", response.Result.GetProperty("content")[0].GetProperty("type").GetString());
                    }),

                    Case(suiteId, "ToolsCallInputSchemaValidation", "tools/call validates required input schema fields", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "requires-input", arguments = new { } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                        TestAssert.True(response.Error.GetProperty("message").GetString()!.Contains("missing required property"), "Error should identify schema validation.");
                    }),

                    Case(suiteId, "ToolsCallOutputSchemaValidation", "tools/call validates structured output schema", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "bad-output", arguments = new { } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                        TestAssert.True(response.Error.GetProperty("message").GetString()!.Contains("structured output"), "Error should identify output validation.");
                    }),

                    Case(suiteId, "ToolsCallPlainReturnConverted", "plain tool return values are converted to text content", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "plain" }, 1, null, ct).ConfigureAwait(false);

                        string text = response.Result.GetProperty("content")[0].GetProperty("text").GetString()!;
                        TestAssert.True(text.Contains("plain"), "Plain return value should be serialized into text content.");
                    }),

                    Case(suiteId, "ToolsCallFullResult", "tools/call preserves full McpToolCallResult content and isError", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "full-result" }, 1, null, ct).ConfigureAwait(false);

                        JsonElement content = response.Result.GetProperty("content");
                        TestAssert.Equal("text", content[0].GetProperty("type").GetString());
                        TestAssert.Equal("image", content[1].GetProperty("type").GetString());
                        TestAssert.True(response.Result.GetProperty("isError").GetBoolean(), "isError should be preserved.");
                    }),

                    Case(suiteId, "ToolsCallMissingParams", "tools/call rejects missing params", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", null, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "ToolsCallMissingName", "tools/call rejects params without a name", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { arguments = new { } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "ToolsCallEmptyName", "tools/call rejects empty names", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "ToolsCallUnknown", "tools/call rejects unknown tools", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "missing" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                        TestAssert.True(response.Error.GetProperty("message").GetString()!.Contains("was not found"), "Error should mention missing tool.");
                    }),

                    Case(suiteId, "ToolsCallHandlerException", "tool handler exceptions map to internal errors", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "explode-tool" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32603, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "DuplicateToolRegistrationReplacesExisting", "registering a tool with the same name replaces the previous definition", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterTool("dup", "old", new { type = "object" }, _ => McpToolCallResult.FromText("old"));
                            server.RegisterTool("dup", "new", new { type = "object" }, _ => McpToolCallResult.FromText("new"));
                        }).ConfigureAwait(false);

                        RpcResult list = await fixture.PostMcpAsync("tools/list", new { }, 1, null, ct).ConfigureAwait(false);
                        int count = list.Result.GetProperty("tools").EnumerateArray().Count(tool => tool.GetProperty("name").GetString() == "dup");
                        RpcResult call = await fixture.PostMcpAsync("tools/call", new { name = "dup" }, 2, list.SessionId, ct).ConfigureAwait(false);

                        TestAssert.Equal(1, count);
                        TestAssert.Equal("new", call.Result.GetProperty("content")[0].GetProperty("text").GetString());
                    }),

                    Case(suiteId, "ResourcesReadStatic", "resources/read returns static resources", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("resources/read", new { uri = "voltaic://static" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal("static contents", response.Result.GetProperty("contents")[0].GetProperty("text").GetString());
                    }),

                    Case(suiteId, "ResourcesReadTemplateDecodedVariable", "resource templates decode URI variables", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("resources/read", new { uri = "voltaic://encoded/hello%20world" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal("hello world", response.Result.GetProperty("contents")[0].GetProperty("text").GetString());
                    }),

                    Case(suiteId, "ResourcesStaticPrecedence", "static resources take precedence over matching templates", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("resources/read", new { uri = "voltaic://docs/special" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal("special static", response.Result.GetProperty("contents")[0].GetProperty("text").GetString());
                    }),

                    Case(suiteId, "ResourcesListPagination", "resources/list paginates large registries", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterManyResources).ConfigureAwait(false);
                        RpcResult first = await fixture.PostMcpAsync("resources/list", new { }, 1, null, ct).ConfigureAwait(false);
                        RpcResult second = await fixture.PostMcpAsync("resources/list", new { cursor = first.Result.GetProperty("nextCursor").GetString() }, 2, first.SessionId, ct).ConfigureAwait(false);

                        TestAssert.Equal(100, first.Result.GetProperty("resources").GetArrayLength());
                        TestAssert.True(second.Result.GetProperty("resources").GetArrayLength() > 0, "Second page should contain remaining resources.");
                    }),

                    Case(suiteId, "ResourceTemplatesPagination", "resources/templates/list paginates large registries", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterManyTemplates).ConfigureAwait(false);
                        RpcResult first = await fixture.PostMcpAsync("resources/templates/list", new { }, 1, null, ct).ConfigureAwait(false);
                        RpcResult second = await fixture.PostMcpAsync("resources/templates/list", new { cursor = first.Result.GetProperty("nextCursor").GetString() }, 2, first.SessionId, ct).ConfigureAwait(false);

                        TestAssert.Equal(100, first.Result.GetProperty("resourceTemplates").GetArrayLength());
                        TestAssert.True(second.Result.GetProperty("resourceTemplates").GetArrayLength() > 0, "Second page should contain remaining templates.");
                    }),

                    Case(suiteId, "ResourcesReadMissingUri", "resources/read rejects missing uri", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("resources/read", new { }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "ResourcesReadEmptyUri", "resources/read rejects empty uri", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("resources/read", new { uri = "" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "ResourcesReadUnknown", "resources/read rejects unknown resources", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("resources/read", new { uri = "voltaic://missing" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "ResourceHandlerException", "resource handler exceptions map to internal errors", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("resources/read", new { uri = "voltaic://explode" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32603, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "InvalidResourceTemplateRegistration", "invalid resource template syntax fails at registration", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("localhost", 0, includeDefaultMethods: false);
                        TestAssert.Throws<ArgumentException>(
                            () => server.RegisterResourceTemplate("voltaic://docs/{}", "bad", "text/plain", uri => new McpReadResourceResult()),
                            "Empty template variable names should fail.");
                        TestAssert.Throws<ArgumentException>(
                            () => server.RegisterResourceTemplate("voltaic://docs/{bad-name}", "bad", "text/plain", uri => new McpReadResourceResult()),
                            "Invalid template variable names should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "PromptsListPagination", "prompts/list paginates large registries", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterManyPrompts).ConfigureAwait(false);
                        RpcResult first = await fixture.PostMcpAsync("prompts/list", new { }, 1, null, ct).ConfigureAwait(false);
                        RpcResult second = await fixture.PostMcpAsync("prompts/list", new { cursor = first.Result.GetProperty("nextCursor").GetString() }, 2, first.SessionId, ct).ConfigureAwait(false);

                        TestAssert.Equal(100, first.Result.GetProperty("prompts").GetArrayLength());
                        TestAssert.True(second.Result.GetProperty("prompts").GetArrayLength() > 0, "Second page should contain remaining prompts.");
                    }),

                    Case(suiteId, "PromptGetRequiredSuccess", "prompts/get accepts required arguments", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("prompts/get", new { name = "required", arguments = new { topic = "Voltaic" } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal("Summarize Voltaic", response.Result.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
                    }),

                    Case(suiteId, "PromptGetMissingRequiredArg", "prompts/get rejects missing required arguments", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("prompts/get", new { name = "required", arguments = new { } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                        TestAssert.True(response.Error.GetProperty("message").GetString()!.Contains("requires argument"), "Error should identify required arg.");
                    }),

                    Case(suiteId, "PromptGetOptionalMissingOk", "prompts/get allows omitted optional arguments", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("prompts/get", new { name = "optional" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal("No arguments required", response.Result.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
                    }),

                    Case(suiteId, "PromptGetMissingName", "prompts/get rejects missing name", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("prompts/get", new { }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "PromptGetEmptyName", "prompts/get rejects empty names", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("prompts/get", new { name = "" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "PromptGetUnknown", "prompts/get rejects unknown prompts", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("prompts/get", new { name = "missing" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "PromptHandlerException", "prompt handler exceptions map to internal errors", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("prompts/get", new { name = "explode-prompt" }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32603, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "CompletionCompletePrompt", "completion/complete returns bounded prompt completions", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("completion/complete", new
                        {
                            @ref = new { type = "ref/prompt", name = "required" },
                            argument = new { name = "topic", value = "Vo" }
                        }, 1, null, ct).ConfigureAwait(false);

                        JsonElement completion = response.Result.GetProperty("completion");
                        TestAssert.Equal("Voltaic", completion.GetProperty("values")[0].GetString());
                        TestAssert.Equal(1, completion.GetProperty("total").GetInt32());
                        TestAssert.False(completion.GetProperty("hasMore").GetBoolean(), "Completion should report no additional matches.");
                    }),

                    Case(suiteId, "CompletionCompleteInvalidReference", "completion/complete rejects invalid references", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult response = await fixture.PostMcpAsync("completion/complete", new
                        {
                            @ref = new { type = "ref/unknown", name = "required" },
                            argument = new { name = "topic", value = "Vo" }
                        }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, response.Error.GetProperty("code").GetInt32());
                    }),

                    Case(suiteId, "ResourceSubscribeUnsubscribe", "resources/subscribe and resources/unsubscribe accept resource URIs", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult subscribe = await fixture.PostMcpAsync("resources/subscribe", new { uri = "voltaic://static" }, 1, null, ct).ConfigureAwait(false);
                        RpcResult unsubscribe = await fixture.PostMcpAsync("resources/unsubscribe", new { uri = "voltaic://static" }, 2, subscribe.SessionId, ct).ConfigureAwait(false);

                        TestAssert.True(subscribe.Result.ValueKind == JsonValueKind.Object, "Subscribe should return an empty result object.");
                        TestAssert.True(unsubscribe.Result.ValueKind == JsonValueKind.Object, "Unsubscribe should return an empty result object.");
                    }),

                    Case(suiteId, "LoggingSetLevel", "logging/setLevel accepts supported levels and rejects invalid levels", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, ConfigureProtocolServer).ConfigureAwait(false);
                        RpcResult success = await fixture.PostMcpAsync("logging/setLevel", new { level = "warning" }, 1, null, ct).ConfigureAwait(false);
                        RpcResult failure = await fixture.PostMcpAsync("logging/setLevel", new { level = "verbose" }, 2, success.SessionId, ct).ConfigureAwait(false);

                        TestAssert.True(success.Result.ValueKind == JsonValueKind.Object, "logging/setLevel should return an empty result object.");
                        TestAssert.Equal(-32602, failure.Error.GetProperty("code").GetInt32());
                    }),
                });
        }

        public static TestSuiteDescriptor HttpClientMatrix()
        {
            const string suiteId = "McpHttp.Client.Matrix";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP HTTP Client Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ConnectStreamableAndCallPing", "McpHttpClient connects to Streamable HTTP and calls ping", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();

                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        string pong = await client.CallAsync<string>("ping", token: ct).ConfigureAwait(false);

                        TestAssert.True(connected, "Client should connect.");
                        TestAssert.True(client.IsConnected, "Client should report connected.");
                        TestAssert.False(String.IsNullOrEmpty(client.SessionId), "Client should capture session ID.");
                        TestAssert.Equal("pong", pong);
                    }),

                    Case(suiteId, "ConnectLegacyAndCallPing", "McpHttpClient connects to legacy /rpc and calls ping", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();

                        bool connected = await client.ConnectAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        string pong = await client.CallAsync<string>("ping", token: ct).ConfigureAwait(false);

                        TestAssert.True(connected, "Client should connect.");
                        TestAssert.Equal("pong", pong);
                    }),

                    Case(suiteId, "ResponseEvents", "McpHttpClient raises request and response events", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TaskCompletionSource<RequestSentEventArgs> sent = new TaskCompletionSource<RequestSentEventArgs>();
                        TaskCompletionSource<ResponseReceivedEventArgs> received = new TaskCompletionSource<ResponseReceivedEventArgs>();
                        client.RequestSent += (_, args) => sent.TrySetResult(args);
                        client.ResponseReceived += (_, args) => received.TrySetResult(args);

                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");

                        RequestSentEventArgs sentArgs = await WaitForTaskAsync(sent.Task, ct).ConfigureAwait(false);
                        ResponseReceivedEventArgs receivedArgs = await WaitForTaskAsync(received.Task, ct).ConfigureAwait(false);
                        TestAssert.Equal("ping", sentArgs.Method);
                        TestAssert.Equal("ping", receivedArgs.Method);
                        TestAssert.True(receivedArgs.IsSuccess, "Ping should be successful.");
                    }),

                    Case(suiteId, "RawCallAndNotify", "McpHttpClient supports raw CallAsync and NotifyAsync", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();

                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");

                        JsonRpcResponse response = await client.CallAsync("ping", token: ct).ConfigureAwait(false);
                        await client.NotifyAsync("notifications/initialized", token: ct).ConfigureAwait(false);

                        JsonElement result = (JsonElement)response.Result!;
                        TestAssert.Equal("pong", result.GetString());
                    }),

                    Case(suiteId, "SseNotificationEvent", "McpHttpClient receives Streamable HTTP SSE notifications", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TaskCompletionSource<JsonRpcRequest> notification = new TaskCompletionSource<JsonRpcRequest>();
                        client.NotificationReceived += (_, request) =>
                        {
                            if (request.Method == "notifications/test")
                            {
                                notification.TrySetResult(request);
                            }
                        };

                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");
                        bool sseStarted = await client.StartSseAsync(ct).ConfigureAwait(false);
                        TestAssert.True(sseStarted, "SSE should start.");

                        await WaitUntilAsync(() => client.IsSseConnected, ct).ConfigureAwait(false);
                        bool queued = fixture.Server.SendNotificationToSession(client.SessionId!, "notifications/test", new { value = 12 });
                        JsonRpcRequest received = await WaitForTaskAsync(notification.Task, ct).ConfigureAwait(false);

                        TestAssert.True(queued, "Notification should be queued.");
                        TestAssert.Equal("notifications/test", received.Method);
                        JsonElement parameters = (JsonElement)received.Params!;
                        TestAssert.Equal(12, parameters.GetProperty("value").GetInt32());
                    }),

                    Case(suiteId, "DisconnectRaisesEvent", "McpHttpClient disconnect clears session and raises event", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TaskCompletionSource<ClientDisconnectedEventArgs> disconnected = new TaskCompletionSource<ClientDisconnectedEventArgs>();
                        client.Disconnected += (_, args) => disconnected.TrySetResult(args);

                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");
                        client.Disconnect();

                        ClientDisconnectedEventArgs args = await WaitForTaskAsync(disconnected.Task, ct).ConfigureAwait(false);
                        TestAssert.False(client.IsConnected, "Client should be disconnected.");
                        TestAssert.Equal(ClientConnectionTypeEnum.Http, args.ConnectionType);
                    }),
                });
        }

        private static void ConfigureProtocolServer(McpHttpServer server)
        {
            server.RegisterTool("plain", "Returns a plain object", new { type = "object" }, _ => new { value = "plain" });
            server.RegisterTool(
                new ToolDefinition
                {
                    Name = "structured",
                    Title = "Structured",
                    Description = "Adds two numbers",
                    InputSchema = new { type = "object" },
                    OutputSchema = new { type = "object" },
                    Annotations = new McpAnnotations { ReadOnlyHint = true },
                    Meta = new Dictionary<string, object?> { ["source"] = "test" }
                },
                args =>
                {
                    int a = args!.Value.GetProperty("a").GetInt32();
                    int b = args.Value.GetProperty("b").GetInt32();
                    return McpToolCallResult.FromStructured(new { total = a + b });
                });
            server.RegisterTool("full-result", "Returns full content", new { type = "object" }, _ => new McpToolCallResult
            {
                IsError = true,
                Content = new List<object>
                {
                    new McpTextContent { Text = "text" },
                    new McpImageContent { Data = "AA==", MimeType = "image/png" }
                }
            });
            server.RegisterTool("explode-tool", "Throws", new { type = "object" }, _ => throw new InvalidOperationException("tool boom"));
            server.RegisterTool(
                "requires-input",
                "Requires a value",
                new
                {
                    type = "object",
                    properties = new
                    {
                        value = new { type = "string" }
                    },
                    required = new[] { "value" }
                },
                _ => "unreachable");
            server.RegisterTool(
                "bad-output",
                "Returns invalid structured output",
                new { type = "object" },
                new
                {
                    type = "object",
                    properties = new
                    {
                        total = new { type = "number" }
                    },
                    required = new[] { "total" }
                },
                _ => McpToolCallResult.FromStructured(new { value = "wrong" }));

            server.RegisterResource("voltaic://static", "static", "text/plain", () => TextResource("voltaic://static", "static contents"));
            server.RegisterResource("voltaic://docs/special", "special", "text/plain", () => TextResource("voltaic://docs/special", "special static"));
            server.RegisterResource("voltaic://explode", "explode", "text/plain", () => throw new InvalidOperationException("resource boom"));
            server.RegisterResourceTemplate("voltaic://docs/{name}", "doc", "text/plain", uri => TextResource(uri, $"dynamic:{uri}"));
            server.RegisterResourceTemplate(
                new McpResourceTemplate { UriTemplate = "voltaic://encoded/{name}", Name = "encoded", MimeType = "text/plain" },
                (uri, variables, token) => Task.FromResult(TextResource(uri, variables["name"])));

            server.RegisterPrompt(
                "required",
                "Prompt requiring a topic",
                new[]
                {
                    new McpPromptArgument { Name = "topic", Required = true },
                    new McpPromptArgument { Name = "style", Required = false }
                },
                args => Prompt($"Summarize {args!.Value.GetProperty("topic").GetString()}"));
            server.RegisterPrompt("optional", "No required arguments", null, _ => Prompt("No arguments required"));
            server.RegisterPrompt("explode-prompt", "Throws", null, _ => throw new InvalidOperationException("prompt boom"));
            server.RegisterCompletionProvider(
                "ref/prompt",
                "required",
                "topic",
                (request, token) => Task.FromResult(new McpCompleteResult
                {
                    Completion = new McpCompletion
                    {
                        Values = new List<string> { "Voltaic" }
                    }
                }));
        }

        private static void RegisterManyTools(McpHttpServer server)
        {
            for (int i = 0; i < 105; i++)
            {
                int value = i;
                server.RegisterTool($"tool-{i:D3}", $"Tool {i}", new { type = "object" }, _ => McpToolCallResult.FromStructured(new { value }));
            }
        }

        private static void RegisterManyResources(McpHttpServer server)
        {
            for (int i = 0; i < 105; i++)
            {
                int value = i;
                server.RegisterResource($"voltaic://resource/{i:D3}", $"resource-{i:D3}", "text/plain", () => TextResource($"voltaic://resource/{value:D3}", value.ToString()));
            }
        }

        private static void RegisterManyTemplates(McpHttpServer server)
        {
            for (int i = 0; i < 105; i++)
            {
                server.RegisterResourceTemplate($"voltaic://template/{i:D3}/{{name}}", $"template-{i:D3}", "text/plain", uri => TextResource(uri, uri));
            }
        }

        private static void RegisterManyPrompts(McpHttpServer server)
        {
            for (int i = 0; i < 105; i++)
            {
                int value = i;
                server.RegisterPrompt($"prompt-{i:D3}", $"Prompt {i}", null, _ => Prompt($"Prompt {value}"));
            }
        }

        private static McpReadResourceResult TextResource(string uri, string text)
        {
            return new McpReadResourceResult
            {
                Contents = new List<object>
                {
                    new McpTextResourceContents
                    {
                        Uri = uri,
                        MimeType = "text/plain",
                        Text = text
                    }
                }
            };
        }

        private static McpGetPromptResult Prompt(string text)
        {
            return new McpGetPromptResult
            {
                Messages = new List<McpPromptMessage>
                {
                    new McpPromptMessage
                    {
                        Role = "user",
                        Content = new McpTextContent { Text = text }
                    }
                }
            };
        }

        private static HttpRequestMessage CreateJsonRpcPost(string url, string method, object? parameters, object? id, string? sessionId)
        {
            JsonRpcRequest request = new JsonRpcRequest { Method = method, Params = parameters, Id = id };
            HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(request), System.Text.Encoding.UTF8, "application/json")
            };
            message.Headers.Accept.ParseAdd("application/json");
            message.Headers.Accept.ParseAdd("text/event-stream");
            if (!String.IsNullOrEmpty(sessionId))
            {
                message.Headers.Add(McpProtocol.SessionIdHeader, sessionId);
            }

            return message;
        }

        private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
        {
            return await reader.ReadLineAsync().WaitAsync(token).ConfigureAwait(false);
        }

        private static async Task<string> ReadUntilDataAsync(StreamReader reader, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                string? line = await ReadLineAsync(reader, token).ConfigureAwait(false);
                if (line == null)
                {
                    throw new EndOfStreamException("SSE stream ended before data arrived.");
                }

                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    return line.Substring("data: ".Length);
                }
            }

            throw new TimeoutException("Timed out waiting for SSE data.");
        }

        private static async Task<T> WaitForTaskAsync<T>(Task<T> task, CancellationToken token)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(5000, token)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException("Timed out waiting for HTTP client event.");
            }

            return await task.ConfigureAwait(false);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for HTTP condition.");
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "http", "matrix" });
        }
    }
}
