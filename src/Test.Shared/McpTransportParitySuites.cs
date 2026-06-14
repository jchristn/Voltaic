namespace Test.Shared
{
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;
    public static class McpTransportParitySuites
    {
        public static TestSuiteDescriptor TcpMcpParity()
        {
            const string suiteId = "McpTcp.Parity";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP TCP Transport Parity",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "Initialize", "McpTcpServer supports MCP initialize over JSON-RPC TCP", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, ConfigureMcpTcpServer).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        JsonElement result = await client.CallAsync<JsonElement>("initialize", new { protocolVersion = McpProtocol.LatestProtocolVersion }, token: ct).ConfigureAwait(false);

                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, result.GetProperty("protocolVersion").GetString());
                        TestAssert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _), "Tools capability should be present.");
                        TestAssert.True(result.GetProperty("capabilities").TryGetProperty("resources", out _), "Resources capability should be present.");
                        TestAssert.True(result.GetProperty("capabilities").TryGetProperty("prompts", out _), "Prompts capability should be present.");
                    }),

                    Case(suiteId, "ToolsListAndCall", "McpTcpServer supports tools/list and tools/call", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, ConfigureMcpTcpServer).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        JsonElement list = await client.CallAsync<JsonElement>("tools/list", new { }, token: ct).ConfigureAwait(false);
                        JsonElement call = await client.CallAsync<JsonElement>("tools/call", new { name = "multiply", arguments = new { a = 6, b = 7 } }, token: ct).ConfigureAwait(false);

                        TestAssert.True(list.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "multiply"), "Registered tool should be listed.");
                        TestAssert.Equal(42, call.GetProperty("structuredContent").GetProperty("product").GetInt32());
                    }),

                    Case(suiteId, "ResourcesRead", "McpTcpServer supports resources/read and templates", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, ConfigureMcpTcpServer).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        JsonElement staticResource = await client.CallAsync<JsonElement>("resources/read", new { uri = "voltaic://tcp/static" }, token: ct).ConfigureAwait(false);
                        JsonElement dynamicResource = await client.CallAsync<JsonElement>("resources/read", new { uri = "voltaic://tcp/dynamic" }, token: ct).ConfigureAwait(false);

                        TestAssert.Equal("tcp static", staticResource.GetProperty("contents")[0].GetProperty("text").GetString());
                        TestAssert.Equal("dynamic:voltaic://tcp/dynamic", dynamicResource.GetProperty("contents")[0].GetProperty("text").GetString());
                    }),

                    Case(suiteId, "PromptsGet", "McpTcpServer supports prompts/get", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, ConfigureMcpTcpServer).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        JsonElement prompt = await client.CallAsync<JsonElement>("prompts/get", new { name = "tcp-prompt", arguments = new { topic = "Voltaic" } }, token: ct).ConfigureAwait(false);
                        Exception missingArg = await CaptureExceptionAsync(() => client.CallAsync<JsonElement>("prompts/get", new { name = "tcp-prompt", arguments = new { } }, token: ct)).ConfigureAwait(false);

                        TestAssert.Equal("TCP Voltaic", prompt.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
                        TestAssert.True(missingArg.Message.Contains("RPC Error -32602"), "Missing required prompt arg should map to invalid params.");
                    }),

                    Case(suiteId, "Notifications", "McpTcpServer MCP notification helpers broadcast to clients", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, ConfigureMcpTcpServer).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        McpTcpServer server = (McpTcpServer)fixture.Server;
                        await WaitUntilAsync(() => server.GetConnectedClients().Count == 1, ct).ConfigureAwait(false);

                        TaskCompletionSource<JsonRpcRequest> notification = new TaskCompletionSource<JsonRpcRequest>();
                        client.NotificationReceived += (_, request) =>
                        {
                            if (request.Method == "notifications/tools/list_changed")
                            {
                                notification.TrySetResult(request);
                            }
                        };

                        await server.NotifyToolsChangedAsync(ct).ConfigureAwait(false);
                        JsonRpcRequest received = await WaitForTaskAsync(notification.Task, ct).ConfigureAwait(false);

                        TestAssert.Equal("notifications/tools/list_changed", received.Method);
                    }),
                });
        }

        public static TestSuiteDescriptor WebSocketMcpParity()
        {
            const string suiteId = "McpWebSocket.Parity";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP WebSocket Transport Parity",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ConnectAndPing", "McpWebsocketsClient connects and calls ping", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        string pong = await client.CallAsync<string>("ping", token: ct).ConfigureAwait(false);

                        TestAssert.True(client.IsConnected, "Client should report connected.");
                        TestAssert.Equal("pong", pong);
                    }),

                    Case(suiteId, "InitializeAndTools", "McpWebsocketsServer supports initialize and tools over WebSocket", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, ConfigureMcpWebSocketServer).ConfigureAwait(false);
                        using McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        JsonElement initialize = await client.CallAsync<JsonElement>("initialize", new { protocolVersion = McpProtocol.LatestProtocolVersion }, token: ct).ConfigureAwait(false);
                        JsonElement call = await client.CallAsync<JsonElement>("tools/call", new { name = "ws-echo", arguments = new { message = "hello" } }, token: ct).ConfigureAwait(false);

                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, initialize.GetProperty("protocolVersion").GetString());
                        TestAssert.Equal("hello", call.GetProperty("structuredContent").GetProperty("message").GetString());
                    }),

                    Case(suiteId, "BroadcastNotification", "McpWebsocketsServer broadcasts notifications to clients", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        await WaitUntilAsync(() => fixture.Server.GetConnectedClients().Count == 1, ct).ConfigureAwait(false);

                        TaskCompletionSource<JsonRpcRequest> notification = new TaskCompletionSource<JsonRpcRequest>();
                        client.NotificationReceived += (_, request) =>
                        {
                            if (request.Method == "notifications/prompts/list_changed")
                            {
                                notification.TrySetResult(request);
                            }
                        };

                        await fixture.Server.NotifyPromptsChangedAsync(ct).ConfigureAwait(false);
                        JsonRpcRequest received = await WaitForTaskAsync(notification.Task, ct).ConfigureAwait(false);

                        TestAssert.Equal("notifications/prompts/list_changed", received.Method);
                    }),

                    Case(suiteId, "KickClient", "McpWebsocketsServer can kick connected clients", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpWebsocketsClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        await WaitUntilAsync(() => fixture.Server.GetConnectedClients().Count == 1, ct).ConfigureAwait(false);

                        string clientId = fixture.Server.GetConnectedClients()[0];
                        bool kicked = fixture.Server.KickClient(clientId);
                        await WaitUntilAsync(() => fixture.Server.GetConnectedClients().Count == 0, ct).ConfigureAwait(false);

                        TestAssert.True(kicked, "Known WebSocket client should be kicked.");
                    }),
                });
        }

        private static void ConfigureMcpTcpServer(McpTcpServer server)
        {
            server.RegisterTool("multiply", "Multiplies numbers", new { type = "object" }, args =>
            {
                int a = args!.Value.GetProperty("a").GetInt32();
                int b = args.Value.GetProperty("b").GetInt32();
                return McpToolCallResult.FromStructured(new { product = a * b });
            });
            server.RegisterResource("voltaic://tcp/static", "static", "text/plain", () => TextResource("voltaic://tcp/static", "tcp static"));
            server.RegisterResourceTemplate("voltaic://tcp/{name}", "tcp-template", "text/plain", uri => TextResource(uri, $"dynamic:{uri}"));
            server.RegisterPrompt(
                "tcp-prompt",
                "TCP prompt",
                new[] { new McpPromptArgument { Name = "topic", Required = true } },
                args => Prompt($"TCP {args!.Value.GetProperty("topic").GetString()}"));
        }

        private static void ConfigureMcpWebSocketServer(McpWebsocketsServer server)
        {
            server.RegisterTool("ws-echo", "Echoes a message", new { type = "object" }, args =>
            {
                return McpToolCallResult.FromStructured(new { message = args!.Value.GetProperty("message").GetString() });
            });
        }

        private static McpReadResourceResult TextResource(string uri, string text)
        {
            return new McpReadResourceResult
            {
                Contents = new List<object>
                {
                    new McpTextResourceContents { Uri = uri, MimeType = "text/plain", Text = text }
                }
            };
        }

        private static McpGetPromptResult Prompt(string text)
        {
            return new McpGetPromptResult
            {
                Messages = new List<McpPromptMessage>
                {
                    new McpPromptMessage { Role = "user", Content = new McpTextContent { Text = text } }
                }
            };
        }

        private static async Task<Exception> CaptureExceptionAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ex;
            }

            throw new InvalidOperationException("Expected an exception.");
        }

        private static async Task<T> WaitForTaskAsync<T>(Task<T> task, CancellationToken token)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(5000, token)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException("Timed out waiting for transport event.");
            }

            return await task.ConfigureAwait(false);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for transport condition.");
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "transport", "matrix" });
        }
    }
}
