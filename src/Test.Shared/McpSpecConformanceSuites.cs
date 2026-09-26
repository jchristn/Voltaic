namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the spec-conformance fixes in v2.1.1: JSON-RPC responses POSTed by a client are accepted with
    /// 202, header-less handshake requests assume <c>2025-03-26</c>, stateless (<c>2026-07-28</c>) requests
    /// must carry the <c>_meta</c> protocol version, and <see cref="McpHttpServer.ProtectedResourceMetadata"/>
    /// is served at the RFC 9728 well-known paths.
    /// </summary>
    public static class McpSpecConformanceSuites
    {
        /// <summary>
        /// Streamable HTTP and authorization conformance cases.
        /// </summary>
        public static TestSuiteDescriptor Conformance()
        {
            const string suiteId = "Mcp.SpecConformance";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Specification Conformance",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "PostedResponseOnSessionGets202", "A JSON-RPC response or error POSTed on a session gets 202 with no body and reaches no handler", async ct =>
                    {
                        int requests = 0;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string? sessionId = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);
                        fixture.Server.RequestReceived += (_, _) => Interlocked.Increment(ref requests);

                        RpcResult result = await SendRawAsync(fixture, "/mcp/", "{\"jsonrpc\":\"2.0\",\"id\":\"srv-1\",\"result\":{}}", sessionId, null, ct).ConfigureAwait(false);
                        RpcResult error = await SendRawAsync(fixture, "/mcp/", "{\"jsonrpc\":\"2.0\",\"id\":\"srv-2\",\"error\":{\"code\":-1,\"message\":\"no\"}}", sessionId, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Accepted, result.StatusCode, $"Body: {result.Body}");
                        TestAssert.Equal(HttpStatusCode.Accepted, error.StatusCode, $"Body: {error.Body}");
                        TestAssert.True(String.IsNullOrEmpty(result.Body) && String.IsNullOrEmpty(error.Body), "202 carries no body.");
                        TestAssert.Equal(0, Volatile.Read(ref requests), "No handler runs for a client response.");
                    }),

                    Case(suiteId, "PostedResponseWithoutSessionOnMcpGets400", "A sessionless JSON-RPC response on /mcp gets an HTTP error status (400)", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult result = await SendRawAsync(fixture, "/mcp/", "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", null, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "PostedResponseOnRpcGets202", "A sessionless JSON-RPC response on /rpc gets 202 and creates no session", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult result = await SendRawAsync(fixture, "/rpc/", "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", null, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Accepted, result.StatusCode);
                        TestAssert.True(result.SessionId == null, "No session header is returned.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "BatchOfResponsesAndNotificationsGets202", "In a batching revision, a batch of only responses and notifications gets 202; a mixed batch answers only the requests", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string? sessionId = await InitializeAsync(fixture, "2025-03-26", ct).ConfigureAwait(false);

                        string onlyResponses = "[{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"result\":{}},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}]";
                        string mixed = "[{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"result\":{}},{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}]";
                        RpcResult accepted = await SendRawAsync(fixture, "/mcp/", onlyResponses, sessionId, "2025-03-26", ct).ConfigureAwait(false);
                        RpcResult answered = await SendRawAsync(fixture, "/mcp/", mixed, sessionId, "2025-03-26", ct).ConfigureAwait(false);
                        RpcResult malformed = await SendRawAsync(fixture, "/mcp/", "[{\"jsonrpc\":", sessionId, "2025-03-26", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Accepted, accepted.StatusCode, $"Body: {accepted.Body}");
                        TestAssert.Equal(HttpStatusCode.OK, answered.StatusCode, $"Body: {answered.Body}");
                        TestAssert.Equal(1, answered.Root.Length, "Only the request is answered.");
                        TestAssert.Equal(7, answered.Root[0].Get("id").Int(), "The answer is for the ping.");
                        TestAssert.Equal(-32700, malformed.Root[0].Get("error").Get("code").Int(), "A malformed batch gets a parse error.");
                    }),

                    Case(suiteId, "HeaderlessBatchAssumes20250326", "A session that negotiated 2025-03-26 may batch without the version header; a batch without a session gets 400", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string? sessionId = await InitializeAsync(fixture, "2025-03-26", ct).ConfigureAwait(false);

                        string batch = "[" + McpHttpTestRequests.BuildBody("ping", 1, null) + "]";
                        RpcResult result = await SendRawAsync(fixture, "/mcp/", batch, sessionId, null, ct).ConfigureAwait(false);
                        RpcResult sessionless = await SendRawAsync(fixture, "/mcp/", batch, null, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, result.StatusCode, $"A 2025-03-26 session may batch. Body: {result.Body}");
                        TestAssert.Equal(HttpStatusCode.BadRequest, sessionless.StatusCode, "A batch needs a session.");
                    }),

                    Case(suiteId, "NegotiatedVersionWinsOverHeaderlessDefault", "A session's negotiated version is used when the header is absent, so 2025-11-25 sessions still reject batches", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string? sessionId = await InitializeAsync(fixture, "2025-11-25", ct).ConfigureAwait(false);

                        string batch = "[" + McpHttpTestRequests.BuildBody("ping", 1, null) + "]";
                        RpcResult result = await SendRawAsync(fixture, "/mcp/", batch, sessionId, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, result.StatusCode, "2025-11-25 does not allow batching.");
                    }),

                    Case(suiteId, "StatelessRequestWithoutMetaGetsHeaderMismatch", "A 2026-07-28 request whose body has no _meta protocol version gets 400 -32020 on /mcp and /rpc", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        Dictionary<string, string> headers = new Dictionary<string, string>
                        {
                            { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                            { McpProtocol.MethodHeader, "tools/list" }
                        };

                        RpcResult mcp = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/list", 1, new { }), headers, true, ct).ConfigureAwait(false);
                        RpcResult rpc = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/list", 2, new { }), headers, true, ct, "/rpc/").ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, mcp.StatusCode, $"Body: {mcp.Body}");
                        TestAssert.Equal(-32020, mcp.Error.Get("code").Int(), "The error is HeaderMismatch.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, rpc.StatusCode, $"Body: {rpc.Body}");
                        TestAssert.Equal(-32020, rpc.Error.Get("code").Int(), "The JSON-RPC endpoint applies the same rule.");
                    }),

                    Case(suiteId, "StatelessRequestWithMatchingMetaSucceeds", "A 2026-07-28 request with matching header and _meta versions succeeds", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult result = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/list", 1, null, null, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, result.StatusCode, $"Body: {result.Body}");
                    }),

                    Case(suiteId, "ProtectedResourceMetadataIsServedWithoutAuth", "ProtectedResourceMetadata is served at both RFC 9728 paths without calling the AuthenticationHandler", async ct =>
                    {
                        bool authCalled = false;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.ProtectedResourceMetadata = new McpProtectedResourceMetadata
                            {
                                Resource = "https://mcp.example.com/mcp",
                                AuthorizationServers = new List<string> { "https://auth.example.com" },
                                ScopesSupported = new List<string> { "tools:read" }
                            };
                            server.AuthenticationHandler = _ =>
                            {
                                authCalled = true;
                                return Task.FromResult(AuthenticationResult.BearerChallenge($"http://localhost{McpProtocol.ProtectedResourceMetadataPath}"));
                            };
                        }).ConfigureAwait(false);

                        using HttpResponseMessage root = await fixture.Client.GetAsync($"{fixture.BaseUrl}{McpProtocol.ProtectedResourceMetadataPath}", ct).ConfigureAwait(false);
                        using HttpResponseMessage scoped = await fixture.Client.GetAsync($"{fixture.BaseUrl}{McpProtocol.ProtectedResourceMetadataPath}/mcp", ct).ConfigureAwait(false);
                        JsonProbe document = JsonProbe.Parse(await root.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

                        TestAssert.Equal(HttpStatusCode.OK, root.StatusCode);
                        TestAssert.Equal(HttpStatusCode.OK, scoped.StatusCode, "The path-scoped form is served too.");
                        TestAssert.Equal("application/json", root.Content.Headers.ContentType?.MediaType);
                        TestAssert.Equal("https://mcp.example.com/mcp", document.Get("resource").String());
                        TestAssert.Equal("https://auth.example.com", document.Get("authorization_servers")[0].String());
                        TestAssert.Equal("tools:read", document.Get("scopes_supported")[0].String());
                        TestAssert.False(document.Has("resource_name"), "Null fields are omitted.");
                        TestAssert.False(authCalled, "The metadata never requires authentication.");
                    }),

                    Case(suiteId, "ProtectedResourceMetadataAbsentOrInvalid", "Without metadata the paths return 404; invalid metadata is rejected; POST gets 405; a disallowed origin gets 403", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpResponseMessage missing = await fixture.Client.GetAsync($"{fixture.BaseUrl}{McpProtocol.ProtectedResourceMetadataPath}", ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, missing.StatusCode, "Nothing is served by default.");

                        TestAssert.Throws<ArgumentException>(() => fixture.Server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { AuthorizationServers = new List<string> { "https://a" } }, "A resource is required.");
                        TestAssert.Throws<ArgumentException>(() => fixture.Server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "https://r" }, "An authorization server is required.");

                        fixture.Server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "https://r", AuthorizationServers = new List<string> { "https://a" } };
                        using HttpResponseMessage post = await fixture.Client.PostAsync($"{fixture.BaseUrl}{McpProtocol.ProtectedResourceMetadataPath}", new StringContent("{}"), ct).ConfigureAwait(false);
                        using HttpRequestMessage foreign = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}{McpProtocol.ProtectedResourceMetadataPath}");
                        foreign.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                        using HttpResponseMessage foreignResponse = await fixture.Client.SendAsync(foreign, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
                        TestAssert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode, "Origin validation still applies.");
                        fixture.Server.ProtectedResourceMetadata = null;
                        TestAssert.Null(fixture.Server.ProtectedResourceMetadata, "Null clears the metadata.");
                    }),

                    Case(suiteId, "CorsReflectsRequestedHeadersForAllowedOrigins", "A preflight from an allowed origin gets the requested header names back (valid tokens only); WWW-Authenticate is exposed; a disallowed origin is still 403", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        using HttpRequestMessage preflight = new HttpRequestMessage(HttpMethod.Options, $"{fixture.BaseUrl}/mcp/");
                        preflight.Headers.TryAddWithoutValidation("Origin", "http://localhost:6274");
                        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
                        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "X-API-Key, Mcp-Param-Region, traceparent, bad header, x-api-key");
                        using HttpResponseMessage reflected = await fixture.Client.SendAsync(preflight, ct).ConfigureAwait(false);

                        using HttpRequestMessage plain = new HttpRequestMessage(HttpMethod.Options, $"{fixture.BaseUrl}/mcp/");
                        plain.Headers.TryAddWithoutValidation("Origin", "http://localhost:6274");
                        using HttpResponseMessage defaults = await fixture.Client.SendAsync(plain, ct).ConfigureAwait(false);

                        using HttpRequestMessage evil = new HttpRequestMessage(HttpMethod.Options, $"{fixture.BaseUrl}/mcp/");
                        evil.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                        evil.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "X-API-Key");
                        using HttpResponseMessage rejected = await fixture.Client.SendAsync(evil, ct).ConfigureAwait(false);

                        TestAssert.Equal("X-API-Key, Mcp-Param-Region, traceparent", reflected.Headers.GetValues("Access-Control-Allow-Headers").Single(), "Requested names are allowed; invalid tokens and duplicates are dropped.");
                        TestAssert.True(reflected.Headers.Vary.Contains("Access-Control-Request-Headers"), "The reflected response varies on the requested headers.");
                        TestAssert.True(defaults.Headers.GetValues("Access-Control-Allow-Headers").Single().Contains("Authorization"), "Without a request list the default list is sent.");
                        TestAssert.True(defaults.Headers.GetValues("Access-Control-Expose-Headers").Single().Contains("WWW-Authenticate"), "WWW-Authenticate is exposed to browser clients.");
                        TestAssert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode, "A disallowed origin gets no reflection.");
                    }),

                    Case(suiteId, "MissingContentTypeAcceptedOnlyFromNonBrowsers", "POST /mcp without Content-Type is accepted without an Origin header and rejected (415) from a browser origin", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string? sessionId = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);

                        HttpStatusCode noOrigin = await PostWithoutContentTypeAsync(fixture, sessionId, null, ct).ConfigureAwait(false);
                        HttpStatusCode browser = await PostWithoutContentTypeAsync(fixture, sessionId, "http://localhost:6274", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, noOrigin, "A non-browser client may omit Content-Type.");
                        TestAssert.Equal(HttpStatusCode.UnsupportedMediaType, browser, "A browser request without Content-Type is still 415.");
                    }),

                    Case(suiteId, "StatelessNotificationNeedsNoRoutingHeaders", "A 2026-07-28 notification is accepted (202) without MCP-Protocol-Version or Mcp-Method; a request without Mcp-Method is still 400", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        Dictionary<string, object?> meta = new Dictionary<string, object?> { { "_meta", McpHttpTestRequests.StatelessMeta() } };

                        RpcResult bare = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("notifications/cancelled", null, meta), new Dictionary<string, string>(), true, ct).ConfigureAwait(false);
                        RpcResult versionOnly = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("notifications/cancelled", null, meta),
                            new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 } }, true, ct).ConfigureAwait(false);
                        RpcResult request = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/list", 1, meta),
                            new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 } }, true, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Accepted, bare.StatusCode, $"Body: {bare.Body}");
                        TestAssert.Equal(HttpStatusCode.Accepted, versionOnly.StatusCode, $"Body: {versionOnly.Body}");
                        TestAssert.Equal(HttpStatusCode.BadRequest, request.StatusCode, "A request still needs Mcp-Method.");
                        TestAssert.Equal(-32020, request.Error.Get("code").Int());
                    }),

                    Case(suiteId, "ToolHandlerExceptionsAreToolExecutionErrors", "A tool handler exception becomes an isError result with a sanitized message (details only in the log), McpToolException text is shown to the model, and McpProtocolException stays a JSON-RPC error", async ct =>
                    {
                        List<string> log = new List<string>();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.Log += (sender, message) =>
                            {
                                lock (log) log.Add(message);
                            };
                            server.RegisterTool("failing", "Fails", new { type = "object" }, _ => throw new TimeoutException("Server=db01;Password=hunter2 timed out"));
                            server.RegisterTool("missing-city", "Explains", new { type = "object" }, _ => throw new McpToolException("City 'Atlantis' was not found."));
                            server.RegisterTool("strict", "Rejects", new { type = "object" }, _ => throw McpProtocolException.InvalidParams("strict says no"));
                        }).ConfigureAwait(false);

                        RpcResult failing = await fixture.PostMcpAsync("tools/call", new { name = "failing", arguments = new { } }, 1, null, ct).ConfigureAwait(false);
                        RpcResult explained = await fixture.PostMcpAsync("tools/call", new { name = "missing-city", arguments = new { } }, 4, null, ct).ConfigureAwait(false);
                        RpcResult strict = await fixture.PostMcpAsync("tools/call", new { name = "strict", arguments = new { } }, 2, null, ct).ConfigureAwait(false);
                        RpcResult stateless = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/call", 3, new Dictionary<string, object?> { { "name", "failing" }, { "arguments", new { } } }, "failing", null, ct).ConfigureAwait(false);

                        TestAssert.True(failing.Result.Get("isError").Bool(), $"The failure is a tool execution error. Body: {failing.Body}");
                        TestAssert.Equal("Tool 'failing' failed because of an internal error.", failing.Result.Get("content")[0].Get("text").String());
                        TestAssert.False(failing.Body.Contains("hunter2"), "Exception details are not sent.");
                        TestAssert.True(await WaitForAsync(() => { lock (log) return log.Any(line => line.Contains("hunter2") && line.Contains("TimeoutException")); }, ct).ConfigureAwait(false), "The details are logged.");
                        TestAssert.True(explained.Result.Get("isError").Bool(), "McpToolException is a tool execution error.");
                        TestAssert.Equal("City 'Atlantis' was not found.", explained.Result.Get("content")[0].Get("text").String(), "Its message is shown as written.");
                        TestAssert.Equal(-32602, strict.Error.Get("code").Int(), "An McpProtocolException is sent as a JSON-RPC error.");
                        TestAssert.Equal("strict says no", strict.Error.Get("message").String());
                        TestAssert.Equal(HttpStatusCode.OK, stateless.StatusCode, $"Body: {stateless.Body}");
                        TestAssert.True(stateless.Result.Get("isError").Bool(), "Stateless requests get the same tool execution error.");
                        TestAssert.Equal("complete", stateless.Result.Get("resultType").String(), "The stateless result is complete.");
                    }),

                    Case(suiteId, "IncludeToolExceptionMessagesShowsDetails", "With IncludeToolExceptionMessages true, the exception message is shown; the setting passes through on every MCP server", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.IncludeToolExceptionMessages = true;
                            server.RegisterTool("failing", "Fails", new { type = "object" }, _ => throw new InvalidOperationException("quota exceeded"));
                        }).ConfigureAwait(false);

                        RpcResult failing = await fixture.PostMcpAsync("tools/call", new { name = "failing", arguments = new { } }, 1, null, ct).ConfigureAwait(false);
                        TestAssert.Equal("Tool 'failing' failed: quota exceeded", failing.Result.Get("content")[0].Get("text").String());

                        using McpServer stdio = new McpServer();
                        using McpTcpServer tcp = new McpTcpServer(System.Net.IPAddress.Loopback, TestPorts.GetFreePort());
                        using McpWebsocketsServer websocket = new McpWebsocketsServer("localhost", TestPorts.GetFreePort());
                        TestAssert.False(stdio.IncludeToolExceptionMessages || tcp.IncludeToolExceptionMessages || websocket.IncludeToolExceptionMessages, "Details are hidden by default.");
                        stdio.IncludeToolExceptionMessages = true;
                        tcp.IncludeToolExceptionMessages = true;
                        websocket.IncludeToolExceptionMessages = true;
                        TestAssert.True(stdio.IncludeToolExceptionMessages && tcp.IncludeToolExceptionMessages && websocket.IncludeToolExceptionMessages, "The setting is stored.");
                        TestAssert.Throws<ArgumentNullException>(() => new McpToolException(""), "McpToolException needs a message.");
                    }),

                    Case(suiteId, "InvalidToolArgumentsAreToolExecutionErrors", "Invalid tool arguments produce isError: true with the reason and never run the handler; an unknown tool stays a protocol error", async ct =>
                    {
                        int invoked = 0;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterTool("strict", "Needs a count", new { type = "object", properties = new { count = new { type = "integer" } }, required = new[] { "count" }, additionalProperties = false },
                                _ => { Interlocked.Increment(ref invoked); return "ran"; });
                        }).ConfigureAwait(false);

                        RpcResult missing = await fixture.PostMcpAsync("tools/call", new { name = "strict", arguments = new { } }, 1, null, ct).ConfigureAwait(false);
                        RpcResult extra = await fixture.PostMcpAsync("tools/call", new { name = "strict", arguments = new { count = 1, other = true } }, 2, null, ct).ConfigureAwait(false);
                        RpcResult unknown = await fixture.PostMcpAsync("tools/call", new { name = "nope", arguments = new { } }, 3, null, ct).ConfigureAwait(false);
                        RpcResult valid = await fixture.PostMcpAsync("tools/call", new { name = "strict", arguments = new { count = 3 } }, 4, null, ct).ConfigureAwait(false);

                        TestAssert.True(missing.Result.Get("isError").Bool() && missing.Result.Get("content")[0].Get("text").String()!.Contains("count"), "A missing argument is a tool execution error naming it.");
                        TestAssert.True(extra.Result.Get("isError").Bool() && extra.Result.Get("content")[0].Get("text").String()!.Contains("other"), "An undeclared argument is a tool execution error naming it.");
                        TestAssert.Equal(-32602, unknown.Error.Get("code").Int(), "An unknown tool is a protocol error.");
                        TestAssert.False(valid.Result.Has("isError") && valid.Result.Get("isError").Bool(), "Valid arguments succeed.");
                        TestAssert.Equal(1, Volatile.Read(ref invoked), "Only the valid call ran the handler.");
                    }),

                    Case(suiteId, "MrtrHandlerSeesInputResponsesAndRequestState", "A tool that returns input_required sees the client's inputResponses and requestState on the retry (McpHttpClient round trip)", async ct =>
                    {
                        List<string> observed = new List<string>();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => RegisterConfirmTool((n, d, sc, h) => server.RegisterTool(n, d, sc, h), observed)).ConfigureAwait(false);

                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStatelessAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false), "The stateless client connects.");
                        JsonRpcResponse response = await client.CallToolStatelessAsync("confirm", new { path = "a.txt" },
                            inputRequired => new Dictionary<string, object?> { { "confirm", new { action = "accept", content = new { } } } }, 3, ct).ConfigureAwait(false);

                        System.Text.Json.JsonElement result = System.Text.Json.JsonSerializer.SerializeToElement(response.Result);
                        TestAssert.Equal("deleted a.txt (state delete-1)", result.GetProperty("content")[0].GetProperty("text").GetString(), "The retry completes with the gathered input.");
                        lock (observed) TestAssert.Equal("first:False|retry:True", String.Join("|", observed), "The handler saw no input first, then the input and state.");
                    }),

                    Case(suiteId, "MrtrWorksOnTcp", "A raw tools/call retry carrying inputResponses and requestState completes on the TCP transport", async ct =>
                    {
                        List<string> observed = new List<string>();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, server => RegisterConfirmTool((n, d, sc, h) => server.RegisterTool(n, d, sc, h), observed)).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        object meta = McpHttpTestRequests.StatelessMeta();

                        System.Text.Json.JsonElement first = await client.CallAsync<System.Text.Json.JsonElement>("tools/call", new Dictionary<string, object?> { { "name", "confirm" }, { "arguments", new { path = "b.txt" } }, { "_meta", meta } }, token: ct).ConfigureAwait(false);
                        System.Text.Json.JsonElement retry = await client.CallAsync<System.Text.Json.JsonElement>("tools/call", new Dictionary<string, object?>
                        {
                            { "name", "confirm" },
                            { "arguments", new { path = "b.txt" } },
                            { "inputResponses", new Dictionary<string, object?> { { "confirm", new { action = "accept" } } } },
                            { "requestState", first.GetProperty("requestState").GetString() },
                            { "_meta", meta }
                        }, token: ct).ConfigureAwait(false);

                        TestAssert.Equal("input_required", first.GetProperty("resultType").GetString(), "The first call asks for input.");
                        TestAssert.Equal("complete", retry.GetProperty("resultType").GetString(), "The retry completes.");
                        TestAssert.Equal("deleted b.txt (state delete-1)", retry.GetProperty("content")[0].GetProperty("text").GetString());
                    }),

                    Case(suiteId, "InputRequiredOnHandshakeEraBecomesToolError", "input_required returned to a handshake-era request becomes an isError tool result; CanRequestInput reports the era", async ct =>
                    {
                        List<bool> canRequest = new List<bool>();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterTool("ask", "Always asks for input", new { type = "object" }, _ =>
                            {
                                lock (canRequest) canRequest.Add(McpToolCallContext.Current!.CanRequestInput);
                                return new McpInputRequiredResult
                                {
                                    InputRequests = new Dictionary<string, McpInputRequest> { { "q", new McpInputRequest { Method = "elicitation/create", Params = new { message = "?" } } } },
                                    RequestState = "s"
                                };
                            });
                        }).ConfigureAwait(false);

                        RpcResult legacy = await fixture.PostMcpAsync("tools/call", new { name = "ask", arguments = new { } }, 1, null, ct).ConfigureAwait(false);
                        RpcResult stateless = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/call", 2, new Dictionary<string, object?> { { "name", "ask" }, { "arguments", new { } } }, "ask", null, ct).ConfigureAwait(false);

                        TestAssert.True(legacy.Result.Get("isError").Bool(), "A handshake-era request gets a tool error.");
                        TestAssert.True(legacy.Result.Get("content")[0].Get("text").String()!.Contains("2026-07-28"), "The error explains the version requirement.");
                        TestAssert.False(legacy.Result.Has("inputRequests"), "No input_required is sent to a handshake-era client.");
                        TestAssert.Equal("input_required", stateless.Result.Get("resultType").String(), "A stateless request gets input_required.");
                        lock (canRequest) TestAssert.Equal("False,True", String.Join(",", canRequest), "CanRequestInput is false for handshake-era and true for stateless requests.");
                    }),

                    Case(suiteId, "ToolCallContextIsScopedToToolHandlers", "McpToolCallContext.Current is set only inside tool handlers and is null elsewhere", async ct =>
                    {
                        string? inMethod = "unset";
                        string? inTool = "unset";
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("custom/probe", _ => { inMethod = McpToolCallContext.Current?.ToolName ?? "null"; return new McpEmptyResult(); });
                            server.RegisterTool("probe", "Reads the context", new { type = "object" }, _ => { inTool = McpToolCallContext.Current?.ToolName ?? "null"; return "ok"; });
                        }).ConfigureAwait(false);

                        await fixture.PostMcpAsync("custom/probe", new { }, 1, null, ct).ConfigureAwait(false);
                        await fixture.PostMcpAsync("tools/call", new { name = "probe", arguments = new { } }, 2, null, ct).ConfigureAwait(false);

                        TestAssert.Equal("null", inMethod, "Outside a tool handler the context is null.");
                        TestAssert.Equal("probe", inTool, "Inside a tool handler the context names the tool.");
                        TestAssert.Throws<ArgumentNullException>(() => new McpToolCallContext("", null, null), "A tool name is required.");
                        TestAssert.False(new McpToolCallContext("t", null, null).IsRetry, "A call without input is not a retry.");
                    }),

                    Case(suiteId, "HttpClientReadsSseResponses", "McpHttpClient accepts POST responses sent as SSE streams: it picks the matching response and raises notifications", async ct =>
                    {
                        using SseResponseServer server = new SseResponseServer();
                        using McpHttpClient client = new McpHttpClient();
                        List<string> notifications = new List<string>();
                        client.NotificationReceived += (_, n) => { lock (notifications) notifications.Add(n.Method); };

                        bool connected = await client.ConnectStreamableAsync(server.BaseUrl, token: ct).ConfigureAwait(false);
                        JsonRpcResponse list = await client.CallAsync("tools/list", token: ct).ConfigureAwait(false);
                        System.Text.Json.JsonElement result = System.Text.Json.JsonSerializer.SerializeToElement(list.Result);

                        TestAssert.True(connected, "The handshake completes over an SSE response.");
                        TestAssert.Equal("sse-session", client.SessionId, "The session header is captured.");
                        TestAssert.Equal("streamed", result.GetProperty("tools")[0].GetProperty("name").GetString(), "The matching response is used, not the unrelated one.");
                        lock (notifications) TestAssert.True(notifications.Count >= 2 && notifications.TrueForAll(m => m == "notifications/progress"), "Notifications on the stream are raised.");
                    }),

                    Case(suiteId, "UnknownVersionNegotiatesOnWebSocket", "initialize with an unknown version negotiates the cap on the WebSocket transport too", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        System.Text.Json.JsonElement result = await client.CallAsync<System.Text.Json.JsonElement>("initialize", new { protocolVersion = "2099-01-01", capabilities = new { }, clientInfo = new { name = "t", version = "1" } }, token: ct).ConfigureAwait(false);
                        TestAssert.Equal(McpProtocol.NewestHandshakeProtocolVersion, result.GetProperty("protocolVersion").GetString());
                    }),
                });
        }

        private static async Task<string?> InitializeAsync(HttpMcpTestServerFixture fixture, string version, CancellationToken token)
        {
            object parameters = new { protocolVersion = version, capabilities = new { }, clientInfo = new { name = "conformance", version = "1.0.0" } };
            RpcResult result = await fixture.PostJsonRpcAsync("/mcp/", "initialize", parameters, "init", null, token).ConfigureAwait(false);
            TestAssert.Equal(version, result.Result.Get("protocolVersion").String(), "The requested version is negotiated.");
            return result.SessionId;
        }

        private static void RegisterConfirmTool(Action<string, string, object, Func<RpcParameters?, object>> register, List<string> observed)
        {
            Func<RpcParameters?, object> handler = args =>
            {
                McpToolCallContext? call = McpToolCallContext.Current;
                string path = args?.GetString("path") ?? "?";
                lock (observed) observed.Add((call!.IsRetry ? "retry:" : "first:") + call.InputResponses.ContainsKey("confirm"));
                if (call.InputResponses.TryGetValue("confirm", out System.Text.Json.JsonElement answer)
                    && answer.TryGetProperty("action", out System.Text.Json.JsonElement action) && action.GetString() == "accept")
                {
                    return McpToolCallResult.FromText($"deleted {path} (state {call.RequestState})");
                }

                return new McpInputRequiredResult
                {
                    InputRequests = new Dictionary<string, McpInputRequest>
                    {
                        { "confirm", new McpInputRequest { Method = "elicitation/create", Params = new { mode = "form", message = $"Delete {path}?", requestedSchema = new { type = "object", properties = new { } } } } }
                    },
                    RequestState = "delete-1"
                };
            };

            object schema = new { type = "object", properties = new { path = new { type = "string" } } };
            register("confirm", "Deletes a file after confirmation", schema, handler);
        }

        private static async Task<HttpStatusCode> PostWithoutContentTypeAsync(HttpMcpTestServerFixture fixture, string? sessionId, string? origin, CancellationToken token)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(McpHttpTestRequests.BuildBody("ping", 1, null)))
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (sessionId != null) request.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, sessionId);
            if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
            using HttpResponseMessage response = await fixture.Client.SendAsync(request, token).ConfigureAwait(false);
            return response.StatusCode;
        }

        private static async Task<RpcResult> SendRawAsync(HttpMcpTestServerFixture fixture, string path, string body, string? sessionId, string? protocolVersion, CancellationToken token)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            if (sessionId != null) headers[McpProtocol.SessionIdHeader] = sessionId;
            if (protocolVersion != null) headers[McpProtocol.ProtocolVersionHeader] = protocolVersion;
            return await McpHttpTestRequests.SendAsync(fixture, body, headers, true, token, path).ConfigureAwait(false);
        }

        private static async Task<bool> WaitForAsync(Func<bool> condition, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            return condition();
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "conformance" });
        }
    }
}
