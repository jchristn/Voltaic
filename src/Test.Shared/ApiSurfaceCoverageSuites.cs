namespace Test.Shared
{
    using System.Reflection;
    using System.Text.Json.Serialization;
    using Touchstone.Core;
    using Voltaic;

    public static class ApiSurfaceCoverageSuites
    {
        public static TestSuiteDescriptor PublicApiInventory()
        {
            const string suiteId = "ApiSurface.Inventory";

            return new TestSuiteDescriptor(
                suiteId,
                "Public API Surface Inventory",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ExportedTypesAreTracked", "Every exported Voltaic type is tracked by the test matrix", ct =>
                    {
                        string[] actual = typeof(JsonRpcClient).Assembly.GetExportedTypes()
                            .Where(type => type.Namespace == "Voltaic")
                            .Select(type => type.Name)
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .ToArray();

                        string[] expected =
                        {
                            "AuthenticationResult",
                            "ClientConnectedEventArgs",
                            "ClientConnection",
                            "ClientConnectionTypeEnum",
                            "ClientDisconnectedEventArgs",
                            "JsonRpcClient",
                            "JsonRpcError",
                            "JsonRpcRequest",
                            "JsonRpcRequestEventArgs",
                            "JsonRpcResponse",
                            "JsonRpcResponseEventArgs",
                            "JsonRpcServer",
                            "McpAnnotations",
                            "McpAudioContent",
                            "McpBlobResourceContents",
                            "McpCancelledNotification",
                            "McpClient",
                            "McpClientCapabilities",
                            "McpCompleteRequest",
                            "McpCompleteResult",
                            "McpCompletion",
                            "McpCompletionArgument",
                            "McpCompletionContext",
                            "McpCompletionReference",
                            "McpEmbeddedResourceContent",
                            "McpGetPromptResult",
                            "McpHttpClient",
                            "McpHttpServer",
                            "McpIcon",
                            "McpImageContent",
                            "McpImplementation",
                            "McpListChangedCapability",
                            "McpListPromptsResult",
                            "McpListResourceTemplatesResult",
                            "McpListResourcesResult",
                            "McpListToolsResult",
                            "McpLogMessageNotification",
                            "McpPaginatedResult",
                            "McpProgressNotification",
                            "McpPrompt",
                            "McpPromptArgument",
                            "McpPromptMessage",
                            "McpProtocol",
                            "McpProtocolException",
                            "McpReadResourceResult",
                            "McpResource",
                            "McpResourceCapability",
                            "McpResourceLinkContent",
                            "McpResourceTemplate",
                            "McpResult",
                            "McpServer",
                            "McpServerCapabilities",
                            "McpSessionLifecycleState",
                            "McpSetLogLevelRequest",
                            "McpTcpClient",
                            "McpTcpServer",
                            "McpTextContent",
                            "McpTextResourceContents",
                            "McpToolCallResult",
                            "McpWebsocketsClient",
                            "McpWebsocketsServer",
                            "MessageFraming",
                            "RequestSentEventArgs",
                            "ResponseReceivedEventArgs",
                            "ToolDefinition"
                        };

                        TestAssert.Equal(String.Join(",", expected.OrderBy(name => name, StringComparer.Ordinal)), String.Join(",", actual));
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "DisposableTypes", "Disposable public types remain covered by disposal tests", ct =>
                    {
                        string[] disposableTypes = typeof(JsonRpcClient).Assembly.GetExportedTypes()
                            .Where(type => type.Namespace == "Voltaic" && typeof(IDisposable).IsAssignableFrom(type))
                            .Select(type => type.Name)
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .ToArray();

                        string[] expected =
                        {
                            "ClientConnection",
                            "JsonRpcClient",
                            "JsonRpcServer",
                            "McpClient",
                            "McpHttpClient",
                            "McpHttpServer",
                            "McpServer",
                            "McpTcpClient",
                            "McpTcpServer",
                            "McpWebsocketsClient",
                            "McpWebsocketsServer"
                        };

                        TestAssert.Equal(String.Join(",", expected), String.Join(",", disposableTypes));
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "EventSourceTypes", "Event source types expose the expected public events", ct =>
                    {
                        AssertEvents<JsonRpcClient>("Log", "NotificationReceived", "Connected", "Disconnected", "RequestSent", "ResponseReceived");
                        AssertEvents<JsonRpcServer>("Log", "ClientConnected", "ClientDisconnected", "RequestReceived", "ResponseSent");
                        AssertEvents<McpClient>("Log", "NotificationReceived", "Connected", "Disconnected", "RequestSent", "ResponseReceived");
                        AssertEvents<McpHttpClient>("Log", "NotificationReceived", "Connected", "Disconnected", "RequestSent", "ResponseReceived");
                        AssertEvents<McpHttpServer>("Log", "ClientConnected", "ClientDisconnected", "RequestReceived", "ResponseSent");
                        AssertEvents<McpWebsocketsClient>("Log", "NotificationReceived", "Connected", "Disconnected", "RequestSent", "ResponseReceived");
                        AssertEvents<McpWebsocketsServer>("Log", "ClientConnected", "ClientDisconnected", "RequestReceived", "ResponseSent");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "JsonWireNames", "Protocol model properties keep expected JSON wire names", ct =>
                    {
                        AssertJsonProperty<JsonRpcRequest>(nameof(JsonRpcRequest.JsonRpc), "jsonrpc");
                        AssertJsonProperty<JsonRpcRequest>(nameof(JsonRpcRequest.Method), "method");
                        AssertJsonProperty<JsonRpcRequest>(nameof(JsonRpcRequest.Params), "params");
                        AssertJsonProperty<JsonRpcRequest>(nameof(JsonRpcRequest.Id), "id");
                        AssertJsonProperty<ToolDefinition>(nameof(ToolDefinition.InputSchema), "inputSchema");
                        AssertJsonProperty<ToolDefinition>(nameof(ToolDefinition.OutputSchema), "outputSchema");
                        AssertJsonProperty<ToolDefinition>(nameof(ToolDefinition.Meta), "_meta");
                        AssertJsonProperty<McpResourceTemplate>(nameof(McpResourceTemplate.UriTemplate), "uriTemplate");
                        AssertJsonProperty<McpPaginatedResult>(nameof(McpPaginatedResult.NextCursor), "nextCursor");
                        AssertJsonProperty<McpToolCallResult>(nameof(McpToolCallResult.StructuredContent), "structuredContent");
                        AssertJsonProperty<McpCompleteRequest>(nameof(McpCompleteRequest.Ref), "ref");
                        AssertJsonProperty<McpCompletionReference>(nameof(McpCompletionReference.Type), "type");
                        AssertJsonProperty<McpCompletionArgument>(nameof(McpCompletionArgument.Value), "value");
                        AssertJsonProperty<McpCompleteResult>(nameof(McpCompleteResult.Completion), "completion");
                        AssertJsonProperty<McpProgressNotification>(nameof(McpProgressNotification.ProgressToken), "progressToken");
                        AssertJsonProperty<McpLogMessageNotification>(nameof(McpLogMessageNotification.Level), "level");
                        return Task.CompletedTask;
                    }),
                });
        }

        private static void AssertEvents<T>(params string[] expected)
        {
            string[] actual = typeof(T).GetEvents(BindingFlags.Instance | BindingFlags.Public)
                .Select(evt => evt.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string[] sortedExpected = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            TestAssert.Equal(String.Join(",", sortedExpected), String.Join(",", actual));
        }

        private static void AssertJsonProperty<T>(string propertyName, string expectedWireName)
        {
            PropertyInfo? property = typeof(T).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            TestAssert.NotNull(property, $"{typeof(T).Name}.{propertyName} should exist.");
            JsonPropertyNameAttribute? attribute = property!.GetCustomAttribute<JsonPropertyNameAttribute>();
            TestAssert.NotNull(attribute, $"{typeof(T).Name}.{propertyName} should declare JsonPropertyName.");
            TestAssert.Equal(expectedWireName, attribute!.Name);
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "coverage", "matrix" });
        }
    }
}
