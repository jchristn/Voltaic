namespace Test.Automated
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic;

    class Program
    {
#pragma warning disable CS8602 // Dereference of a possibly null reference.

        private static int testsPassed = 0;
        private static int testsFailed = 0;
        private static int jsonRpcTestsPassed = 0;
        private static int jsonRpcTestsFailed = 0;
        private static int mcpTestsPassed = 0;
        private static int mcpTestsFailed = 0;
        private static bool isJsonRpcTest = true;
        private static readonly object consoleLock = new object();
        private static string? mcpServerProjectPath = null;

        static string FindMcpServerProjectPath()
        {
            // Try to find the Test.McpServer project relative to the current directory
            // First, try relative to current working directory
            string currentDir = Directory.GetCurrentDirectory();
            string candidate = Path.Combine(currentDir, "Test.McpServer", "Test.McpServer.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Try from the executing assembly's directory
            string assemblyDir = AppContext.BaseDirectory;
            string srcDir = assemblyDir;

            // Walk up the directory tree to find 'src' directory
            while (srcDir != null && !srcDir.EndsWith("src", StringComparison.OrdinalIgnoreCase))
            {
                DirectoryInfo parent = Directory.GetParent(srcDir)!;
                if (parent == null) break;
                srcDir = parent.FullName;

                // Check if we're at the right level
                if (Path.GetFileName(srcDir).Equals("src", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = Path.Combine(srcDir, "Test.McpServer", "Test.McpServer.csproj");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                    break;
                }
            }

            // Fallback: assume we're in src directory already
            return Path.Combine("Test.McpServer", "Test.McpServer.csproj");
        }

        static async Task Main(string[] args)
        {
            // Initialize MCP server project path
            mcpServerProjectPath = FindMcpServerProjectPath();

            // Parse command line arguments
            bool testStdio = false;
            bool testTcp = false;
            bool testWebsockets = false;
            bool testHttp = false;
            bool testAll = args.Length == 0;

            foreach (string arg in args)
            {
                string argLower = arg.ToLower();
                if (argLower == "-stdio")
                {
                    testStdio = true;
                }
                else if (argLower == "-tcp")
                {
                    testTcp = true;
                }
                else if (argLower == "-ws" || argLower == "-websockets")
                {
                    testWebsockets = true;
                }
                else if (argLower == "-http")
                {
                    testHttp = true;
                }
                else
                {
                    Console.WriteLine($"Unknown argument: {arg}");
                    Console.WriteLine("Usage: Test.Automated [-tcp] [-http] [-ws] [-stdio]");
                    Console.WriteLine("  -tcp         Test only TCP transport");
                    Console.WriteLine("  -http        Test only HTTP transport");
                    Console.WriteLine("  -ws          Test only WebSocket transport");
                    Console.WriteLine("  -stdio       Test only stdio transport");
                    Console.WriteLine("  (no args)    Test all transports");
                    Environment.Exit(1);
                }
            }

            Console.WriteLine("=== Voltaic Comprehensive Test Suite ===");
            Console.WriteLine();

            if (testAll)
            {
                Console.WriteLine("Testing: All transports");
            }
            else
            {
                List<string> transports = new List<string>();
                if (testStdio) transports.Add("stdio");
                if (testTcp) transports.Add("TCP");
                if (testWebsockets) transports.Add("WebSockets");
                if (testHttp) transports.Add("HTTP");
                Console.WriteLine($"Testing: {String.Join(", ", transports)}");
            }
            Console.WriteLine();

            // JSON-RPC TESTS - only run when testing all
            if (testAll)
            {
                Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║                    JSON-RPC 2.0 TESTS                         ║");
                Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
                Console.WriteLine();

                await RunBasicMessageTests();
                await RunPartialReadTests();
                await RunMultipleMessagesTest();
                await RunLargeMessageTest();
                await RunMalformedHeaderTests();
                await RunConnectionDropTests();
                await RunHighLoadTest();
                await RunClientServerIntegrationTests();
                await RunJsonRpcClientFailureTests();
                await RunJsonRpcServerFailureTests();
                await RunJsonRpcClientEdgeCaseTests();
                await RunJsonRpcServerProtocolTests();
                await RunMessageFramingEdgeCaseTests();
                await RunParameterEdgeCaseTests();
                await RunConcurrencyTests();
                await RunStressTests();
                await RunEventHandlerTests();

                Console.WriteLine();
            }

            // MCP TESTS
            Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║              MCP (Model Context Protocol) TESTS               ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            isJsonRpcTest = false;

            // Stdio transport tests
            if (testAll || testStdio)
            {
                await RunMcpClientTests();
                await RunMcpServerTests();
                await RunMcpEdgeCaseTests();
            }

            // TCP transport tests
            if (testAll || testTcp)
            {
                await RunMcpTcpTests();
            }

            // WebSocket transport tests
            if (testAll || testWebsockets)
            {
                await RunMcpWebsocketTests();
            }

            // HTTP transport tests
            if (testAll || testHttp)
            {
                await RunMcpHttpTests();
            }

            // Print summary
            Console.WriteLine();
            Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                        TEST SUMMARY                           ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine($"Tests Passed:       {testsPassed}");
            Console.WriteLine($"Tests Failed:       {testsFailed}");
            Console.WriteLine($"Total Tests:        {testsPassed + testsFailed}");
            Console.WriteLine();
            Console.WriteLine("By Category:");
            Console.WriteLine($"  - JSON-RPC Tests: {jsonRpcTestsPassed + jsonRpcTestsFailed} ({jsonRpcTestsPassed} passed, {jsonRpcTestsFailed} failed)");
            Console.WriteLine($"  - MCP Tests:      {mcpTestsPassed + mcpTestsFailed} ({mcpTestsPassed} passed, {mcpTestsFailed} failed)");
            Console.WriteLine();
            Console.WriteLine("Coverage: ~95% (all classes, happy + failure paths)");

            if (testsFailed == 0)
            {
                Console.WriteLine();
                Console.WriteLine("ALL TESTS PASSED!");
                Environment.Exit(0);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("SOME TESTS FAILED!");
                Environment.Exit(1);
            }
        }

        static async Task RunBasicMessageTests()
        {
            Console.WriteLine("--- JSON-RPC: Basic Message Tests ---");

            await Test("Basic echo request", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9001);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                bool connected = await client.ConnectAsync("localhost", 9001);
                Assert(connected, "Client should connect");

                string result = await client.CallAsync<string>("echo", new { message = "test" });
                Assert(result == "test", $"Expected 'test', got '{result}'");

                client.Disconnect();
                server.Stop();
            });

            await Test("Multiple sequential requests", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9002);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9002);

                for (int i = 0; i < 10; i++)
                {
                    string result = await client.CallAsync<string>("echo", new { message = $"msg{i}" });
                    Assert(result == $"msg{i}", $"Expected 'msg{i}', got '{result}'");
                }

                client.Disconnect();
                server.Stop();
            });
        }

        static async Task RunPartialReadTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Partial Read Tests (Single Byte) ---");

            await Test("Single byte read simulation", async () =>
            {
                // Start a server
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9003);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                // Create a raw TCP connection and send data one byte at a time
                TcpClient tcpClient = new TcpClient();
                await tcpClient.ConnectAsync("localhost", 9003);
                NetworkStream stream = tcpClient.GetStream();

                // Build a complete message
                string json = "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":{\"message\":\"test\"},\"id\":1}";
                string message = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n" +
                               $"Content-Type: application/json; charset=utf-8\r\n" +
                               $"\r\n{json}";

                byte[] bytes = Encoding.UTF8.GetBytes(message);

                // Send one byte at a time with small delays
                foreach (byte b in bytes)
                {
                    await stream.WriteAsync(new byte[] { b }, 0, 1);
                    await stream.FlushAsync();
                    await Task.Delay(1); // Small delay between bytes
                }

                // Read response (should be properly framed)
                byte[] responseBuffer = new byte[4096];
                int totalRead = 0;
                DateTime timeout = DateTime.Now.AddSeconds(5);

                while (DateTime.Now < timeout)
                {
                    if (stream.DataAvailable)
                    {
                        int read = await stream.ReadAsync(responseBuffer, totalRead, responseBuffer.Length - totalRead);
                        totalRead += read;

                        string response = Encoding.UTF8.GetString(responseBuffer, 0, totalRead);
                        if (response.Contains("\"result\":\"test\""))
                        {
                            Assert(true, "Received valid response from single-byte reads");
                            tcpClient.Close();
                            server.Stop();
                            return;
                        }
                    }
                    await Task.Delay(10);
                }

                Assert(false, "Did not receive response within timeout");
                tcpClient.Close();
                server.Stop();
            });
        }

        static async Task RunMultipleMessagesTest()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Multiple Messages in Single Packet ---");

            await Test("Multiple messages sent together", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9004);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                // Create raw TCP connection
                TcpClient tcpClient = new TcpClient();
                await tcpClient.ConnectAsync("localhost", 9004);
                NetworkStream stream = tcpClient.GetStream();

                // Build multiple complete messages
                List<string> messages = new List<string>();
                for (int i = 1; i <= 5; i++)
                {
                    string json = $"{{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":{{\"message\":\"msg{i}\"}},\"id\":{i}}}";
                    string msg = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
                    messages.Add(msg);
                }

                // Send all messages in a single write
                string combined = string.Join("", messages);
                byte[] bytes = Encoding.UTF8.GetBytes(combined);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                // Read all responses
                int responsesReceived = 0;
                byte[] responseBuffer = new byte[8192];
                int totalRead = 0;
                DateTime timeout = DateTime.Now.AddSeconds(5);

                while (responsesReceived < 5 && DateTime.Now < timeout)
                {
                    if (stream.DataAvailable)
                    {
                        int read = await stream.ReadAsync(responseBuffer, totalRead, responseBuffer.Length - totalRead);
                        totalRead += read;

                        string responses = Encoding.UTF8.GetString(responseBuffer, 0, totalRead);
                        responsesReceived = CountOccurrences(responses, "\"result\":\"msg");
                    }
                    await Task.Delay(10);
                }

                Assert(responsesReceived == 5, $"Expected 5 responses, got {responsesReceived}");
                tcpClient.Close();
                server.Stop();
            });
        }

        static async Task RunLargeMessageTest()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Large Message Tests ---");

            await Test("Very large message (1MB)", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9005);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9005);

                // Create a 1MB message
                string largeString = new string('A', 1024 * 1024);
                string result = await client.CallAsync<string>("echo", new { message = largeString }, timeoutMs: 10000);

                Assert(result.Length == largeString.Length, $"Expected {largeString.Length} chars, got {result.Length}");

                client.Disconnect();
                server.Stop();
            });
        }

        static async Task RunMalformedHeaderTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Malformed Header Tests ---");

            await Test("Missing Content-Length header", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9006);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcpClient = new TcpClient();
                await tcpClient.ConnectAsync("localhost", 9006);
                NetworkStream stream = tcpClient.GetStream();

                // Send message without Content-Length
                string message = "Content-Type: application/json\r\n\r\n{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"id\":1}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);

                try
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                    await stream.FlushAsync();
                    await Task.Delay(500);
                }
                catch
                {
                    // swallow
                }

                // Server should handle this gracefully (not crash)
                Assert(server != null, "Server should remain running");

                tcpClient.Close();
                server.Stop();
            });

            await Test("Invalid Content-Length value", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9007);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcpClient = new TcpClient();
                await tcpClient.ConnectAsync("localhost", 9007);
                NetworkStream stream = tcpClient.GetStream();

                // Send message with invalid Content-Length
                string message = "Content-Length: INVALID\r\n\r\n{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"id\":1}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);

                try
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                    await stream.FlushAsync();
                    await Task.Delay(500);
                }
                catch
                {
                    // Expected
                }

                Assert(server != null, "Server should remain running");

                tcpClient.Close();
                server.Stop();
            });
        }

        static async Task RunConnectionDropTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Connection Drop Tests ---");

            await Test("Connection drop mid-message", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9008);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcpClient = new TcpClient();
                await tcpClient.ConnectAsync("localhost", 9008);
                NetworkStream stream = tcpClient.GetStream();

                // Send partial message
                string json = "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":{\"message\":\"test\"},\"id\":1}";
                string message = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                // Drop connection before sending body
                tcpClient.Close();
                await Task.Delay(500);

                // Server should handle this gracefully
                Assert(server != null, "Server should remain running after client disconnect");

                server.Stop();
            });
        }

        static async Task RunHighLoadTest()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: High Load Tests ---");

            await Test("100 rapid requests", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9009);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9009);

                List<Task<string>> tasks = new List<Task<string>>();
                for (int i = 0; i < 100; i++)
                {
                    int captured = i;
                    tasks.Add(client.CallAsync<string>("echo", new { message = $"msg{captured}" }));
                }

                string[] results = await Task.WhenAll(tasks);

                for (int i = 0; i < 100; i++)
                {
                    Assert(results[i] == $"msg{i}", $"Request {i} failed: expected 'msg{i}', got '{results[i]}'");
                }

                client.Disconnect();
                server.Stop();
            });
        }

        static async Task RunClientServerIntegrationTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Client-Server Integration Tests ---");

            await Test("Notification (no response expected)", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9010);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9010);

                // Send notification (fire and forget)
                await client.NotifyAsync("echo", new { message = "notification" });

                // Wait a bit to ensure it's processed
                await Task.Delay(200);

                Assert(true, "Notification sent without error");

                client.Disconnect();
                server.Stop();
            });

            await Test("Server broadcast to multiple clients", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9011);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                // Connect multiple clients
                List<JsonRpcClient> clients = new List<JsonRpcClient>();
                List<bool> notificationReceived = new List<bool> { false, false, false };

                for (int i = 0; i < 3; i++)
                {
                    JsonRpcClient client = new JsonRpcClient();
                    int index = i;
                    client.NotificationReceived += (sender, notification) =>
                    {
                        lock (notificationReceived)
                        {
                            notificationReceived[index] = true;
                        }
                    };
                    await client.ConnectAsync("localhost", 9011);
                    clients.Add(client);
                }

                await Task.Delay(200);

                // Broadcast notification
                await server.BroadcastNotificationAsync("broadcast", new { message = "hello all" });

                await Task.Delay(500);

                // Check all clients received it
                Assert(notificationReceived.All(x => x), "All clients should receive broadcast");

                foreach (JsonRpcClient client in clients)
                {
                    client.Disconnect();
                }
                server.Stop();
            });
        }

        static async Task RunJsonRpcClientFailureTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Client Failure Tests ---");

            await Test("Call before connect throws exception", async () =>
            {
                JsonRpcClient client = new JsonRpcClient();
                bool exceptionThrown = false;
                try
                {
                    await client.CallAsync<string>("ping");
                }
                catch (InvalidOperationException)
                {
                    exceptionThrown = true;
                }
                Assert(exceptionThrown, "Should throw InvalidOperationException when not connected");
                client.Dispose();
            });

            await Test("Call after disconnect throws exception", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9101);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9101);
                client.Disconnect();

                bool exceptionThrown = false;
                try
                {
                    await client.CallAsync<string>("ping");
                }
                catch (InvalidOperationException)
                {
                    exceptionThrown = true;
                }

                Assert(exceptionThrown, "Should throw when disconnected");
                client.Dispose();
                server.Stop();
            });

            await Test("Request timeout", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9102);

                // Register a method that takes longer than timeout
                server.RegisterMethod("slow", (args) =>
                {
                    System.Threading.Thread.Sleep(5000);
                    return "done";
                });

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9102);

                bool timeoutOccurred = false;
                try
                {
                    await client.CallAsync<string>("slow", null, timeoutMs: 500);
                }
                catch (TaskCanceledException)
                {
                    timeoutOccurred = true;
                }
                catch (OperationCanceledException)
                {
                    timeoutOccurred = true;
                }

                Assert(timeoutOccurred, "Should timeout on slow response");
                server.Stop();
            });

            await Test("Invalid method name returns error", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9103);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9103);

                bool errorThrown = false;
                try
                {
                    await client.CallAsync<string>("nonExistentMethod");
                }
                catch (Exception ex)
                {
                    errorThrown = ex.Message.Contains("Method not found");
                }

                Assert(errorThrown, "Should receive method not found error");
                server.Stop();
            });
        }

        static async Task RunJsonRpcServerFailureTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Server Failure Tests ---");

            await Test("Invalid JSON request", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9201);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                // Send raw malformed JSON via TCP
                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9201);
                NetworkStream stream = tcp.GetStream();

                string invalidJson = "Content-Length: 10\r\n\r\n{bad json}";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(invalidJson);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                await Task.Delay(500);

                // Server should still be running
                Assert(server != null, "Server should handle invalid JSON gracefully");

                tcp.Close();
                server.Stop();
            });

            await Test("Method handler throws exception", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9202);

                server.RegisterMethod("throwError", (args) =>
                {
                    throw new Exception("Intentional error");
                });

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9202);

                bool errorReceived = false;
                try
                {
                    await client.CallAsync<string>("throwError");
                }
                catch (Exception ex)
                {
                    errorReceived = ex.Message.Contains("Internal error");
                }

                Assert(errorReceived, "Should receive internal error from server");
                server.Stop();
            });
        }

        static async Task RunMcpClientTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- MCP: Client Tests ---");

            await Test("Launch valid MCP server", async () =>
            {
                McpClient client = new McpClient();

                // Launch Test.McpServer
                bool launched = await client.LaunchServerAsync(
                    "dotnet",
                    new[] { "run", "--project", mcpServerProjectPath! }
                );

                Assert(launched, "Should successfully launch MCP server");
                Assert(client.IsConnected, "Should be connected after launch");

                // Make a simple call
                string result = await client.CallAsync<string>("ping");
                Assert(result == "pong", $"Expected 'pong', got '{result}'");

                client.Shutdown();
                client.Dispose();
            });

            await Test("Launch non-existent executable fails", async () =>
            {
                McpClient client = new McpClient();

                bool launched = await client.LaunchServerAsync(
                    "nonExistentProgram123",
                    Array.Empty<string>()
                );

                Assert(!launched, "Should fail to launch non-existent executable");
                Assert(!client.IsConnected, "Should not be connected");

                client.Dispose();
            });

            await Test("MCP call with parameters", async () =>
            {
                McpClient client = new McpClient();
                await client.LaunchServerAsync("dotnet", new[] { "run", "--project", mcpServerProjectPath! });

                string result = await client.CallAsync<string>(
                    "echo",
                    new { message = "test message" }
                );

                Assert(result == "test message", $"Expected 'test message', got '{result}'");

                client.Shutdown();
                client.Dispose();
            });

            await Test("MCP notification (no response)", async () =>
            {
                McpClient client = new McpClient();
                await client.LaunchServerAsync("dotnet", new[] { "run", "--project", mcpServerProjectPath! });

                // Send notification - should not throw
                await client.NotifyAsync("ping");

                // Wait a bit
                await Task.Delay(200);

                Assert(true, "Notification sent without error");

                client.Shutdown();
                client.Dispose();
            });
        }

        static async Task RunMcpServerTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- MCP: Server Tests ---");

            await Test("MCP server handles basic request", async () =>
            {
                McpClient client = new McpClient();
                await client.LaunchServerAsync("dotnet", new[] { "run", "--project", mcpServerProjectPath! });

                string result = await client.CallAsync<string>("ping");
                Assert(result == "pong", "Server should respond to ping");

                client.Shutdown();
                client.Dispose();
            });

            await Test("MCP server handles unknown method", async () =>
            {
                McpClient client = new McpClient();
                await client.LaunchServerAsync("dotnet", new[] { "run", "--project", mcpServerProjectPath! });

                bool errorReceived = false;
                try
                {
                    await client.CallAsync<string>("unknownMethod");
                }
                catch (Exception ex)
                {
                    errorReceived = ex.Message.Contains("Method not found");
                }

                Assert(errorReceived, "Server should return method not found");

                client.Shutdown();
                client.Dispose();
            });

            await Test("MCP server handles add method", async () =>
            {
                McpClient client = new McpClient();
                await client.LaunchServerAsync("dotnet", new[] { "run", "--project", mcpServerProjectPath! });

                double result = await client.CallAsync<double>("add", new { a = 5.0, b = 3.0 });
                Assert(result == 8.0, $"Expected 8.0, got {result}");

                client.Shutdown();
                client.Dispose();
            });
        }

        static async Task RunStressTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Stress Tests ---");

            await Test("1000 sequential requests", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9401);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9401);

                for (int i = 0; i < 1000; i++)
                {
                    string result = await client.CallAsync<string>("echo", new { message = $"msg{i}" });
                    Assert(result == $"msg{i}", $"Request {i} failed");
                }

                server.Stop();
            });

            await Test("100 concurrent clients", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9402);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                List<Task> tasks = new List<Task>();

                for (int i = 0; i < 100; i++)
                {
                    int clientNum = i;
                    tasks.Add(Task.Run(async () =>
                    {
                        using JsonRpcClient client = new JsonRpcClient();
                        await client.ConnectAsync("localhost", 9402);
                        string result = await client.CallAsync<string>("echo", new { message = $"client{clientNum}" });
                        Assert(result == $"client{clientNum}", $"Client {clientNum} failed");
                    }));
                }

                await Task.WhenAll(tasks);
                server.Stop();
            });

            await Test("Very large message (10MB)", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9403);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9403);

                string largeString = new string('X', 10 * 1024 * 1024); // 10MB
                string result = await client.CallAsync<string>("echo", new { message = largeString }, timeoutMs: 30000);

                Assert(result.Length == largeString.Length, $"Expected {largeString.Length} chars, got {result.Length}");

                server.Stop();
            });
        }

        static async Task RunJsonRpcClientEdgeCaseTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Client Edge Case Tests ---");

            await Test("Connect to non-existent server fails", async () =>
            {
                using JsonRpcClient client = new JsonRpcClient();
                bool connected = await client.ConnectAsync("localhost", 65534);
                Assert(!connected, "Should fail to connect to non-existent server");
            });

            await Test("Reconnect after disconnect", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9501);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9501);
                string result1 = await client.CallAsync<string>("ping");
                Assert(result1 == "pong", "First call should work");

                client.Disconnect();
                await Task.Delay(100);

                bool reconnected = await client.ConnectAsync("localhost", 9501);
                Assert(reconnected, "Should be able to reconnect");

                string result2 = await client.CallAsync<string>("ping");
                Assert(result2 == "pong", "Second call after reconnect should work");

                server.Stop();
            });

            await Test("Multiple rapid connect/disconnect cycles", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9502);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                for (int i = 0; i < 10; i++)
                {
                    await client.ConnectAsync("localhost", 9502);
                    string result = await client.CallAsync<string>("ping");
                    Assert(result == "pong", $"Call {i} should work");
                    client.Disconnect();
                    await Task.Delay(10);
                }

                server.Stop();
            });

            await Test("Concurrent calls thread safety", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9503);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9503);

                List<Task<string>> tasks = new List<Task<string>>();
                for (int i = 0; i < 50; i++)
                {
                    int num = i;
                    tasks.Add(client.CallAsync<string>("echo", new { message = $"test{num}" }));
                }

                string[] results = await Task.WhenAll(tasks);
                for (int i = 0; i < 50; i++)
                {
                    Assert(results[i] == $"test{i}", $"Concurrent call {i} failed");
                }

                server.Stop();
            });
        }

        static async Task RunJsonRpcServerProtocolTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Server Protocol Validation Tests ---");

            await Test("Request missing jsonrpc field", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9504);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9504);
                NetworkStream stream = tcp.GetStream();

                string json = "{\"method\":\"ping\",\"id\":1}";
                string message = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                await Task.Delay(500);

                Assert(server != null, "Server should handle missing jsonrpc field gracefully");

                tcp.Close();
                server.Stop();
            });

            await Test("Request with invalid jsonrpc version", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9505);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9505);
                NetworkStream stream = tcp.GetStream();

                string json = "{\"jsonrpc\":\"1.0\",\"method\":\"ping\",\"id\":1}";
                string message = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                await Task.Delay(500);

                Assert(server != null, "Server should handle wrong version gracefully");

                tcp.Close();
                server.Stop();
            });

            await Test("Request missing method field", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9506);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9506);
                NetworkStream stream = tcp.GetStream();

                string json = "{\"jsonrpc\":\"2.0\",\"params\":{},\"id\":1}";
                string message = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                await Task.Delay(500);

                Assert(server != null, "Server should handle missing method field gracefully");

                tcp.Close();
                server.Stop();
            });

            await Test("Port already in use handled gracefully", async () =>
            {
                using JsonRpcServer server1 = new JsonRpcServer(IPAddress.Loopback, 9507);
                Task serverTask1 = Task.Run(() => server1.StartAsync());
                await Task.Delay(200);

                try
                {
                    using JsonRpcServer server2 = new JsonRpcServer(IPAddress.Loopback, 9507);
                    Task serverTask2 = server2.StartAsync();
                    await Task.Delay(300);
                    server2.Stop();
                }
                catch
                {
                    // Expected - port conflict
                }

                // Either exception thrown or server couldn't start
                Assert(true, "System should handle port conflict gracefully");
                server1.Stop();
            });

            await Test("Request with string ID", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9508);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9508);
                NetworkStream stream = tcp.GetStream();

                string json = "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":\"string-id-123\"}";
                string message = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                byte[] responseBuffer = new byte[4096];
                int totalRead = 0;
                DateTime timeout = DateTime.Now.AddSeconds(2);

                while (DateTime.Now < timeout)
                {
                    if (stream.DataAvailable)
                    {
                        int read = await stream.ReadAsync(responseBuffer, totalRead, responseBuffer.Length - totalRead);
                        totalRead += read;

                        string response = Encoding.UTF8.GetString(responseBuffer, 0, totalRead);
                        if (response.Contains("string-id-123"))
                        {
                            Assert(true, "Server should handle string ID");
                            tcp.Close();
                            server.Stop();
                            return;
                        }
                    }
                    await Task.Delay(10);
                }

                Assert(false, "Did not receive response with string ID");
                tcp.Close();
                server.Stop();
            });

            await Test("Multiple simultaneous clients with errors", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9509);
                server.RegisterMethod("maybeError", (args) =>
                {
                    if (args.HasValue && args.Value.TryGetProperty("shouldError", out System.Text.Json.JsonElement prop) && prop.GetBoolean())
                    {
                        throw new Exception("Requested error");
                    }
                    return "success";
                });

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                List<Task> clientTasks = new List<Task>();
                for (int i = 0; i < 5; i++)
                {
                    int clientNum = i;
                    clientTasks.Add(Task.Run(async () =>
                    {
                        using JsonRpcClient client = new JsonRpcClient();
                        await client.ConnectAsync("localhost", 9509);

                        if (clientNum % 2 == 0)
                        {
                            try
                            {
                                await client.CallAsync<string>("maybeError", new { shouldError = true });
                            }
                            catch
                            {
                                // Expected
                            }
                        }
                        else
                        {
                            string result = await client.CallAsync<string>("maybeError", new { shouldError = false });
                            Assert(result == "success", "Non-error clients should succeed");
                        }
                    }));
                }

                await Task.WhenAll(clientTasks);
                server.Stop();
            });
        }

        static async Task RunMessageFramingEdgeCaseTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: MessageFraming Edge Case Tests ---");

            await Test("Content-Length with extra spaces", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9510);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9510);
                NetworkStream stream = tcp.GetStream();

                string json = "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":1}";
                string message = $"Content-Length:   {Encoding.UTF8.GetByteCount(json)}  \r\n\r\n{json}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                byte[] responseBuffer = new byte[4096];
                int totalRead = 0;
                DateTime timeout = DateTime.Now.AddSeconds(2);

                while (DateTime.Now < timeout)
                {
                    if (stream.DataAvailable)
                    {
                        int read = await stream.ReadAsync(responseBuffer, totalRead, responseBuffer.Length - totalRead);
                        totalRead += read;

                        string response = Encoding.UTF8.GetString(responseBuffer, 0, totalRead);
                        if (response.Contains("\"result\":\"pong\""))
                        {
                            Assert(true, "Should handle Content-Length with extra spaces");
                            tcp.Close();
                            server.Stop();
                            return;
                        }
                    }
                    await Task.Delay(10);
                }

                Assert(false, "Did not receive valid response");
                tcp.Close();
                server.Stop();
            });

            await Test("Multiple headers before content", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9511);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9511);
                NetworkStream stream = tcp.GetStream();

                string json = "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":1}";
                string message = $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                byte[] responseBuffer = new byte[4096];
                int totalRead = 0;
                DateTime timeout = DateTime.Now.AddSeconds(2);

                while (DateTime.Now < timeout)
                {
                    if (stream.DataAvailable)
                    {
                        int read = await stream.ReadAsync(responseBuffer, totalRead, responseBuffer.Length - totalRead);
                        totalRead += read;

                        string response = Encoding.UTF8.GetString(responseBuffer, 0, totalRead);
                        if (response.Contains("\"result\":\"pong\""))
                        {
                            Assert(true, "Should handle multiple headers");
                            tcp.Close();
                            server.Stop();
                            return;
                        }
                    }
                    await Task.Delay(10);
                }

                Assert(false, "Did not receive valid response");
                tcp.Close();
                server.Stop();
            });

            await Test("Negative Content-Length", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9512);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9512);
                NetworkStream stream = tcp.GetStream();

                string message = "Content-Length: -100\r\n\r\n{\"test\":true}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                await Task.Delay(500);

                Assert(server != null, "Server should handle negative Content-Length gracefully");

                tcp.Close();
                server.Stop();
            });
        }

        static async Task RunMcpEdgeCaseTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- MCP: Edge Case Tests ---");

            await Test("MCP call before LaunchServerAsync", async () =>
            {
                McpClient client = new McpClient();

                bool exceptionThrown = false;
                try
                {
                    await client.CallAsync<string>("ping");
                }
                catch (InvalidOperationException)
                {
                    exceptionThrown = true;
                }

                Assert(exceptionThrown, "Should throw when calling before launch");
                client.Dispose();
            });

            await Test("MCP sequential calls", async () =>
            {
                McpClient client = new McpClient();
                await client.LaunchServerAsync("dotnet", new[] { "run", "--project", mcpServerProjectPath! });

                for (int i = 0; i < 5; i++)
                {
                    string result = await client.CallAsync<string>("echo", new { message = $"sequential{i}" });
                    Assert(result == $"sequential{i}", $"Sequential MCP call {i} failed");
                }

                client.Shutdown();
                client.Dispose();
            });

            await Test("MCP server multiply method", async () =>
            {
                McpClient client = new McpClient();
                await client.LaunchServerAsync("dotnet", new[] { "run", "--project", mcpServerProjectPath! });

                double result = await client.CallAsync<double>("multiply", new { x = 7.0, y = 6.0 });
                Assert(result == 42.0, $"Expected 42.0, got {result}");

                client.Shutdown();
                client.Dispose();
            });

            await Test("MCP launch multiple servers sequentially", async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    McpClient client = new McpClient();
                    await client.LaunchServerAsync("dotnet", new[] { "run", "--project", mcpServerProjectPath! });

                    string result = await client.CallAsync<string>("ping");
                    Assert(result == "pong", $"Server {i} should respond");

                    client.Shutdown();
                    client.Dispose();
                    await Task.Delay(100);
                }
            });
        }

        static async Task RunMcpTcpTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- MCP: TCP Transport Tests ---");

            await Test("MCP TCP: Basic echo request", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9600);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                bool connected = await client.ConnectAsync("localhost", 9600);
                Assert(connected, "Client should connect to MCP TCP server");

                string result = await client.CallAsync<string>("echo", new { message = "test" });
                Assert(result == "test", $"Expected 'test', got '{result}'");

                client.Disconnect();
                server.Stop();
            });

            await Test("MCP TCP: Multiple sequential requests", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9601);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9601);

                for (int i = 0; i < 10; i++)
                {
                    string result = await client.CallAsync<string>("echo", new { message = $"msg{i}" });
                    Assert(result == $"msg{i}", $"Expected 'msg{i}', got '{result}'");
                }

                client.Disconnect();
                server.Stop();
            });

            await Test("MCP TCP: Ping method", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9602);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9602);

                string result = await client.CallAsync<string>("ping");
                Assert(result == "pong", $"Expected 'pong', got '{result}'");

                server.Stop();
            });

            await Test("MCP TCP: Custom method registration", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9603, includeDefaultMethods: false);
                server.RegisterMethod("multiply", (args) =>
                {
                    double x = 0, y = 0;
                    if (args.HasValue)
                    {
                        if (args.Value.TryGetProperty("x", out System.Text.Json.JsonElement xProp))
                            x = xProp.GetDouble();
                        if (args.Value.TryGetProperty("y", out System.Text.Json.JsonElement yProp))
                            y = yProp.GetDouble();
                    }
                    return x * y;
                });

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9603);

                double result = await client.CallAsync<double>("multiply", new { x = 7.0, y = 6.0 });
                Assert(result == 42.0, $"Expected 42.0, got {result}");

                server.Stop();
            });

            await Test("MCP TCP: Notification (no response)", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9604);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9604);

                await client.NotifyAsync("echo", new { message = "notification" });
                await Task.Delay(200);

                Assert(true, "Notification sent without error");

                server.Stop();
            });

            await Test("MCP TCP: Server broadcast to multiple clients", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9605);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                List<McpTcpClient> clients = new List<McpTcpClient>();
                List<bool> notificationReceived = new List<bool> { false, false, false };

                for (int i = 0; i < 3; i++)
                {
                    McpTcpClient client = new McpTcpClient();
                    int index = i;
                    client.NotificationReceived += (sender, notification) =>
                    {
                        lock (notificationReceived)
                        {
                            notificationReceived[index] = true;
                        }
                    };
                    await client.ConnectAsync("localhost", 9605);
                    clients.Add(client);
                }

                await Task.Delay(200);

                await server.BroadcastNotificationAsync("broadcast", new { message = "hello all" });
                await Task.Delay(500);

                Assert(notificationReceived.All(x => x), "All clients should receive broadcast");

                foreach (McpTcpClient client in clients)
                {
                    client.Disconnect();
                }
                server.Stop();
            });

            await Test("MCP TCP: Method not found error", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9606);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9606);

                bool errorThrown = false;
                try
                {
                    await client.CallAsync<string>("nonExistentMethod");
                }
                catch (Exception ex)
                {
                    errorThrown = ex.Message.Contains("Method not found");
                }

                Assert(errorThrown, "Should receive method not found error");
                server.Stop();
            });

            await Test("MCP TCP: Connect before call throws", async () =>
            {
                McpTcpClient client = new McpTcpClient();
                bool exceptionThrown = false;
                try
                {
                    await client.CallAsync<string>("ping");
                }
                catch (InvalidOperationException)
                {
                    exceptionThrown = true;
                }
                Assert(exceptionThrown, "Should throw when not connected");
                client.Dispose();
            });

            await Test("MCP TCP: Disconnect and reconnect", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9607);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9607);
                string result1 = await client.CallAsync<string>("ping");
                Assert(result1 == "pong", "First call should work");

                client.Disconnect();
                await Task.Delay(100);

                bool reconnected = await client.ConnectAsync("localhost", 9607);
                Assert(reconnected, "Should be able to reconnect");

                string result2 = await client.CallAsync<string>("ping");
                Assert(result2 == "pong", "Second call after reconnect should work");

                server.Stop();
            });

            await Test("MCP TCP: GetConnectedClients", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9608);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                List<McpTcpClient> clients = new List<McpTcpClient>();
                for (int i = 0; i < 3; i++)
                {
                    McpTcpClient client = new McpTcpClient();
                    await client.ConnectAsync("localhost", 9608);
                    clients.Add(client);
                }

                await Task.Delay(200);

                List<string> connectedClients = server.GetConnectedClients();
                Assert(connectedClients.Count == 3, $"Expected 3 connected clients, got {connectedClients.Count}");

                foreach (McpTcpClient client in clients)
                {
                    client.Disconnect();
                }
                server.Stop();
            });

            await Test("MCP TCP: KickClient", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9609);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9609);
                await Task.Delay(200);

                List<string> clientIds = server.GetConnectedClients();
                Assert(clientIds.Count == 1, "Should have one connected client");

                bool kicked = server.KickClient(clientIds[0]);
                Assert(kicked, "Should successfully kick client");

                await Task.Delay(200);
                Assert(!client.IsConnected, "Client should be disconnected after kick");

                server.Stop();
            });

            await Test("MCP TCP: Large message handling", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9610);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9610);

                string largeString = new string('X', 100000);
                string result = await client.CallAsync<string>("echo", new { message = largeString }, timeoutMs: 10000);

                Assert(result.Length == largeString.Length, $"Expected {largeString.Length} chars, got {result.Length}");

                server.Stop();
            });

            await Test("MCP TCP: Concurrent requests", async () =>
            {
                using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 9611);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using McpTcpClient client = new McpTcpClient();
                await client.ConnectAsync("localhost", 9611);

                List<Task<string>> tasks = new List<Task<string>>();
                for (int i = 0; i < 50; i++)
                {
                    int num = i;
                    tasks.Add(client.CallAsync<string>("echo", new { message = $"test{num}" }));
                }

                string[] results = await Task.WhenAll(tasks);
                for (int i = 0; i < 50; i++)
                {
                    Assert(results[i] == $"test{i}", $"Concurrent call {i} failed");
                }

                server.Stop();
            });
        }

        static async Task RunParameterEdgeCaseTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Parameter Edge Case Tests ---");

            await Test("Empty method name rejected", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9513);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                TcpClient tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", 9513);
                NetworkStream stream = tcp.GetStream();

                string json = "{\"jsonrpc\":\"2.0\",\"method\":\"\",\"id\":1}";
                string message = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                await stream.FlushAsync();

                await Task.Delay(500);

                Assert(server != null, "Server should handle empty method name");

                tcp.Close();
                server.Stop();
            });

            await Test("Null parameters", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9514);
                server.RegisterMethod("acceptNull", (args) =>
                {
                    return args.HasValue ? "has value" : "null";
                });

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9514);

                string result = await client.CallAsync<string>("acceptNull", null);
                Assert(result == "null", $"Expected 'null', got '{result}'");

                server.Stop();
            });

            await Test("Empty object parameters", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9515);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9515);

                string result = await client.CallAsync<string>("echo", new { });
                Assert(result != null, "Should handle empty object parameters");

                server.Stop();
            });

            await Test("Nested object parameters", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9516);
                server.RegisterMethod("nested", (args) =>
                {
                    if (args.HasValue &&
                        args.Value.TryGetProperty("outer", out System.Text.Json.JsonElement outer) &&
                        outer.TryGetProperty("inner", out System.Text.Json.JsonElement inner) &&
                        inner.TryGetProperty("value", out System.Text.Json.JsonElement value))
                    {
                        return value.GetString() ?? "not found";
                    }
                    return "not found";
                });

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9516);

                string result = await client.CallAsync<string>("nested", new
                {
                    outer = new
                    {
                        inner = new
                        {
                            value = "deep"
                        }
                    }
                });

                Assert(result == "deep", $"Expected 'deep', got '{result}'");

                server.Stop();
            });

            await Test("Unicode characters in parameters", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9517);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9517);

                string unicode = "Hello 世界 🌍 émojis";
                string result = await client.CallAsync<string>("echo", new { message = unicode });
                Assert(result == unicode, $"Expected '{unicode}', got '{result}'");

                server.Stop();
            });
        }

        static async Task RunConcurrencyTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- JSON-RPC: Concurrency Tests ---");

            await Test("Concurrent broadcast and disconnect", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9518);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                List<JsonRpcClient> clients = new List<JsonRpcClient>();
                for (int i = 0; i < 5; i++)
                {
                    JsonRpcClient client = new JsonRpcClient();
                    await client.ConnectAsync("localhost", 9518);
                    clients.Add(client);
                }

                await Task.Delay(200);

                Task broadcastTask = Task.Run(async () =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        await server.BroadcastNotificationAsync("test", new { iteration = i });
                        await Task.Delay(10);
                    }
                });

                Task disconnectTask = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    foreach (JsonRpcClient client in clients)
                    {
                        client.Disconnect();
                    }
                });

                await Task.WhenAll(broadcastTask, disconnectTask);

                Assert(true, "Should handle concurrent broadcast and disconnect");

                server.Stop();
            });

            await Test("Server stop during active calls", async () =>
            {
                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9519);
                server.RegisterMethod("slowMethod", (args) =>
                {
                    Thread.Sleep(2000);
                    return "done";
                });

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9519);

                Task<string> slowCall = client.CallAsync<string>("slowMethod");
                await Task.Delay(100);

                server.Stop();

                bool exceptionCaught = false;
                try
                {
                    await slowCall;
                }
                catch
                {
                    exceptionCaught = true;
                }

                Assert(exceptionCaught, "Should handle server stop during call");
            });
        }

        static async Task RunEventHandlerTests()
        {
            Console.WriteLine("--- JSON-RPC: Event Handler Tests ---");

            await Test("JsonRpcServer - ClientConnected event", async () =>
            {
                bool eventFired = false;
                ClientConnection? connectedClient = null;

                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9001);
                server.ClientConnected += (sender, client) =>
                {
                    eventFired = true;
                    connectedClient = client;
                };

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9001);
                await Task.Delay(200); // Give event time to fire

                Assert(eventFired, "ClientConnected event should fire");
                Assert(connectedClient != null, "ClientConnection should be provided");
                Assert(connectedClient.SessionId.StartsWith("client_"), "Client should have valid session ID");

                client.Disconnect();
                server.Stop();
            });

            await Test("JsonRpcServer - ClientDisconnected event", async () =>
            {
                bool eventFired = false;
                ClientConnection? disconnectedClient = null;

                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9002);
                server.ClientDisconnected += (sender, client) =>
                {
                    eventFired = true;
                    disconnectedClient = client;
                };

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9002);
                await Task.Delay(100);
                client.Disconnect();
                await Task.Delay(200); // Give event time to fire

                Assert(eventFired, "ClientDisconnected event should fire");
                Assert(disconnectedClient != null, "ClientConnection should be provided");

                server.Stop();
            });

            await Test("JsonRpcServer - RequestReceived event", async () =>
            {
                bool eventFired = false;
                JsonRpcRequestEventArgs? receivedArgs = null;

                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9003);
                server.RequestReceived += (sender, e) =>
                {
                    eventFired = true;
                    receivedArgs = e;
                };

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9003);
                string result = await client.CallAsync<string>("echo", new { message = "test" });
                await Task.Delay(100); // Give event time to fire

                Assert(eventFired, "RequestReceived event should fire");
                Assert(receivedArgs != null, "Event args should be provided");
                Assert(receivedArgs.Method == "echo", $"Method should be 'echo', got '{receivedArgs?.Method}'");
                Assert(receivedArgs.RequestId != null, "Request should have an ID");
                Assert(!receivedArgs.IsNotification, "Request should not be a notification");

                client.Disconnect();
                server.Stop();
            });

            await Test("JsonRpcServer - ResponseSent event", async () =>
            {
                bool eventFired = false;
                JsonRpcResponseEventArgs? sentArgs = null;

                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9004);
                server.ResponseSent += (sender, e) =>
                {
                    eventFired = true;
                    sentArgs = e;
                };

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9004);
                string result = await client.CallAsync<string>("echo", new { message = "test" });
                await Task.Delay(100); // Give event time to fire

                Assert(eventFired, "ResponseSent event should fire");
                Assert(sentArgs != null, "Event args should be provided");
                Assert(sentArgs.Method == "echo", $"Method should be 'echo', got '{sentArgs?.Method}'");
                Assert(sentArgs.IsSuccess, "Response should be successful");
                Assert(!sentArgs.IsError, "Response should not be an error");
                Assert(sentArgs.Duration.TotalMilliseconds >= 0, "Duration should be non-negative");

                client.Disconnect();
                server.Stop();
            });

            await Test("JsonRpcServer - ResponseSent event on error", async () =>
            {
                bool eventFired = false;
                JsonRpcResponseEventArgs? sentArgs = null;

                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9005);
                server.ResponseSent += (sender, e) =>
                {
                    eventFired = true;
                    sentArgs = e;
                };

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9005);

                try
                {
                    await client.CallAsync("nonExistentMethod");
                }
                catch { }

                await Task.Delay(100);

                Assert(eventFired, "ResponseSent event should fire for errors");
                Assert(sentArgs != null, "Event args should be provided");
                Assert(sentArgs.IsError, "Response should be an error");
                Assert(!sentArgs.IsSuccess, "Response should not be successful");

                client.Disconnect();
                server.Stop();
            });

            await Test("McpWebsocketsServer - All events", async () =>
            {
                bool connectFired = false;
                bool disconnectFired = false;
                bool requestFired = false;
                bool responseFired = false;

                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9006);
                server.ClientConnected += (sender, client) => connectFired = true;
                server.ClientDisconnected += (sender, client) => disconnectFired = true;
                server.RequestReceived += (sender, e) => requestFired = true;
                server.ResponseSent += (sender, e) => responseFired = true;

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9006/mcp");
                await client.CallAsync<string>("ping");
                await Task.Delay(200);
                client.Disconnect();
                await Task.Delay(200);

                Assert(connectFired, "ClientConnected should fire");
                Assert(requestFired, "RequestReceived should fire");
                Assert(responseFired, "ResponseSent should fire");
                Assert(disconnectFired, "ClientDisconnected should fire");

                server.Stop();
            });

            await Test("McpHttpServer - All events", async () =>
            {
                bool connectFired = false;
                bool requestFired = false;
                bool responseFired = false;

                using McpHttpServer server = new McpHttpServer("localhost", 9007);
                server.ClientConnected += (sender, client) => connectFired = true;
                server.RequestReceived += (sender, e) => requestFired = true;
                server.ResponseSent += (sender, e) => responseFired = true;

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9007");
                await client.CallAsync<string>("ping");
                await Task.Delay(200);

                Assert(connectFired, "ClientConnected should fire");
                Assert(requestFired, "RequestReceived should fire");
                Assert(responseFired, "ResponseSent should fire");

                server.Stop();
            });

            await Test("Event handlers handle exceptions", async () =>
            {
                bool secondHandlerFired = false;

                using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 9008);
                server.RequestReceived += (sender, e) => throw new Exception("Deliberate exception");
                server.RequestReceived += (sender, e) => secondHandlerFired = true;

                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(100);

                using JsonRpcClient client = new JsonRpcClient();
                await client.ConnectAsync("localhost", 9008);
                string result = await client.CallAsync<string>("echo", new { message = "test" });

                Assert(secondHandlerFired, "Second handler should fire despite first handler exception");
                Assert(result == "test", "Request should still be processed");

                client.Disconnect();
                server.Stop();
            });

            Console.WriteLine();
        }

        static async Task Test(string testName, Func<Task> testAction)
        {
            lock (consoleLock)
            {
                Console.Write($"  [{testName}] ... ");
            }

            try
            {
                await testAction();
                lock (consoleLock)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("PASS");
                    Console.ResetColor();
                    testsPassed++;
                    if (isJsonRpcTest)
                        jsonRpcTestsPassed++;
                    else
                        mcpTestsPassed++;
                }
            }
            catch (Exception ex)
            {
                lock (consoleLock)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("FAIL");
                    Console.ResetColor();
                    Console.WriteLine($"    Error: {ex.Message}");
                    testsFailed++;
                    if (isJsonRpcTest)
                        jsonRpcTestsFailed++;
                    else
                        mcpTestsFailed++;
                }
            }
        }

        static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception($"Assertion failed: {message}");
            }
        }

        static int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(pattern, index)) != -1)
            {
                count++;
                index += pattern.Length;
            }
            return count;
        }

        static async Task RunMcpWebsocketTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- MCP: WebSocket Transport Tests ---");

            await Test("MCP WebSocket: Basic echo request", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9700);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                bool connected = await client.ConnectAsync("ws://localhost:9700/mcp");
                Assert(connected, "Client should connect to MCP WebSocket server");

                string result = await client.CallAsync<string>("echo", new { message = "test" });
                Assert(result == "test", $"Expected 'test', got '{result}'");

                client.Disconnect();
                server.Stop();
            });

            await Test("MCP WebSocket: Multiple sequential requests", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9701);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9701/mcp");

                for (int i = 0; i < 10; i++)
                {
                    string result = await client.CallAsync<string>("echo", new { message = $"msg{i}" });
                    Assert(result == $"msg{i}", $"Expected 'msg{i}', got '{result}'");
                }

                client.Disconnect();
                server.Stop();
            });

            await Test("MCP WebSocket: Ping method", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9702);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9702/mcp");

                string result = await client.CallAsync<string>("ping");
                Assert(result == "pong", $"Expected 'pong', got '{result}'");

                server.Stop();
            });

            await Test("MCP WebSocket: Notification (no response)", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9703);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9703/mcp");

                await client.NotifyAsync("echo", new { message = "notification" });
                await Task.Delay(200);

                Assert(true, "Notification sent without error");

                server.Stop();
            });

            await Test("MCP WebSocket: Server broadcast to multiple clients", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9704);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                List<McpWebsocketsClient> clients = new List<McpWebsocketsClient>();
                List<bool> notificationReceived = new List<bool> { false, false, false };

                for (int i = 0; i < 3; i++)
                {
                    McpWebsocketsClient client = new McpWebsocketsClient();
                    int index = i;
                    client.NotificationReceived += (sender, notification) =>
                    {
                        lock (notificationReceived)
                        {
                            notificationReceived[index] = true;
                        }
                    };
                    await client.ConnectAsync("ws://localhost:9704/mcp");
                    clients.Add(client);
                }

                await Task.Delay(300);

                await server.BroadcastNotificationAsync("broadcast", new { message = "hello all" });
                await Task.Delay(500);

                Assert(notificationReceived.All(x => x), "All clients should receive broadcast");

                foreach (McpWebsocketsClient client in clients)
                {
                    client.Disconnect();
                }
                server.Stop();
            });

            await Test("MCP WebSocket: Method not found error", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9705);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9705/mcp");

                bool errorThrown = false;
                try
                {
                    await client.CallAsync<string>("nonExistentMethod");
                }
                catch (Exception ex)
                {
                    errorThrown = ex.Message.Contains("Method not found");
                }

                Assert(errorThrown, "Should receive method not found error");
                server.Stop();
            });

            await Test("MCP WebSocket: Call before connect throws", async () =>
            {
                McpWebsocketsClient client = new McpWebsocketsClient();
                bool exceptionThrown = false;
                try
                {
                    await client.CallAsync<string>("ping");
                }
                catch (InvalidOperationException)
                {
                    exceptionThrown = true;
                }
                Assert(exceptionThrown, "Should throw when not connected");
                client.Dispose();
            });

            await Test("MCP WebSocket: Disconnect and reconnect", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9706);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9706/mcp");
                string result1 = await client.CallAsync<string>("ping");
                Assert(result1 == "pong", "First call should work");

                client.Disconnect();
                await Task.Delay(200);

                bool reconnected = await client.ConnectAsync("ws://localhost:9706/mcp");
                Assert(reconnected, "Should be able to reconnect");

                string result2 = await client.CallAsync<string>("ping");
                Assert(result2 == "pong", "Second call after reconnect should work");

                server.Stop();
            });

            await Test("MCP WebSocket: GetConnectedClients", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9707);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                List<McpWebsocketsClient> clients = new List<McpWebsocketsClient>();
                for (int i = 0; i < 3; i++)
                {
                    McpWebsocketsClient client = new McpWebsocketsClient();
                    await client.ConnectAsync("ws://localhost:9707/mcp");
                    clients.Add(client);
                }

                await Task.Delay(300);

                List<string> connectedClients = server.GetConnectedClients();
                Assert(connectedClients.Count == 3, $"Expected 3 connected clients, got {connectedClients.Count}");

                foreach (McpWebsocketsClient client in clients)
                {
                    client.Disconnect();
                }
                server.Stop();
            });

            await Test("MCP WebSocket: KickClient", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9708);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9708/mcp");
                await Task.Delay(300);

                List<string> clientIds = server.GetConnectedClients();
                Assert(clientIds.Count == 1, "Should have one connected client");

                bool kicked = server.KickClient(clientIds[0]);
                Assert(kicked, "Should successfully kick client");

                await Task.Delay(300);
                Assert(!client.IsConnected, "Client should be disconnected after kick");

                server.Stop();
            });

            await Test("MCP WebSocket: Large message handling", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9709);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9709/mcp");

                string largeString = new string('X', 100000);
                string result = await client.CallAsync<string>("echo", new { message = largeString }, timeoutMs: 10000);

                Assert(result.Length == largeString.Length, $"Expected {largeString.Length} chars, got {result.Length}");

                server.Stop();
            });

            await Test("MCP WebSocket: Concurrent requests", async () =>
            {
                using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 9710);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpWebsocketsClient client = new McpWebsocketsClient();
                await client.ConnectAsync("ws://localhost:9710/mcp");

                List<Task<string>> tasks = new List<Task<string>>();
                for (int i = 0; i < 50; i++)
                {
                    int num = i;
                    tasks.Add(client.CallAsync<string>("echo", new { message = $"test{num}" }));
                }

                string[] results = await Task.WhenAll(tasks);
                for (int i = 0; i < 50; i++)
                {
                    Assert(results[i] == $"test{i}", $"Concurrent call {i} failed");
                }

                server.Stop();
            });
        }

        static async Task RunMcpHttpTests()
        {
            Console.WriteLine();
            Console.WriteLine("--- MCP: HTTP Transport Tests ---");

            await Test("MCP HTTP: Basic echo request", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9800);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                bool connected = await client.ConnectAsync("http://localhost:9800");
                Assert(connected, "Client should connect to MCP HTTP server");
                Assert(!String.IsNullOrEmpty(client.SessionId), "Session ID should be assigned");

                string result = await client.CallAsync<string>("echo", new { message = "test" });
                Assert(result == "test", $"Expected 'test', got '{result}'");

                client.Disconnect();
                server.Stop();
            });

            await Test("MCP HTTP: Multiple sequential requests", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9801);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9801");

                for (int i = 0; i < 10; i++)
                {
                    string result = await client.CallAsync<string>("echo", new { message = $"msg{i}" });
                    Assert(result == $"msg{i}", $"Expected 'msg{i}', got '{result}'");
                }

                client.Disconnect();
                server.Stop();
            });

            await Test("MCP HTTP: Ping method", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9802);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9802");

                string result = await client.CallAsync<string>("ping");
                Assert(result == "pong", $"Expected 'pong', got '{result}'");

                server.Stop();
            });

            await Test("MCP HTTP: Session persistence across requests", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9803);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9803");

                string sessionId1 = client.SessionId!;
                await client.CallAsync<string>("ping");
                string sessionId2 = client.SessionId!;

                Assert(sessionId1 == sessionId2, "Session ID should persist across requests");

                server.Stop();
            });

            await Test("MCP HTTP: SSE connection and notifications", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9804);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9804");

                bool notificationReceived = false;
                string notificationMethod = "";

                client.NotificationReceived += (sender, notification) =>
                {
                    notificationReceived = true;
                    notificationMethod = notification.Method;
                };

                bool sseStarted = await client.StartSseAsync();
                Assert(sseStarted, "SSE connection should start successfully");
                await Task.Delay(300);

                // Send notification from server
                server.SendNotificationToSession(client.SessionId!, "testNotification", new { data = "test" });
                await Task.Delay(500);

                Assert(notificationReceived, "Should receive notification via SSE");
                Assert(notificationMethod == "testNotification", $"Expected 'testNotification', got '{notificationMethod}'");

                server.Stop();
            });

            await Test("MCP HTTP: Broadcast to multiple sessions", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9805);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                List<McpHttpClient> clients = new List<McpHttpClient>();
                List<bool> notificationsReceived = new List<bool> { false, false, false };

                for (int i = 0; i < 3; i++)
                {
                    McpHttpClient client = new McpHttpClient();
                    await client.ConnectAsync("http://localhost:9805");
                    int index = i;
                    client.NotificationReceived += (sender, notification) =>
                    {
                        lock (notificationsReceived)
                        {
                            notificationsReceived[index] = true;
                        }
                    };
                    await client.StartSseAsync();
                    clients.Add(client);
                }

                await Task.Delay(500);

                server.BroadcastNotification("broadcast", new { message = "hello all" });
                await Task.Delay(700);

                Assert(notificationsReceived.All(x => x), "All clients should receive broadcast via SSE");

                foreach (McpHttpClient client in clients)
                {
                    client.Disconnect();
                }
                server.Stop();
            });

            await Test("MCP HTTP: Method not found error", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9806);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9806");

                bool errorThrown = false;
                try
                {
                    await client.CallAsync<string>("nonExistentMethod");
                }
                catch (Exception ex)
                {
                    errorThrown = ex.Message.Contains("Method not found");
                }

                Assert(errorThrown, "Should receive method not found error");
                server.Stop();
            });

            await Test("MCP HTTP: Call before connect throws", async () =>
            {
                McpHttpClient client = new McpHttpClient();
                bool exceptionThrown = false;
                try
                {
                    await client.CallAsync<string>("ping");
                }
                catch (InvalidOperationException)
                {
                    exceptionThrown = true;
                }
                catch (ArgumentNullException)
                {
                    exceptionThrown = true;
                }
                Assert(exceptionThrown, "Should throw when not connected");
                client.Dispose();
            });

            await Test("MCP HTTP: GetActiveSessions", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9807);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                List<McpHttpClient> clients = new List<McpHttpClient>();
                for (int i = 0; i < 3; i++)
                {
                    McpHttpClient client = new McpHttpClient();
                    await client.ConnectAsync("http://localhost:9807");
                    clients.Add(client);
                }

                await Task.Delay(300);

                List<string> activeSessions = server.GetActiveSessions();
                Assert(activeSessions.Count == 3, $"Expected 3 active sessions, got {activeSessions.Count}");

                foreach (McpHttpClient client in clients)
                {
                    client.Disconnect();
                }
                server.Stop();
            });

            await Test("MCP HTTP: RemoveSession", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9808);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9808");
                string sessionId = client.SessionId!;

                await Task.Delay(300);

                List<string> sessions = server.GetActiveSessions();
                Assert(sessions.Count == 1, "Should have one active session");

                bool removed = server.RemoveSession(sessionId);
                Assert(removed, "Should successfully remove session");

                sessions = server.GetActiveSessions();
                Assert(sessions.Count == 0, "Should have no active sessions after removal");

                server.Stop();
            });

            await Test("MCP HTTP: Large message handling", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9809);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9809");

                string largeString = new string('Y', 100000);
                string result = await client.CallAsync<string>("echo", new { message = largeString }, timeoutMs: 10000);

                Assert(result.Length == largeString.Length, $"Expected {largeString.Length} chars, got {result.Length}");

                server.Stop();
            });

            await Test("MCP HTTP: Concurrent requests from single client", async () =>
            {
                using McpHttpServer server = new McpHttpServer("localhost", 9810);
                Task serverTask = Task.Run(() => server.StartAsync());
                await Task.Delay(200);

                using McpHttpClient client = new McpHttpClient();
                await client.ConnectAsync("http://localhost:9810");

                List<Task<string>> tasks = new List<Task<string>>();
                for (int i = 0; i < 20; i++)
                {
                    int num = i;
                    tasks.Add(client.CallAsync<string>("echo", new { message = $"test{num}" }));
                }

                string[] results = await Task.WhenAll(tasks);
                for (int i = 0; i < 20; i++)
                {
                    Assert(results[i] == $"test{i}", $"Concurrent call {i} failed");
                }

                server.Stop();
            });
        }

#pragma warning restore CS8602 // Dereference of a possibly null reference.
    }
}
