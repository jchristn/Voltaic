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
            Console.Error.WriteLine("=== MCP Test Server Starting ===");
            Console.Error.WriteLine("Reading from stdin, writing to stdout");
            Console.Error.WriteLine("Press Ctrl+C or close stdin to stop");
            Console.Error.WriteLine();

            McpServer server = new McpServer();

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

                    if (args.HasValue)
                    {
                        if (args.Value.TryGetProperty("a", out JsonElement aProp))
                            a = aProp.GetDouble();
                        if (args.Value.TryGetProperty("b", out JsonElement bProp))
                            b = bProp.GetDouble();
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

                    if (args.HasValue)
                    {
                        if (args.Value.TryGetProperty("x", out JsonElement xProp))
                            x = xProp.GetDouble();
                        if (args.Value.TryGetProperty("y", out JsonElement yProp))
                            y = yProp.GetDouble();
                    }
                    return x * y;
                });

            // Register an async method with cancellation support
            server.RegisterMethod("asyncLookup", async (JsonElement? args, CancellationToken token) =>
            {
                string key = "default";
                if (args.HasValue && args.Value.TryGetProperty("key", out JsonElement keyProp))
                    key = keyProp.GetString() ?? "default";
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
                    string topic = args.HasValue && args.Value.TryGetProperty("topic", out JsonElement topicElement)
                        ? topicElement.GetString() ?? "unknown"
                        : "unknown";

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
    }
}
