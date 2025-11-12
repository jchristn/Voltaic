namespace ServerProgram
{
    using System;
    using System.Net;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Voltaic.JsonRpc;
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Usage: ServerProgram <port>");
                return;
            }

            if (!int.TryParse(args[0], out int port))
            {
                Console.WriteLine("Invalid port number");
                return;
            }

            Console.WriteLine("=== JSON-RPC 2.0 Server ===");
            Console.WriteLine($"Starting server on port {port}...");

            JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, port);

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

            server.RegisterMethod("multiply", (args) =>
            {
                double x = 0, y = 0;
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
            Console.WriteLine("  list        - List connected clients");
            Console.WriteLine("  kick <id>   - Kick a client");
            Console.WriteLine("  broadcast <method> [message] - Send notification to all clients");
            Console.WriteLine("  methods     - List available RPC methods");
            Console.WriteLine("  stop        - Stop the server");
            Console.WriteLine("  exit        - Exit program");
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
                        List<string> clients = server.GetConnectedClients();
                        if (clients.Count == 0)
                        {
                            Console.WriteLine("No clients connected");
                        }
                        else
                        {
                            Console.WriteLine("Connected clients:");
                            foreach (string client in clients)
                            {
                                Console.WriteLine($"  - {client}");
                            }
                        }
                        break;

                    case "kick":
                        if (parts.Length < 2)
                        {
                            Console.WriteLine("Usage: kick <client_id>");
                        }
                        else
                        {
                            if (server.KickClient(parts[1]))
                            {
                                Console.WriteLine($"Kicked client {parts[1]}");
                            }
                            else
                            {
                                Console.WriteLine($"Client {parts[1]} not found");
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

                            await server.BroadcastNotificationAsync(method, param);
                            Console.WriteLine($"Broadcast sent: {method}");
                        }
                        break;

                    case "methods":
                        Console.WriteLine("Available RPC methods:");
                        Console.WriteLine("  - echo(message)     : Echoes back the message");
                        Console.WriteLine("  - getTime()         : Returns current UTC time");
                        Console.WriteLine("  - add(a, b)         : Adds two numbers");
                        Console.WriteLine("  - multiply(x, y)    : Multiplies two numbers");
                        Console.WriteLine("  - greet(name)       : Returns a greeting");
                        Console.WriteLine("  - getClients()      : Returns list of connected clients");
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

                    default:
                        Console.WriteLine($"Unknown command: {command}");
                        Console.WriteLine("Type 'help' to see available commands");
                        break;

                    case "help":
                        Console.WriteLine("Available commands:");
                        Console.WriteLine("  list        - List connected clients");
                        Console.WriteLine("  kick <id>   - Kick a client");
                        Console.WriteLine("  broadcast <method> [message] - Send notification to all clients");
                        Console.WriteLine("  methods     - List available RPC methods");
                        Console.WriteLine("  stop        - Stop the server");
                        Console.WriteLine("  exit        - Exit program");
                        break;
                }
            }

            Console.WriteLine("Goodbye!");
        }
    }
}