namespace McpHttpServerProgram
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Usage: Test.McpHttpServer <port>");
                return;
            }

            if (!int.TryParse(args[0], out int port))
            {
                Console.WriteLine("Invalid port number");
                return;
            }

            Console.WriteLine("=== MCP HTTP Server ===");
            Console.WriteLine($"Starting server on port {port}...");

            McpHttpServer server = new McpHttpServer(port);

            // Subscribe to log events
            server.Log += (sender, message) => Console.WriteLine(message);

            // Register custom methods
            server.RegisterMethod("greet", (args) =>
            {
                string name = "Anonymous";
                if (args.HasValue && args.Value.TryGetProperty("name", out JsonElement nameProp))
                    name = nameProp.GetString() ?? "Anonymous";
                return $"Hello, {name}!";
            });

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

            // Start server in background
            Task serverTask = Task.Run(async () => await server.StartAsync());

            Console.WriteLine("\nAvailable commands:");
            Console.WriteLine("  list          - List active sessions");
            Console.WriteLine("  remove <id>   - Remove a session");
            Console.WriteLine("  broadcast <method> [message]       - Broadcast notification to all sessions");
            Console.WriteLine("  send <session> <method> [message]  - Send notification to specific session");
            Console.WriteLine("  methods       - List available RPC methods");
            Console.WriteLine("  stop          - Stop the server");
            Console.WriteLine("  exit          - Exit program");
            Console.WriteLine();

            bool running = true;
            while (running)
            {
                Console.Write("> ");
                string? input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string command = parts[0].ToLower();

                switch (command)
                {
                    case "list":
                        List<string> sessions = server.GetActiveSessions();
                        if (sessions.Count == 0)
                        {
                            Console.WriteLine("No active sessions");
                        }
                        else
                        {
                            Console.WriteLine("Active sessions:");
                            foreach (string session in sessions)
                            {
                                Console.WriteLine($"  - {session}");
                            }
                        }
                        break;

                    case "remove":
                        if (parts.Length < 2)
                        {
                            Console.WriteLine("Usage: remove <session_id>");
                        }
                        else
                        {
                            if (server.RemoveSession(parts[1]))
                            {
                                Console.WriteLine($"Removed session {parts[1]}");
                            }
                            else
                            {
                                Console.WriteLine($"Session {parts[1]} not found");
                            }
                        }
                        break;

                    case "broadcast":
                        if (parts.Length < 2)
                        {
                            Console.WriteLine("Usage: broadcast <method> [message]");
                        }
                        else
                        {
                            string method = parts[1];
                            object? param = null;

                            if (parts.Length > 2)
                            {
                                string message = string.Join(" ", parts[2..]);
                                param = new { message = message };
                            }

                            server.BroadcastNotification(method, param);
                            Console.WriteLine($"Broadcast sent: {method}");
                        }
                        break;

                    case "send":
                        if (parts.Length < 3)
                        {
                            Console.WriteLine("Usage: send <session_id> <method> [message]");
                        }
                        else
                        {
                            string sessionId = parts[1];
                            string method = parts[2];
                            object? param = null;

                            if (parts.Length > 3)
                            {
                                string message = string.Join(" ", parts[3..]);
                                param = new { message = message };
                            }

                            if (server.SendNotificationToSession(sessionId, method, param))
                            {
                                Console.WriteLine($"Notification sent to session {sessionId}");
                            }
                            else
                            {
                                Console.WriteLine($"Session {sessionId} not found");
                            }
                        }
                        break;

                    case "methods":
                        Console.WriteLine("Available RPC methods:");
                        Console.WriteLine("  - echo(message)     : Echoes back the message");
                        Console.WriteLine("  - getTime()         : Returns current UTC time");
                        Console.WriteLine("  - add(a, b)         : Adds two numbers");
                        Console.WriteLine("  - multiply(x, y)    : Multiplies two numbers");
                        Console.WriteLine("  - greet(name)       : Returns a greeting");
                        Console.WriteLine("  - getSessions()     : Returns list of active sessions");
                        Console.WriteLine("  - ping()            : Returns 'pong'");
                        break;

                    case "stop":
                        server.Stop();
                        Console.WriteLine("Server stopped");
                        break;

                    case "exit":
                    case "quit":
                        server.Stop();
                        running = false;
                        break;

                    case "help":
                        Console.WriteLine("Available commands:");
                        Console.WriteLine("  list          - List active sessions");
                        Console.WriteLine("  remove <id>   - Remove a session");
                        Console.WriteLine("  broadcast <method> [message]       - Broadcast notification to all sessions");
                        Console.WriteLine("  send <session> <method> [message]  - Send notification to specific session");
                        Console.WriteLine("  methods       - List available RPC methods");
                        Console.WriteLine("  stop          - Stop the server");
                        Console.WriteLine("  exit          - Exit program");
                        break;

                    default:
                        Console.WriteLine($"Unknown command: {command}");
                        Console.WriteLine("Type 'help' to see available commands");
                        break;
                }
            }

            Console.WriteLine("Goodbye!");
        }
    }
}
