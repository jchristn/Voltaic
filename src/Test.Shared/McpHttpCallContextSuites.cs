namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Touchstone suite covering <see cref="RpcCallContext"/> propagation through
    /// <see cref="McpHttpServer"/>. The cases prove that the authenticated caller produced by the
    /// server's <see cref="McpHttpServer.AuthenticationHandler"/> is made ambient for the request and
    /// is observable inside a <c>tools/call</c> handler — both through <see cref="RpcCallContext.Current"/>
    /// (Design A) and through the explicit-context registration overload (Design B) — and that the
    /// context is null when the request was not authenticated, is never established for the pre-auth
    /// <c>ping</c> bypass, and is isolated across concurrent callers.
    /// </summary>
    public static class McpHttpCallContextSuites
    {
        /// <summary>
        /// Builds the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public static TestSuiteDescriptor CallContextPropagation()
        {
            const string suiteId = "McpHttp.Server.CallContext";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP HTTP Server Call Context",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "AmbientContextReflectsAuthenticatedCaller", "RpcCallContext.Current inside a tools/call handler reflects the AuthenticationResult", async ct =>
                    {
                        ContextCapture capture = new ContextCapture();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct,
                            configure: server =>
                            {
                                server.AuthenticationHandler = AuthAs("user-42", new Dictionary<string, string>
                                {
                                    { "tenantId", "tenant-1" },
                                    { "isAdmin", "true" }
                                });
                                server.RegisterTool("whoami", "Records the ambient caller", new { type = "object" },
                                    (RpcParameters? args, CancellationToken token) =>
                                    {
                                        capture.Record(RpcCallContext.Current);
                                        return Task.FromResult<object>(McpToolCallResult.FromText("ok"));
                                    });
                            }).ConfigureAwait(false);

                        RpcResult response = await fixture.PostMcpAsync("tools/call", new { name = "whoami", arguments = new { } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.True(capture.Invoked, "Tool handler should have run.");
                        TestAssert.False(capture.ContextWasNull, "RpcCallContext.Current should be non-null for an authenticated request.");
                        TestAssert.Equal("user-42", capture.Principal, "Principal should match the AuthenticationResult.");
                        TestAssert.Equal("tenant-1", capture.TenantId, "tenantId claim should flow to the handler.");
                        TestAssert.Equal("true", capture.IsAdmin, "isAdmin claim should flow to the handler.");
                        TestAssert.False(response.Root.Has("error"), "tools/call should not report an error.");
                    }),

                    Case(suiteId, "ExplicitContextOverloadReceivesCaller", "The explicit-context RegisterTool overload receives the same caller as RpcCallContext.Current", async ct =>
                    {
                        ContextCapture capture = new ContextCapture();
                        bool sameAsCurrent = false;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct,
                            configure: server =>
                            {
                                server.AuthenticationHandler = AuthAs("svc-account", new Dictionary<string, string> { { "tenantId", "tenant-9" } });
                                server.RegisterTool("whoami-explicit", "Records the explicit caller", new { type = "object" },
                                    (RpcParameters? args, RpcCallContext? caller, CancellationToken token) =>
                                    {
                                        sameAsCurrent = ReferenceEquals(caller, RpcCallContext.Current);
                                        capture.Record(caller);
                                        return Task.FromResult<object>(McpToolCallResult.FromText("ok"));
                                    });
                            }).ConfigureAwait(false);

                        await fixture.PostMcpAsync("tools/call", new { name = "whoami-explicit", arguments = new { } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.True(capture.Invoked, "Explicit-context tool handler should have run.");
                        TestAssert.False(capture.ContextWasNull, "The explicit context parameter should be non-null for an authenticated request.");
                        TestAssert.True(sameAsCurrent, "The explicit context parameter should be the same instance as RpcCallContext.Current.");
                        TestAssert.Equal("svc-account", capture.Principal, "Explicit context Principal should match the AuthenticationResult.");
                        TestAssert.Equal("tenant-9", capture.TenantId, "Explicit context should carry the tenantId claim.");
                    }),

                    Case(suiteId, "ContextNullWithoutAuthenticationHandler", "RpcCallContext.Current is null when no AuthenticationHandler is configured", async ct =>
                    {
                        ContextCapture capture = new ContextCapture();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct,
                            configure: server =>
                            {
                                // No AuthenticationHandler is set.
                                server.RegisterTool("whoami-anon", "Records the ambient caller", new { type = "object" },
                                    (RpcParameters? args, CancellationToken token) =>
                                    {
                                        capture.Record(RpcCallContext.Current);
                                        return Task.FromResult<object>(McpToolCallResult.FromText("ok"));
                                    });
                            }).ConfigureAwait(false);

                        await fixture.PostMcpAsync("tools/call", new { name = "whoami-anon", arguments = new { } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.True(capture.Invoked, "Tool handler should have run.");
                        TestAssert.True(capture.ContextWasNull, "RpcCallContext.Current should be null when no AuthenticationHandler is configured.");
                    }),

                    Case(suiteId, "PingBypassEstablishesNoContext", "The pre-auth ping bypass never runs authentication, so no caller context is established", async ct =>
                    {
                        AuthProbe probe = new AuthProbe();
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct,
                            configure: server =>
                            {
                                server.AuthenticationHandler = request =>
                                {
                                    probe.Invoked = true;
                                    return Task.FromResult(new AuthenticationResult { IsAuthenticated = true, Principal = "should-not-apply" });
                                };
                            }).ConfigureAwait(false);

                        RpcResult response = await fixture.PostMcpAsync("ping", new { }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.False(probe.Invoked, "The ping handshake must bypass authentication, so no caller context is established for it.");
                        TestAssert.False(response.Root.Has("error"), "ping should succeed.");
                    }),

                    Case(suiteId, "ConcurrentCallersAreIsolated", "Concurrent authenticated callers each observe only their own context", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(
                            ct,
                            configure: server =>
                            {
                                // Derive the principal from the caller's Authorization header so each request is distinct.
                                server.AuthenticationHandler = request =>
                                {
                                    string? principal = request.Headers["Authorization"];
                                    return Task.FromResult(new AuthenticationResult { IsAuthenticated = true, Principal = principal });
                                };

                                // Echo back whatever principal is ambient while this handler runs.
                                server.RegisterTool("echo-principal", "Echoes the ambient principal", new { type = "object" },
                                    (RpcParameters? args, CancellationToken token) =>
                                        Task.FromResult<object>(McpToolCallResult.FromText(RpcCallContext.Current?.Principal ?? "<null>")));
                            }).ConfigureAwait(false);

                        const int callers = 12;
                        IEnumerable<Task<string>> calls = Enumerable.Range(0, callers)
                            .Select(index => CallEchoPrincipalAsync(fixture, $"principal-{index}", ct));
                        string[] bodies = await Task.WhenAll(calls).ConfigureAwait(false);

                        for (int index = 0; index < callers; index++)
                        {
                            string expected = $"principal-{index}";
                            TestAssert.True(
                                bodies[index].Contains(expected, StringComparison.Ordinal),
                                $"Caller {index} should observe only its own principal '{expected}' but the response was: {bodies[index]}");
                        }
                    }),
                });
        }

        private static async Task<string> CallEchoPrincipalAsync(HttpMcpTestServerFixture fixture, string principal, CancellationToken token)
        {
            JsonRpcRequest request = new JsonRpcRequest
            {
                Method = "tools/call",
                Params = new { name = "echo-principal", arguments = new { } },
                Id = 1
            };

            using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
            };
            message.Headers.Accept.ParseAdd("application/json");
            message.Headers.Accept.ParseAdd("text/event-stream");
            message.Headers.Add("Authorization", principal);

            using HttpResponseMessage response = await fixture.SendRawAsync(message, token).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }

        private static Func<System.Net.HttpListenerRequest, Task<AuthenticationResult>> AuthAs(string principal, Dictionary<string, string> claims)
        {
            return request => Task.FromResult(new AuthenticationResult
            {
                IsAuthenticated = true,
                Principal = principal,
                Claims = claims
            });
        }

        private sealed class ContextCapture
        {
            public volatile bool Invoked;
            public volatile bool ContextWasNull;
            public string? Principal;
            public string? TenantId;
            public string? IsAdmin;

            public void Record(RpcCallContext? context)
            {
                Invoked = true;
                ContextWasNull = context == null;
                Principal = context?.Principal;
                if (context != null)
                {
                    TenantId = context.Claims.TryGetValue("tenantId", out string? tenant) ? tenant : null;
                    IsAdmin = context.Claims.TryGetValue("isAdmin", out string? admin) ? admin : null;
                }
            }
        }

        private sealed class AuthProbe
        {
            public volatile bool Invoked;
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "http", "auth", "context" });
        }
    }
}
