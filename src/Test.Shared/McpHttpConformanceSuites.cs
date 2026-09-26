namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers Streamable HTTP conformance: notifications on the POST response stream (switched to SSE only when
    /// needed), stateless logLevel and _meta rules, disconnect-as-cancellation, bearer challenges, insufficient scope,
    /// session ownership, exact endpoint paths, and the client side (404 recovery, DELETE on disconnect, cancellation,
    /// version negotiation after -32022, broken-stream re-issue, and resultType validation).
    /// </summary>
    public static class McpHttpConformanceSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Streamable HTTP server cases.
        /// </summary>
        public static TestSuiteDescriptor Server()
        {
            const string suiteId = "McpHttp.Conformance";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP Streamable HTTP server conformance",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ProgressSwitchesTheResponseToSse", "A handshake-era tools/call with a progress token gets an SSE response carrying progress then the result; without one the response is plain JSON", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        string? session = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);
                        RpcResult streamed = await fixture.PostJsonRpcAsync("/mcp/", "tools/call", new { name = "progress", arguments = new { }, _meta = new { progressToken = "tok" } }, 1, session, ct).ConfigureAwait(false);
                        List<JsonProbe> events = SseMessages(streamed.Body);

                        TestAssert.Equal("text/event-stream", streamed.ContentType, "The response became an event stream.");
                        TestAssert.Equal("notifications/progress", events[0].Get("method").String(), "Progress comes first.");
                        TestAssert.Equal("tok", events[0].Get("params").Get("progressToken").String(), "It carries the request's token.");
                        TestAssert.Equal(1, events.Last().Get("id").Int(), "The response ends the stream.");

                        RpcResult plain = await fixture.PostJsonRpcAsync("/mcp/", "tools/call", new { name = "progress", arguments = new { } }, 2, session, ct).ConfigureAwait(false);
                        TestAssert.Equal("application/json", plain.ContentType, "Without a token nothing is streamed.");
                        TestAssert.Equal(2, plain.Root.Get("id").Int(), "The result is returned as JSON.");
                    }),

                    Case(suiteId, "StatelessLogLevelControlsLogNotifications", "A stateless request receives notifications/message only when _meta carries a logLevel the entry meets; an unknown logLevel is invalid params", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        Dictionary<string, object?> meta = McpHttpTestRequests.StatelessMeta();
                        meta[McpProtocol.MetaLogLevelKey] = "warning";
                        RpcResult logged = await SendStatelessAsync(fixture, "tools/call", new Dictionary<string, object?> { { "name", "logs" }, { "arguments", new { } }, { "_meta", meta } }, "logs", ct).ConfigureAwait(false);
                        List<JsonProbe> events = SseMessages(logged.Body);
                        TestAssert.Equal(2, events.Count, $"One log entry and the result: {logged.Body}");
                        TestAssert.Equal("error", events[0].Get("params").Get("level").String(), "Only the entry at or above warning is sent.");
                        TestAssert.Equal("complete", events[1].Get("result").Get("resultType").String(), "The result follows.");

                        RpcResult silent = await SendStatelessAsync(fixture, "tools/call", new Dictionary<string, object?> { { "name", "logs" }, { "arguments", new { } }, { "_meta", McpHttpTestRequests.StatelessMeta() } }, "logs", ct).ConfigureAwait(false);
                        TestAssert.Equal("application/json", silent.ContentType, "Without logLevel no logs are sent.");

                        meta[McpProtocol.MetaLogLevelKey] = "chatty";
                        RpcResult invalid = await SendStatelessAsync(fixture, "tools/call", new Dictionary<string, object?> { { "name", "logs" }, { "arguments", new { } }, { "_meta", meta } }, "logs", ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode, "An unknown logLevel is rejected.");
                        TestAssert.Equal(-32602, invalid.Error.Get("code").Int(), "It is invalid params.");
                    }),

                    Case(suiteId, "StatelessMetaIsValidatedAndServerInfoIsReturned", "A stateless request without clientCapabilities is invalid params; results carry serverInfo in _meta", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        Dictionary<string, object?> meta = McpHttpTestRequests.StatelessMeta();
                        meta.Remove(McpProtocol.MetaClientCapabilitiesKey);
                        RpcResult missing = await SendStatelessAsync(fixture, "tools/list", new Dictionary<string, object?> { { "_meta", meta } }, null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, missing.StatusCode, "Missing clientCapabilities is rejected.");
                        TestAssert.Equal(-32602, missing.Error.Get("code").Int(), "It is invalid params.");

                        RpcResult listed = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/list", 1, null, null, null, ct).ConfigureAwait(false);
                        JsonProbe serverInfo = listed.Result.Get("_meta").Get(McpProtocol.MetaServerInfoKey);
                        TestAssert.Equal("Voltaic.Test", serverInfo.Get("name").String(), "serverInfo is in the result _meta.");
                    }),

                    Case(suiteId, "ClosingAStatelessStreamCancelsTheRequest", "When a stateless client disconnects, the next keep-alive fails and the handler's token is cancelled", async ct =>
                    {
                        SlowToolProbe probe = new SlowToolProbe();
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct, server => server.ResponseKeepAliveMs = 100, probe).ConfigureAwait(false);
                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "name", "slow" }, { "arguments", new { ms = 15000 } }, { "_meta", McpHttpTestRequests.StatelessMeta() } };
                        using HttpRequestMessage request = StatelessRequest(fixture, "tools/call", parameters, "slow");
                        using CancellationTokenSource abandon = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        abandon.CancelAfter(500);
                        try
                        {
                            using HttpResponseMessage response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, abandon.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }

                        TestAssert.True(await WaitUntilAsync(() => probe.Cancelled, ct).ConfigureAwait(false), "The handler was cancelled after the client went away.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => fixture.Server.ResponseKeepAliveMs = -1, "The keep-alive interval cannot be negative.");
                    }),

                    Case(suiteId, "UnauthorizedResponsesAlwaysChallenge", "A 401 from the authentication handler without a challenge gets WWW-Authenticate: Bearer, with resource_metadata when metadata is configured", async ct =>
                    {
                        await using HttpMcpTestServerFixture plain = await McpHttpTestRequests.StartAsync(true, null, ct).ConfigureAwait(false);
                        RpcResult bare = await McpHttpTestRequests.SendAsync(plain, "tools/list", 1, new { }, null, null, null, ct).ConfigureAwait(false);
                        using HttpResponseMessage bareRaw = await SendRawPostAsync(plain, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode, "The request is unauthorized.");
                        TestAssert.True(bareRaw.Headers.WwwAuthenticate.Any(h => h.Scheme == "Bearer"), "A Bearer challenge is added.");

                        await using HttpMcpTestServerFixture described = await McpHttpTestRequests.StartAsync(true, server => server.ProtectedResourceMetadata = new McpProtectedResourceMetadata
                        {
                            Resource = "http://localhost/mcp",
                            AuthorizationServers = new List<string> { "https://auth.example.com" }
                        }, ct).ConfigureAwait(false);
                        using HttpResponseMessage withMetadata = await SendRawPostAsync(described, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", null, ct).ConfigureAwait(false);
                        string challenge = withMetadata.Headers.WwwAuthenticate.ToString();
                        TestAssert.True(challenge.Contains("resource_metadata=\"") && challenge.Contains(McpProtocol.ProtectedResourceMetadataPath), $"The challenge points at the metadata: {challenge}");
                    }),

                    Case(suiteId, "InsufficientScopeIsForbiddenWithAChallenge", "AuthenticationResult.InsufficientScope and McpInsufficientScopeException both produce 403 with error=\"insufficient_scope\" and the scope", async ct =>
                    {
                        await using HttpMcpTestServerFixture gate = await McpHttpTestRequests.StartAsync(false, server => server.AuthenticationHandler = _ => Task.FromResult(AuthenticationResult.InsufficientScope("mcp:admin")), ct).ConfigureAwait(false);
                        using HttpResponseMessage denied = await SendRawPostAsync(gate, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.Forbidden, denied.StatusCode, "The handler's 403 is returned.");
                        string gateChallenge = denied.Headers.WwwAuthenticate.ToString();
                        TestAssert.True(gateChallenge.Contains("error=\"insufficient_scope\"") && gateChallenge.Contains("scope=\"mcp:admin\""), $"The challenge names the scope: {gateChallenge}");

                        await using HttpMcpTestServerFixture tool = await StartAsync(ct).ConfigureAwait(false);
                        string? session = await tool.InitializeSessionAsync(ct).ConfigureAwait(false);
                        using HttpResponseMessage forbidden = await SendRawPostAsync(tool, McpHttpTestRequests.BuildBody("tools/call", 5, new { name = "admin", arguments = new { } }), session, ct).ConfigureAwait(false);
                        string toolChallenge = forbidden.Headers.WwwAuthenticate.ToString();
                        JsonProbe body = JsonProbe.Parse(await forbidden.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                        TestAssert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode, "A scope failure inside a tool is 403.");
                        TestAssert.True(toolChallenge.Contains("scope=\"files:write\""), $"The challenge names the tool's scope: {toolChallenge}");
                        TestAssert.Equal(McpInsufficientScopeException.ErrorCode, body.Get("error").Get("code").Int(), "The body is a JSON-RPC error.");
                        TestAssert.Equal(5, body.Get("id").Int(), "The error answers the request.");
                    }),

                    Case(suiteId, "SessionsCanOnlyBeDeletedByTheirOwner", "DELETE from a different principal gets 404 and leaves the session; its owner can delete it", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, server => server.AuthenticationHandler = request =>
                        {
                            string? header = request.Headers["Authorization"];
                            string? principal = header == "Bearer alice" ? "alice" : header == "Bearer bob" ? "bob" : null;
                            return Task.FromResult(new AuthenticationResult { IsAuthenticated = principal != null, Principal = principal, StatusCode = principal != null ? 200 : 401 });
                        }, ct).ConfigureAwait(false);
                        RpcResult created = await McpHttpTestRequests.InitializeAsync(fixture, null, "Bearer alice", ct).ConfigureAwait(false);
                        string session = created.SessionId!;

                        RpcResult stolen = await McpHttpTestRequests.DeleteSessionAsync(fixture, session, "Bearer bob", ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, stolen.StatusCode, "Another principal cannot delete the session.");
                        TestAssert.Equal(-32001, stolen.Error.Get("code").Int(), "The 404 body is a JSON-RPC error.");
                        TestAssert.True(fixture.Server.GetActiveSessions().Contains(session), "The session survives.");

                        RpcResult deleted = await McpHttpTestRequests.DeleteSessionAsync(fixture, session, "Bearer alice", ct).ConfigureAwait(false);
                        TestAssert.True((int)deleted.StatusCode < 300, $"The owner deletes it: {deleted.StatusCode}");
                        TestAssert.False(fixture.Server.GetActiveSessions().Contains(session), "The session is gone.");
                    }),

                    Case(suiteId, "EndpointPathsMatchExactly", "Only the configured path is the MCP endpoint (a longer path is 404), and a session in the query string is not accepted on /mcp", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        string? session = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);
                        RpcResult longer = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("ping", 1, null), new Dictionary<string, string>(), true, ct, "/mcpx").ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, longer.StatusCode, "/mcpx is not the MCP endpoint.");

                        RpcResult query = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/list", 1, new { }), new Dictionary<string, string>(), true, ct, "/mcp?session=" + session).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, query.StatusCode, "A query-string session is ignored, so the request is sessionless.");
                    }),

                    Case(suiteId, "BatchesFollowTheSessionVersion", "A 2025-03-26 session may POST a batch and gets an array; an empty batch is invalid; a 2025-11-25 session may not batch", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        string old = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-03-26", null, ct).ConfigureAwait(false)).SessionId!;
                        Dictionary<string, string> oldHeaders = new Dictionary<string, string> { { McpProtocol.SessionIdHeader, old }, { McpProtocol.ProtocolVersionHeader, "2025-03-26" } };
                        RpcResult batch = await McpHttpTestRequests.SendAsync(fixture, "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}]", oldHeaders, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(2, batch.Root.Length, $"Both requests are answered: {batch.Body}");
                        RpcResult empty = await McpHttpTestRequests.SendAsync(fixture, "[]", oldHeaders, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(-32600, empty.Error.Get("code").Int(), "An empty batch is invalid.");

                        string current = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        Dictionary<string, string> currentHeaders = new Dictionary<string, string> { { McpProtocol.SessionIdHeader, current }, { McpProtocol.ProtocolVersionHeader, "2025-11-25" } };
                        RpcResult rejected = await McpHttpTestRequests.SendAsync(fixture, "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]", currentHeaders, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode, "2025-11-25 has no batching.");
                    }),

                    Case(suiteId, "UnknownSessionErrorsAreJson", "GET and DELETE with an unknown session ID get 404 with a JSON-RPC error body", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage get = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/mcp/");
                        get.Headers.Accept.ParseAdd("text/event-stream");
                        get.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, "nope");
                        using HttpResponseMessage getResponse = await fixture.SendRawAsync(get, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode, "GET with an unknown session is 404.");
                        TestAssert.Equal(-32001, JsonProbe.Parse(await getResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Get("error").Get("code").Int(), "The body is a JSON-RPC error.");

                        RpcResult delete = await McpHttpTestRequests.DeleteSessionAsync(fixture, "nope", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, delete.StatusCode, "DELETE with an unknown session is 404.");
                        TestAssert.Equal(-32001, delete.Error.Get("code").Int(), "The body is a JSON-RPC error.");
                    }),
                });
        }

        /// <summary>
        /// McpHttpClient conformance cases.
        /// </summary>
        public static TestSuiteDescriptor Client()
        {
            const string suiteId = "McpHttp.ClientConformance";
            return new TestSuiteDescriptor(
                suiteId,
                "McpHttpClient session recovery, cancellation, and negotiation",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ClientReinitializesAfterSessionLoss", "When the server answers 404 for the session, the client initializes a new session and retries the request once", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        string first = client.SessionId!;
                        fixture.Server.RemoveSession(first);

                        JsonProbe tools = JsonProbe.From(await client.CallAsync<object?>("tools/list", new { }, 10000, ct).ConfigureAwait(false));
                        TestAssert.True(tools.Has("tools"), "The request succeeds after recovery.");
                        TestAssert.True(client.SessionId != null && client.SessionId != first, "A new session was created.");
                    }),

                    Case(suiteId, "ClientDeletesItsSessionOnDisconnect", "Disconnect sends DELETE so the server ends the session", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        string session = client.SessionId!;
                        TestAssert.True(fixture.Server.GetActiveSessions().Contains(session), "The session exists.");
                        client.Disconnect();
                        TestAssert.True(await WaitUntilAsync(() => !fixture.Server.GetActiveSessions().Contains(session), ct).ConfigureAwait(false), "The server ended the session.");
                    }),

                    Case(suiteId, "ClientCancelsTimedOutRequests", "A timed-out McpHttpClient call sends notifications/cancelled, and the server cancels the handler", async ct =>
                    {
                        SlowToolProbe probe = new SlowToolProbe();
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct, null, probe).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        Exception? timedOut = await CaptureAsync(() => client.CallAsync<object?>("tools/call", new { name = "slow", arguments = new { ms = 15000 } }, 400, ct)).ConfigureAwait(false);
                        TestAssert.True(timedOut != null, "The call times out.");
                        TestAssert.True(await WaitUntilAsync(() => probe.Cancelled, ct).ConfigureAwait(false), "The server cancelled the handler.");
                    }),

                    Case(suiteId, "ClientSendsItsCapabilities", "ClientCapabilities entries are sent in initialize", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer();
                        using McpHttpClient client = new McpHttpClient();
                        client.ClientCapabilities["experimental"] = new { probe = true };
                        TestAssert.True(await client.ConnectStreamableAsync(server.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        TestAssert.True(server.InitializeBodies.TryPeek(out string? body), "initialize was sent.");
                        JsonProbe capabilities = JsonProbe.Parse(body!).Get("params").Get("capabilities");
                        TestAssert.True(capabilities.Get("experimental").Get("probe").Bool(), $"The capability is declared: {body}");
                    }),

                    Case(suiteId, "StatelessClientRetriesAfterUnsupportedVersion", "When the server rejects the requested version with -32022, the stateless client retries with a version the server lists", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", "2099-01-01", true, ct).ConfigureAwait(false), "The client connects after retrying.");
                        TestAssert.Equal(McpProtocol.ProtocolVersion20260728, client.ProtocolVersion, "The server's stateless version is chosen.");

                        using McpHttpClient strict = new McpHttpClient();
                        TestAssert.False(await strict.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", "2099-01-01", false, ct).ConfigureAwait(false), "Without negotiation the connection fails.");
                    }),

                    Case(suiteId, "StatelessClientReissuesAfterABrokenStream", "When a stateless SSE response ends without a response, the client re-issues the request once with a new ID", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer { IssueSession = false };
                        int calls = 0;
                        server.RequestHandler = async (method, idJson, body, response, token) =>
                        {
                            if (method == "tools/call" && Interlocked.Increment(ref calls) == 1)
                            {
                                FakeStreamableHttpServer.BeginSse(response);
                                await FakeStreamableHttpServer.WriteSseAsync(response, ": dropped", token).ConfigureAwait(false);
                                response.Close();
                                return;
                            }

                            string result = method == "server/discover"
                                ? "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"},\"resultType\":\"complete\"}"
                                : "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"resultType\":\"complete\"}";
                            await FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + result + "}", token).ConfigureAwait(false);
                        };
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStatelessAsync(server.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects.");
                        JsonRpcResponse response = await client.SendStatelessAsync("tools/call", new Dictionary<string, object?> { { "name", "t" }, { "arguments", new { } } }, null, ct).ConfigureAwait(false);

                        List<string> ids = server.RequestBodies.Select(b => JsonProbe.Parse(b)).Where(b => b.Get("method").String() == "tools/call").Select(b => b.Get("id").String()!).ToList();
                        TestAssert.True(response.Error == null, "The re-issued request succeeds.");
                        TestAssert.Equal(2, ids.Count, "The request was sent twice.");
                        TestAssert.True(ids[0] != ids[1], "The re-issued request has a new ID.");
                    }),

                    Case(suiteId, "StatelessClientRejectsUnknownResultTypes", "A stateless result with an unrecognized resultType becomes an internal error", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer { IssueSession = false };
                        server.RequestHandler = (method, idJson, body, response, token) =>
                        {
                            string result = method == "server/discover"
                                ? "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"},\"resultType\":\"complete\"}"
                                : "{\"tools\":[],\"resultType\":\"mystery\"}";
                            return FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + result + "}", token);
                        };
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStatelessAsync(server.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects.");
                        JsonRpcResponse response = await client.SendStatelessAsync("tools/list", null, null, ct).ConfigureAwait(false);
                        TestAssert.Equal(-32603, response.Error?.Code, "The unknown resultType is rejected.");
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "conformance" });
        }

        private static Task<HttpMcpTestServerFixture> StartAsync(CancellationToken token, Action<McpHttpServer>? configure = null, SlowToolProbe? probe = null)
        {
            SlowToolProbe slow = probe ?? new SlowToolProbe();
            return HttpMcpTestServerFixture.StartAsync(token, server =>
            {
                server.RegisterTool("progress", "Reports progress", new { type = "object" }, async (RpcParameters? args, CancellationToken ct) =>
                {
                    await McpToolCallContext.Current!.ReportProgressAsync(1, 2, "half", ct).ConfigureAwait(false);
                    return "ok";
                });
                server.RegisterTool("logs", "Logs twice", new { type = "object" }, async (RpcParameters? args, CancellationToken ct) =>
                {
                    await McpToolCallContext.Current!.LogAsync("info", "routine", null, ct).ConfigureAwait(false);
                    await McpToolCallContext.Current!.LogAsync("error", "broken", null, ct).ConfigureAwait(false);
                    return "ok";
                });
                server.RegisterTool("slow", "Waits", new { type = "object", properties = new { ms = new { type = "integer" } } }, async (RpcParameters? args, CancellationToken ct) =>
                {
                    try
                    {
                        await Task.Delay((int)(args?.GetInt64("ms") ?? 1000), ct).ConfigureAwait(false);
                        return "finished";
                    }
                    catch (OperationCanceledException)
                    {
                        slow.Cancelled = true;
                        throw;
                    }
                });
                server.RegisterTool("admin", "Needs a scope", new { type = "object" }, args => throw new McpInsufficientScopeException("files:write"));
                configure?.Invoke(server);
            });
        }

        private static HttpRequestMessage StatelessRequest(HttpMcpTestServerFixture fixture, string method, Dictionary<string, object?> parameters, string? name)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/")
            {
                Content = new StringContent(McpHttpTestRequests.BuildBody(method, 1, parameters), Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728);
            request.Headers.TryAddWithoutValidation(McpProtocol.MethodHeader, method);
            if (name != null) request.Headers.TryAddWithoutValidation(McpProtocol.NameHeader, name);
            return request;
        }

        private static Task<RpcResult> SendStatelessAsync(HttpMcpTestServerFixture fixture, string method, Dictionary<string, object?> parameters, string? name, CancellationToken token)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                { McpProtocol.MethodHeader, method }
            };
            if (name != null) headers[McpProtocol.NameHeader] = name;
            return McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody(method, 1, parameters), headers, true, token);
        }

        private static async Task<HttpResponseMessage> SendRawPostAsync(HttpMcpTestServerFixture fixture, string body, string? session, CancellationToken token)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (session != null) request.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
            return await fixture.SendRawAsync(request, token).ConfigureAwait(false);
        }

        private static List<JsonProbe> SseMessages(string body)
        {
            return body.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line.Substring(5).Trim())
                .Where(data => data.Length > 0)
                .Select(data => JsonProbe.Parse(data))
                .ToList();
        }

        private static async Task<Exception?> CaptureAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow + _Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            return condition();
        }
    }
}
