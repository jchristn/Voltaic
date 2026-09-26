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
    using Voltaic.A2A;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers browser-origin and network-exposure protections on the HttpListener-based servers:
    /// <see cref="OriginPolicy"/>, <see cref="LoopbackAddresses"/>, CORS headers that echo an allowed origin,
    /// loopback-only enforcement for servers bound to <c>localhost</c>, the JSON content-type requirement on
    /// the MCP endpoint, and authentication-failure headers (<see cref="AuthenticationResult.Headers"/> and
    /// <see cref="AuthenticationResult.BearerChallenge"/>) on <see cref="McpHttpServer"/> and
    /// <see cref="A2AHttpServer"/>.
    /// </summary>
    public static class HttpAccessSecuritySuites
    {
        private const string EvilOrigin = "https://evil.example";
        private const string LoopbackOrigin = "http://localhost:6274";

        /// <summary>
        /// Unit coverage for the origin, loopback, and authentication-result helpers.
        /// </summary>
        public static TestSuiteDescriptor Policies()
        {
            const string suiteId = "Security.Policies";

            return new TestSuiteDescriptor(
                suiteId,
                "Origin, Loopback, and Authentication Result Policies",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "DefaultOriginPolicyAllowsMissingAndLoopbackOrigins", "The default OriginPolicy allows requests without Origin and loopback origins on any port", ct =>
                    {
                        OriginPolicy policy = new OriginPolicy();
                        TestAssert.True(policy.IsAllowed(null), "No Origin header is a non-browser client.");
                        foreach (string origin in new[] { "http://localhost", "http://localhost:6274", "https://LOCALHOST:8443", "http://127.0.0.1:3000", "http://127.9.9.9", "http://[::1]:8080" })
                        {
                            TestAssert.True(policy.IsAllowed(origin), $"Loopback origin '{origin}' is allowed.");
                        }

                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "DefaultOriginPolicyRejectsForeignAndLookalikeOrigins", "The default OriginPolicy rejects foreign, lookalike, opaque, and malformed origins", ct =>
                    {
                        OriginPolicy policy = new OriginPolicy();
                        foreach (string origin in new[] { EvilOrigin, "http://localhost.evil.example", "http://127.0.0.1.evil.example", "null", "file://", "chrome-extension://abcdef", "", "   ", "http://user@localhost", "http://localhost/path", "ftp://localhost" })
                        {
                            TestAssert.False(policy.IsAllowed(origin), $"Origin '{origin}' is rejected.");
                        }

                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "AllowedOriginsMatchSchemeHostAndPort", "AllowedOrigins match scheme, host, and port, treating default ports as equal", ct =>
                    {
                        OriginPolicy policy = new OriginPolicy { AllowedOrigins = new List<string> { "https://app.example.com", "http://intranet.local:8080/" } };

                        TestAssert.True(policy.IsAllowed("https://app.example.com"), "An exact match is allowed.");
                        TestAssert.True(policy.IsAllowed("https://APP.example.com:443"), "Case and the default port are normalized.");
                        TestAssert.True(policy.IsAllowed("http://intranet.local:8080"), "A trailing slash in the entry is ignored.");
                        TestAssert.False(policy.IsAllowed("http://app.example.com"), "A different scheme is rejected.");
                        TestAssert.False(policy.IsAllowed("https://app.example.com:8443"), "A different port is rejected.");
                        TestAssert.False(policy.IsAllowed("https://other.example.com"), "An unlisted host is rejected.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "OriginPolicyOptionsChangeTheRules", "Wildcard, AllowLoopbackOrigins false, OriginValidator, and null AllowedOrigins behave as documented", ct =>
                    {
                        OriginPolicy wildcard = new OriginPolicy { AllowedOrigins = new List<string> { "*" } };
                        OriginPolicy noLoopback = new OriginPolicy { AllowLoopbackOrigins = false };
                        OriginPolicy custom = new OriginPolicy { OriginValidator = origin => origin == "https://only.example" };
                        OriginPolicy throwing = new OriginPolicy { OriginValidator = _ => throw new InvalidOperationException("boom") };
                        OriginPolicy nullList = new OriginPolicy { AllowedOrigins = null! };

                        TestAssert.True(wildcard.IsAllowed(EvilOrigin), "* allows every origin.");
                        TestAssert.False(noLoopback.IsAllowed(LoopbackOrigin), "Loopback origins can be disallowed.");
                        TestAssert.True(noLoopback.IsAllowed(null), "Requests without Origin stay allowed.");
                        TestAssert.True(custom.IsAllowed("https://only.example"), "The validator can allow an origin.");
                        TestAssert.False(custom.IsAllowed(LoopbackOrigin), "The validator replaces the built-in rules.");
                        TestAssert.False(custom.IsAllowed(null), "The validator also decides for a missing Origin.");
                        TestAssert.False(throwing.IsAllowed(LoopbackOrigin), "A throwing validator rejects.");
                        TestAssert.NotNull(nullList.AllowedOrigins, "Null AllowedOrigins stores an empty list.");
                        TestAssert.True(OriginPolicy.IsLoopbackOrigin("http://127.0.0.1:1"), "IsLoopbackOrigin recognizes 127.0.0.1.");
                        TestAssert.False(OriginPolicy.IsLoopbackOrigin(null), "IsLoopbackOrigin rejects null.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "LoopbackAddressesRecognizeLoopbackOnly", "LoopbackAddresses maps IPv4-mapped IPv6 and recognizes loopback host names only", ct =>
                    {
                        TestAssert.True(LoopbackAddresses.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")), "IPv4-mapped 127.0.0.1 is loopback.");
                        TestAssert.False(LoopbackAddresses.IsLoopback(IPAddress.Parse("::ffff:192.168.1.10")), "IPv4-mapped 192.168.x.x is not loopback.");
                        TestAssert.True(LoopbackAddresses.IsLoopback(IPAddress.IPv6Loopback), "::1 is loopback.");
                        TestAssert.True(LoopbackAddresses.IsLoopback(IPAddress.Parse("127.4.5.6")), "127.0.0.0/8 is loopback.");
                        TestAssert.False(LoopbackAddresses.IsLoopback(IPAddress.Parse("10.0.0.1")), "A private address is not loopback.");
                        TestAssert.False(LoopbackAddresses.IsLoopback(null), "Null is not loopback.");

                        foreach (string name in new[] { "localhost", "LocalHost", "127.0.0.1", "127.1.2.3", "::1", "[::1]" })
                        {
                            TestAssert.True(LoopbackAddresses.IsLoopbackHostname(name), $"'{name}' is a loopback host name.");
                        }

                        foreach (string? name in new[] { null, "", "*", "+", "0.0.0.0", "example.com", "192.168.1.1", "localhost.example" })
                        {
                            TestAssert.False(LoopbackAddresses.IsLoopbackHostname(name), $"'{name}' is not a loopback host name.");
                        }

                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "AuthenticationResultHeadersAreNeverNull", "AuthenticationResult.Headers defaults to an empty case-insensitive dictionary and never returns null", ct =>
                    {
                        AuthenticationResult result = new AuthenticationResult();
                        TestAssert.Equal(0, result.Headers.Count, "Headers start empty.");
                        result.Headers["retry-after"] = "5";
                        TestAssert.True(result.Headers.ContainsKey("Retry-After"), "Header names are case-insensitive.");
                        result.Headers = null!;
                        TestAssert.NotNull(result.Headers, "Setting null stores an empty dictionary.");
                        result.Headers = new Dictionary<string, string> { { "X-Test", "1" } };
                        TestAssert.True(result.Headers.ContainsKey("x-test"), "An assigned dictionary becomes case-insensitive.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "BearerChallengeFormatsRfc6750", "AuthenticationResult.BearerChallenge builds a 401 with a correctly quoted WWW-Authenticate challenge", ct =>
                    {
                        AuthenticationResult bare = AuthenticationResult.BearerChallenge();
                        AuthenticationResult full = AuthenticationResult.BearerChallenge("https://api.example/.well-known/oauth-protected-resource", "invalid_token", "bad \"token\" \\ here", "denied");

                        TestAssert.False(bare.IsAuthenticated, "A challenge is a failure.");
                        TestAssert.Equal(401, bare.StatusCode, "A challenge is a 401.");
                        TestAssert.Equal("Bearer", bare.Headers["WWW-Authenticate"], "Without parameters the challenge is bare.");
                        TestAssert.Equal(
                            "Bearer resource_metadata=\"https://api.example/.well-known/oauth-protected-resource\", error=\"invalid_token\", error_description=\"bad \\\"token\\\" \\\\ here\"",
                            full.Headers["WWW-Authenticate"],
                            "Parameters are quoted and escaped.");
                        TestAssert.Equal("denied", full.ErrorMessage, "The error message is carried.");
                        return Task.CompletedTask;
                    }),
                });
        }

        /// <summary>
        /// Integration coverage for <see cref="McpHttpServer"/> and <see cref="A2AHttpServer"/>.
        /// </summary>
        public static TestSuiteDescriptor HttpServers()
        {
            const string suiteId = "Security.HttpServers";

            return new TestSuiteDescriptor(
                suiteId,
                "HTTP Server Origin, CORS, Loopback, and Authentication Headers",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "DisallowedOriginPreflightGets403WithoutCors", "A preflight from a disallowed origin gets 403 and no Access-Control headers", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Options, $"{fixture.BaseUrl}/mcp/");
                        request.Headers.TryAddWithoutValidation("Origin", EvilOrigin);
                        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                        TestAssert.False(response.Headers.Any(header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)), "No CORS headers are sent.");
                    }),

                    Case(suiteId, "DisallowedOriginPostNeverRunsTheTool", "POSTs from a disallowed origin (JSON and text/plain, /mcp and /rpc) get 403 and never run the tool", async ct =>
                    {
                        MarkerProbe probe = new MarkerProbe();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => RegisterMarker(server, probe)).ConfigureAwait(false);

                        string body = McpHttpTestRequests.BuildBody("tools/call", 1, new { name = "marker", arguments = new { } });
                        foreach (string path in new[] { "/mcp/", "/rpc/" })
                        {
                            foreach (string contentType in new[] { "application/json", "text/plain" })
                            {
                                using HttpResponseMessage response = await PostAsync(fixture, path, body, contentType, EvilOrigin, ct).ConfigureAwait(false);
                                TestAssert.Equal(HttpStatusCode.Forbidden, response.StatusCode, $"{path} with {contentType} is rejected.");
                                TestAssert.False(response.Headers.Contains("Access-Control-Allow-Origin"), "No CORS grant is sent.");
                            }
                        }

                        TestAssert.Equal(0, probe.Count, "The tool never runs.");
                    }),

                    Case(suiteId, "NonJsonBodyOnMcpGets415", "A text/plain POST to /mcp without Origin gets 415 and never runs the tool", async ct =>
                    {
                        MarkerProbe probe = new MarkerProbe();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => RegisterMarker(server, probe)).ConfigureAwait(false);
                        string? sessionId = await fixture.InitializeSessionAsync(ct).ConfigureAwait(false);

                        string body = McpHttpTestRequests.BuildBody("tools/call", 1, new { name = "marker", arguments = new { } });
                        using HttpResponseMessage response = await PostAsync(fixture, "/mcp/", body, "text/plain", null, ct, sessionId).ConfigureAwait(false);
                        using HttpResponseMessage charset = await PostAsync(fixture, "/mcp/", McpHttpTestRequests.BuildBody("ping", 2, null), "application/json; charset=utf-8", null, ct, sessionId).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
                        TestAssert.Equal(HttpStatusCode.OK, charset.StatusCode, "application/json with parameters is accepted.");
                        TestAssert.Equal(0, probe.Count, "The tool never runs.");
                    }),

                    Case(suiteId, "LoopbackOriginIsAllowedAndEchoed", "A loopback origin is served, echoed in Access-Control-Allow-Origin, and gets Vary: Origin", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpResponseMessage response = await PostAsync(fixture, "/rpc/", McpHttpTestRequests.BuildBody("ping", 1, null), "application/json", LoopbackOrigin, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.Equal(LoopbackOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").First(), "The origin is echoed.");
                        TestAssert.True(response.Headers.Vary.Contains("Origin"), "Vary: Origin is sent.");
                        TestAssert.False(response.Headers.GetValues("Access-Control-Allow-Headers").First().Contains("*"), "Allow-Headers is an explicit list.");
                        TestAssert.True(response.Headers.GetValues("Access-Control-Allow-Headers").First().Contains("Authorization"), "Allow-Headers covers Authorization.");
                    }),

                    Case(suiteId, "AllowlistedOriginIsAllowedOtherPortIsNot", "An allowlisted origin is served; the same host on another port is rejected", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.OriginPolicy.AllowedOrigins.Add("https://app.example.com");
                        }).ConfigureAwait(false);

                        string body = McpHttpTestRequests.BuildBody("ping", 1, null);
                        using HttpResponseMessage allowed = await PostAsync(fixture, "/rpc/", body, "application/json", "https://app.example.com", ct).ConfigureAwait(false);
                        using HttpResponseMessage otherPort = await PostAsync(fixture, "/rpc/", body, "application/json", "https://app.example.com:8443", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, allowed.StatusCode);
                        TestAssert.Equal("https://app.example.com", allowed.Headers.GetValues("Access-Control-Allow-Origin").First());
                        TestAssert.Equal(HttpStatusCode.Forbidden, otherPort.StatusCode);
                    }),

                    Case(suiteId, "NoOriginGetsNoCorsHeaders", "A request without Origin is served and receives no CORS headers", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpResponseMessage response = await PostAsync(fixture, "/rpc/", McpHttpTestRequests.BuildBody("ping", 1, null), "application/json", null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.False(response.Headers.Contains("Access-Control-Allow-Origin"), "Non-browser clients get no CORS grant.");
                    }),

                    Case(suiteId, "EventsStreamEchoesOriginNeverWildcard", "/events sends the allowed origin, never *", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string? sessionId = await fixture.InitializeSessionAsync(ct, "/rpc/").ConfigureAwait(false);

                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/events/");
                        request.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, sessionId);
                        request.Headers.TryAddWithoutValidation("Origin", LoopbackOrigin);
                        using HttpResponseMessage response = await fixture.SendRawAsync(request, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.Equal(LoopbackOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single(), "The stream echoes the origin.");
                    }),

                    Case(suiteId, "CorsDisabledSendsNoHeadersButStillValidatesOrigin", "With EnableCors false an allowed origin is served without CORS headers and a disallowed origin is still rejected", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.EnableCors = false).ConfigureAwait(false);
                        string body = McpHttpTestRequests.BuildBody("ping", 1, null);
                        using HttpResponseMessage allowed = await PostAsync(fixture, "/rpc/", body, "application/json", LoopbackOrigin, ct).ConfigureAwait(false);
                        using HttpResponseMessage rejected = await PostAsync(fixture, "/rpc/", body, "application/json", EvilOrigin, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, allowed.StatusCode);
                        TestAssert.False(allowed.Headers.Contains("Access-Control-Allow-Origin"), "No CORS headers when disabled.");
                        TestAssert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode, "Origin validation does not depend on CORS.");
                    }),

                    Case(suiteId, "CustomOriginValidatorIsHonored", "A custom OriginValidator replaces the built-in rules on the server", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.OriginPolicy = new OriginPolicy { OriginValidator = origin => origin == null || origin == "https://trusted.example" };
                        }).ConfigureAwait(false);

                        string body = McpHttpTestRequests.BuildBody("ping", 1, null);
                        using HttpResponseMessage trusted = await PostAsync(fixture, "/rpc/", body, "application/json", "https://trusted.example", ct).ConfigureAwait(false);
                        using HttpResponseMessage loopback = await PostAsync(fixture, "/rpc/", body, "application/json", LoopbackOrigin, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, trusted.StatusCode);
                        TestAssert.Equal(HttpStatusCode.Forbidden, loopback.StatusCode, "The validator decides alone.");
                    }),

                    Case(suiteId, "AuthenticationFailureCarriesResultHeaders", "A failed authentication writes AuthenticationResult.Headers, such as WWW-Authenticate, on the 401", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = _ =>
                            {
                                AuthenticationResult result = AuthenticationResult.BearerChallenge("https://api.example/.well-known/oauth-protected-resource", "invalid_token", null, "denied");
                                result.Headers["Retry-After"] = "5";
                                return Task.FromResult(result);
                            };
                        }).ConfigureAwait(false);

                        using HttpResponseMessage response = await PostAsync(fixture, "/mcp/", McpHttpTestRequests.BuildBody("tools/list", 1, new { }), "application/json", LoopbackOrigin, ct).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                        TestAssert.Equal("Bearer resource_metadata=\"https://api.example/.well-known/oauth-protected-resource\", error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString());
                        TestAssert.Equal("5", response.Headers.GetValues("Retry-After").First(), "Every result header is written.");
                        TestAssert.Equal(LoopbackOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").First(), "An allowed origin can read the challenge.");
                        TestAssert.Equal("denied", body);
                    }),

                    Case(suiteId, "DisallowedOriginIsRejectedBeforeAuthentication", "A request with valid credentials from a disallowed origin is still rejected with 403", async ct =>
                    {
                        bool authInvoked = false;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = _ =>
                            {
                                authInvoked = true;
                                return Task.FromResult(new AuthenticationResult { IsAuthenticated = true, Principal = "ok" });
                            };
                        }).ConfigureAwait(false);

                        using HttpResponseMessage response = await PostAsync(fixture, "/rpc/", McpHttpTestRequests.BuildBody("tools/list", 1, new { }), "application/json", EvilOrigin, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                        TestAssert.False(authInvoked, "Origin is checked before authentication.");
                    }),

                    Case(suiteId, "LoopbackRestrictionDefaultsFollowHostname", "RestrictToLoopbackClients defaults to true for loopback host names and false otherwise, on every HTTP server", ct =>
                    {
                        AgentCard card = new AgentCard { Name = "t", Description = "t", Version = "1" };
                        using McpHttpServer localhost = new McpHttpServer("localhost", 1);
                        using McpHttpServer ipv4 = new McpHttpServer("127.0.0.1", 1);
                        using McpHttpServer wildcard = new McpHttpServer("*", 1);
                        using McpHttpServer plus = new McpHttpServer("+", 1);
                        using McpWebsocketsServer websocket = new McpWebsocketsServer("localhost", 1);
                        using McpWebsocketsServer websocketAll = new McpWebsocketsServer("+", 1);
                        using A2AHttpServer a2a = new A2AHttpServer("localhost", 1, card);
                        using A2AHttpServer a2aAll = new A2AHttpServer("*", 1, card);

                        TestAssert.True(localhost.RestrictToLoopbackClients && ipv4.RestrictToLoopbackClients, "Loopback names restrict.");
                        TestAssert.False(wildcard.RestrictToLoopbackClients || plus.RestrictToLoopbackClients, "Wildcards do not restrict.");
                        TestAssert.True(websocket.RestrictToLoopbackClients && !websocketAll.RestrictToLoopbackClients, "WebSocket follows the same rule.");
                        TestAssert.True(a2a.RestrictToLoopbackClients && !a2aAll.RestrictToLoopbackClients, "A2A follows the same rule.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "RemoteClientWithSpoofedHostIsRejected", "A client on a non-loopback address sending Host: localhost gets 403 from a localhost-bound server (or cannot connect at all)", async ct =>
                    {
                        MarkerProbe probe = new MarkerProbe();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => RegisterMarker(server, probe)).ConfigureAwait(false);
                        IPAddress? lan = RawSocketRequests.FindNonLoopbackIPv4();
                        if (lan == null) return;

                        string body = McpHttpTestRequests.BuildBody("tools/call", 1, new { name = "marker", arguments = new { } });
                        string? response = await RawSocketRequests.SendAsync(lan, fixture.Port, RawSocketRequests.HttpPost("/rpc/", $"localhost:{fixture.Port}", body, "application/json", null), true, ct).ConfigureAwait(false);
                        int status = RawSocketRequests.StatusCode(response);

                        TestAssert.True(status == 0 || status == 403, $"A remote client is refused (status {status}).");
                        TestAssert.Equal(0, probe.Count, "The tool never runs.");
                    }),

                    Case(suiteId, "LoopbackRestrictionCanBeDisabled", "With RestrictToLoopbackClients false, a remote client reaching a localhost-bound server is served", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.RestrictToLoopbackClients = false).ConfigureAwait(false);
                        IPAddress? lan = RawSocketRequests.FindNonLoopbackIPv4();
                        if (lan == null) return;

                        string? response = await RawSocketRequests.SendAsync(lan, fixture.Port, RawSocketRequests.HttpPost("/rpc/", $"localhost:{fixture.Port}", McpHttpTestRequests.BuildBody("ping", 1, null), "application/json", null), true, ct).ConfigureAwait(false);
                        int status = RawSocketRequests.StatusCode(response);

                        // Platforms that bind localhost to loopback cannot be reached from the LAN address at all.
                        TestAssert.True(status == 0 || status == 200, $"The opt-out serves the remote client (status {status}).");
                    }),

                    Case(suiteId, "A2ARejectsDisallowedOriginAndEchoesLoopback", "A2AHttpServer rejects a disallowed origin with 403, even for the public Agent Card, and echoes a loopback origin", async ct =>
                    {
                        await using A2AServerFixture fixture = await A2AServerFixture.StartAsync(ct).ConfigureAwait(false);

                        using HttpRequestMessage evil = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}{A2AProtocol.AgentCardPath}");
                        evil.Headers.TryAddWithoutValidation("Origin", EvilOrigin);
                        using HttpResponseMessage evilResponse = await fixture.Client.SendAsync(evil, ct).ConfigureAwait(false);

                        using HttpRequestMessage local = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}{A2AProtocol.AgentCardPath}");
                        local.Headers.TryAddWithoutValidation("Origin", LoopbackOrigin);
                        using HttpResponseMessage localResponse = await fixture.Client.SendAsync(local, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Forbidden, evilResponse.StatusCode);
                        TestAssert.False(evilResponse.Headers.Contains("Access-Control-Allow-Origin"), "No CORS grant for a rejected origin.");
                        TestAssert.Equal(HttpStatusCode.OK, localResponse.StatusCode);
                        TestAssert.Equal(LoopbackOrigin, localResponse.Headers.GetValues("Access-Control-Allow-Origin").Single(), "The loopback origin is echoed, never *.");
                    }),

                    Case(suiteId, "A2AAuthenticationFailureCarriesResultHeaders", "A2AHttpServer writes AuthenticationResult.Headers on an authentication failure", async ct =>
                    {
                        await using A2AServerFixture fixture = await A2AServerFixture.StartAsync(ct, server =>
                        {
                            server.AuthenticationHandler = _ => Task.FromResult(AuthenticationResult.BearerChallenge(null, "invalid_token"));
                        }).ConfigureAwait(false);

                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/a2a")
                        {
                            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"GetTask\",\"params\":{\"id\":\"x\"}}", Encoding.UTF8, "application/json")
                        };
                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                        TestAssert.Equal("Bearer error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString());
                    }),
                });
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpMcpTestServerFixture fixture, string path, string body, string contentType, string? origin, CancellationToken token, string? sessionId = null)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}{path}")
            {
                Content = new StringContent(body, Encoding.UTF8)
            };
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (sessionId != null) request.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, sessionId);

            HttpResponseMessage response = await fixture.Client.SendAsync(request, token).ConfigureAwait(false);
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            return response;
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
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "security", "http" });
        }
    }
}
