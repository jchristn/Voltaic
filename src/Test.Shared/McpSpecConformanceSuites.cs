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

                    Case(suiteId, "HeaderlessBatchAssumes20250326", "Without MCP-Protocol-Version or a negotiated version, the server assumes 2025-03-26, which allows batching", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.RequireInitializedSessions = false).ConfigureAwait(false);

                        string batch = "[" + McpHttpTestRequests.BuildBody("ping", 1, null) + "]";
                        RpcResult result = await SendRawAsync(fixture, "/mcp/", batch, null, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, result.StatusCode, $"A header-less batch is treated as 2025-03-26. Body: {result.Body}");
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
                });
        }

        private static async Task<string?> InitializeAsync(HttpMcpTestServerFixture fixture, string version, CancellationToken token)
        {
            object parameters = new { protocolVersion = version, capabilities = new { }, clientInfo = new { name = "conformance", version = "1.0.0" } };
            RpcResult result = await fixture.PostJsonRpcAsync("/mcp/", "initialize", parameters, "init", null, token).ConfigureAwait(false);
            TestAssert.Equal(version, result.Result.Get("protocolVersion").String(), "The requested version is negotiated.");
            return result.SessionId;
        }

        private static async Task<RpcResult> SendRawAsync(HttpMcpTestServerFixture fixture, string path, string body, string? sessionId, string? protocolVersion, CancellationToken token)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            if (sessionId != null) headers[McpProtocol.SessionIdHeader] = sessionId;
            if (protocolVersion != null) headers[McpProtocol.ProtocolVersionHeader] = protocolVersion;
            return await McpHttpTestRequests.SendAsync(fixture, body, headers, true, token, path).ConfigureAwait(false);
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
