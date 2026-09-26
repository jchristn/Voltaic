namespace Test.McpServer
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;
    using Voltaic.Mcp;
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--probe-client")
            {
                await ProbeClientAsync().ConfigureAwait(false);
                return;
            }

            if (args.Length > 0 && args[0] == "--raw-utf8")
            {
                await RawUtf8ServerAsync().ConfigureAwait(false);
                return;
            }

            Console.Error.WriteLine("=== MCP Test Server Starting ===");
            Console.Error.WriteLine("Reading from stdin, writing to stdout");
            Console.Error.WriteLine("Press Ctrl+C or close stdin to stop");
            Console.Error.WriteLine();

            // The test host enables the diagnostic echo and getTime tools so the stdio suites can exercise them.
            McpServer server = new McpServer(includeDiagnosticTools: true);

            // Tests shorten the ping cycle to exercise the ping-failure shutdown.
            if (int.TryParse(Environment.GetEnvironmentVariable("VOLTAIC_PING_MS"), out int pingMs))
            {
                server.PingIntervalMs = pingMs;
                server.PingTimeoutMs = Math.Max(100, pingMs);
            }

            // Subscribe to logs (goes to stderr)
            server.Log += (sender, message) =>
            {
                // Already logged to stderr by McpServer
            };

            // Register additional custom tools
            server.RegisterTool("add",
                "Adds two numbers together",
                new
                {
                    type = "object",
                    properties = new
                    {
                        a = new { type = "number", description = "First number" },
                        b = new { type = "number", description = "Second number" }
                    },
                    required = new[] { "a", "b" }
                },
                (args) =>
                {
                    double a = 0;
                    double b = 0;

                    if ((args?.HasValue ?? false))
                    {
                        a = args?.GetDouble("a") ?? a;
                        b = args?.GetDouble("b") ?? b;
                    }
                    return a + b;
                });

            server.RegisterTool("multiply",
                "Multiplies two numbers together",
                new
                {
                    type = "object",
                    properties = new
                    {
                        x = new { type = "number", description = "First number" },
                        y = new { type = "number", description = "Second number" }
                    },
                    required = new[] { "x", "y" }
                },
                (args) =>
                {
                    double x = 0;
                    double y = 0;

                    if ((args?.HasValue ?? false))
                    {
                        x = args?.GetDouble("x") ?? x;
                        y = args?.GetDouble("y") ?? y;
                    }
                    return x * y;
                });

            // A tool that writes to the console: the stdio server must keep this off stdout.
            server.RegisterTool("chatty",
                "Writes to the console and returns done",
                new { type = "object", properties = new { } },
                (args) =>
                {
                    Console.WriteLine("chatty tool output that must not reach stdout");
                    Console.Out.Flush();
                    return "done";
                });

            // Register an async method with cancellation support
            server.RegisterMethod("asyncLookup", async (RpcParameters? args, CancellationToken token) =>
            {
                string key = args?.GetString("key") ?? "default";
                await Task.Delay(100, token);
                // MCP results are JSON objects.
                return (object)new { value = $"value-for-{key}" };
            });

            server.RegisterResource(
                "voltaic://stdio/static",
                "stdio-static",
                "text/plain",
                () => new McpReadResourceResult
                {
                    Contents = new List<object>
                    {
                        new McpTextResourceContents
                        {
                            Uri = "voltaic://stdio/static",
                            MimeType = "text/plain",
                            Text = "stdio static"
                        }
                    }
                });

            server.RegisterResourceTemplate(
                "voltaic://stdio/{name}",
                "stdio-template",
                "text/plain",
                uri => new McpReadResourceResult
                {
                    Contents = new List<object>
                    {
                        new McpTextResourceContents
                        {
                            Uri = uri,
                            MimeType = "text/plain",
                            Text = $"dynamic:{uri}"
                        }
                    }
                });

            server.RegisterPrompt(
                "stdio-prompt",
                "Builds a simple stdio prompt",
                new[] { new McpPromptArgument { Name = "topic", Required = true } },
                args =>
                {
                    string topic = args?.GetString("topic") ?? "unknown";

                    return new McpGetPromptResult
                    {
                        Messages = new List<McpPromptMessage>
                        {
                            new McpPromptMessage
                            {
                                Role = "user",
                                Content = new McpTextContent { Text = $"Stdio {topic}" }
                            }
                        }
                    };
                });

            // Run server (blocks until stdin closes or Ctrl+C)
            CancellationTokenSource cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await server.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Server cancelled by user");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Server error: {ex.Message}");
                Environment.Exit(1);
            }

            Console.Error.WriteLine("=== MCP Test Server Stopped ===");
        }
    
        // A fake stdio server for client tests: it sends requests to the client (ping, a registered method, an unknown
        // method) and reflects every message the client sends back as a notifications/echo notification, so a test can
        // observe the client's responses through NotificationReceived.
        // A minimal stdio server that writes raw (unescaped) UTF-8, as servers built on other SDKs do: it answers
        // initialize, and answers "raw/text" with non-ASCII text.
        static async Task RawUtf8ServerAsync()
        {
            using System.IO.StreamReader stdin = new System.IO.StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using System.IO.Stream stdout = Console.OpenStandardOutput();
            JsonSerializerOptions raw = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string? line;
            while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                using JsonDocument request = JsonDocument.Parse(line);
                if (!request.RootElement.TryGetProperty("id", out JsonElement id) || !request.RootElement.TryGetProperty("method", out JsonElement method)) continue;
                object result = method.GetString() == "initialize"
                    ? new { protocolVersion = "2025-11-25", capabilities = new { }, serverInfo = new { name = "raw-utf8", version = "1" } }
                    : new { text = "héllo 世界" };
                string json = "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + JsonSerializer.Serialize(result, raw) + "}\n";
                byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
                await stdout.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await stdout.FlushAsync().ConfigureAwait(false);
            }
        }

        static async Task ProbeClientAsync()
        {
            string[] requests =
            {
                "{\"jsonrpc\":\"2.0\",\"id\":\"srv-ping\",\"method\":\"ping\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":\"srv-roots\",\"method\":\"roots/list\",\"params\":{}}",
                "{\"jsonrpc\":\"2.0\",\"id\":\"srv-unknown\",\"method\":\"unknown/method\"}"
            };

            string? line;
            while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Contains("\"probe/start\"", StringComparison.Ordinal))
                {
                    foreach (string request in requests)
                    {
                        await Console.Out.WriteLineAsync(request).ConfigureAwait(false);
                    }

                    await Console.Out.FlushAsync().ConfigureAwait(false);
                    continue;
                }

                string echo = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/echo\",\"params\":" + line + "}";
                await Console.Out.WriteLineAsync(echo).ConfigureAwait(false);
                await Console.Out.FlushAsync().ConfigureAwait(false);
            }
        }
    }
}
