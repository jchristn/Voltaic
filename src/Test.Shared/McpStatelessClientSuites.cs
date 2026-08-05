namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Coverage for the stateless (2026-07-28) client: discovery, typed tool calls, and the
    /// Multi Round-Trip Requests retry loop, exercised against a live stateless server.
    /// </summary>
    public static class McpStatelessClientSuites
    {
        public static TestSuiteDescriptor StatelessClient()
        {
            const string suiteId = "McpVersion.StatelessClient";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Stateless Client (2026-07-28)",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ClientDiscovers", "The client connects statelessly and discovers supported versions", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartServerAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();

                        bool connected = await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", token: ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Stateless connect should succeed.");
                        TestAssert.True(client.IsStateless, "Client should report stateless mode.");

                        McpDiscoverResult discover = await client.DiscoverAsync(ct).ConfigureAwait(false);
                        TestAssert.True(discover.SupportedVersions.Contains("2026-07-28"), "Discovery should advertise the stateless revision.");
                    }),

                    Case(suiteId, "ClientCallsTool", "The client invokes a tool statelessly and reads the result", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartServerAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", token: ct).ConfigureAwait(false);

                        JsonRpcResponse response = await client.CallToolStatelessAsync("noop", null, null, 3, ct).ConfigureAwait(false);
                        TestAssert.Null(response.Error, "The tool call should not error.");

                        McpToolCallResult? result = Deserialize<McpToolCallResult>(response.Result);
                        TestAssert.NotNull(result, "Tool result should deserialize.");
                        TestAssert.True(result!.Content.Count > 0, "Tool result should carry content.");
                    }),

                    Case(suiteId, "ClientMrtrRetry", "The client satisfies an input-required result and receives the final result", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartConfirmServerAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", token: ct).ConfigureAwait(false);

                        bool callbackInvoked = false;
                        JsonRpcResponse response = await client.CallToolStatelessAsync(
                            "confirm",
                            null,
                            inputRequired =>
                            {
                                callbackInvoked = true;
                                TestAssert.NotNull(inputRequired.InputRequests, "The client should receive input requests.");
                                return new Dictionary<string, object?>
                                {
                                    { "confirm", new { action = "accept", content = new { name = "octocat" } } }
                                };
                            },
                            3,
                            ct).ConfigureAwait(false);

                        TestAssert.True(callbackInvoked, "The input-response callback should be invoked.");
                        TestAssert.Null(response.Error, "The final response should not error.");
                        TestAssert.Null(ResultTypeOf(response.Result), "The final response should be a normal tool result.");
                        McpToolCallResult? result = Deserialize<McpToolCallResult>(response.Result);
                        TestAssert.NotNull(result, "Final result should deserialize.");
                        TestAssert.True(result!.Content.Count > 0, "Final result should carry content.");
                    }),
                });
        }

        private static async Task<HttpMcpTestServerFixture> StartServerAsync(CancellationToken token)
        {
            return await HttpMcpTestServerFixture.StartAsync(token, server =>
            {
                server.AdvertiseTasksExtension = true;
                server.RegisterTool("noop", "Does nothing", new { type = "object", properties = new { }, required = new string[] { } }, (_) => "ok");
            }).ConfigureAwait(false);
        }

        private static async Task<HttpMcpTestServerFixture> StartConfirmServerAsync(CancellationToken token)
        {
            return await HttpMcpTestServerFixture.StartAsync(token, server =>
            {
                int[] calls = new int[1];
                object schema = new { type = "object", properties = new { }, required = new string[] { } };

                server.RegisterTool("confirm", "Requires one round of input", schema, (_) =>
                {
                    calls[0] = calls[0] + 1;
                    if (calls[0] == 1)
                    {
                        return new McpInputRequiredResult
                        {
                            InputRequests = new Dictionary<string, McpInputRequest>
                            {
                                { "confirm", new McpInputRequest { Method = "elicitation/create", Params = new { message = "Proceed?" } } }
                            },
                            RequestState = "state-1"
                        };
                    }

                    return (object)"done";
                });
            }).ConfigureAwait(false);
        }

        private static T? Deserialize<T>(object? result)
        {
            if (result == null)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result));
        }

        private static string? ResultTypeOf(object? result)
        {
            if (result == null)
            {
                return null;
            }

            ResultTypeProbe? probe = JsonSerializer.Deserialize<ResultTypeProbe>(JsonSerializer.Serialize(result));
            return probe?.ResultType;
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "version", "stateless", "client" });
        }

        private sealed class ResultTypeProbe
        {
            [JsonPropertyName("resultType")]
            public string? ResultType { get; set; }
        }
    }
}
