namespace Test.Shared
{
    using System.Diagnostics;
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic;

    public static class McpStdioIntegrationSuites
    {
        public static TestSuiteDescriptor StdioMcpParity()
        {
            const string suiteId = "McpStdio.Integration";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Stdio Integration Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "LaunchPingAndShutdown", "McpClient launches a stdio server, calls ping, and shuts down cleanly", async ct =>
                    {
                        using McpClient client = new McpClient();
                        TaskCompletionSource<ClientConnectedEventArgs> connected = NewTcs<ClientConnectedEventArgs>();
                        TaskCompletionSource<ClientDisconnectedEventArgs> disconnected = NewTcs<ClientDisconnectedEventArgs>();
                        TaskCompletionSource<string> serverLog = NewTcs<string>();

                        client.Connected += (_, args) => connected.TrySetResult(args);
                        client.Disconnected += (_, args) => disconnected.TrySetResult(args);
                        client.Log += (_, message) =>
                        {
                            if (message.Contains("MCP Test Server Starting", StringComparison.Ordinal))
                            {
                                serverLog.TrySetResult(message);
                            }
                        };

                        await LaunchTestServerAsync(client, ct).ConfigureAwait(false);
                        string pong = await client.CallAsync<string>("ping", timeoutMs: 15000, token: ct).ConfigureAwait(false);

                        ClientConnectedEventArgs connectedArgs = await WaitForTaskAsync(connected.Task, ct).ConfigureAwait(false);
                        string log = await WaitForTaskAsync(serverLog.Task, ct).ConfigureAwait(false);

                        TestAssert.True(client.IsConnected, "Stdio client should report connected after launch.");
                        TestAssert.Equal("pong", pong);
                        TestAssert.Equal(ClientConnectionTypeEnum.Stdio, connectedArgs.ConnectionType);
                        TestAssert.True(connectedArgs.Endpoint.Contains("dotnet", StringComparison.OrdinalIgnoreCase), "Endpoint should include launched executable.");
                        TestAssert.True(log.Contains("[SERVER STDERR]", StringComparison.Ordinal), "Server diagnostics should be captured from stderr.");

                        client.Shutdown();
                        ClientDisconnectedEventArgs disconnectedArgs = await WaitForTaskAsync(disconnected.Task, ct).ConfigureAwait(false);
                        TestAssert.Equal(ClientConnectionTypeEnum.Stdio, disconnectedArgs.ConnectionType);
                        TestAssert.False(client.IsConnected, "Stdio client should be disconnected after shutdown.");
                    }),

                    Case(suiteId, "CallsRegisteredMethodsWithParameters", "McpClient calls stdio methods with parameters and typed results", async ct =>
                    {
                        using McpClient client = new McpClient();
                        await LaunchTestServerAsync(client, ct).ConfigureAwait(false);

                        string echo = await client.CallAsync<string>("echo", new { message = "test message" }, timeoutMs: 15000, token: ct).ConfigureAwait(false);
                        double add = await client.CallAsync<double>("add", new { a = 5.0, b = 3.0 }, timeoutMs: 15000, token: ct).ConfigureAwait(false);
                        double multiply = await client.CallAsync<double>("multiply", new { x = 7.0, y = 6.0 }, timeoutMs: 15000, token: ct).ConfigureAwait(false);
                        string lookup = await client.CallAsync<string>("asyncLookup", new { key = "alpha" }, timeoutMs: 15000, token: ct).ConfigureAwait(false);

                        TestAssert.Equal("test message", echo);
                        TestAssert.Equal(8.0, add);
                        TestAssert.Equal(42.0, multiply);
                        TestAssert.Equal("value-for-alpha", lookup);

                        client.Shutdown();
                    }),

