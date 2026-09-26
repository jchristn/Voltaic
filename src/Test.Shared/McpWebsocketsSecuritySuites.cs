namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers <see cref="McpWebsocketsServer"/> origin validation, loopback enforcement, and the
    /// <see cref="McpWebsocketsServer.AuthenticationHandler"/> on the upgrade request (including the caller
    /// context that flows into handlers), plus <see cref="McpWebsocketsClient.SetRequestHeader"/>.
    /// </summary>
    public static class McpWebsocketsSecuritySuites
    {
        private const string EvilOrigin = "https://evil.example";

        /// <summary>
        /// WebSocket origin, loopback, and authentication cases.
        /// </summary>
        public static TestSuiteDescriptor WebSocketSecurity()
        {
            const string suiteId = "Security.WebSocket";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP WebSocket Origin, Loopback, and Authentication",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "DisallowedOriginUpgradeGets403", "An upgrade from a disallowed origin gets 403 and no connection is established", async ct =>
                    {
                        int connected = 0;
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server => server.ClientConnected += (_, _) => Interlocked.Increment(ref connected)).ConfigureAwait(false);

                        string? response = await RawSocketRequests.SendAsync(IPAddress.Loopback, fixture.Port, RawSocketRequests.WebSocketUpgrade("/mcp/", $"localhost:{fixture.Port}", EvilOrigin), true, ct).ConfigureAwait(false);

                        TestAssert.Equal(403, RawSocketRequests.StatusCode(response), $"Response: {response}");
                        TestAssert.Equal(0, Volatile.Read(ref connected), "No client connection is raised.");
                    }),

                    Case(suiteId, "LoopbackAndMissingOriginUpgradesAreAccepted", "Upgrades with a loopback origin or without Origin are accepted", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);

                        string? loopback = await RawSocketRequests.SendAsync(IPAddress.Loopback, fixture.Port, RawSocketRequests.WebSocketUpgrade("/mcp/", $"localhost:{fixture.Port}", "http://localhost:5173"), true, ct).ConfigureAwait(false);
                        using McpWebsocketsClient client = new McpWebsocketsClient();
                        bool connected = await client.ConnectAsync(fixture.Url, ct).ConfigureAwait(false);

                        TestAssert.Equal(101, RawSocketRequests.StatusCode(loopback), $"Response: {loopback}");
                        TestAssert.True(connected, "A client without Origin connects.");
                    }),

                    Case(suiteId, "AllowlistedOriginUpgradeIsAccepted", "An upgrade from an origin in OriginPolicy.AllowedOrigins is accepted", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server => server.OriginPolicy.AllowedOrigins.Add("https://app.example.com")).ConfigureAwait(false);

                        string? allowed = await RawSocketRequests.SendAsync(IPAddress.Loopback, fixture.Port, RawSocketRequests.WebSocketUpgrade("/mcp/", $"localhost:{fixture.Port}", "https://app.example.com"), true, ct).ConfigureAwait(false);
                        string? rejected = await RawSocketRequests.SendAsync(IPAddress.Loopback, fixture.Port, RawSocketRequests.WebSocketUpgrade("/mcp/", $"localhost:{fixture.Port}", "https://app.example.com:444"), true, ct).ConfigureAwait(false);

                        TestAssert.Equal(101, RawSocketRequests.StatusCode(allowed), $"Response: {allowed}");
                        TestAssert.Equal(403, RawSocketRequests.StatusCode(rejected), $"Response: {rejected}");
                    }),

                    Case(suiteId, "AuthenticationRejectsMissingOrWrongCredentials", "With an AuthenticationHandler, upgrades without or with wrong credentials get 401 with the result headers", async ct =>
                    {
                        int connected = 0;
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = RequireBearerGood;
                            server.ClientConnected += (_, _) => Interlocked.Increment(ref connected);
                        }).ConfigureAwait(false);

                        using McpWebsocketsClient anonymous = new McpWebsocketsClient();
                        bool anonymousConnected = await anonymous.ConnectAsync(fixture.Url, ct).ConfigureAwait(false);
                        string? wrong = await RawSocketRequests.SendAsync(IPAddress.Loopback, fixture.Port, RawSocketRequests.WebSocketUpgrade("/mcp/", $"localhost:{fixture.Port}", null, "Bearer wrong"), true, ct).ConfigureAwait(false);

                        TestAssert.False(anonymousConnected, "A client without credentials cannot connect.");
                        TestAssert.Equal(401, RawSocketRequests.StatusCode(wrong), $"Response: {wrong}");
                        TestAssert.True(RawSocketRequests.HasHeader(wrong, "WWW-Authenticate"), "The 401 carries WWW-Authenticate.");
                        TestAssert.Equal(0, Volatile.Read(ref connected), "No client connection is raised.");
                    }),

                    Case(suiteId, "ValidCredentialsConnectAndCallerFlowsIntoHandlers", "A client with SetRequestHeader credentials connects; the caller reaches RpcCallContext.Current, the explicit-context overload, and ClientConnection.Caller", async ct =>
                    {
                        TaskCompletionSource<ClientConnection> connectedClient = new TaskCompletionSource<ClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = RequireBearerGood;
                            server.ClientConnected += (_, client) => connectedClient.TrySetResult(client);
                            server.RegisterTool("who", "Returns the ambient caller", new { type = "object" }, _ =>
                                $"{RpcCallContext.Current?.Principal}|{(RpcCallContext.Current != null && RpcCallContext.Current.Claims.TryGetValue("tenant", out string? tenant) ? tenant : "")}");
                            server.RegisterTool("who-explicit", "Returns the explicit caller", new { type = "object" },
                                (RpcParameters? args, RpcCallContext? caller, CancellationToken token) => Task.FromResult<object>(caller?.Principal ?? "<null>"));
                        }).ConfigureAwait(false);

                        using McpWebsocketsClient client = new McpWebsocketsClient();
                        client.SetRequestHeader("Authorization", "Bearer good");
                        bool connected = await client.ConnectAsync(fixture.Url, ct).ConfigureAwait(false);
                        TestAssert.True(connected, "A client with valid credentials connects.");

                        string ambient = ToolText(await client.CallAsync<JsonElement>("tools/call", new { name = "who", arguments = new { } }, token: ct).ConfigureAwait(false));
                        string explicitCaller = ToolText(await client.CallAsync<JsonElement>("tools/call", new { name = "who-explicit", arguments = new { } }, token: ct).ConfigureAwait(false));
                        ClientConnection serverSide = await connectedClient.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

                        TestAssert.Equal("good-user|acme", ambient, "RpcCallContext.Current carries the principal and claims.");
                        TestAssert.Equal("good-user", explicitCaller, "The explicit-context overload receives the caller.");
                        TestAssert.Equal("good-user", serverSide.Caller?.Principal, "ClientConnection.Caller exposes who connected.");
                    }),

                    Case(suiteId, "NoHandlerMeansNullCaller", "Without an AuthenticationHandler, the caller context on WebSocket is null", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server =>
                        {
                            server.RegisterTool("who", "Returns the ambient caller", new { type = "object" }, _ => RpcCallContext.Current == null ? "<null>" : "set");
                        }).ConfigureAwait(false);

                        using McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        string text = ToolText(await client.CallAsync<JsonElement>("tools/call", new { name = "who", arguments = new { } }, token: ct).ConfigureAwait(false));

                        TestAssert.Equal("<null>", text);
                    }),

                    Case(suiteId, "ValidCredentialsFromDisallowedOriginGet403", "Valid credentials do not bypass the origin check", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server => server.AuthenticationHandler = RequireBearerGood).ConfigureAwait(false);

                        string? response = await RawSocketRequests.SendAsync(IPAddress.Loopback, fixture.Port, RawSocketRequests.WebSocketUpgrade("/mcp/", $"localhost:{fixture.Port}", EvilOrigin, "Bearer good"), true, ct).ConfigureAwait(false);

                        TestAssert.Equal(403, RawSocketRequests.StatusCode(response), $"Response: {response}");
                    }),

                    Case(suiteId, "SetRequestHeaderRemovalAppliesToNextConnect", "Removing a header with a null value takes effect on the next connect, and a null name throws", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server => server.AuthenticationHandler = RequireBearerGood).ConfigureAwait(false);

                        using McpWebsocketsClient client = new McpWebsocketsClient();
                        client.SetRequestHeader("authorization", "Bearer good");
                        bool first = await client.ConnectAsync(fixture.Url, ct).ConfigureAwait(false);
                        client.Disconnect();
                        client.SetRequestHeader("Authorization", null);
                        bool second = await client.ConnectAsync(fixture.Url, ct).ConfigureAwait(false);

                        TestAssert.True(first, "The header authenticates the first connection.");
                        TestAssert.False(second, "After removal (case-insensitive name) the header is no longer sent.");
                        TestAssert.Throws<ArgumentNullException>(() => client.SetRequestHeader("", "x"), "An empty name throws.");
                    }),

                    Case(suiteId, "RemoteClientWithSpoofedHostIsRejected", "A WebSocket upgrade from a non-loopback address with Host: localhost gets 403 (or cannot connect)", async ct =>
                    {
                        int connected = 0;
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server => server.ClientConnected += (_, _) => Interlocked.Increment(ref connected)).ConfigureAwait(false);
                        IPAddress? lan = RawSocketRequests.FindNonLoopbackIPv4();
                        if (lan == null) return;

                        string? response = await RawSocketRequests.SendAsync(lan, fixture.Port, RawSocketRequests.WebSocketUpgrade("/mcp/", $"localhost:{fixture.Port}", null), true, ct).ConfigureAwait(false);
                        int status = RawSocketRequests.StatusCode(response);

                        TestAssert.True(status == 0 || status == 403, $"A remote client is refused (status {status}).");
                        TestAssert.Equal(0, Volatile.Read(ref connected), "No client connection is raised.");
                    }),
                });
        }

        private static Task<AuthenticationResult> RequireBearerGood(HttpListenerRequest request)
        {
            if (request.Headers["Authorization"] == "Bearer good")
            {
                return Task.FromResult(new AuthenticationResult
                {
                    IsAuthenticated = true,
                    Principal = "good-user",
                    Claims = new Dictionary<string, string> { { "tenant", "acme" } }
                });
            }

            return Task.FromResult(AuthenticationResult.BearerChallenge(null, "invalid_token", null, "denied"));
        }

        private static string ToolText(JsonElement result)
        {
            return result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "security", "websocket" });
        }
    }
}
