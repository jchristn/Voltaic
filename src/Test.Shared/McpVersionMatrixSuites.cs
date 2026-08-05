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
    /// Discrete positive and negative end-to-end suites for each supported MCP protocol revision,
    /// exercising the behavior available in that revision over the live HTTP server: version
    /// negotiation, core operations, and the version-gated JSON-RPC batching policy (permitted
    /// through 2025-03-26, removed from 2025-06-18 onward). The stateless 2026-07-28 revision is
    /// additionally covered in depth by the McpVersion.Stateless and McpVersion.StatelessClient
    /// suites.
    /// </summary>
    public static class McpVersionMatrixSuites
    {
        /// <summary>2024-11-05: handshake era, batching permitted.</summary>
        public static TestSuiteDescriptor Version20241105() => Handshake("2024-11-05", supportsBatching: true);

        /// <summary>2025-03-26: handshake era, batching permitted.</summary>
        public static TestSuiteDescriptor Version20250326() => Handshake("2025-03-26", supportsBatching: true);

        /// <summary>2025-06-18: handshake era, batching removed.</summary>
        public static TestSuiteDescriptor Version20250618() => Handshake("2025-06-18", supportsBatching: false);

        /// <summary>2025-11-25: handshake era, batching removed.</summary>
        public static TestSuiteDescriptor Version20251125() => Handshake("2025-11-25", supportsBatching: false);

        /// <summary>2026-07-28: stateless era.</summary>
        public static TestSuiteDescriptor Version20260728()
        {
            const string version = "2026-07-28";
            const string suiteId = "Mcp.Version.2026-07-28";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP 2026-07-28 end-to-end",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "DiscoverAdvertisesVersionPositive", "server/discover advertises 2026-07-28", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "_meta", Meta(version) } };
                        using HttpResponseMessage response = await SendStatelessAsync(fixture, "server/discover", parameters, null, version, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "Discovery should return 200.");
                        McpDiscoverResult? discover = RpcResponseHelpers.ResultAs<McpDiscoverResult>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                        TestAssert.NotNull(discover, "Discovery result should deserialize.");
                        TestAssert.True(discover!.SupportedVersions.Contains(version), "Discovery should advertise 2026-07-28.");
                    }),

                    Case(suiteId, "ToolCallPositive", "stateless tools/call returns a result", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?>
                        {
                            { "name", "sum" },
                            { "arguments", new { a = 2, b = 3 } },
                            { "_meta", Meta(version) }
                        };
                        using HttpResponseMessage response = await SendStatelessAsync(fixture, "tools/call", parameters, "sum", version, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "tools/call should return 200.");
                        McpToolCallResult? result = RpcResponseHelpers.ResultAs<McpToolCallResult>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                        TestAssert.NotNull(result, "Tool result should deserialize.");
                        TestAssert.True(result!.Content.Count > 0, "Tool result should carry content.");
                    }),

                    Case(suiteId, "BatchRejectedNegative", "a JSON-RPC batch is rejected on the stateless endpoint", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using HttpResponseMessage response = await PostBatchAsync(fixture, BatchBody(), null, version, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Stateless batch should be rejected.");
                    }),

                    Case(suiteId, "UnsupportedVersionNegative", "an unsupported stateless version is rejected with -32022", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "_meta", Meta("1999-01-01") } };
                        using HttpResponseMessage response = await SendStatelessAsync(fixture, "server/discover", parameters, null, "1999-01-01", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Unsupported version should be 400.");
                        TestAssert.Equal(-32022, RpcResponseHelpers.ErrorCode(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), "Unsupported version maps to -32022.");
                    }),
                });
        }

        private static TestSuiteDescriptor Handshake(string version, bool supportsBatching)
        {
            string suiteId = $"Mcp.Version.{version}";

            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                Case(suiteId, "NegotiatePositive", $"initialize negotiates and echoes {version}", async ct =>
                {
                    await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                    RpcResult response = await InitializeAsync(fixture, version, ct).ConfigureAwait(false);

                    JsonProbe result = JsonProbe.Parse(response.Body).Get("result");
                    TestAssert.Equal(version, result.Get("protocolVersion").String(), "Server should echo the negotiated version.");
                    TestAssert.True(result.Get("capabilities").IsObject, "Capabilities should be advertised.");
                    TestAssert.False(String.IsNullOrEmpty(response.SessionId), "A session id should be returned.");
                }),

                Case(suiteId, "CoreOperationsPositive", $"tools/list and tools/call work under {version}", async ct =>
                {
                    await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                    RpcResult init = await InitializeAsync(fixture, version, ct).ConfigureAwait(false);

                    RpcResult list = await fixture.PostMcpAsync("tools/list", new { }, "list", init.SessionId, ct).ConfigureAwait(false);
                    JsonProbe tools = JsonProbe.Parse(list.Body).Get("result").Get("tools");
                    TestAssert.True(tools.EnumerateArray().Any(tool => tool.Get("name").String() == "sum"), "The registered tool should be listed.");

                    RpcResult call = await fixture.PostMcpAsync("tools/call", new { name = "sum", arguments = new { a = 2, b = 3 } }, "call", init.SessionId, ct).ConfigureAwait(false);
                    JsonProbe callResult = JsonProbe.Parse(call.Body).Get("result");
                    TestAssert.Equal(5, callResult.Get("structuredContent").Get("total").Int(), "tools/call should return the structured total.");
                }),

                Case(suiteId, "UnsupportedVersionNegative", $"initialize rejects an unsupported version under {version}", async ct =>
                {
                    await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                    RpcResult response = await fixture.PostMcpAsync("initialize", new { protocolVersion = "1900-01-01" }, "init", null, ct).ConfigureAwait(false);
                    TestAssert.Equal(-32602, JsonProbe.Parse(response.Body).Get("error").Get("code").Int(), "Unsupported version should be an invalid-params error.");
                }),

                Case(suiteId, "UnsupportedHeaderNegative", $"an unsupported MCP-Protocol-Version header is rejected under {version}", async ct =>
                {
                    await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                    using HttpResponseMessage response = await PostSingleAsync(fixture, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{}}", null, "1899-01-01", ct).ConfigureAwait(false);
                    TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "An unsupported protocol-version header should be 400.");
                }),
            };

            if (supportsBatching)
            {
                cases.Add(Case(suiteId, "BatchAcceptedPositive", $"a JSON-RPC batch is accepted under {version}", async ct =>
                {
                    await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                    RpcResult init = await InitializeAsync(fixture, version, ct).ConfigureAwait(false);

                    using HttpResponseMessage response = await PostBatchAsync(fixture, BatchBody(), init.SessionId, null, ct).ConfigureAwait(false);
                    TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "A batch should be accepted.");
                    JsonProbe array = JsonProbe.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    TestAssert.True(array.IsArray, "A batch response should be a JSON array.");
                    TestAssert.Equal(2, array.Length, "The batch response should carry one result per request.");
                }));
            }
            else
            {
                cases.Add(Case(suiteId, "BatchRejectedNegative", $"a JSON-RPC batch is rejected under {version}", async ct =>
                {
                    await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                    RpcResult init = await InitializeAsync(fixture, version, ct).ConfigureAwait(false);

                    using HttpResponseMessage response = await PostBatchAsync(fixture, BatchBody(), init.SessionId, null, ct).ConfigureAwait(false);
                    TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "A batch should be rejected once batching is removed.");
                    TestAssert.Equal(-32600, RpcResponseHelpers.ErrorCode(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), "Batch rejection should be an invalid-request error.");
                }));
            }

            return new TestSuiteDescriptor(suiteId, $"MCP {version} end-to-end", cases);
        }

        private static async Task<HttpMcpTestServerFixture> StartAsync(CancellationToken token)
        {
            return await HttpMcpTestServerFixture.StartAsync(token, server =>
            {
                server.AdvertiseTasksExtension = true;
                object inputSchema = new
                {
                    type = "object",
                    properties = new { a = new { type = "number" }, b = new { type = "number" } },
                    required = new[] { "a", "b" }
                };
                object outputSchema = new { type = "object", properties = new { total = new { type = "number" } } };

                server.RegisterTool("sum", "Adds two numbers", inputSchema, outputSchema, (args) =>
                {
                    double total = (args?.GetDouble("a") ?? 0) + (args?.GetDouble("b") ?? 0);
                    return McpToolCallResult.FromStructured(new { total });
                });
            }).ConfigureAwait(false);
        }

        private static async Task<RpcResult> InitializeAsync(HttpMcpTestServerFixture fixture, string version, CancellationToken token)
        {
            return await fixture.PostMcpAsync("initialize", new { protocolVersion = version, clientInfo = new { name = "matrix", version = "1.0.0" } }, "init", null, token).ConfigureAwait(false);
        }

        private static string BatchBody()
        {
            return "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}]";
        }

        private static Dictionary<string, object?> Meta(string version)
        {
            return new Dictionary<string, object?>
            {
                { "io.modelcontextprotocol/protocolVersion", version },
                { "io.modelcontextprotocol/clientInfo", new { name = "matrix", version = "1.0.0" } },
                { "io.modelcontextprotocol/clientCapabilities", new { } }
            };
        }

        private static async Task<HttpResponseMessage> PostSingleAsync(HttpMcpTestServerFixture fixture, string body, string? sessionId, string? protocolHeader, CancellationToken token)
        {
            return await SendAsync(fixture, body, sessionId, protocolHeader, null, null, token).ConfigureAwait(false);
        }

        private static async Task<HttpResponseMessage> PostBatchAsync(HttpMcpTestServerFixture fixture, string body, string? sessionId, string? protocolHeader, CancellationToken token)
        {
            return await SendAsync(fixture, body, sessionId, protocolHeader, null, null, token).ConfigureAwait(false);
        }

        private static async Task<HttpResponseMessage> SendStatelessAsync(HttpMcpTestServerFixture fixture, string method, Dictionary<string, object?> parameters, string? name, string version, CancellationToken token)
        {
            Dictionary<string, object?> body = new Dictionary<string, object?>
            {
                { "jsonrpc", "2.0" },
                { "id", method },
                { "method", method },
                { "params", parameters }
            };

            return await SendAsync(fixture, System.Text.Json.JsonSerializer.Serialize(body), null, version, method, name, token).ConfigureAwait(false);
        }

        private static async Task<HttpResponseMessage> SendAsync(
            HttpMcpTestServerFixture fixture,
            string body,
            string? sessionId,
            string? protocolHeader,
            string? methodHeader,
            string? nameHeader,
            CancellationToken token)
        {
            HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            message.Headers.Accept.ParseAdd("application/json");
            message.Headers.Accept.ParseAdd("text/event-stream");
            if (!String.IsNullOrEmpty(sessionId))
            {
                message.Headers.Add(McpProtocol.SessionIdHeader, sessionId);
            }

            if (!String.IsNullOrEmpty(protocolHeader))
            {
                message.Headers.Add(McpProtocol.ProtocolVersionHeader, protocolHeader);
            }

            if (!String.IsNullOrEmpty(methodHeader))
            {
                message.Headers.Add(McpProtocol.MethodHeader, methodHeader);
            }

            if (!String.IsNullOrEmpty(nameHeader))
            {
                message.Headers.Add(McpProtocol.NameHeader, nameHeader);
            }

            return await fixture.Client.SendAsync(message, token).ConfigureAwait(false);
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "version", "matrix" });
        }
    }
}