                    Case(suiteId, "InitializeToolsResourcesAndPrompts", "McpClient exercises MCP initialize, tools, resources, and prompts over stdio", async ct =>
                    {
                        using McpClient client = new McpClient();
                        await LaunchTestServerAsync(client, ct).ConfigureAwait(false);

                        JsonElement initialize = await client.CallAsync<JsonElement>("initialize", new { protocolVersion = McpProtocol.LatestProtocolVersion }, timeoutMs: 15000, token: ct).ConfigureAwait(false);
                        JsonElement tools = await client.CallAsync<JsonElement>("tools/list", new { }, timeoutMs: 15000, token: ct).ConfigureAwait(false);
                        JsonElement toolCall = await client.CallAsync<JsonElement>("tools/call", new { name = "ping", arguments = new { } }, timeoutMs: 15000, token: ct).ConfigureAwait(false);
                        JsonElement staticResource = await client.CallAsync<JsonElement>("resources/read", new { uri = "voltaic://stdio/static" }, timeoutMs: 15000, token: ct).ConfigureAwait(false);
                        JsonElement dynamicResource = await client.CallAsync<JsonElement>("resources/read", new { uri = "voltaic://stdio/dynamic" }, timeoutMs: 15000, token: ct).ConfigureAwait(false);
                        JsonElement prompt = await client.CallAsync<JsonElement>("prompts/get", new { name = "stdio-prompt", arguments = new { topic = "Voltaic" } }, timeoutMs: 15000, token: ct).ConfigureAwait(false);

                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, initialize.GetProperty("protocolVersion").GetString());
                        TestAssert.Equal("Voltaic.Mcp.StdioServer", initialize.GetProperty("serverInfo").GetProperty("name").GetString());
                        TestAssert.True(initialize.GetProperty("capabilities").TryGetProperty("tools", out _), "Tools capability should be advertised.");
                        TestAssert.True(initialize.GetProperty("capabilities").TryGetProperty("resources", out _), "Resources capability should be advertised.");
                        TestAssert.True(initialize.GetProperty("capabilities").TryGetProperty("prompts", out _), "Prompts capability should be advertised.");

                        string[] toolNames = tools.GetProperty("tools")
                            .EnumerateArray()
                            .Select(tool => tool.GetProperty("name").GetString() ?? String.Empty)
                            .ToArray();
                        TestAssert.True(toolNames.Contains("ping"), "tools/list should include built-in ping.");
                        TestAssert.True(toolNames.Contains("echo"), "tools/list should include built-in echo.");
                        TestAssert.True(toolNames.Contains("add"), "tools/list should include test-server add.");
                        TestAssert.True(toolNames.Contains("multiply"), "tools/list should include test-server multiply.");

                        TestAssert.Equal("text", toolCall.GetProperty("content")[0].GetProperty("type").GetString());
                        TestAssert.True(toolCall.GetProperty("content")[0].GetProperty("text").GetString()?.Contains("pong", StringComparison.Ordinal) == true, "tools/call ping should return pong content.");
                        TestAssert.Equal("stdio static", staticResource.GetProperty("contents")[0].GetProperty("text").GetString());
                        TestAssert.Equal("dynamic:voltaic://stdio/dynamic", dynamicResource.GetProperty("contents")[0].GetProperty("text").GetString());
                        TestAssert.Equal("Stdio Voltaic", prompt.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());

                        client.Shutdown();
                    }),

                    Case(suiteId, "NotificationsAndEvents", "McpClient sends stdio notifications and raises request/response events", async ct =>
                    {
                        using McpClient client = new McpClient();
                        TaskCompletionSource<RequestSentEventArgs> initializedSent = NewTcs<RequestSentEventArgs>();
                        TaskCompletionSource<ResponseReceivedEventArgs> pingResponse = NewTcs<ResponseReceivedEventArgs>();

                        client.RequestSent += (_, args) =>
                        {
                            if (args.Method == "notifications/initialized")
                            {
                                initializedSent.TrySetResult(args);
                            }
                        };
                        client.ResponseReceived += (_, args) =>
                        {
                            if (args.Method == "ping")
                            {
                                pingResponse.TrySetResult(args);
                            }
                        };

                        await LaunchTestServerAsync(client, ct).ConfigureAwait(false);
                        await client.NotifyAsync("notifications/initialized", token: ct).ConfigureAwait(false);
                        await client.NotifyAsync("ping", token: ct).ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        string pong = await client.CallAsync<string>("ping", timeoutMs: 15000, token: ct).ConfigureAwait(false);

                        RequestSentEventArgs notificationArgs = await WaitForTaskAsync(initializedSent.Task, ct).ConfigureAwait(false);
                        ResponseReceivedEventArgs responseArgs = await WaitForTaskAsync(pingResponse.Task, ct).ConfigureAwait(false);

                        TestAssert.True(notificationArgs.IsNotification, "initialized should be sent as a notification.");
                        TestAssert.Null(notificationArgs.RequestId, "Notification should not carry a request id.");
                        TestAssert.Equal("pong", pong);
                        TestAssert.True(responseArgs.IsSuccess, "Ping response event should represent success.");

                        client.Shutdown();
                    }),

