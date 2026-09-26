namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers handshake-era session rules on <see cref="McpHttpServer"/>: sessions are created only by a
    /// successful <c>initialize</c>, session IDs are always server-generated, unknown IDs get 404, sessionless
    /// requests get 400 on the MCP endpoint and run without a session on the JSON-RPC endpoint, sessions are
    /// bound to the principal that created them, and <see cref="McpHttpServer.RequireInitializedSessions"/>
    /// relaxes only the sessionless rule. Also covers <see cref="McpHttpClient"/> performing the MCP handshake.
    /// </summary>
    public static class McpHttpSessionSuites
    {
        /// <summary>
        /// Session creation, rejection, binding, and client handshake cases.
        /// </summary>
        public static TestSuiteDescriptor SessionLifecycle()
        {
            const string suiteId = "McpHttp.Sessions";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP HTTP Session Lifecycle",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "InitializeCreatesSessionAndRaisesClientConnectedOnce", "A successful initialize creates one session, raises ClientConnected once, and the session works until DELETE", async ct =>
                    {
                        int connected = 0;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.ClientConnected += (_, _) => Interlocked.Increment(ref connected);
                            RegisterMarker(server);
                        }).ConfigureAwait(false);

                        string? sessionId = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);
                        RpcResult list = await fixture.PostJsonRpcAsync("/mcp/", "tools/list", new { }, 2, sessionId, ct).ConfigureAwait(false);
                        RpcResult delete = await McpHttpTestRequests.DeleteSessionAsync(fixture, sessionId!, null, ct).ConfigureAwait(false);

                        TestAssert.False(String.IsNullOrEmpty(sessionId), "initialize returns MCP-Session-Id.");
                        TestAssert.Equal(HttpStatusCode.OK, list.StatusCode, "The issued session serves tools/list.");
                        TestAssert.Equal(sessionId, list.SessionId, "The session ID is echoed on later responses.");
                        TestAssert.Equal(HttpStatusCode.OK, delete.StatusCode, "DELETE of the issued session succeeds.");
                        TestAssert.Equal(1, Volatile.Read(ref connected), "ClientConnected fires exactly once.");
                    }),

                    Case(suiteId, "RejectedInitializeCreatesNoSession", "initialize with an unsupported version returns -32602 and creates no session on /mcp or /rpc", async ct =>
                    {
                        int connected = 0;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.ClientConnected += (_, _) => Interlocked.Increment(ref connected);
                        }).ConfigureAwait(false);

                        object parameters = new { protocolVersion = "1999-01-01", capabilities = new { }, clientInfo = new { name = "t", version = "1" } };
                        RpcResult mcp = await fixture.PostJsonRpcAsync("/mcp/", "initialize", parameters, 1, null, ct).ConfigureAwait(false);
                        RpcResult rpc = await fixture.PostJsonRpcAsync("/rpc/", "initialize", parameters, 2, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(-32602, mcp.Error.Get("code").Int(), "/mcp rejects the version.");
                        TestAssert.Equal(-32602, rpc.Error.Get("code").Int(), "/rpc rejects the version.");
                        TestAssert.True(mcp.SessionId == null && rpc.SessionId == null, "A rejected initialize returns no MCP-Session-Id.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is registered.");
                        TestAssert.Equal(0, Volatile.Read(ref connected), "ClientConnected is not raised.");
                    }),

                    Case(suiteId, "SessionlessRequestOnMcpReturns400", "A sessionless tools/list on /mcp returns 400 with -32600 and creates no session", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterMarker).ConfigureAwait(false);

                        RpcResult list = await fixture.PostJsonRpcAsync("/mcp/", "tools/list", new { }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, list.StatusCode, $"Body: {list.Body}");
                        TestAssert.Equal(-32600, list.Error.Get("code").Int(), "The error is invalid request.");
                        TestAssert.True(list.SessionId == null, "No session header is returned.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "SessionlessToolCallOnMcpNeverRunsTheTool", "A sessionless tools/call on /mcp is rejected before the tool runs", async ct =>
                    {
                        MarkerProbe probe = new MarkerProbe();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => RegisterMarker(server, probe)).ConfigureAwait(false);

                        RpcResult call = await fixture.PostJsonRpcAsync("/mcp/", "tools/call", new { name = "marker", arguments = new { } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, call.StatusCode, $"Body: {call.Body}");
                        TestAssert.Equal(0, probe.Count, "The tool never runs.");
                    }),

                    Case(suiteId, "SessionlessNotificationOnMcpReturns400", "A sessionless notification on /mcp returns 400", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult notification = await fixture.PostJsonRpcAsync("/mcp/", "notifications/initialized", null, null, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, notification.StatusCode);
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "SessionlessBatchOnMcpReturns400", "A sessionless batch on /mcp returns 400 even in a revision that allows batching", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        string body = "[" + McpHttpTestRequests.BuildBody("ping", 1, null) + "," + McpHttpTestRequests.BuildBody("tools/list", 2, new { }) + "]";
                        RpcResult batch = await McpHttpTestRequests.SendAsync(fixture, body,
                            new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, "2025-03-26" } }, true, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, batch.StatusCode, $"Body: {batch.Body}");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "SessionlessPingOnMcpIsAnsweredWithoutSession", "A sessionless ping on /mcp is answered as a connectivity check and creates no session", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult ping = await fixture.PostJsonRpcAsync("/mcp/", "ping", null, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, ping.StatusCode);
                        TestAssert.True(ping.Result.IsObject && ping.Result.Length == 0, "ping returns {}.");
                        TestAssert.True(ping.SessionId == null, "No session header is returned.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "UnknownSessionOnMcpReturns404AndIsNotAdopted", "A made-up MCP-Session-Id on /mcp returns 404 with -32001 and is never adopted", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult list = await fixture.PostJsonRpcAsync("/mcp/", "tools/list", new { }, 1, "made-up-session-123", ct).ConfigureAwait(false);
                        RpcResult delete = await McpHttpTestRequests.DeleteSessionAsync(fixture, "made-up-session-123", null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.NotFound, list.StatusCode, $"Body: {list.Body}");
                        TestAssert.Equal(-32001, list.Error.Get("code").Int(), "The error is session not found.");
                        TestAssert.False(list.Body.Contains("made-up-session-123"), "The rejected ID is not echoed.");
                        TestAssert.True(list.SessionId == null, "No session header is echoed.");
                        TestAssert.Equal(HttpStatusCode.NotFound, delete.StatusCode, "The made-up ID was never registered.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "UnknownSessionOnRpcReturns404", "A made-up MCP-Session-Id on /rpc returns 404 and is never adopted", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult list = await fixture.PostJsonRpcAsync("/rpc/", "tools/list", new { }, 1, "made-up-session-456", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.NotFound, list.StatusCode, $"Body: {list.Body}");
                        TestAssert.Equal(-32001, list.Error.Get("code").Int(), "The error is session not found.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "ExpiredSessionReturns404UntilReinitialized", "A session removed by expiry returns 404, and a new initialize recovers", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);

                        string? first = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);
                        TestAssert.True(fixture.Server.RemoveSession(first!), "The session is removed, as the idle sweep would.");
                        RpcResult stale = await fixture.PostJsonRpcAsync("/mcp/", "tools/list", new { }, 1, first, ct).ConfigureAwait(false);
                        string? second = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);
                        RpcResult fresh = await fixture.PostJsonRpcAsync("/mcp/", "tools/list", new { }, 2, second, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.NotFound, stale.StatusCode, "The expired session is not found.");
                        TestAssert.False(StringComparer.Ordinal.Equals(first, second), "Re-initializing issues a new ID.");
                        TestAssert.Equal(HttpStatusCode.OK, fresh.StatusCode, "The new session works.");
                    }),

                    Case(suiteId, "RpcInitializeCreatesSessionUsableOnRpc", "initialize on /rpc returns a session that works on /rpc", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterMarker).ConfigureAwait(false);

                        string? sessionId = await fixture.InitializeSessionAsync(ct, "/rpc/").ConfigureAwait(false);
                        RpcResult list = await fixture.PostJsonRpcAsync("/rpc/", "tools/list", new { }, 2, sessionId, ct).ConfigureAwait(false);

                        TestAssert.False(String.IsNullOrEmpty(sessionId), "initialize on /rpc returns a session.");
                        TestAssert.Equal(HttpStatusCode.OK, list.StatusCode);
                        TestAssert.Equal(sessionId, list.SessionId, "The session is echoed.");
                        TestAssert.Equal(1, fixture.Server.GetActiveSessions().Count, "Exactly one session exists.");
                    }),

                    Case(suiteId, "SessionlessRpcRequestsWorkWithoutSessions", "Sessionless tools/list and tools/call on /rpc succeed without creating sessions", async ct =>
                    {
                        MarkerProbe probe = new MarkerProbe();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => RegisterMarker(server, probe)).ConfigureAwait(false);

                        RpcResult list = await fixture.PostJsonRpcAsync("/rpc/", "tools/list", new { }, 1, null, ct).ConfigureAwait(false);
                        RpcResult call = await fixture.PostJsonRpcAsync("/rpc/", "tools/call", new { name = "marker", arguments = new { } }, 2, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, list.StatusCode);
                        TestAssert.Equal(HttpStatusCode.OK, call.StatusCode);
                        TestAssert.Equal(1, probe.Count, "The tool ran.");
                        TestAssert.True(list.SessionId == null && call.SessionId == null, "No session header is returned.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "StatelessRequestsCreateNoSession", "Stateless 2026-07-28 requests create no session on /mcp or /rpc", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, RegisterMarker).ConfigureAwait(false);

                        RpcResult mcp = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/list", 1, null, null, null, ct).ConfigureAwait(false);
                        RpcResult rpc = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/list", 2, null, null, null, ct, "/rpc/").ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, mcp.StatusCode, $"Body: {mcp.Body}");
                        TestAssert.Equal(HttpStatusCode.OK, rpc.StatusCode, $"Body: {rpc.Body}");
                        TestAssert.True(mcp.SessionId == null && rpc.SessionId == null, "No session header is returned.");
                        TestAssert.Equal(0, fixture.Server.GetActiveSessions().Count, "No session is created.");
                    }),

                    Case(suiteId, "QueryStringSessionIgnoredOnPost", "POST ignores the session query parameter, so session IDs stay out of URLs", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string? sessionId = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);

                        RpcResult list = await fixture.PostJsonRpcAsync($"/mcp/?session={sessionId}", "tools/list", new { }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, list.StatusCode, "Without the header the request is sessionless.");
                    }),

                    Case(suiteId, "GetStreamsAcceptSessionQueryParameter", "GET streams accept the session query parameter for EventSource clients and reject unknown IDs with 404", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string? sessionId = await fixture.InitializeSessionAsync(ct, "/rpc/").ConfigureAwait(false);

                        using HttpRequestMessage known = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/events/?session={sessionId}");
                        using HttpResponseMessage knownResponse = await fixture.SendRawAsync(known, ct).ConfigureAwait(false);
                        using HttpRequestMessage unknown = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/events/?session=never-issued");
                        using HttpResponseMessage unknownResponse = await fixture.SendRawAsync(unknown, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, knownResponse.StatusCode, "A known session opens the stream.");
                        TestAssert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode, "An unknown session is not found.");
                    }),

                    Case(suiteId, "SessionIsBoundToItsPrincipal", "A session created by one authenticated principal returns 404 to a different principal", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = request => Task.FromResult(new AuthenticationResult
                            {
                                IsAuthenticated = !String.IsNullOrEmpty(request.Headers["Authorization"]),
                                Principal = request.Headers["Authorization"]
                            });
                            RegisterMarker(server);
                        }).ConfigureAwait(false);

                        RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, null, "alice", ct).ConfigureAwait(false);
                        string sessionId = initialize.SessionId!;
                        RpcResult asAlice = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 2, new { }, sessionId, null, "alice", ct).ConfigureAwait(false);
                        RpcResult asMallory = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 3, new { }, sessionId, null, "mallory", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, asAlice.StatusCode, "The owner can use the session.");
                        TestAssert.Equal(HttpStatusCode.NotFound, asMallory.StatusCode, "Another principal cannot adopt the session.");
                    }),

                    Case(suiteId, "RelaxedModeIssuesSessionsForSuccessfulRequests", "With RequireInitializedSessions false, successful sessionless requests get a session, failed ones do not, and unknown IDs still get 404", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RequireInitializedSessions = false;
                        }).ConfigureAwait(false);

                        RpcResult ping = await fixture.PostJsonRpcAsync("/mcp/", "ping", null, 1, null, ct).ConfigureAwait(false);
                        RpcResult failed = await fixture.PostJsonRpcAsync("/mcp/", "no/such/method", null, 2, null, ct).ConfigureAwait(false);
                        RpcResult rpcPing = await fixture.PostJsonRpcAsync("/rpc/", "ping", null, 3, null, ct).ConfigureAwait(false);
                        RpcResult unknown = await fixture.PostJsonRpcAsync("/mcp/", "ping", null, 4, "made-up", ct).ConfigureAwait(false);

                        TestAssert.False(String.IsNullOrEmpty(ping.SessionId), "A successful sessionless ping on /mcp is issued a session.");
                        TestAssert.True(failed.SessionId == null, "A failed request is not issued a session.");
                        TestAssert.False(String.IsNullOrEmpty(rpcPing.SessionId), "A successful sessionless ping on /rpc is issued a session.");
                        TestAssert.Equal(HttpStatusCode.NotFound, unknown.StatusCode, "Unknown IDs are still rejected.");
                        TestAssert.Equal(2, fixture.Server.GetActiveSessions().Count, "Only the two successful requests created sessions.");
                    }),

                    Case(suiteId, "RequireInitializedSessionsDefaultsToTrue", "RequireInitializedSessions defaults to true", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("localhost", TestPorts.GetFreePort());
                        TestAssert.True(server.RequireInitializedSessions, "Strict sessions are the default.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "SessionErrorFactoriesUseSpecCodes", "SessionNotFound is -32001 without the session ID, and SessionRequired is -32600", ct =>
                    {
                        McpProtocolException notFound = McpProtocolException.SessionNotFound();
                        McpProtocolException required = McpProtocolException.SessionRequired();

                        TestAssert.Equal(-32001, notFound.Code);
                        TestAssert.True(notFound.Message.Contains("initialize"), "SessionNotFound tells the client to re-initialize.");
                        TestAssert.Equal(-32600, required.Code);
                        TestAssert.True(required.Message.Contains("MCP-Session-Id"), "SessionRequired names the missing header.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "MarkActivityUpdatesLastActivity", "ClientConnection.MarkActivity moves LastActivity forward, and Caller defaults to null", async ct =>
                    {
                        using ClientConnection connection = new ClientConnection("activity");
                        DateTime before = connection.LastActivity;
                        await Task.Delay(20, ct).ConfigureAwait(false);
                        connection.MarkActivity();

                        TestAssert.True(connection.LastActivity > before, "LastActivity advances.");
                        TestAssert.Null(connection.Caller, "No caller is set by default.");
                        connection.Caller = new RpcCallContext("p", null);
                        TestAssert.Equal("p", connection.Caller.Principal, "Caller round-trips.");
                    }),

                    Case(suiteId, "RequestsKeepSessionsAlive", "Requests on a session mark it active, so an in-use session is not treated as idle", async ct =>
                    {
                        ClientConnection? captured = null;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.ClientConnected += (_, connection) => captured = connection;
                        }).ConfigureAwait(false);

                        string? sessionId = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);
                        DateTime afterInitialize = captured!.LastActivity;
                        await Task.Delay(30, ct).ConfigureAwait(false);
                        await fixture.PostJsonRpcAsync("/mcp/", "ping", null, 2, sessionId, ct).ConfigureAwait(false);

                        TestAssert.True(captured.LastActivity > afterInitialize, "A request marks the session active.");
                    }),

                    Case(suiteId, "ClientHandshakeNegotiatesVersionAndSendsInitialized", "McpHttpClient connect sends initialize and notifications/initialized and adopts the negotiated version", async ct =>
                    {
                        List<string> methods = new List<string>();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.MaximumHandshakeProtocolVersion = "2025-03-26";
                            server.RequestReceived += (_, args) => { lock (methods) methods.Add(args.Request.Method); };
                        }).ConfigureAwait(false);

                        using McpHttpClient client = new McpHttpClient();
                        client.ClientName = "session-suite";
                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);

                        TestAssert.True(connected, "The client connects.");
                        TestAssert.False(String.IsNullOrEmpty(client.SessionId), "The client holds the issued session.");
                        TestAssert.Equal("2025-03-26", client.ProtocolVersion, "The client adopts the negotiated version.");
                        lock (methods)
                        {
                            TestAssert.Equal("initialize", methods.FirstOrDefault(), "The first request is initialize.");
                            TestAssert.True(methods.Contains("notifications/initialized"), "The client sends notifications/initialized.");
                            TestAssert.False(methods.Contains("ping"), "The handshake does not use ping.");
                        }
                    }),

                    Case(suiteId, "LegacyClientConnectsAndReceivesEventsNotifications", "McpHttpClient.ConnectAsync opens a session on /rpc and receives notifications over /events", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TaskCompletionSource<JsonRpcRequest> received = new TaskCompletionSource<JsonRpcRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
                        client.NotificationReceived += (_, notification) =>
                        {
                            if (notification.Method == "notifications/legacy") received.TrySetResult(notification);
                        };

                        bool connected = await client.ConnectAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "The legacy client connects.");
                        TestAssert.False(String.IsNullOrEmpty(client.SessionId), "The legacy client holds a session from initialize.");
                        TestAssert.True(await client.StartSseAsync(ct).ConfigureAwait(false), "SSE starts on /events.");

                        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                        while (!client.IsSseConnected && DateTime.UtcNow < deadline) await Task.Delay(20, ct).ConfigureAwait(false);
                        TestAssert.True(fixture.Server.SendNotificationToSession(client.SessionId!, "notifications/legacy", new { value = 1 }), "The notification is queued.");

                        Task winner = await Task.WhenAny(received.Task, Task.Delay(5000, ct)).ConfigureAwait(false);
                        TestAssert.True(winner == received.Task, "The notification arrives over /events.");
                    }),
                });
        }

        private static void RegisterMarker(McpHttpServer server)
        {
            RegisterMarker(server, new MarkerProbe());
        }

        private static void RegisterMarker(McpHttpServer server, MarkerProbe probe)
        {
            server.RegisterTool("marker", "Counts its invocations", new { type = "object" }, _ =>
            {
                Interlocked.Increment(ref probe.Invocations);
                return "invoked";
            });
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "sessions" });
        }
    }
}
