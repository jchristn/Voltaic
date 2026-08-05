namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Positive and negative coverage for the stateless 2026-07-28 Streamable HTTP path: per-request
    /// metadata, the required routing headers (<c>MCP-Protocol-Version</c>, <c>Mcp-Method</c>,
    /// <c>Mcp-Name</c>), header/body validation, notifications, and unknown-method handling.
    /// </summary>
    public static class McpStatelessTransportSuites
    {
        public static TestSuiteDescriptor StatelessStreamableHttp()
        {
            const string suiteId = "McpVersion.Stateless";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Stateless Streamable HTTP (2026-07-28)",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ToolCallSucceeds", "A well-formed stateless tools/call returns a single JSON result", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?>
                        {
                            { "name", "noop" },
                            { "arguments", new { } },
                            { "_meta", Meta("2026-07-28") }
                        };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "tools/call", 1, parameters, "2026-07-28", "tools/call", "noop", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "Stateless tools/call should return 200.");
                        TestAssert.False(response.Headers.Contains(McpProtocol.SessionIdHeader), "Stateless responses must not carry a session id.");

                        McpToolCallResult? result = RpcResponseHelpers.ResultAs<McpToolCallResult>(await BodyAsync(response, ct).ConfigureAwait(false));
                        TestAssert.NotNull(result, "Tool result should deserialize.");
                        TestAssert.True(result!.Content.Count > 0, "Tool result should include content.");
                    }),

                    Case(suiteId, "DiscoverSucceeds", "A stateless server/discover returns the discovery result", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "_meta", Meta("2026-07-28") } };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "server/discover", "d1", parameters, "2026-07-28", "server/discover", null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "Stateless server/discover should return 200.");
                        McpDiscoverResult? result = RpcResponseHelpers.ResultAs<McpDiscoverResult>(await BodyAsync(response, ct).ConfigureAwait(false));
                        TestAssert.NotNull(result, "Discovery result should deserialize.");
                        TestAssert.Equal("complete", result!.ResultType, "Discovery uses resultType complete.");
                    }),

                    Case(suiteId, "NotificationReturns202", "A stateless notification returns 202 with no body", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "_meta", Meta("2026-07-28") } };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "notifications/progress", null, parameters, "2026-07-28", "notifications/progress", null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Accepted, response.StatusCode, "Stateless notifications should return 202.");
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        TestAssert.True(String.IsNullOrEmpty(body), "Accepted notifications should have no body.");
                    }),

                    Case(suiteId, "MissingProtocolHeaderRejected", "A stateless request without the protocol-version header is rejected", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        // No header and no _meta version, but routing headers make the request stateless.
                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "name", "noop" }, { "arguments", new { } } };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "tools/call", 1, parameters, null, "tools/call", "noop", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Missing protocol header should be 400.");
                        TestAssert.Equal(-32020, RpcResponseHelpers.ErrorCode(await BodyAsync(response, ct).ConfigureAwait(false)), "Missing header maps to HeaderMismatch.");
                    }),

                    Case(suiteId, "HeaderBodyVersionMismatchRejected", "A header/body protocol-version mismatch returns -32020", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?>
                        {
                            { "name", "noop" },
                            { "arguments", new { } },
                            { "_meta", Meta("2025-11-25") }
                        };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "tools/call", 1, parameters, "2026-07-28", "tools/call", "noop", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Mismatch should be 400.");
                        TestAssert.Equal(-32020, RpcResponseHelpers.ErrorCode(await BodyAsync(response, ct).ConfigureAwait(false)), "Mismatch maps to -32020.");
                    }),

                    Case(suiteId, "NameHeaderMismatchRejected", "An Mcp-Name that disagrees with the body returns -32020", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?>
                        {
                            { "name", "noop" },
                            { "arguments", new { } },
                            { "_meta", Meta("2026-07-28") }
                        };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "tools/call", 1, parameters, "2026-07-28", "tools/call", "wrong", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Name mismatch should be 400.");
                        TestAssert.Equal(-32020, RpcResponseHelpers.ErrorCode(await BodyAsync(response, ct).ConfigureAwait(false)), "Name mismatch maps to -32020.");
                    }),

                    Case(suiteId, "MissingMethodHeaderRejected", "A stateless request without Mcp-Method is rejected", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "_meta", Meta("2026-07-28") } };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "server/discover", "d1", parameters, "2026-07-28", null, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Missing Mcp-Method should be 400.");
                        TestAssert.Equal(-32020, RpcResponseHelpers.ErrorCode(await BodyAsync(response, ct).ConfigureAwait(false)), "Missing method header maps to -32020.");
                    }),

                    Case(suiteId, "UnsupportedVersionRejected", "An unsupported protocol version returns -32022", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        // Header and body agree on the unsupported version so the unsupported-version
                        // check runs rather than the header/body mismatch check.
                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "_meta", Meta("1999-01-01") } };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "server/discover", "d1", parameters, "1999-01-01", "server/discover", null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Unsupported version should be 400.");
                        TestAssert.Equal(-32022, RpcResponseHelpers.ErrorCode(await BodyAsync(response, ct).ConfigureAwait(false)), "Unsupported version maps to -32022.");
                    }),

                    Case(suiteId, "UnknownMethodReturns404", "An unknown method on the stateless path returns 404 with -32601", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithToolAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "_meta", Meta("2026-07-28") } };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "does/notexist", 7, parameters, "2026-07-28", "does/notexist", null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.NotFound, response.StatusCode, "Unknown method should be 404.");
                        TestAssert.Equal(-32601, RpcResponseHelpers.ErrorCode(await BodyAsync(response, ct).ConfigureAwait(false)), "Unknown method maps to -32601.");
                    }),

                    Case(suiteId, "MrtrInputRequiredEmitted", "A tool can return an input-required result over tools/call", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithConfirmToolAsync(ct).ConfigureAwait(false);

                        Dictionary<string, object?> parameters = new Dictionary<string, object?>
                        {
                            { "name", "confirm" },
                            { "arguments", new { } },
                            { "_meta", Meta("2026-07-28") }
                        };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "tools/call", 1, parameters, "2026-07-28", "tools/call", "confirm", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "An input-required result is still a 200 response.");
                        string body = await BodyAsync(response, ct).ConfigureAwait(false);
                        TestAssert.Equal("input_required", RpcResponseHelpers.ResultType(body), "resultType should be input_required.");
                        McpInputRequiredResult? result = RpcResponseHelpers.ResultAs<McpInputRequiredResult>(body);
                        TestAssert.NotNull(result, "Input-required result should deserialize.");
                        TestAssert.NotNull(result!.InputRequests, "inputRequests should be present.");
                        TestAssert.NotNull(result.RequestState, "requestState should be present.");
                    }),

                    Case(suiteId, "MrtrRetrySucceeds", "Re-calling the tool with the gathered input returns the final result", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartWithConfirmToolAsync(ct).ConfigureAwait(false);

                        // Voltaic surfaces tool arguments to handlers, so the gathered input is carried
                        // in arguments here; the round trip mirrors the MRTR retry with inputResponses.
                        Dictionary<string, object?> parameters = new Dictionary<string, object?>
                        {
                            { "name", "confirm" },
                            { "arguments", new { confirmed = true } },
                            { "_meta", Meta("2026-07-28") }
                        };

                        using HttpResponseMessage response = await SendStatelessAsync(
                            fixture, "tools/call", 2, parameters, "2026-07-28", "tools/call", "confirm", ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "The retry should succeed.");
                        string body = await BodyAsync(response, ct).ConfigureAwait(false);
                        TestAssert.Null(RpcResponseHelpers.ResultType(body), "The final result is a normal tool result, not input_required.");
                        McpToolCallResult? result = RpcResponseHelpers.ResultAs<McpToolCallResult>(body);
                        TestAssert.NotNull(result, "Final result should deserialize.");
                        TestAssert.True(result!.Content.Count > 0, "The final result should carry content.");
                    }),
                });
        }

        private static async Task<HttpMcpTestServerFixture> StartWithToolAsync(CancellationToken token)
        {
            return await HttpMcpTestServerFixture.StartAsync(token, server =>
            {
                server.AdvertiseTasksExtension = true;
                server.RegisterTool("noop", "Does nothing", new { type = "object", properties = new { }, required = new string[] { } }, (_) => "ok");
            }).ConfigureAwait(false);
        }

        private static async Task<HttpMcpTestServerFixture> StartWithConfirmToolAsync(CancellationToken token)
        {
            return await HttpMcpTestServerFixture.StartAsync(token, server =>
            {
                object schema = new
                {
                    type = "object",
                    properties = new { confirmed = new { type = "boolean" } },
                    required = new string[] { }
                };

                server.RegisterTool("confirm", "Requires confirmation", schema, (args) =>
                {
                    ConfirmArguments? parsed = args == null ? null : JsonSerializer.Deserialize<ConfirmArguments>(JsonSerializer.Serialize(args));
                    if (parsed != null && parsed.Confirmed)
                    {
                        return (object)"confirmed";
                    }

                    return new McpInputRequiredResult
                    {
                        InputRequests = new Dictionary<string, McpInputRequest>
                        {
                            { "confirm", new McpInputRequest { Method = "elicitation/create", Params = new { message = "Proceed?" } } }
                        },
                        RequestState = "state-1"
                    };
                });
            }).ConfigureAwait(false);
        }

        private static Dictionary<string, object?> Meta(string version)
        {
            return new Dictionary<string, object?>
            {
                { "io.modelcontextprotocol/protocolVersion", version },
                { "io.modelcontextprotocol/clientInfo", new { name = "test-client", version = "1.0.0" } },
                { "io.modelcontextprotocol/clientCapabilities", new { } }
            };
        }

        private static async Task<HttpResponseMessage> SendStatelessAsync(
            HttpMcpTestServerFixture fixture,
            string bodyMethod,
            object? id,
            object? parameters,
            string? protocolHeader,
            string? methodHeader,
            string? nameHeader,
            CancellationToken token)
        {
            Dictionary<string, object?> body = new Dictionary<string, object?>
            {
                { "jsonrpc", "2.0" },
                { "method", bodyMethod },
                { "params", parameters }
            };

            if (id != null)
            {
                body["id"] = id;
            }

            HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            message.Headers.Accept.ParseAdd("application/json");
            message.Headers.Accept.ParseAdd("text/event-stream");

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

        private static async Task<string> BodyAsync(HttpResponseMessage response, CancellationToken token)
        {
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "version", "stateless" });
        }

        private sealed class ConfirmArguments
        {
            [JsonPropertyName("confirmed")]
            public bool Confirmed { get; set; }
        }
    }
}