                    Case(suiteId, "ErrorsAndMalformedInput", "Stdio transport maps RPC errors and malformed JSON correctly", async ct =>
                    {
                        using McpClient client = new McpClient();
                        await LaunchTestServerAsync(client, ct).ConfigureAwait(false);

                        Exception missingMethod = await CaptureExceptionAsync(() => client.CallAsync<string>("unknownMethod", timeoutMs: 15000, token: ct)).ConfigureAwait(false);
                        Exception invalidToolParams = await CaptureExceptionAsync(() => client.CallAsync<JsonElement>("tools/call", new { }, timeoutMs: 15000, token: ct)).ConfigureAwait(false);

                        TestAssert.True(missingMethod.Message.Contains("RPC Error -32601", StringComparison.Ordinal), "Unknown methods should map to method-not-found.");
                        TestAssert.True(invalidToolParams.Message.Contains("RPC Error -32602", StringComparison.Ordinal), "Invalid MCP tool params should map to invalid-params.");

                        client.Shutdown();

                        await using RawStdioProcess raw = await RawStdioProcess.StartAsync(ct).ConfigureAwait(false);
                        await raw.Process.StandardInput.WriteLineAsync("{not-json").ConfigureAwait(false);
                        await raw.Process.StandardInput.FlushAsync().ConfigureAwait(false);

                        string responseLine = await ReadLineAsync(raw.Process.StandardOutput, ct).ConfigureAwait(false);
                        JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseLine);

                        TestAssert.NotNull(response, "Malformed JSON should produce a JSON-RPC response.");
                        TestAssert.NotNull(response!.Error, "Malformed JSON should produce an error response.");
                        TestAssert.Equal(-32700, response.Error!.Code);
                        TestAssert.Null(response.Id, "Parse errors should use null id.");
                    }),
                });
        }

        private static async Task LaunchTestServerAsync(McpClient client, CancellationToken token)
        {
            bool launched = await client.LaunchServerAsync("dotnet", TestServerArguments(), token).ConfigureAwait(false);
            if (!launched)
            {
                throw new InvalidOperationException("Failed to launch the Test.McpServer subprocess.");
            }
        }

        private static string[] TestServerArguments()
        {
            return new[]
            {
                "run",
                "--project",
                FindTestMcpServerProjectPath(),
                "--framework",
                CurrentTargetFramework(),
                "--no-restore"
            };
        }

        private static string CurrentTargetFramework()
        {
#if NET8_0
            return "net8.0";
#elif NET10_0
            return "net10.0";
#else
            return "net8.0";
#endif
        }

        private static string FindTestMcpServerProjectPath()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                DirectoryInfo? directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    string srcCandidate = Path.Combine(directory.FullName, "src", "Test.McpServer", "Test.McpServer.csproj");
                    if (File.Exists(srcCandidate))
                    {
                        return srcCandidate;
                    }

                    string siblingCandidate = Path.Combine(directory.FullName, "Test.McpServer", "Test.McpServer.csproj");
                    if (File.Exists(siblingCandidate))
                    {
                        return siblingCandidate;
                    }

                    directory = directory.Parent;
                }
            }

            throw new FileNotFoundException("Could not locate Test.McpServer.csproj.");
        }

        private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken token)
        {
            Task<string?> readTask = reader.ReadLineAsync();
            Task completed = await Task.WhenAny(readTask, Task.Delay(15000, token)).ConfigureAwait(false);
            if (completed != readTask)
            {
                throw new TimeoutException("Timed out waiting for stdio server output.");
            }

            return await readTask.ConfigureAwait(false) ?? throw new EndOfStreamException("Stdio server closed stdout.");
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
            Task completed = await Task.WhenAny(task, Task.Delay(15000, token)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException("Timed out waiting for stdio integration event.");
            }

            return await task.ConfigureAwait(false);
        }

        private static TaskCompletionSource<T> NewTcs<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "stdio", "integration", "matrix" });
        }

        private sealed class RawStdioProcess : IAsyncDisposable
        {
            private RawStdioProcess(Process process)
            {
                Process = process;
            }

            public Process Process { get; }

            public static Task<RawStdioProcess> StartAsync(CancellationToken token)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (string argument in TestServerArguments())
                {
                    startInfo.ArgumentList.Add(argument);
                }

                Process process = new Process
                {
                    StartInfo = startInfo
                };

                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException("Failed to start the raw Test.McpServer subprocess.");
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                            if (line == null)
                            {
                                break;
                            }
                        }
                    }
                    catch
                    {
                    }
                }, CancellationToken.None);

                return Task.FromResult(new RawStdioProcess(process));
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    Process.StandardInput.Close();
                }
                catch
                {
                }

                Task exited = Process.WaitForExitAsync();
                Task completed = await Task.WhenAny(exited, Task.Delay(5000)).ConfigureAwait(false);
                if (completed != exited && !Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().ConfigureAwait(false);
                }

                Process.Dispose();
            }
        }
    }
}
