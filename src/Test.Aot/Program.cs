namespace Test.Aot
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.A2A;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Native AOT smoke test: runs real round-trips on every Voltaic transport with reflection-based JSON disabled.
    /// Exit code 0 means every check passed. Run with <c>--stdio-server</c> to act as the stdio MCP server the stdio
    /// scenario launches.
    /// </summary>
    internal static class Program
    {
        private static int _Failures = 0;
        private static int _Passes = 0;

        private static async Task<int> Main(string[] args)
        {
            VoltaicJson.AddTypeInfoResolver(AotJsonContext.Default);

            if (args.Length > 0 && args[0] == "--stdio-server")
            {
                McpServer stdioServer = new McpServer(includeDiagnosticTools: true);
                McpTools.Register(stdioServer.RegisterTool, stdioServer.RegisterResource, stdioServer.RegisterPrompt);
                await stdioServer.RunAsync().ConfigureAwait(false);
                return 0;
            }

            Console.WriteLine("Voltaic Native AOT smoke test");
            Console.WriteLine("  Runtime:              " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Console.WriteLine("  Dynamic code:         " + System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
            Console.WriteLine("  Reflection-based JSON: " + JsonSerializer.IsReflectionEnabledByDefault);
            Console.WriteLine();

            Stopwatch total = Stopwatch.StartNew();
            await Scenario("JSON-RPC over TCP", JsonRpcTcpAsync).ConfigureAwait(false);
            await Scenario("MCP over stdio", McpStdioAsync).ConfigureAwait(false);
            await Scenario("MCP over TCP", McpTcpAsync).ConfigureAwait(false);
            await Scenario("MCP over WebSocket", McpWebSocketAsync).ConfigureAwait(false);
            await Scenario("MCP over Streamable HTTP", McpHttpAsync).ConfigureAwait(false);
            await Scenario("MCP stateless HTTP (2026-07-28)", McpStatelessAsync).ConfigureAwait(false);
            await Scenario("A2A JSON-RPC, HTTP+JSON, and gRPC", A2AAsync).ConfigureAwait(false);
            await Scenario("Application JSON options", ApplicationOptionsAsync).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"{_Passes} passed, {_Failures} failed in {total.ElapsedMilliseconds} ms");
            return _Failures == 0 ? 0 : 1;
        }

        private static async Task JsonRpcTcpAsync()
        {
            int port = FreePort();
            using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, port);
            server.RegisterMethod("add", args =>
            {
                AddArguments arguments = args?.Deserialize<AddArguments>() ?? new AddArguments();
                return new SumResult { Total = arguments.A + arguments.B };
            });
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task serverTask = server.StartAsync(stop.Token);

            using JsonRpcClient client = new JsonRpcClient();
            Check("connect", await client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false));
            SumResult sum = await client.CallAsync<SumResult>("add", new AddArguments { A = 1, B = 2 }).ConfigureAwait(false);
            Check("typed parameters and result", sum.Total == 3);
            object? raw = await client.CallAsync("add", new Dictionary<string, object?> { { "a", 4 }, { "b", 5 } }).ConfigureAwait(false);
            Check("dictionary parameters, untyped result", raw is JsonElement element && element.GetProperty("total").GetDouble() == 9);

            client.Disconnect();
            server.Stop();
            stop.Cancel();
            await serverTask.ConfigureAwait(false);
        }

        private static async Task McpStdioAsync()
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
            using McpClient client = new McpClient();
            Check("launch server", await client.LaunchServerAsync(executable, new[] { "--stdio-server" }).ConfigureAwait(false));
            await McpChecksAsync(new StdioCaller(client)).ConfigureAwait(false);
            Check("automatic initialize", client.InitializeResult != null);
            client.Shutdown();
        }

        private static async Task McpTcpAsync()
        {
            int port = FreePort();
            using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, port, includeDiagnosticTools: true);
            McpTools.Register(server.RegisterTool, server.RegisterResource, server.RegisterPrompt);
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task serverTask = server.StartAsync(stop.Token);

            using McpTcpClient client = new McpTcpClient();
            Check("connect", await client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false));
            await McpChecksAsync(new TcpCaller(client)).ConfigureAwait(false);
            Check("automatic initialize", client.InitializeResult != null);

            client.Disconnect();
            server.Stop();
            stop.Cancel();
            await serverTask.ConfigureAwait(false);
        }

        private static async Task McpWebSocketAsync()
        {
            int port = FreePort();
            using McpWebsocketsServer server = new McpWebsocketsServer("localhost", port, "/mcp", includeDiagnosticTools: true);
            McpTools.Register(server.RegisterTool, server.RegisterResource, server.RegisterPrompt);
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task serverTask = server.StartAsync(stop.Token);

            using McpWebsocketsClient client = new McpWebsocketsClient();
            Check("connect", await client.ConnectAsync($"ws://localhost:{port}/mcp").ConfigureAwait(false));
            await McpChecksAsync(new WebSocketCaller(client)).ConfigureAwait(false);
            Check("automatic initialize", client.InitializeResult != null);

            client.Disconnect();
            server.Stop();
            stop.Cancel();
            await Settle(serverTask).ConfigureAwait(false);
        }

        private static async Task McpHttpAsync()
        {
            int port = FreePort();
            using McpHttpServer server = new McpHttpServer("localhost", port, includeDiagnosticTools: true);
            McpTools.Register(server.RegisterTool, server.RegisterResource, server.RegisterPrompt);
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task serverTask = server.StartAsync(stop.Token);

            using McpHttpClient client = new McpHttpClient();
            Check("connect and initialize", await client.ConnectStreamableAsync($"http://localhost:{port}").ConfigureAwait(false));
            await McpChecksAsync(new HttpCaller(client)).ConfigureAwait(false);

            client.Disconnect();
            server.Stop();
            stop.Cancel();
            await Settle(serverTask).ConfigureAwait(false);
        }

        private static async Task McpStatelessAsync()
        {
            int port = FreePort();
            using McpHttpServer server = new McpHttpServer("localhost", port, includeDiagnosticTools: true);
            McpTools.Register(server.RegisterTool, server.RegisterResource, server.RegisterPrompt);
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task serverTask = server.StartAsync(stop.Token);

            using McpHttpClient client = new McpHttpClient();
            Check("connect", await client.ConnectStatelessAsync($"http://localhost:{port}").ConfigureAwait(false));
            McpDiscoverResult discover = await client.DiscoverAsync().ConfigureAwait(false);
            Check("server/discover", discover.SupportedVersions != null && discover.SupportedVersions.Contains(McpProtocol.NewestProtocolVersion));

            JsonRpcResponse response = await client.CallToolStatelessAsync("add", new AddArguments { A = 20, B = 22 }).ConfigureAwait(false);
            Check("tools/call", response.Error == null && response.Result is JsonElement result
                && result.GetProperty("structuredContent").GetProperty("total").GetDouble() == 42
                && result.GetProperty("resultType").GetString() == "complete");

            McpListToolsResult tools = await client.CallStatelessAsync<McpListToolsResult>("tools/list").ConfigureAwait(false);
            Check("tools/list", tools.Tools.Any(tool => tool.Name == "greet"));

            client.Disconnect();
            server.Stop();
            stop.Cancel();
            await Settle(serverTask).ConfigureAwait(false);
        }

        private static async Task McpChecksAsync(McpCaller caller)
        {
            JsonElement pong = await caller.CallAsync<JsonElement>("ping", null).ConfigureAwait(false);
            Check("ping", pong.ValueKind == JsonValueKind.Object);

            McpListToolsResult tools = await caller.CallAsync<McpListToolsResult>("tools/list", null).ConfigureAwait(false);
            string[] names = tools.Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Check("tools/list", names.SequenceEqual(new[] { "add", "echo", "getTime", "greet", "status", "unregistered" }), string.Join(",", names));

            McpToolCallResult sum = await CallToolAsync(caller, "add", new AddArguments { A = 2, B = 3 }).ConfigureAwait(false);
            Check("tools/call with typed arguments and structured result",
                sum.IsError != true && sum.StructuredContent is JsonElement structured && structured.GetProperty("total").GetDouble() == 5);

            McpToolCallResult greeting = await CallToolAsync(caller, "greet", new Dictionary<string, object?> { { "name", "Ada" } }).ConfigureAwait(false);
            Check("tools/call with a plain object result", greeting.IsError != true && TextOf(greeting).Contains("Hello, Ada", StringComparison.Ordinal), TextOf(greeting));

            McpToolCallResult rejected = await CallToolAsync(caller, "greet", new Dictionary<string, object?> { { "name", "Ada1" } }).ConfigureAwait(false);
            Check("input schema pattern rejects", rejected.IsError == true);

            McpToolCallResult echo = await CallToolAsync(caller, "echo", new Dictionary<string, object?> { { "message", "native" } }).ConfigureAwait(false);
            Check("diagnostic echo tool", TextOf(echo).Contains("native", StringComparison.Ordinal), TextOf(echo));

            string unregistered;
            try
            {
                McpToolCallResult failed = await CallToolAsync(caller, "unregistered", new Dictionary<string, object?>()).ConfigureAwait(false);
                unregistered = failed.IsError == true ? "isError: " + TextOf(failed) : "succeeded: " + TextOf(failed);
            }
            catch (Exception ex)
            {
                unregistered = ex.GetType().Name + ": " + ex.Message;
            }

            McpToolCallResult after = await CallToolAsync(caller, "status", new Dictionary<string, object?>()).ConfigureAwait(false);
            Check("an unregistered result type fails only its call", (JsonSerializer.IsReflectionEnabledByDefault || !unregistered.StartsWith("succeeded", StringComparison.Ordinal)) && TextOf(after) == "ok", unregistered);
            Console.WriteLine("        unregistered type -> " + unregistered);

            McpReadResourceResult resource = await caller.CallAsync<McpReadResourceResult>("resources/read", new Dictionary<string, object?> { { "uri", "aot://status" } }).ConfigureAwait(false);
            Check("resources/read", resource.Contents.Count == 1);

            McpListPromptsResult prompts = await caller.CallAsync<McpListPromptsResult>("prompts/list", null).ConfigureAwait(false);
            Check("prompts/list", prompts.Prompts.Any(prompt => prompt.Name == "summarize"));

            McpGetPromptResult prompt = await caller.CallAsync<McpGetPromptResult>("prompts/get", new Dictionary<string, object?>
            {
                { "name", "summarize" },
                { "arguments", new Dictionary<string, string> { { "topic", "AOT" } } }
            }).ConfigureAwait(false);
            Check("prompts/get", prompt.Messages.Count == 1);

            bool unknownRejected = false;
            try
            {
                await caller.CallAsync<JsonElement>("resources/read", new Dictionary<string, object?> { { "uri", "aot://missing" } }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                unknownRejected = true;
            }

            Check("error with data for an unknown resource", unknownRejected);
        }

        private static async Task A2AAsync()
        {
            int port = FreePort();
            int grpcPort = FreePort();
            string baseUrl = $"http://localhost:{port}";
            AgentCard card = new AgentCard
            {
                Name = "AOT agent",
                Description = "Echo agent for the Native AOT smoke test.",
                Version = "1.0.0",
                SupportedInterfaces = new List<AgentInterface>
                {
                    new AgentInterface { Url = baseUrl + "/a2a", ProtocolBinding = "JSONRPC", ProtocolVersion = A2AProtocol.ProtocolVersion },
                    new AgentInterface { Url = baseUrl, ProtocolBinding = "HTTP+JSON", ProtocolVersion = A2AProtocol.ProtocolVersion },
                    new AgentInterface { Url = $"http://localhost:{grpcPort}", ProtocolBinding = "GRPC", ProtocolVersion = A2AProtocol.ProtocolVersion }
                },
                Capabilities = new AgentCapabilities { Streaming = true },
                Skills = new List<AgentSkill> { new AgentSkill { Id = "echo", Name = "Echo", Description = "Echoes text.", Tags = new List<string> { "echo" } } },
                DefaultInputModes = new List<string> { "text/plain" },
                DefaultOutputModes = new List<string> { "text/plain" }
            };

            InMemoryA2ATaskStore store = new InMemoryA2ATaskStore();
            EchoAgent agent = new EchoAgent();
            using A2AHttpServer server = new A2AHttpServer("localhost", port, card, agent, store);
            using A2AGrpcServer grpcServer = new A2AGrpcServer("localhost", grpcPort, card, agent, store);
            await server.StartAsync().ConfigureAwait(false);
            await grpcServer.StartAsync().ConfigureAwait(false);

            using A2ACardResolver resolver = new A2ACardResolver();
            AgentCard fetched = await resolver.GetAgentCardAsync(baseUrl).ConfigureAwait(false);
            Check("agent card", fetched.Name == "AOT agent" && fetched.Skills.Count == 1);

            using A2AClient client = new A2AClient(baseUrl + "/a2a");
            SendMessageResponse sent = await client.SendMessageAsync(Request("json-rpc")).ConfigureAwait(false);
            AgentTask? task = sent.Task;
            Check("JSON-RPC message/send", task != null && task.Status.State == TaskState.Completed);
            if (task != null)
            {
                AgentTask stored = await client.GetTaskAsync(new GetTaskRequest { Id = task.Id }).ConfigureAwait(false);
                Check("JSON-RPC tasks/get", stored.Id == task.Id && stored.Artifacts != null && stored.Artifacts.Count == 1);
            }

            int events = 0;
            await foreach (StreamResponse update in client.SendStreamingMessageAsync(Request("stream")).ConfigureAwait(false))
            {
                events++;
            }

            Check("JSON-RPC streaming over SSE", events >= 3, events + " events");

            using A2AHttpJsonClient rest = new A2AHttpJsonClient(baseUrl);
            SendMessageResponse restSent = await rest.SendMessageAsync(Request("rest")).ConfigureAwait(false);
            Check("HTTP+JSON message:send", restSent.Task != null && restSent.Task.Status.State == TaskState.Completed);
            ListTasksResponse listed = await rest.ListTasksAsync(new ListTasksRequest()).ConfigureAwait(false);
            Check("HTTP+JSON tasks list", listed.Tasks.Count >= 3, listed.Tasks.Count + " tasks");

            bool notFound = false;
            try
            {
                await client.GetTaskAsync(new GetTaskRequest { Id = "missing" }).ConfigureAwait(false);
            }
            catch (A2AProtocolException ex) when (ex.ErrorCode == A2AErrorCode.TaskNotFound)
            {
                notFound = true;
            }

            Check("JSON-RPC TaskNotFound error", notFound);

            using A2AGrpcClient grpc = new A2AGrpcClient($"http://localhost:{grpcPort}");
            SendMessageResponse grpcSent = await grpc.SendMessageAsync(Request("grpc")).ConfigureAwait(false);
            Check("gRPC SendMessage", grpcSent.Task != null && grpcSent.Task.Status.State == TaskState.Completed);
            if (grpcSent.Task != null)
            {
                AgentTask grpcTask = await grpc.GetTaskAsync(new GetTaskRequest { Id = grpcSent.Task.Id }).ConfigureAwait(false);
                Check("gRPC GetTask", grpcTask.Id == grpcSent.Task.Id);
            }

            server.Stop();
            grpcServer.Stop();
        }

        private static Task ApplicationOptionsAsync()
        {
            JsonSerializerOptions options = new JsonSerializerOptions { TypeInfoResolver = VoltaicJson.TypeInfoResolver };
            McpToolCallResult result = McpToolCallResult.FromStructured(new SumResult { Total = 7 });
            string json = JsonSerializer.Serialize(result, options.GetTypeInfo(typeof(McpToolCallResult)));
            Check("Voltaic models through VoltaicJson.TypeInfoResolver", json.Contains("\"structuredContent\":{\"total\":7}", StringComparison.Ordinal), json);

            bool refused = false;
            try
            {
                JsonSerializer.Serialize(new { anonymous = true }, options.GetTypeInfo(typeof(object)));
            }
            catch (NotSupportedException)
            {
                refused = true;
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            Check("unregistered types are refused, not reflected", refused || JsonSerializer.IsReflectionEnabledByDefault);
            return Task.CompletedTask;
        }

        private static SendMessageRequest Request(string text)
        {
            return new SendMessageRequest
            {
                Message = new Message
                {
                    Role = Role.User,
                    MessageId = Guid.NewGuid().ToString("N"),
                    Parts = new List<Part> { Part.FromText(text) }
                }
            };
        }

        private static Task<McpToolCallResult> CallToolAsync(McpCaller caller, string name, object arguments)
        {
            return caller.CallAsync<McpToolCallResult>("tools/call", new Dictionary<string, object?> { { "name", name }, { "arguments", arguments } });
        }

        private static string TextOf(McpToolCallResult result)
        {
            return string.Join(" ", result.Content.OfType<JsonElement>()
                .Where(block => block.TryGetProperty("text", out _))
                .Select(block => block.GetProperty("text").GetString()));
        }

        private static async Task Scenario(string name, Func<Task> body)
        {
            Console.WriteLine(name);
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                Task run = body();
                Task finished = await Task.WhenAny(run, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
                if (finished != run) throw new TimeoutException("The scenario did not finish within 60 seconds.");
                await run.ConfigureAwait(false);
                Console.WriteLine($"  done in {watch.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                _Failures++;
                Console.WriteLine($"  FAIL  scenario threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        private static void Check(string name, bool passed, string? detail = null)
        {
            if (passed)
            {
                _Passes++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                _Failures++;
                Console.WriteLine("  FAIL  " + name + (detail == null ? string.Empty : " (" + detail + ")"));
            }
        }

        private static async Task Settle(Task serverTask)
        {
            await Task.WhenAny(serverTask, Task.Delay(5000)).ConfigureAwait(false);
        }

        private static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
