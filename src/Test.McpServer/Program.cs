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

            Console.Error.WriteLine("=== MCP Test Server Starting ===");
            Console.Error.WriteLine("Reading from stdin, writing to stdout");
            Console.Error.WriteLine("Press Ctrl+C or close stdin to stop");
            Console.Error.WriteLine();

            // The test host enables the diagnostic echo and getTime tools so the stdio suites can exercise them.
            McpServer server = new McpServer(includeDiagnosticTools: true);

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

            // Register an async method with cancellation support
            server.RegisterMethod("asyncLookup", async (RpcParameters? args, CancellationToken token) =>
            {
                string key = args?.GetString("key") ?? "default";
                await Task.Delay(100, token);
                return (object)$"value-for-{key}";
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
