namespace Test.Shared
{
    using System.Net;
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;
    public static class McpServerApiSuites
    {
        public static TestSuiteDescriptor PublicRegistrationApi()
        {
            const string suiteId = "McpServer.Api";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Server Public API",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "DefaultProtocolVersionAcrossServers", "MCP servers default to the target protocol version", ct =>
                    {
                        using McpServer stdio = new McpServer(includeDefaultMethods: false);
                        using McpHttpServer http = new McpHttpServer("localhost", 0, includeDefaultMethods: false);
                        using McpTcpServer tcp = new McpTcpServer(IPAddress.Loopback, 0, includeDefaultMethods: false);
                        using McpWebsocketsServer websocket = new McpWebsocketsServer("localhost", 0, includeDefaultMethods: false);

                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, stdio.ProtocolVersion);
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, http.ProtocolVersion);
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, tcp.ProtocolVersion);
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, websocket.ProtocolVersion);

                        stdio.ProtocolVersion = null!;
                        http.ProtocolVersion = null!;
                        tcp.ProtocolVersion = null!;
                        websocket.ProtocolVersion = null!;

                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, stdio.ProtocolVersion);
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, http.ProtocolVersion);
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, tcp.ProtocolVersion);
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, websocket.ProtocolVersion);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ToolRegistrationValidation", "RegisterTool overloads validate required metadata", ct =>
                    {
                        using McpServer server = new McpServer(includeDefaultMethods: false);

                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterTool(null!, "description", new { type = "object" }, _ => "ok"),
                            "Tool name should be required.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterTool("tool", null!, new { type = "object" }, _ => "ok"),
                            "Tool description should be required.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterTool("tool", "description", null!, _ => "ok"),
                            "Input schema should be required.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterTool("tool", "description", new { type = "object" }, (Func<JsonElement?, object>)null!),
                            "Tool handler should be required.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResourceAndPromptRegistrationValidation", "Resource and prompt APIs validate required data", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("localhost", 0, includeDefaultMethods: false);

                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterResource(null!, "name", "text/plain", () => new McpReadResourceResult()),
                            "Resource URI should be required.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterResource("voltaic://resource", null!, "text/plain", () => new McpReadResourceResult()),
                            "Resource name should be required.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterResource("voltaic://resource", "name", "text/plain", null!),
                            "Resource handler should be required.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterResourceTemplate(null!, "template", "text/plain", uri => new McpReadResourceResult()),
                            "Template URI should be required.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterPrompt(null!, "description", null, _ => new McpGetPromptResult()),
                            "Prompt name should be required.");
                        TestAssert.Throws<ArgumentNullException>(
                            () => server.RegisterPrompt("prompt", "description", null, null!),
                            "Prompt handler should be required.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "FullToolDefinitionRegistration", "Full tool definitions preserve metadata and direct method compatibility", ct =>
                    {
                        using McpWebsocketsServer server = new McpWebsocketsServer("localhost", 0, includeDefaultMethods: false);
                        ToolDefinition definition = new ToolDefinition
                        {
                            Name = "inspect",
                            Description = "Inspects input",
                            InputSchema = new { type = "object" },
                            OutputSchema = new { type = "object" },
                            Title = "Inspect",
                            Annotations = new McpAnnotations { ReadOnlyHint = true }
                        };

                        server.RegisterTool(definition, args => McpToolCallResult.FromStructured(new { ok = true }));

                        string json = JsonSerializer.Serialize(definition);
                        TestAssert.True(json.Contains("\"outputSchema\""), "Output schema should be retained.");
                        TestAssert.True(json.Contains("\"readOnlyHint\":true"), "Annotations should be retained.");
                        return Task.CompletedTask;
                    }),
                });
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "server" });
        }
    }
}
