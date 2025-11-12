namespace McpHttpClientProgram
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Usage: Test.McpHttpClient <port>");
                return;
            }

            if (!int.TryParse(args[0], out int port))
            {
                Console.WriteLine("Invalid port number");
                return;
            }

            Console.WriteLine("=== MCP HTTP Client ===");
            Console.WriteLine();

            McpHttpClient client = new McpHttpClient();

            // Subscribe to logs
            client.Log += (sender, message) => Console.WriteLine(message);

            // Subscribe to notifications from server
            client.NotificationReceived += (sender, notification) =>
            {
                Console.WriteLine($"\n[Notification] {notification.Method}: {notification.Params}");
                Console.Write("> ");
            };

            Console.WriteLine("Available commands:");
            Console.WriteLine("  connect [url]           - Connect to HTTP server");
            Console.WriteLine("  disconnect              - Disconnect from server");
            Console.WriteLine("  startsse                - Start SSE connection for notifications");
            Console.WriteLine("  stopsse                 - Stop SSE connection");
            Console.WriteLine("  call <method> [json]    - Call RPC method");
            Console.WriteLine("  status                  - Show connection status");
            Console.WriteLine("  session                 - Show session ID");
            Console.WriteLine("  examples                - Show example commands");
            Console.WriteLine("  exit                    - Exit program");
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
                        case "connect":
                            string url = $"http://localhost:{port}";

                            if (parts.Length > 1)
                            {
                                url = parts[1];
                            }

                            if (await client.ConnectAsync(url).ConfigureAwait(false))
                            {
                                Console.WriteLine("Connected successfully!");
                                Console.WriteLine($"Session ID: {client.SessionId}");
                            }
                            else
                            {
                                Console.WriteLine("Connection failed");
                            }
                            break;

                        case "disconnect":
                            client.Disconnect();
                            break;

                        case "startsse":
                            if (!client.IsConnected)
                            {
                                Console.WriteLine("Not connected. Use 'connect' first.");
                                break;
                            }

                            if (await client.StartSseAsync().ConfigureAwait(false))
                            {
                                Console.WriteLine("SSE connection started");
                            }
                            else
                            {
                                Console.WriteLine("Failed to start SSE");
                            }
                            break;

                        case "stopsse":
                            client.StopSse();
                            Console.WriteLine("SSE connection stopped");
                            break;

                        case "call":
                            if (!client.IsConnected)
                            {
                                Console.WriteLine("Not connected. Use 'connect' first.");
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

                        case "status":
                            Console.WriteLine($"Connection status: {(client.IsConnected ? "Connected" : "Disconnected")}");
                            Console.WriteLine($"SSE status: {(client.IsSseConnected ? "Connected" : "Disconnected")}");
                            break;

                        case "session":
                            if (String.IsNullOrEmpty(client.SessionId))
                            {
                                Console.WriteLine("No session established");
                            }
                            else
                            {
                                Console.WriteLine($"Session ID: {client.SessionId}");
                            }
                            break;

                        case "examples":
                            Console.WriteLine("\nExample commands:");
                            Console.WriteLine("  connect                             - Connect to localhost on default port");
                            Console.WriteLine("  connect http://192.168.1.100:8080   - Connect to specific URL");
                            Console.WriteLine("  startsse                            - Start receiving notifications");
                            Console.WriteLine("  call ping                           - Simple call without parameters");
                            Console.WriteLine("  call echo {\"message\":\"Hello\"}     - Call with parameters");
                            Console.WriteLine("  call add {\"a\":5,\"b\":3}            - Call add method");
                            Console.WriteLine("  call multiply {\"x\":4,\"y\":7}       - Call multiply method");
                            Console.WriteLine("  call greet {\"name\":\"Alice\"}       - Call greet method");
                            Console.WriteLine("  call getTime                        - Get server time");
                            Console.WriteLine("  call getSessions                    - Get list of active sessions");
                            break;

                        case "exit":
                        case "quit":
                            client.Dispose();
                            running = false;
                            break;

                        case "help":
                            Console.WriteLine("Available commands:");
                            Console.WriteLine("  connect [url]           - Connect to HTTP server");
                            Console.WriteLine("  disconnect              - Disconnect from server");
                            Console.WriteLine("  startsse                - Start SSE connection for notifications");
                            Console.WriteLine("  stopsse                 - Stop SSE connection");
                            Console.WriteLine("  call <method> [json]    - Call RPC method");
                            Console.WriteLine("  status                  - Show connection status");
                            Console.WriteLine("  session                 - Show session ID");
                            Console.WriteLine("  examples                - Show example commands");
                            Console.WriteLine("  exit                    - Exit program");
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
