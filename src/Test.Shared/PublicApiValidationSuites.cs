namespace Test.Shared
{
    using System.Net;
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic;

    public static class PublicApiValidationSuites
    {
        public static TestSuiteDescriptor ClientApiValidation()
        {
            const string suiteId = "PublicApi.Clients.Validation";

            return new TestSuiteDescriptor(
                suiteId,
                "Client API Validation Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "JsonRpcClientDefaults", "JsonRpcClient exposes disconnected defaults", ct =>
                    {
                        using JsonRpcClient client = new JsonRpcClient();
                        TestAssert.False(client.IsConnected, "Client should start disconnected.");
                        TestAssert.Null(client.TcpClient, "TCP client should start null.");
                        TestAssert.Null(client.TokenSource, "Token source should start null.");
                        TestAssert.Equal("application/json; charset=utf-8", client.DefaultContentType);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "JsonRpcClientDefaultContentTypeNullFallback", "JsonRpcClient DefaultContentType falls back on null", ct =>
                    {
                        using JsonRpcClient client = new JsonRpcClient();
                        client.DefaultContentType = "application/custom";
                        TestAssert.Equal("application/custom", client.DefaultContentType);
                        client.DefaultContentType = null!;
                        TestAssert.Equal("application/json; charset=utf-8", client.DefaultContentType);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "JsonRpcClientConnectValidation", "JsonRpcClient ConnectAsync validates host and port", async ct =>
                    {
                        using JsonRpcClient client = new JsonRpcClient();
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => client.ConnectAsync(null!, 1, ct), "Null host should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => client.ConnectAsync("", 1, ct), "Empty host should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ConnectAsync("localhost", -1, ct), "Negative port should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ConnectAsync("localhost", 65536, ct), "Port above range should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "JsonRpcClientRefusedConnectionReturnsFalse", "JsonRpcClient ConnectAsync returns false when no server is listening", async ct =>
                    {
                        int port = TestPorts.GetFreePort();
                        using JsonRpcClient client = new JsonRpcClient();
                        bool connected = await client.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false);
                        TestAssert.False(connected, "Refused connections should return false.");
                    }),

                    Case(suiteId, "JsonRpcClientDisconnectedCallAndNotifyFail", "JsonRpcClient rejects calls and notifications when disconnected", async ct =>
                    {
                        using JsonRpcClient client = new JsonRpcClient();
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync<string>("ping", token: ct), "Disconnected call should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.NotifyAsync("event", token: ct), "Disconnected notify should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "JsonRpcClientDisconnectDisposeIdempotent", "JsonRpcClient disconnect and dispose are idempotent", ct =>
                    {
                        JsonRpcClient client = new JsonRpcClient();
                        client.Disconnect();
                        client.Disconnect();
                        client.Dispose();
                        client.Dispose();
                        TestAssert.False(client.IsConnected, "Disposed client should not be connected.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpTcpClientIsJsonRpcClient", "McpTcpClient keeps JsonRpcClient behavior", ct =>
                    {
                        using McpTcpClient client = new McpTcpClient();
                        TestAssert.False(client.IsConnected, "TCP MCP client should start disconnected.");
                        client.DefaultContentType = null!;
                        TestAssert.Equal("application/json; charset=utf-8", client.DefaultContentType);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpClientDisconnectedBehavior", "McpClient rejects calls and notifications before launch", async ct =>
                    {
                        using McpClient client = new McpClient();
                        TestAssert.False(client.IsConnected, "MCP stdio client should start disconnected.");
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync<string>("ping", token: ct), "Disconnected call should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.NotifyAsync("event", token: ct), "Disconnected notify should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "McpClientLaunchFailureReturnsFalse", "McpClient LaunchServerAsync returns false for a missing executable", async ct =>
                    {
                        using McpClient client = new McpClient();
                        bool launched = await client.LaunchServerAsync("definitely-not-a-real-voltaic-test-executable.exe", Array.Empty<string>(), ct).ConfigureAwait(false);
                        TestAssert.False(launched, "Missing process should not launch.");
                    }),

                    Case(suiteId, "McpClientShutdownDisposeIdempotent", "McpClient shutdown and dispose are idempotent", ct =>
                    {
                        McpClient client = new McpClient();
                        client.Shutdown();
                        client.Shutdown();
                        client.Dispose();
                        client.Dispose();
                        TestAssert.False(client.IsConnected, "Disposed stdio client should not be connected.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpHttpClientDefaults", "McpHttpClient exposes disconnected defaults", ct =>
                    {
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.Null(client.SessionId, "Session should start null.");
                        TestAssert.False(client.IsConnected, "HTTP client should start disconnected.");
                        TestAssert.False(client.IsSseConnected, "SSE should start disconnected.");
                        TestAssert.Equal(30000, client.RequestTimeoutMs);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpHttpClientRequestTimeoutValidation", "McpHttpClient validates RequestTimeoutMs", ct =>
                    {
                        using McpHttpClient client = new McpHttpClient();
                        client.RequestTimeoutMs = 1000;
                        TestAssert.Equal(1000, client.RequestTimeoutMs);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => client.RequestTimeoutMs = 999, "Timeout below 1000ms should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpHttpClientConnectValidation", "McpHttpClient validates connection URLs", async ct =>
                    {
                        using McpHttpClient client = new McpHttpClient();
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => client.ConnectAsync(null!, token: ct), "Null URL should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => client.ConnectAsync("", token: ct), "Empty URL should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => client.ConnectStreamableAsync(null!, token: ct), "Null streamable URL should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => client.ConnectStreamableAsync("", token: ct), "Empty streamable URL should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "McpHttpClientDisconnectedOperations", "McpHttpClient rejects disconnected SSE and calls", async ct =>
                    {
                        using McpHttpClient client = new McpHttpClient();
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.StartSseAsync(ct), "SSE without a session should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync<string>("ping", token: ct), "Call without connection should fail.").ConfigureAwait(false);
                        client.StopSse();
                        client.Disconnect();
                    }),

                    Case(suiteId, "McpWebsocketsClientDefaultsAndValidation", "McpWebsocketsClient validates disconnected operations", async ct =>
                    {
                        using McpWebsocketsClient client = new McpWebsocketsClient();
                        TestAssert.False(client.IsConnected, "WebSocket client should start disconnected.");
                        TestAssert.Equal(1048576, client.MaxMessageSize);
                        client.MaxMessageSize = 4096;
                        TestAssert.Equal(4096, client.MaxMessageSize);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => client.MaxMessageSize = 4095, "Message size below 4096 should fail.");
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => client.ConnectAsync(null!, ct), "Null URL should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => client.ConnectAsync("", ct), "Empty URL should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync<string>("ping", token: ct), "Disconnected call should fail.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.NotifyAsync("event", token: ct), "Disconnected notify should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "McpWebsocketsClientInvalidUrlReturnsFalse", "McpWebsocketsClient returns false for malformed URLs", async ct =>
                    {
                        using McpWebsocketsClient client = new McpWebsocketsClient();
                        bool connected = await client.ConnectAsync("not-a-uri", ct).ConfigureAwait(false);
                        TestAssert.False(connected, "Malformed URL should not connect.");
                    }),
                });
        }

        public static TestSuiteDescriptor ServerApiValidation()
        {
            const string suiteId = "PublicApi.Servers.Validation";

            return new TestSuiteDescriptor(
                suiteId,
                "Server API Validation Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "JsonRpcServerConstructorValidation", "JsonRpcServer constructor validates address and port", ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(() => new JsonRpcServer(null!, 0), "Null IP should fail.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => new JsonRpcServer(IPAddress.Loopback, -1), "Negative port should fail.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => new JsonRpcServer(IPAddress.Loopback, 65536), "Port above range should fail.");
                        using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 0, includeDefaultMethods: false);
                        TestAssert.Equal(100, server.MaxQueueSize);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "JsonRpcServerPropertyValidation", "JsonRpcServer validates MaxQueueSize and DefaultContentType", ct =>
                    {
                        using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 0, includeDefaultMethods: false);
                        server.MaxQueueSize = 1;
                        TestAssert.Equal(1, server.MaxQueueSize);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.MaxQueueSize = 0, "Queue size below one should fail.");
                        server.DefaultContentType = "application/json";
                        TestAssert.Equal("application/json", server.DefaultContentType);
                        server.DefaultContentType = null!;
                        TestAssert.Equal("application/json; charset=utf-8", server.DefaultContentType);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "JsonRpcServerRegisterMethodValidation", "JsonRpcServer RegisterMethod overloads validate handlers", ct =>
                    {
                        using JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 0, includeDefaultMethods: false);
                        TestAssert.Throws<ArgumentNullException>(() => server.RegisterMethod("sync", (Func<JsonElement?, object>)null!), "Sync handler should be required.");
                        TestAssert.Throws<ArgumentNullException>(() => server.RegisterMethod("async", (Func<JsonElement?, Task<object>>)null!), "Async handler should be required.");
                        TestAssert.Throws<ArgumentNullException>(() => server.RegisterMethod("token", (Func<JsonElement?, CancellationToken, Task<object>>)null!), "Token handler should be required.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "JsonRpcServerStopDisposeIdempotent", "JsonRpcServer Stop and Dispose are idempotent", ct =>
                    {
                        JsonRpcServer server = new JsonRpcServer(IPAddress.Loopback, 0, includeDefaultMethods: false);
                        server.Stop();
                        server.Stop();
                        server.Dispose();
                        server.Dispose();
                        TestAssert.Equal(0, server.GetConnectedClients().Count);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpServerPropertiesAndDispose", "McpServer properties use null fallbacks and Dispose is idempotent", ct =>
                    {
                        McpServer server = new McpServer(includeDefaultMethods: false);
                        server.ProtocolVersion = "custom";
                        server.ServerName = "name";
                        server.ServerVersion = "version";
                        TestAssert.Equal("custom", server.ProtocolVersion);
                        TestAssert.Equal("name", server.ServerName);
                        TestAssert.Equal("version", server.ServerVersion);
                        server.ProtocolVersion = null!;
                        server.ServerName = null!;
                        server.ServerVersion = null!;
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, server.ProtocolVersion);
                        TestAssert.Equal("Voltaic.Mcp.StdioServer", server.ServerName);
                        TestAssert.Equal("1.0.0", server.ServerVersion);
                        server.Dispose();
                        server.Dispose();
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpServerRegisterMethodValidation", "McpServer RegisterMethod overloads validate handlers", ct =>
                    {
                        using McpServer server = new McpServer(includeDefaultMethods: false);
                        TestAssert.Throws<ArgumentNullException>(() => server.RegisterMethod("sync", (Func<JsonElement?, object>)null!), "Sync handler should be required.");
                        TestAssert.Throws<ArgumentNullException>(() => server.RegisterMethod("async", (Func<JsonElement?, Task<object>>)null!), "Async handler should be required.");
                        TestAssert.Throws<ArgumentNullException>(() => server.RegisterMethod("token", (Func<JsonElement?, CancellationToken, Task<object>>)null!), "Token handler should be required.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpServerRegistrationValidation", "McpServer validates tools, resources, templates, and prompts", ct =>
                    {
                        using McpServer server = new McpServer(includeDefaultMethods: false);
                        TestRegisterValidation(server.RegisterTool, server.RegisterResource, server.RegisterResourceTemplate, server.RegisterPrompt);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpHttpServerConstructorValidation", "McpHttpServer constructor validates host and port", ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(() => new McpHttpServer(null!, 0), "Null host should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => new McpHttpServer("", 0), "Empty host should fail.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => new McpHttpServer("localhost", -1), "Negative port should fail.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => new McpHttpServer("localhost", 65536), "Port above range should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpHttpServerProperties", "McpHttpServer validates mutable server properties", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("localhost", 0, includeDefaultMethods: false);
                        server.SessionTimeoutSeconds = 10;
                        TestAssert.Equal(10, server.SessionTimeoutSeconds);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.SessionTimeoutSeconds = 9, "Session timeout below ten should fail.");
                        server.MaxQueueSize = 1;
                        TestAssert.Equal(1, server.MaxQueueSize);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.MaxQueueSize = 0, "Queue size below one should fail.");
                        server.CorsHeaders = null!;
                        TestAssert.Equal(0, server.CorsHeaders.Count);
                        server.EnableCors = false;
                        TestAssert.False(server.EnableCors, "EnableCors should be settable.");
                        server.ProtocolVersion = null!;
                        server.ServerName = null!;
                        server.ServerVersion = null!;
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, server.ProtocolVersion);
                        TestAssert.Equal("Voltaic.Mcp.HttpServer", server.ServerName);
                        TestAssert.Equal("1.0.0", server.ServerVersion);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpHttpServerSessionHelpers", "McpHttpServer session helper methods handle missing sessions", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("localhost", 0, includeDefaultMethods: false);
                        TestAssert.False(server.SendNotificationToSession("missing", "event"), "Missing session should not queue notifications.");
                        server.BroadcastNotification("event");
                        server.NotifyToolsChanged();
                        server.NotifyResourcesChanged();
                        server.NotifyPromptsChanged();
                        TestAssert.Throws<ArgumentNullException>(() => server.NotifyResourceUpdated(null!), "Null URI should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => server.NotifyResourceUpdated(""), "Empty URI should fail.");
                        TestAssert.Equal(0, server.GetActiveSessions().Count);
                        TestAssert.Equal(0, server.GetConnectedClients().Count);
                        TestAssert.False(server.KickClient("missing"), "Missing client should not be kicked.");
                        TestAssert.False(server.RemoveSession("missing"), "Missing session should not be removed.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpHttpServerRegistrationValidation", "McpHttpServer validates tools, resources, templates, and prompts", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("localhost", 0, includeDefaultMethods: false);
                        TestRegisterValidation(server.RegisterTool, server.RegisterResource, server.RegisterResourceTemplate, server.RegisterPrompt);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpTcpServerPropertiesAndValidation", "McpTcpServer validates constructor, properties, and notification helpers", async ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(() => new McpTcpServer(null!, 0), "Null IP should fail.");
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 0, includeDefaultMethods: false);
                        server.ProtocolVersion = null!;
                        server.ServerName = null!;
                        server.ServerVersion = null!;
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, server.ProtocolVersion);
                        TestAssert.Equal("Voltaic.Mcp.TcpServer", server.ServerName);
                        TestAssert.Equal("1.0.0", server.ServerVersion);
                        await server.NotifyToolsChangedAsync(ct).ConfigureAwait(false);
                        await server.NotifyResourcesChangedAsync(ct).ConfigureAwait(false);
                        await server.NotifyPromptsChangedAsync(ct).ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => server.NotifyResourceUpdatedAsync(null!, ct), "Null URI should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "McpTcpServerRegistrationValidation", "McpTcpServer validates tools, resources, templates, and prompts", ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, 0, includeDefaultMethods: false);
                        TestRegisterValidation(server.RegisterTool, server.RegisterResource, server.RegisterResourceTemplate, server.RegisterPrompt);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpWebsocketsServerConstructorValidation", "McpWebsocketsServer constructor validates host and port", ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(() => new McpWebsocketsServer(null!, 0), "Null host should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => new McpWebsocketsServer("", 0), "Empty host should fail.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => new McpWebsocketsServer("localhost", -1), "Negative port should fail.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => new McpWebsocketsServer("localhost", 65536), "Port above range should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "McpWebsocketsServerProperties", "McpWebsocketsServer validates mutable properties", async ct =>
                    {
                        using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 0, includeDefaultMethods: false);
                        server.MaxMessageSize = 4096;
                        TestAssert.Equal(4096, server.MaxMessageSize);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.MaxMessageSize = 4095, "Message size below 4096 should fail.");
                        server.KeepAliveIntervalSeconds = 0;
                        TestAssert.Equal(0, server.KeepAliveIntervalSeconds);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.KeepAliveIntervalSeconds = -1, "Negative keep-alive should fail.");
                        server.MaxQueueSize = 1;
                        TestAssert.Equal(1, server.MaxQueueSize);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.MaxQueueSize = 0, "Queue size below one should fail.");
                        server.ProtocolVersion = null!;
                        server.ServerName = null!;
                        server.ServerVersion = null!;
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, server.ProtocolVersion);
                        TestAssert.Equal("Voltaic.Mcp.WebSocketsServer", server.ServerName);
                        TestAssert.Equal("1.0.0", server.ServerVersion);
                        await server.NotifyToolsChangedAsync(ct).ConfigureAwait(false);
                        await server.NotifyResourcesChangedAsync(ct).ConfigureAwait(false);
                        await server.NotifyPromptsChangedAsync(ct).ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => server.NotifyResourceUpdatedAsync(null!, ct), "Null URI should fail.").ConfigureAwait(false);
                        TestAssert.False(server.KickClient("missing"), "Missing client should not be kicked.");
                        TestAssert.Equal(0, server.GetConnectedClients().Count);
                    }),

                    Case(suiteId, "McpWebsocketsServerRegistrationValidation", "McpWebsocketsServer validates tools, resources, templates, and prompts", ct =>
                    {
                        using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 0, includeDefaultMethods: false);
                        TestRegisterValidation(server.RegisterTool, server.RegisterResource, server.RegisterResourceTemplate, server.RegisterPrompt);
                        return Task.CompletedTask;
                    }),
                });
        }

        private static void TestRegisterValidation(
            Action<string, string, object, Func<JsonElement?, object>> registerTool,
            Action<string, string, string, Func<McpReadResourceResult>> registerResource,
            Action<string, string, string, Func<string, McpReadResourceResult>> registerTemplate,
            Action<string, string, IEnumerable<McpPromptArgument>?, Func<JsonElement?, McpGetPromptResult>> registerPrompt)
        {
            TestAssert.Throws<ArgumentNullException>(() => registerTool(null!, "description", new { type = "object" }, _ => "ok"), "Null tool name should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTool("", "description", new { type = "object" }, _ => "ok"), "Empty tool name should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTool("tool", null!, new { type = "object" }, _ => "ok"), "Null tool description should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTool("tool", "", new { type = "object" }, _ => "ok"), "Empty tool description should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTool("tool", "description", null!, _ => "ok"), "Null input schema should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTool("tool", "description", new { type = "object" }, null!), "Null tool handler should fail.");

            TestAssert.Throws<ArgumentNullException>(() => registerResource(null!, "name", "text/plain", () => new McpReadResourceResult()), "Null resource URI should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerResource("", "name", "text/plain", () => new McpReadResourceResult()), "Empty resource URI should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerResource("voltaic://r", null!, "text/plain", () => new McpReadResourceResult()), "Null resource name should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerResource("voltaic://r", "", "text/plain", () => new McpReadResourceResult()), "Empty resource name should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerResource("voltaic://r", "name", "text/plain", null!), "Null resource handler should fail.");

            TestAssert.Throws<ArgumentNullException>(() => registerTemplate(null!, "name", "text/plain", uri => new McpReadResourceResult()), "Null template URI should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTemplate("", "name", "text/plain", uri => new McpReadResourceResult()), "Empty template URI should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTemplate("voltaic://{name}", null!, "text/plain", uri => new McpReadResourceResult()), "Null template name should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTemplate("voltaic://{name}", "", "text/plain", uri => new McpReadResourceResult()), "Empty template name should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerTemplate("voltaic://{name}", "name", "text/plain", null!), "Null template handler should fail.");

            TestAssert.Throws<ArgumentNullException>(() => registerPrompt(null!, "description", null, args => new McpGetPromptResult()), "Null prompt name should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerPrompt("", "description", null, args => new McpGetPromptResult()), "Empty prompt name should fail.");
            TestAssert.Throws<ArgumentNullException>(() => registerPrompt("prompt", "description", null, null!), "Null prompt handler should fail.");
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "validation", "matrix" });
        }
    }
}
