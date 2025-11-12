namespace ClientProgram
{
    using Voltaic.JsonRpc;
    using System;
    using System.Net.Sockets;
    using System.Text.Json;
    using System.Threading.Tasks;

    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Usage: Test.JsonRpcClient <port>");
                return;
            }

            if (!int.TryParse(args[0], out int port))
            {
                Console.WriteLine("Invalid port number");
                return;
            }

            Console.WriteLine("=== JSON-RPC 2.0 Client ===");
            Console.WriteLine($"Default port: {port}");

            JsonRpcClient client = new JsonRpcClient();

            // Subscribe to log events
            client.Log += (sender, message) => Console.WriteLine(message);

            // Subscribe to notifications from server
            client.NotificationReceived += (sender, notification) =>
            {
                Console.WriteLine($"\n[Notification] {notification.Method}: {notification.Params}");
                Console.Write("> ");
            };

            Console.WriteLine("\nAvailable commands:");
            Console.WriteLine("  connect [host] [port] - Connect to server (default: localhost and specified port)");
            Console.WriteLine("  disconnect            - Disconnect from server");
            Console.WriteLine("  call <method> [json]  - Call RPC method with optional JSON params");
            Console.WriteLine("  notify <method> [json]- Send notification (no response expected)");
            Console.WriteLine("  status                - Show connection status");
            Console.WriteLine("  examples              - Show example commands");
            Console.WriteLine("  exit                  - Exit program");
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
                            string host = "localhost";
                            int connectPort = port;

                            if (parts.Length > 1)
                            {
                                string[] connectParts = parts[1].Split(' ');
                                if (connectParts.Length > 0)
                                    host = connectParts[0];
                                if (connectParts.Length > 1 && int.TryParse(connectParts[1], out int p))
                                    connectPort = p;
                            }

                            if (await client.ConnectAsync(host, connectPort))
                            {
                                Console.WriteLine("Connected successfully!");
                            }
                            else
                            {
                                Console.WriteLine("Connection failed");
                            }
                            break;

                        case "disconnect":
                            client.Disconnect();
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
                                object? result = await client.CallAsync(method, callParams);
                                Console.WriteLine($"Result: {result}");
                            }
                            catch (Exception callEx)
                            {
                                Console.WriteLine($"Error: {callEx.Message}");
                            }
                            break;

                        case "notify":
                            if (!client.IsConnected)
                            {
                                Console.WriteLine("Not connected. Use 'connect' first.");
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

                            await client.NotifyAsync(notifyMethod, notifyParams);
                            Console.WriteLine("Notification sent");
                            break;

                        case "status":
                            Console.WriteLine($"Connection status: {(client.IsConnected ? "Connected" : "Disconnected")}");
                            break;

                        case "examples":
                            Console.WriteLine("\nExample commands:");
                            Console.WriteLine("  connect                            - Connect to localhost on default port");
                            Console.WriteLine("  connect 192.168.1.100 8080         - Connect to specific host and port");
                            Console.WriteLine("  call ping                          - Simple call without parameters");
                            Console.WriteLine("  call echo {\"message\":\"Hello\"}  - Call with parameters");
                            Console.WriteLine("  call add {\"a\":5,\"b\":3}         - Call add method");
                            Console.WriteLine("  call multiply {\"x\":4,\"y\":7}    - Call multiply method");
                            Console.WriteLine("  call greet {\"name\":\"Alice\"}    - Call greet method");
                            Console.WriteLine("  call getTime                       - Get server time");
                            Console.WriteLine("  call getClients                    - Get list of connected clients");
                            Console.WriteLine("  notify log {\"message\":\"Test\"}  - Send notification");
                            break;

                        case "exit":
                        case "quit":
                            client.Dispose();
                            running = false;
                            break;

                        case "help":
                            Console.WriteLine("Available commands:");
                            Console.WriteLine("  connect [host] [port]   - Connect to server");
                            Console.WriteLine("  disconnect              - Disconnect from server");
                            Console.WriteLine("  call <method> [json]    - Call RPC method");
                            Console.WriteLine("  notify <method> [json]  - Send notification");
                            Console.WriteLine("  status                  - Show connection status");
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
