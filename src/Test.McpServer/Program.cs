namespace McpServerProgram
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
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

            // Register additional custom methods
            server.RegisterMethod("add", (args) =>
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

            server.RegisterMethod("multiply", (args) =>
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
