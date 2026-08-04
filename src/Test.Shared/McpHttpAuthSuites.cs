namespace Test.Shared
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Touchstone suite covering <see cref="McpHttpClient.SetRequestHeader(string, string?)"/>. The
    /// cases prove that a configured header — bearer authorization or a custom API-key header — is
    /// actually transmitted on the wire by inspecting the request the server receives through its
    /// <see cref="McpHttpServer.AuthenticationHandler"/>, and that removing a header or omitting one
    /// leaves it off the request. The <c>ping</c> handshake bypasses server authentication, so each
    /// case issues a non-ping call to route the request through the authentication handler.
    /// </summary>
    public static class McpHttpAuthSuites
    {
        /// <summary>
        /// Builds the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public static TestSuiteDescriptor RequestHeaderMatrix()
        {
            const string suiteId = "McpHttp.Client.Auth";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP HTTP Client Auth Headers",
                new System.Collections.Generic.List<TestCaseDescriptor>
                {
                    Case(suiteId, "BearerHeaderReachesServer", "A bearer Authorization header set on the client reaches the server", async ct =>
                    {
                        HeaderCapture capture = new HeaderCapture();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct, configure: server => server.AuthenticationHandler = CapturingHandler(capture)).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();

                        client.SetRequestHeader("Authorization", "Bearer test-token-abc");
                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");

                        // A non-ping call routes through the authentication handler, which records headers.
                        await client.CallAsync("tools/list", token: ct).ConfigureAwait(false);

                        TestAssert.True(capture.Invoked, "Authentication handler should have run for a non-ping call.");
                        TestAssert.Equal("Bearer test-token-abc", capture.Authorization);
                    }),

                    Case(suiteId, "ApiKeyHeaderReachesServer", "A custom API-key header set on the client reaches the server", async ct =>
                    {
                        HeaderCapture capture = new HeaderCapture();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct, configure: server => server.AuthenticationHandler = CapturingHandler(capture)).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();

                        client.SetRequestHeader("X-API-Key", "secret-key-123");
                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");

                        await client.CallAsync("tools/list", token: ct).ConfigureAwait(false);

                        TestAssert.True(capture.Invoked, "Authentication handler should have run for a non-ping call.");
                        TestAssert.Equal("secret-key-123", capture.ApiKey);
                    }),

                    Case(suiteId, "NoHeaderMeansNoAuthorization", "Without a configured header the request carries no Authorization", async ct =>
                    {
                        HeaderCapture capture = new HeaderCapture();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct, configure: server => server.AuthenticationHandler = CapturingHandler(capture)).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();

                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");

                        await client.CallAsync("tools/list", token: ct).ConfigureAwait(false);

                        TestAssert.True(capture.Invoked, "Authentication handler should have run for a non-ping call.");
                        TestAssert.True(String.IsNullOrEmpty(capture.Authorization), "No Authorization header should be present.");
                    }),

                    Case(suiteId, "RemovingHeaderClearsIt", "Setting a header then clearing it leaves it off the request", async ct =>
                    {
                        HeaderCapture capture = new HeaderCapture();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct, configure: server => server.AuthenticationHandler = CapturingHandler(capture)).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();

                        client.SetRequestHeader("Authorization", "Bearer will-be-removed");
                        client.SetRequestHeader("Authorization", null);
                        bool connected = await client.ConnectStreamableAsync(fixture.BaseUrl, token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");

                        await client.CallAsync("tools/list", token: ct).ConfigureAwait(false);

                        TestAssert.True(capture.Invoked, "Authentication handler should have run for a non-ping call.");
                        TestAssert.True(String.IsNullOrEmpty(capture.Authorization), "Cleared Authorization header should be absent.");
                    }),

                    Case(suiteId, "NullOrEmptyNameThrows", "SetRequestHeader rejects a null or empty header name", ct =>
                    {
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.Throws<ArgumentNullException>(() => client.SetRequestHeader(null!, "value"), "Null header name should throw.");
                        TestAssert.Throws<ArgumentNullException>(() => client.SetRequestHeader(String.Empty, "value"), "Empty header name should throw.");
                        return Task.CompletedTask;
                    }),
                });
        }

        private sealed class HeaderCapture
        {
            public volatile bool Invoked;
            public string? Authorization;
            public string? ApiKey;
        }

        private static Func<System.Net.HttpListenerRequest, Task<AuthenticationResult>> CapturingHandler(HeaderCapture capture)
        {
            return request =>
            {
                capture.Authorization = request.Headers["Authorization"];
                capture.ApiKey = request.Headers["X-API-Key"];
                capture.Invoked = true;
                return Task.FromResult(new AuthenticationResult { IsAuthenticated = true });
            };
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "http", "auth" });
        }
    }
}
