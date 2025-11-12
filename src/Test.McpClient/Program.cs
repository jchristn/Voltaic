namespace McpClientProgram
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== MCP Test Client ===");
            Console.WriteLine();

            McpClient client = new McpClient();

            // Subscribe to logs
            client.Log += (sender, message) => Console.WriteLine(message);

            // Subscribe to notifications from server
            client.NotificationReceived += (sender, notification) =>
            {
                Console.WriteLine($"\n[Notification] {notification.Method}: {notification.Params}");
                Console.Write("> ");
            };

            Console.WriteLine("Available commands:");
            Console.WriteLine("  launch <executable> [args...] - Launch MCP server subprocess");
            Console.WriteLine("  call <method> [json]          - Call RPC method");
            Console.WriteLine("  notify <method> [json]        - Send notification");
            Console.WriteLine("  status                        - Show connection status");
            Console.WriteLine("  shutdown                      - Shutdown server gracefully");
            Console.WriteLine("  exit                          - Exit program");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  launch dotnet run --project src/Test.McpServer/Test.McpServer.csproj");
            Console.WriteLine("  call ping");
            Console.WriteLine("  call echo {\"message\":\"Hello\"}");
            Console.WriteLine("  call add {\"a\":5,\"b\":3}");
            Console.WriteLine();

            bool running = true;
            while (running)
            {
                Console.Write("> ");
                string? input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                string command = parts[0].ToLower();

                try
                {
                    switch (command)
                    {
                        case "launch":
                            if (parts.Length < 2)
                            {
                                Console.WriteLine("Usage: launch <executable> [args...]");
                                break;
                            }

                            string[] launchParts = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            string executable = launchParts[0];
                            string[] serverArgs = launchParts.Length > 1
                                ? launchParts[1..]
                                : Array.Empty<string>();

                            if (await client.LaunchServerAsync(executable, serverArgs).ConfigureAwait(false))
                            {
                                Console.WriteLine("Server launched successfully!");
                            }
                            else
                            {
                                Console.WriteLine("Failed to launch server");
                            }
                            break;

                        case "call":
                            if (!client.IsConnected)
                            {
                                Console.WriteLine("Not connected. Use 'launch' first.");
                                break;
                            }

                            if (parts.Length < 2)
                            {
                                Console.WriteLine("Usage: call <method> [json_params]");
                                break;
                            }

                            string[] callParts = parts[1].Split(' ', 2);
                            string method = callParts[0];
                            object? callParams = null;

                            if (callParts.Length > 1)
                            {
                                try
                                {
                                    callParams = JsonSerializer.Deserialize<JsonElement>(callParts[1]);
                                }
                                catch
                                {
                                    Console.WriteLine("Invalid JSON parameters");
                                    break;
                                }
                            }

                            try
                            {
                                object? result = await client.CallAsync<object>(method, callParams).ConfigureAwait(false);
                                Console.WriteLine($"Result: {JsonSerializer.Serialize(result)}");
                            }
                            catch (Exception callEx)
                            {
                                Console.WriteLine($"Error: {callEx.Message}");
                            }
                            break;

                        case "notify":
                            if (!client.IsConnected)
                            {
                                Console.WriteLine("Not connected. Use 'launch' first.");
                                break;
                            }

                            if (parts.Length < 2)
                            {
                                Console.WriteLine("Usage: notify <method> [json_params]");
                                break;
                            }

                            string[] notifyParts = parts[1].Split(' ', 2);
                            string notifyMethod = notifyParts[0];
                            object? notifyParams = null;

                            if (notifyParts.Length > 1)
                            {
                                try
                                {
                                    notifyParams = JsonSerializer.Deserialize<JsonElement>(notifyParts[1]);
                                }
                                catch
                                {
                                    Console.WriteLine("Invalid JSON parameters");
                                    break;
                                }
                            }

                            await client.NotifyAsync(notifyMethod, notifyParams).ConfigureAwait(false);
                            Console.WriteLine("Notification sent");
                            break;

                        case "status":
                            Console.WriteLine($"Connection status: {(client.IsConnected ? "Connected" : "Disconnected")}");
                            break;

                        case "shutdown":
                            client.Shutdown();
                            Console.WriteLine("Server shutdown requested");
                            break;

                        case "exit":
                        case "quit":
                            client.Dispose();
                            running = false;
                            break;

                        case "help":
                            Console.WriteLine("Available commands:");
                            Console.WriteLine("  launch <executable> [args...] - Launch MCP server subprocess");
                            Console.WriteLine("  call <method> [json]          - Call RPC method");
                            Console.WriteLine("  notify <method> [json]        - Send notification");
                            Console.WriteLine("  status                        - Show connection status");
                            Console.WriteLine("  shutdown                      - Shutdown server gracefully");
                            Console.WriteLine("  exit                          - Exit program");
                            break;

                        default:
                            Console.WriteLine($"Unknown command: {command}");
                            Console.WriteLine("Type 'help' to see available commands");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }

            Console.WriteLine("Goodbye!");
        }
    }
}
