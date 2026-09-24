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
    /// Positive and negative coverage for handshake version negotiation: <c>initialize</c> never agrees to
    /// a stateless-era revision, the <c>MaximumHandshakeProtocolVersion</c> cap is honored and validated on
    /// every server type, and the cap applies identically with and without an <c>AuthenticationHandler</c>.
    /// </summary>
    public static class McpHandshakeNegotiationSuites
    {
        /// <summary>
        /// Builds the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public static TestSuiteDescriptor HandshakeNegotiation()
        {
            const string suiteId = "McpHttp.Server.Negotiation";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Handshake Negotiation",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "NegotiateHandshakeVersionRules", "NegotiateHandshakeVersion follows every rule of the negotiation table", ct =>
                    {
                        string newest = McpProtocol.NewestHandshakeProtocolVersion;

                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, McpProtocol.NegotiateHandshakeVersion(null, newest), "No version requested returns the default.");
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, McpProtocol.NegotiateHandshakeVersion("", newest), "A blank version returns the default.");
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, McpProtocol.NegotiateHandshakeVersion("   ", newest), "A whitespace version returns the default.");

                        foreach (McpProtocolVersionInfo info in McpProtocol.SupportedVersions.Where(entry => entry.Era == McpProtocolEra.Handshake))
                        {
                            TestAssert.Equal(info.Version, McpProtocol.NegotiateHandshakeVersion(info.Version, newest), $"A handshake-era request for {info.Version} is echoed.");
                        }

                        TestAssert.Equal(McpProtocol.ProtocolVersion20251125, McpProtocol.NegotiateHandshakeVersion(McpProtocol.ProtocolVersion20260728, newest), "A stateless-era request is answered with the cap.");

                        string cap = McpProtocol.ProtocolVersion20250618;
                        TestAssert.Equal(cap, McpProtocol.NegotiateHandshakeVersion(McpProtocol.ProtocolVersion20251125, cap), "A request above the cap is lowered to the cap.");
                        TestAssert.Equal(cap, McpProtocol.NegotiateHandshakeVersion(McpProtocol.ProtocolVersion20250618, cap), "A request at the cap is echoed.");
                        TestAssert.Equal(McpProtocol.ProtocolVersion20250326, McpProtocol.NegotiateHandshakeVersion(McpProtocol.ProtocolVersion20250326, cap), "A request below the cap is echoed.");
                        TestAssert.Equal(cap, McpProtocol.NegotiateHandshakeVersion(null, cap), "The default is lowered to the cap when the cap is older.");
                        TestAssert.Equal(cap, McpProtocol.NegotiateHandshakeVersion(McpProtocol.ProtocolVersion20260728, cap), "A stateless-era request is answered with a lowered cap.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "NegotiateHandshakeVersionRejectsInvalidInput", "NegotiateHandshakeVersion rejects unknown requests and non-handshake caps", ct =>
                    {
                        string newest = McpProtocol.NewestHandshakeProtocolVersion;

                        TestAssert.Throws<ArgumentException>(() => McpProtocol.NegotiateHandshakeVersion("1900-01-01", newest), "An unknown requested version should fail.");
                        TestAssert.Throws<ArgumentException>(() => McpProtocol.NegotiateHandshakeVersion("not-a-version", newest), "A malformed requested version should fail.");
                        TestAssert.Throws<ArgumentException>(() => McpProtocol.NegotiateHandshakeVersion(null, McpProtocol.ProtocolVersion20260728), "A stateless-era cap should fail.");
                        TestAssert.Throws<ArgumentException>(() => McpProtocol.NegotiateHandshakeVersion(null, "1900-01-01"), "An unknown cap should fail.");
                        TestAssert.Throws<ArgumentException>(() => McpProtocol.NegotiateHandshakeVersion(null, ""), "A blank cap should fail.");
                        TestAssert.Throws<ArgumentException>(() => McpProtocol.NegotiateHandshakeVersion(null, null!), "A null cap should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "NewestHandshakeVersionDerivedFromRegistry", "NewestHandshakeProtocolVersion is the newest handshake-era registry entry", ct =>
                    {
                        McpProtocolVersionInfo expected = McpProtocol.SupportedVersions.Last(info => info.Era == McpProtocolEra.Handshake);

                        TestAssert.Equal(expected.Version, McpProtocol.NewestHandshakeProtocolVersion, "The value is derived from the registry.");
                        TestAssert.Equal(McpProtocol.ProtocolVersion20251125, McpProtocol.NewestHandshakeProtocolVersion, "Today the newest handshake-era revision is 2025-11-25.");
                        TestAssert.True(McpProtocol.NewestHandshakeProtocolVersion != McpProtocol.NewestProtocolVersion, "The newest handshake-era revision is not the stateless revision.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "IsHandshakeVersionClassifiesVersions", "IsHandshakeVersion accepts handshake-era revisions only", ct =>
                    {
                        TestAssert.True(McpProtocol.IsHandshakeVersion(McpProtocol.ProtocolVersion20241105), "2024-11-05 is handshake era.");
                        TestAssert.True(McpProtocol.IsHandshakeVersion(McpProtocol.ProtocolVersion20251125), "2025-11-25 is handshake era.");
                        TestAssert.False(McpProtocol.IsHandshakeVersion(McpProtocol.ProtocolVersion20260728), "2026-07-28 is stateless era.");
                        TestAssert.False(McpProtocol.IsHandshakeVersion(null), "Null is not a handshake version.");
                        TestAssert.False(McpProtocol.IsHandshakeVersion(""), "Blank is not a handshake version.");
                        TestAssert.False(McpProtocol.IsHandshakeVersion("1900-01-01"), "An unknown version is not a handshake version.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "NegotiateVersionRemainsEraAgnostic", "The public NegotiateVersion keeps its v1.0 behavior", ct =>
                    {
                        TestAssert.Equal(McpProtocol.ProtocolVersion20260728, McpProtocol.NegotiateVersion(McpProtocol.ProtocolVersion20260728), "NegotiateVersion still echoes any supported version.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "InitializeRequesting20260728NegotiatesHandshake", "initialize asking for 2026-07-28 gets 2025-11-25 and a usable session", async ct =>
                    {
                        await AssertStatelessRequestNegotiatesHandshakeAsync(false, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "InitializeRequesting20260728NegotiatesHandshakeWithAuth", "With an AuthenticationHandler, initialize asking for 2026-07-28 gets 2025-11-25 and a usable session", async ct =>
                    {
                        await AssertStatelessRequestNegotiatesHandshakeAsync(true, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "InitializeWithStatelessHeaderStaysHandshake", "initialize sent with a 2026-07-28 protocol header still takes the capped handshake path", async ct =>
                    {
                        foreach (bool withAuth in new[] { false, true })
                        {
                            await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(withAuth, null, ct).ConfigureAwait(false);
                            string? authorization = withAuth ? McpHttpTestRequests.ValidToken : null;

                            RpcResult response = await McpHttpTestRequests.SendAsync(
                                fixture, "initialize", 1,
                                new { protocolVersion = McpProtocol.ProtocolVersion20260728, capabilities = new { }, clientInfo = new { name = "t", version = "1" } },
                                null, McpProtocol.ProtocolVersion20260728, authorization, ct).ConfigureAwait(false);

                            TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, $"initialize should succeed (auth={withAuth}).");
                            TestAssert.Equal(McpProtocol.ProtocolVersion20251125, response.Result.Get("protocolVersion").String(), $"initialize must not agree to the stateless revision (auth={withAuth}).");
                            TestAssert.NotNull(response.SessionId, $"The handshake path issues a session id (auth={withAuth}).");
                        }
                    }),

                    Case(suiteId, "InitializeWithoutVersionUsesDefault", "initialize without a protocolVersion returns the default handshake version", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(true, null, ct).ConfigureAwait(false);
                        RpcResult response = await McpHttpTestRequests.InitializeAsync(fixture, null, McpHttpTestRequests.ValidToken, ct).ConfigureAwait(false);
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, response.Result.Get("protocolVersion").String());
                    }),

                    Case(suiteId, "StatelessDefaultProtocolVersionIsCapped", "A ProtocolVersion default set to 2026-07-28 is still capped during initialize", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, server => server.ProtocolVersion = McpProtocol.ProtocolVersion20260728, ct).ConfigureAwait(false);
                        RpcResult response = await McpHttpTestRequests.InitializeAsync(fixture, null, null, ct).ConfigureAwait(false);
                        TestAssert.Equal(McpProtocol.ProtocolVersion20251125, response.Result.Get("protocolVersion").String(), "A stateless-era default must not leak through initialize.");
                    }),

                    Case(suiteId, "MaximumHandshakeProtocolVersionHonored", "A lowered MaximumHandshakeProtocolVersion caps every initialize request", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(
                            true, server => server.MaximumHandshakeProtocolVersion = McpProtocol.ProtocolVersion20250618, ct).ConfigureAwait(false);

                        Dictionary<string, string> expected = new Dictionary<string, string>
                        {
                            { McpProtocol.ProtocolVersion20260728, McpProtocol.ProtocolVersion20250618 },
                            { McpProtocol.ProtocolVersion20251125, McpProtocol.ProtocolVersion20250618 },
                            { McpProtocol.ProtocolVersion20250618, McpProtocol.ProtocolVersion20250618 },
                            { McpProtocol.ProtocolVersion20250326, McpProtocol.ProtocolVersion20250326 },
                            { McpProtocol.ProtocolVersion20241105, McpProtocol.ProtocolVersion20241105 }
                        };

                        foreach (KeyValuePair<string, string> pair in expected)
                        {
                            RpcResult response = await McpHttpTestRequests.InitializeAsync(fixture, pair.Key, McpHttpTestRequests.ValidToken, ct).ConfigureAwait(false);
                            TestAssert.Equal(pair.Value, response.Result.Get("protocolVersion").String(), $"Requesting {pair.Key} under a 2025-06-18 cap.");
                        }

                        RpcResult defaulted = await McpHttpTestRequests.InitializeAsync(fixture, null, McpHttpTestRequests.ValidToken, ct).ConfigureAwait(false);
                        TestAssert.Equal(McpProtocol.ProtocolVersion20250618, defaulted.Result.Get("protocolVersion").String(), "The default is capped too.");
                    }),

                    Case(suiteId, "MaximumHandshakeProtocolVersionValidatedOnEveryServer", "Every server type validates MaximumHandshakeProtocolVersion", ct =>
                    {
                        int port = TestPorts.GetFreePort();
                        using McpHttpServer http = new McpHttpServer(McpHttpTestRequests.Loopback, port);
                        using McpServer stdio = new McpServer();
                        using McpTcpServer tcp = new McpTcpServer(IPAddress.Loopback, port);
                        using McpWebsocketsServer websocket = new McpWebsocketsServer(McpHttpTestRequests.Loopback, port, "/mcp");

                        List<KeyValuePair<string, Action<string?>>> setters = new List<KeyValuePair<string, Action<string?>>>
                        {
                            new KeyValuePair<string, Action<string?>>("McpHttpServer", value => http.MaximumHandshakeProtocolVersion = value!),
                            new KeyValuePair<string, Action<string?>>("McpServer", value => stdio.MaximumHandshakeProtocolVersion = value!),
                            new KeyValuePair<string, Action<string?>>("McpTcpServer", value => tcp.MaximumHandshakeProtocolVersion = value!),
                            new KeyValuePair<string, Action<string?>>("McpWebsocketsServer", value => websocket.MaximumHandshakeProtocolVersion = value!)
                        };

                        List<KeyValuePair<string, Func<string>>> getters = new List<KeyValuePair<string, Func<string>>>
                        {
                            new KeyValuePair<string, Func<string>>("McpHttpServer", () => http.MaximumHandshakeProtocolVersion),
                            new KeyValuePair<string, Func<string>>("McpServer", () => stdio.MaximumHandshakeProtocolVersion),
                            new KeyValuePair<string, Func<string>>("McpTcpServer", () => tcp.MaximumHandshakeProtocolVersion),
                            new KeyValuePair<string, Func<string>>("McpWebsocketsServer", () => websocket.MaximumHandshakeProtocolVersion)
                        };

                        for (int index = 0; index < setters.Count; index++)
                        {
                            string name = setters[index].Key;
                            Action<string?> set = setters[index].Value;
                            Func<string> get = getters[index].Value;

                            TestAssert.Equal(McpProtocol.NewestHandshakeProtocolVersion, get(), $"{name} defaults to the newest handshake revision.");
                            TestAssert.Throws<ArgumentException>(() => set(McpProtocol.ProtocolVersion20260728), $"{name} rejects the stateless revision.");
                            TestAssert.Throws<ArgumentException>(() => set("1900-01-01"), $"{name} rejects an unknown revision.");
                            TestAssert.Throws<ArgumentException>(() => set(""), $"{name} rejects a blank revision.");
                            TestAssert.Equal(McpProtocol.NewestHandshakeProtocolVersion, get(), $"{name} keeps its value after a rejected set.");

                            set(McpProtocol.ProtocolVersion20250326);
                            TestAssert.Equal(McpProtocol.ProtocolVersion20250326, get(), $"{name} accepts a handshake revision.");

                            set(null);
                            TestAssert.Equal(McpProtocol.NewestHandshakeProtocolVersion, get(), $"{name} restores the default on null.");
                        }

                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "InitializeUnknownVersionStillRejected", "initialize with an unknown version is still rejected with invalid params", async ct =>
                    {
                        foreach (bool withAuth in new[] { false, true })
                        {
                            await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(withAuth, null, ct).ConfigureAwait(false);
                            RpcResult response = await McpHttpTestRequests.InitializeAsync(fixture, "1900-01-01", withAuth ? McpHttpTestRequests.ValidToken : null, ct).ConfigureAwait(false);
                            TestAssert.Equal(-32602, response.Error.Get("code").Int(), $"An unknown version maps to invalid params (auth={withAuth}).");
                        }
                    }),

                    Case(suiteId, "InitializeWithoutCredentialsRejected", "On an authenticated server, initialize without credentials is rejected before negotiation", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(true, null, ct).ConfigureAwait(false);

                        RpcResult missing = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20260728, null, ct).ConfigureAwait(false);
                        RpcResult wrong = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20260728, "Bearer wrong", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode, "Missing credentials should be 401.");
                        TestAssert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode, "Wrong credentials should be 401.");
                        TestAssert.Null(missing.SessionId, "No session is issued without authentication.");
                    }),
                });
        }

        private static async Task AssertStatelessRequestNegotiatesHandshakeAsync(bool withAuth, CancellationToken token)
        {
            await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(
                withAuth,
                server => server.RegisterTool("probe", "Probe tool", new { type = "object" }, _ => "probed"),
                token).ConfigureAwait(false);
            string? authorization = withAuth ? McpHttpTestRequests.ValidToken : null;

            RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20260728, authorization, token).ConfigureAwait(false);
            TestAssert.Equal(HttpStatusCode.OK, initialize.StatusCode, "initialize should succeed.");
            TestAssert.Equal(McpProtocol.ProtocolVersion20251125, initialize.Result.Get("protocolVersion").String(), "initialize must answer with the newest handshake revision.");
            TestAssert.NotNull(initialize.SessionId, "initialize should issue a session id.");

            RpcResult initialized = await McpHttpTestRequests.SendAsync(
                fixture, "notifications/initialized", null, null, initialize.SessionId, McpProtocol.ProtocolVersion20251125, authorization, token).ConfigureAwait(false);
            TestAssert.Equal(HttpStatusCode.Accepted, initialized.StatusCode, "The initialized notification is accepted.");

            RpcResult tools = await McpHttpTestRequests.SendAsync(
                fixture, "tools/list", 2, new { }, initialize.SessionId, McpProtocol.ProtocolVersion20251125, authorization, token).ConfigureAwait(false);
            TestAssert.Equal(HttpStatusCode.OK, tools.StatusCode, "tools/list on the negotiated session should succeed.");
            TestAssert.True(tools.Result.Get("tools").EnumerateArray().Any(tool => tool.Get("name").String() == "probe"), "tools/list should return the registered tool.");
            TestAssert.False(tools.Result.Has("resultType"), "Handshake-era results carry no resultType.");

            RpcResult call = await McpHttpTestRequests.SendAsync(
                fixture, "tools/call", 3, new { name = "probe", arguments = new { } }, initialize.SessionId, McpProtocol.ProtocolVersion20251125, authorization, token).ConfigureAwait(false);
            TestAssert.Equal("probed", call.Result.Get("content")[0].Get("text").String(), "tools/call on the negotiated session should succeed.");
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "version", "negotiation" });
        }
    }
}
