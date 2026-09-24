namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Proves that an <see cref="McpHttpServer"/> with an <c>AuthenticationHandler</c> applies exactly the
    /// same MCP protocol pipeline as one without: version resolution, stateless routing, the batching gate,
    /// session tracking and termination, and the Accept and protocol-version header checks. Before v1.1.0
    /// authenticated requests took a separate path that skipped most of these.
    /// </summary>
    public static class McpHttpAuthParitySuites
    {
        /// <summary>
        /// Builds the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public static TestSuiteDescriptor AuthParity()
        {
            const string suiteId = "McpHttp.Server.AuthParity";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP HTTP Authenticated Path Parity",
                new List<TestCaseDescriptor>
                {
                    Parity(suiteId, "BatchAllowedOn20250326", "A JSON-RPC batch on a 2025-03-26 session is processed", async (fixture, auth, ct) =>
                    {
                        RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20250326, auth, ct).ConfigureAwait(false);
                        string batch = "[" + McpHttpTestRequests.BuildBody("tools/list", 1, new { }) + "," + McpHttpTestRequests.BuildBody("tools/call", 2, new { name = "echo-tool", arguments = new { } }) + "]";
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, batch, Headers(initialize.SessionId, null, auth), true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, $"The batch should be processed. Body: {response.Body}");
                        TestAssert.True(response.Root.IsArray && response.Root.Length == 2, "The batch returns two responses.");
                        return new List<RpcResult> { initialize, response };
                    }),

                    Parity(suiteId, "BatchRejectedOn20251125", "A JSON-RPC batch on a 2025-11-25 session is rejected with -32600", async (fixture, auth, ct) =>
                    {
                        RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20251125, auth, ct).ConfigureAwait(false);
                        string batch = "[" + McpHttpTestRequests.BuildBody("tools/list", 1, new { }) + "]";
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, batch, Headers(initialize.SessionId, McpProtocol.ProtocolVersion20251125, auth), true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Batching is removed in 2025-11-25.");
                        TestAssert.Equal(-32600, response.Error.Get("code").Int(), "The batch is an invalid request.");
                        return new List<RpcResult> { initialize, response };
                    }),

                    Parity(suiteId, "StatelessMissingMethodHeaderRejected", "A stateless request without Mcp-Method is rejected with -32020", async (fixture, auth, ct) =>
                    {
                        Dictionary<string, string> headers = Headers(null, McpProtocol.ProtocolVersion20260728, auth);
                        string body = McpHttpTestRequests.BuildBody("tools/list", 1, new Dictionary<string, object?> { { "_meta", McpHttpTestRequests.StatelessMeta() } });
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, body, headers, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "A missing Mcp-Method header is 400.");
                        TestAssert.Equal(-32020, response.Error.Get("code").Int(), "A missing Mcp-Method header maps to -32020.");
                        return new List<RpcResult> { response };
                    }),

                    Parity(suiteId, "StatelessMethodHeaderMismatchRejected", "A stateless Mcp-Method that disagrees with the body is rejected with -32020", async (fixture, auth, ct) =>
                    {
                        Dictionary<string, string> headers = Headers(null, McpProtocol.ProtocolVersion20260728, auth);
                        headers[McpProtocol.MethodHeader] = "prompts/list";
                        string body = McpHttpTestRequests.BuildBody("tools/list", 1, new Dictionary<string, object?> { { "_meta", McpHttpTestRequests.StatelessMeta() } });
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, body, headers, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(-32020, response.Error.Get("code").Int(), "A mismatched Mcp-Method header maps to -32020.");
                        return new List<RpcResult> { response };
                    }),

                    Parity(suiteId, "StatelessUnknownMethodReturns404", "An unknown stateless method returns 404 with -32601", async (fixture, auth, ct) =>
                    {
                        RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, "does/notexist", 1, null, null, auth, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, response.StatusCode, "An unknown stateless method is 404.");
                        return new List<RpcResult> { response };
                    }),

                    Parity(suiteId, "StatelessToolsListStamped", "A stateless tools/list is stamped with resultType and cache guidance", async (fixture, auth, ct) =>
                    {
                        RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/list", 1, null, null, auth, ct).ConfigureAwait(false);
                        TestAssert.Equal("complete", response.Result.Get("resultType").String(), "resultType is present.");
                        TestAssert.Equal("private", response.Result.Get("cacheScope").String(), "cacheScope is present.");
                        return new List<RpcResult> { response };
                    }),

                    Parity(suiteId, "UnsupportedVersionHeaderRejected", "An unsupported MCP-Protocol-Version header is rejected with -32022", async (fixture, auth, ct) =>
                    {
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 1, new { }, null, "1999-01-01", auth, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "An unsupported version header is 400.");
                        TestAssert.Equal(-32022, response.Error.Get("code").Int(), "An unsupported version header maps to -32022.");
                        return new List<RpcResult> { response };
                    }),

                    Parity(suiteId, "TerminatedSessionReturns404", "A request on a terminated session returns 404", async (fixture, auth, ct) =>
                    {
                        RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20251125, auth, ct).ConfigureAwait(false);
                        RpcResult delete = await McpHttpTestRequests.DeleteSessionAsync(fixture, initialize.SessionId!, auth, ct).ConfigureAwait(false);
                        RpcResult after = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 2, new { }, initialize.SessionId, McpProtocol.ProtocolVersion20251125, auth, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, delete.StatusCode, "DELETE terminates the session.");
                        TestAssert.Equal(HttpStatusCode.NotFound, after.StatusCode, "The terminated session is rejected.");
                        return new List<RpcResult> { delete, after };
                    }),

                    Parity(suiteId, "MissingAcceptReturns406", "A POST without the Streamable HTTP Accept header returns 406", async (fixture, auth, ct) =>
                    {
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/list", 1, new { }), Headers(null, null, auth), false, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode, "A missing Accept header is 406.");
                        return new List<RpcResult> { response };
                    }),

                    Parity(suiteId, "NotificationReturns202", "A handshake-era notification returns 202 with no body", async (fixture, auth, ct) =>
                    {
                        RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20251125, auth, ct).ConfigureAwait(false);
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, "notifications/initialized", null, null, initialize.SessionId, McpProtocol.ProtocolVersion20251125, auth, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.Accepted, response.StatusCode, "Notifications are accepted with 202.");
                        return new List<RpcResult> { response };
                    }),

                    Case(suiteId, "StatelessRequestsCreateNoSessionsWithAuth", "Authenticated stateless requests do not accumulate server sessions", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(true, RegisterTools, ct).ConfigureAwait(false);

                        for (int index = 0; index < 10; index++)
                        {
                            RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/list", index, null, null, McpHttpTestRequests.ValidToken, ct).ConfigureAwait(false);
                            TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "Each stateless request succeeds.");
                        }

                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "Stateless traffic must not create sessions (it created one per request before v1.1.0).");
                    }),

                    Case(suiteId, "StatelessRequestWithoutCredentialsRejected", "A stateless request without valid credentials is rejected before dispatch", async ct =>
                    {
                        int calls = 0;
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(true, server =>
                            server.RegisterTool("counted", "Counts calls", new { type = "object" }, _ =>
                            {
                                Interlocked.Increment(ref calls);
                                return "counted";
                            }), ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "name", "counted" }, { "arguments", new { } } };
                        RpcResult missing = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/call", 1, parameters, "counted", null, ct).ConfigureAwait(false);
                        RpcResult wrong = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/call", 2, parameters, "counted", "Bearer wrong", ct).ConfigureAwait(false);
                        RpcResult discover = await McpHttpTestRequests.SendStatelessAsync(fixture, "server/discover", 3, null, null, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode, "Missing credentials are 401.");
                        TestAssert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode, "Wrong credentials are 401.");
                        TestAssert.Equal(HttpStatusCode.Unauthorized, discover.StatusCode, "server/discover is not an authentication bypass.");
                        TestAssert.Equal(0, calls, "The tool never runs for an unauthenticated request.");
                    }),

                    Case(suiteId, "PingBypassStillSkipsAuthentication", "The ping bypass still works and now runs through the shared pipeline", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(true, null, ct).ConfigureAwait(false);
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, "ping", 1, new { }, null, null, null, ct).ConfigureAwait(false);
                        TestAssert.True(response.StatusCode != HttpStatusCode.Unauthorized, "ping must not require credentials.");
                    }),
                });
        }

        private static TestCaseDescriptor Parity(
            string suiteId,
            string caseId,
            string displayName,
            Func<HttpMcpTestServerFixture, string?, CancellationToken, Task<List<RpcResult>>> scenario)
        {
            return Case(suiteId, caseId, displayName + " identically with and without authentication", async ct =>
            {
                List<RpcResult> unauthenticated;
                await using (HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, RegisterTools, ct).ConfigureAwait(false))
                {
                    unauthenticated = await scenario(fixture, null, ct).ConfigureAwait(false);
                }

                List<RpcResult> authenticated;
                await using (HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(true, RegisterTools, ct).ConfigureAwait(false))
                {
                    authenticated = await scenario(fixture, McpHttpTestRequests.ValidToken, ct).ConfigureAwait(false);
                }

                TestAssert.Equal(unauthenticated.Count, authenticated.Count, "Both runs produce the same number of responses.");
                for (int index = 0; index < unauthenticated.Count; index++)
                {
                    TestAssert.Equal(unauthenticated[index].StatusCode, authenticated[index].StatusCode, $"Response {index} status matches.");
                    TestAssert.Equal(unauthenticated[index].Body, authenticated[index].Body, $"Response {index} body matches.");
                    TestAssert.Equal(unauthenticated[index].SessionId == null, authenticated[index].SessionId == null, $"Response {index} session header presence matches.");
                }
            });
        }

        private static Dictionary<string, string> Headers(string? sessionId, string? protocolVersion, string? authorization)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            if (sessionId != null)
            {
                headers[McpProtocol.SessionIdHeader] = sessionId;
            }

            if (protocolVersion != null)
            {
                headers[McpProtocol.ProtocolVersionHeader] = protocolVersion;
            }

            if (authorization != null)
            {
                headers["Authorization"] = authorization;
            }

            return headers;
        }

        private static void RegisterTools(McpHttpServer server)
        {
            server.RegisterTool("echo-tool", "Echo tool", new { type = "object" }, _ => "echoed");
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "auth", "parity" });
        }
    }
}
