namespace Sample.McpServer
{
    using System;
    using System.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic;

    class Program
    {
        private const int _HttpPort = 8100;
        private const int _TcpPort = 8101;
        private const int _WebsocketPort = 8102;
        private const string _Localhost = "127.0.0.1";

        // Tool handler functions - defined once and reused
        private static readonly Func<JsonElement?, object> _GenerateRandomNumberHandler = (args) =>
        {
            int min = 0;
            int max = 100;

            if (args.HasValue)
            {
                if (args.Value.TryGetProperty("min", out JsonElement minProp))
                    min = minProp.GetInt32();
                if (args.Value.TryGetProperty("max", out JsonElement maxProp))
                    max = maxProp.GetInt32();
            }

            Random random = new Random();
            return random.Next(min, max + 1);
        };

        private static readonly Func<JsonElement?, object> _AddHandler = (args) =>
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
        };

        private static readonly Func<JsonElement?, object> _MultiplyHandler = (args) =>
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

            return a * b;
        };

        private static readonly Func<JsonElement?, object> _DivideHandler = (args) =>
        {
            double a = 0;
            double b = 1;

            if (args.HasValue)
            {
                if (args.Value.TryGetProperty("a", out JsonElement aProp))
                    a = aProp.GetDouble();
                if (args.Value.TryGetProperty("b", out JsonElement bProp))
                    b = bProp.GetDouble();
            }

            if (b == 0)
                throw new ArgumentException("Division by zero is not allowed");

            return a / b;
        };

        private static readonly Func<JsonElement?, object> _SubtractHandler = (args) =>
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

            return a - b;
        };

        private static readonly Func<JsonElement?, object> _GreetHandler = (args) =>
        {
            string name = "World";

            if (args.HasValue)
            {
                if (args.Value.TryGetProperty("name", out JsonElement nameProp))
                    name = nameProp.GetString() ?? "World";
            }

            return $"Hello, {name}!";
        };

        private static readonly Func<JsonElement?, object> _GetTimeHandler = (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        private static readonly Func<JsonElement?, object> _EchoHandler = (args) =>
        {
            if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                return messageProp.GetString() ?? "empty";
            return "empty";
        };

        private static readonly Func<JsonElement?, object> _PingHandler = (_) => "pong";

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Voltaic MCP Sample Server ===");
            Console.WriteLine($"HTTP Server:       http://{_Localhost}:{_HttpPort}/rpc");
            Console.WriteLine($"TCP Server:        tcp://{_Localhost}:{_TcpPort}");
            Console.WriteLine($"WebSocket Server:  ws://{_Localhost}:{_WebsocketPort}/mcp");
            Console.WriteLine();
            Console.WriteLine("Press Ctrl+C to stop");
            Console.WriteLine();

            CancellationTokenSource cts = new CancellationTokenSource();

            // Create all three server types
            McpHttpServer httpServer = new McpHttpServer(_Localhost, _HttpPort, "/rpc", "/events", includeDefaultMethods: true);
            McpTcpServer tcpServer = new McpTcpServer(IPAddress.Parse(_Localhost), _TcpPort, includeDefaultMethods: true);
            McpWebsocketsServer wsServer = new McpWebsocketsServer(_Localhost, _WebsocketPort, "/mcp", includeDefaultMethods: true);

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine();
                Console.WriteLine("Stopping servers...");
                cts.Cancel();
                httpServer.Stop();
                tcpServer.Stop();
                wsServer.Stop();
            };

            // Register logging for all servers
            httpServer.Log += (sender, message) => Console.WriteLine($"[HTTP] {message}");
            tcpServer.Log += (sender, message) => Console.WriteLine($"[TCP] {message}");
            wsServer.Log += (sender, message) => Console.WriteLine($"[WS] {message}");

            // Register custom tools on HTTP server (uses RegisterTool for MCP protocol)
            RegisterHttpTools(httpServer);

            // Register custom methods on TCP server
            RegisterTcpMethods(tcpServer);

            // Register custom methods on WebSocket server
            RegisterWebSocketMethods(wsServer);

            try
            {
                // Start all servers in parallel
                Task httpTask = Task.Run(async () => await httpServer.StartAsync(cts.Token).ConfigureAwait(false));
                Task tcpTask = Task.Run(async () => await tcpServer.StartAsync(cts.Token).ConfigureAwait(false));
                Task wsTask = Task.Run(async () => await wsServer.StartAsync(cts.Token).ConfigureAwait(false));

                // Wait for cancellation
                await Task.WhenAll(httpTask, tcpTask, wsTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Servers stopped by user");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
                Environment.Exit(1);
            }
            finally
            {
                httpServer.Dispose();
                tcpServer.Dispose();
                wsServer.Dispose();
            }

            Console.WriteLine("=== Servers Stopped ===");
        }

        private static void RegisterHttpTools(McpHttpServer server)
        {
            server.RegisterTool(
                "generateRandomNumber",
                "Generates a random number between the specified minimum and maximum values (inclusive)",
                new
                {
                    type = "object",
                    properties = new
                    {
                        min = new { type = "integer", description = "Minimum value (inclusive)" },
                        max = new { type = "integer", description = "Maximum value (inclusive)" }
                    },
                    required = new[] { "min", "max" }
                },
                _GenerateRandomNumberHandler);

            server.RegisterTool(
                "add",
                "Adds two numbers together and returns the result",
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
                _AddHandler);

            server.RegisterTool(
                "multiply",
                "Multiplies two numbers together and returns the result",
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
                _MultiplyHandler);

            server.RegisterTool(
                "divide",
                "Divides the first number by the second number and returns the result",
                new
                {
                    type = "object",
                    properties = new
                    {
                        a = new { type = "number", description = "Numerator" },
                        b = new { type = "number", description = "Denominator (cannot be zero)" }
                    },
                    required = new[] { "a", "b" }
                },
                _DivideHandler);

            server.RegisterTool(
                "subtract",
                "Subtracts the second number from the first number and returns the result",
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
                _SubtractHandler);

            server.RegisterTool(
                "greet",
                "Generates a greeting message for the specified name",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Name of the person to greet" }
                    },
                    required = new[] { "name" }
                },
                _GreetHandler);

            server.RegisterTool(
                "getTime",
                "Returns the current UTC time in ISO format",
                new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                },
                _GetTimeHandler);

            server.RegisterTool(
                "echo",
                "Echoes back the provided message",
                new
                {
                    type = "object",
                    properties = new
                    {
                        message = new { type = "string", description = "The message to echo back" }
                    },
                    required = new[] { "message" }
                },
                _EchoHandler);

            server.RegisterTool(
                "ping",
                "Returns 'pong' to verify server connectivity",
                new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
                },
                _PingHandler);
        }

        private static void RegisterTcpMethods(McpTcpServer server)
        {
            // generateRandomNumber method
            server.RegisterMethod("generateRandomNumber", (args) =>
            {
                int min = 0;
                int max = 100;

                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("min", out JsonElement minProp))
                        min = minProp.GetInt32();
                    if (args.Value.TryGetProperty("max", out JsonElement maxProp))
                        max = maxProp.GetInt32();
                }

                Random random = new Random();
                return random.Next(min, max + 1);
            });

            // add method
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

            // multiply method
            server.RegisterMethod("multiply", (args) =>
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

                return a * b;
            });

            // divide method
            server.RegisterMethod("divide", (args) =>
            {
                double a = 0;
                double b = 1;

                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("a", out JsonElement aProp))
                        a = aProp.GetDouble();
                    if (args.Value.TryGetProperty("b", out JsonElement bProp))
                        b = bProp.GetDouble();
                }

                if (b == 0)
                    throw new ArgumentException("Division by zero is not allowed");

                return a / b;
            });

            // subtract method
            server.RegisterMethod("subtract", (args) =>
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

                return a - b;
            });

            // greet method
            server.RegisterMethod("greet", (args) =>
            {
                string name = "World";

                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("name", out JsonElement nameProp))
                        name = nameProp.GetString() ?? "World";
                }

                return $"Hello, {name}!";
            });

            // getTime method
            server.RegisterMethod("getTime", (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

            // echo method
            server.RegisterMethod("echo", (args) =>
            {
                if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                    return messageProp.GetString() ?? "empty";
                return "empty";
            });

            // ping method
            server.RegisterMethod("ping", (_) => "pong");
        }

        private static void RegisterWebSocketMethods(McpWebsocketsServer server)
        {
            // generateRandomNumber method
            server.RegisterMethod("generateRandomNumber", (args) =>
            {
                int min = 0;
                int max = 100;

                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("min", out JsonElement minProp))
                        min = minProp.GetInt32();
                    if (args.Value.TryGetProperty("max", out JsonElement maxProp))
                        max = maxProp.GetInt32();
                }

                Random random = new Random();
                return random.Next(min, max + 1);
            });

            // add method
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

            // multiply method
            server.RegisterMethod("multiply", (args) =>
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

                return a * b;
            });

            // divide method
            server.RegisterMethod("divide", (args) =>
            {
                double a = 0;
                double b = 1;

                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("a", out JsonElement aProp))
                        a = aProp.GetDouble();
                    if (args.Value.TryGetProperty("b", out JsonElement bProp))
                        b = bProp.GetDouble();
                }

                if (b == 0)
                    throw new ArgumentException("Division by zero is not allowed");

                return a / b;
            });

            // subtract method
            server.RegisterMethod("subtract", (args) =>
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

                return a - b;
            });

            // greet method
            server.RegisterMethod("greet", (args) =>
            {
                string name = "World";

                if (args.HasValue)
                {
                    if (args.Value.TryGetProperty("name", out JsonElement nameProp))
                        name = nameProp.GetString() ?? "World";
                }

                return $"Hello, {name}!";
            });

            // getTime method
            server.RegisterMethod("getTime", (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

            // echo method
            server.RegisterMethod("echo", (args) =>
            {
                if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                    return messageProp.GetString() ?? "empty";
                return "empty";
            });

            // ping method
            server.RegisterMethod("ping", (_) => "pong");
        }
    }
}
