namespace Test.Shared
{
    using System.Reflection;
    using System.Text.Json.Serialization;
    using Touchstone.Core;
    using Voltaic.A2A;
    using Voltaic.Core;
    using Voltaic.Mcp;
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
                            .Where(type => type.Namespace != null && type.Namespace.StartsWith("Voltaic.", StringComparison.Ordinal))
                            .Select(type => type.Namespace + "." + type.Name)
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .ToArray();

                        string[] expected =
                        {
                            "Voltaic.A2A.A2AAgentEventQueue",
                            "Voltaic.A2A.A2ACardResolver",
                            "Voltaic.A2A.A2AClient",
                            "Voltaic.A2A.A2AErrorCode",
                            "Voltaic.A2A.A2AGrpcClient",
                            "Voltaic.A2A.A2AGrpcServer",
                            "Voltaic.A2A.A2AHttpJsonClient",
                            "Voltaic.A2A.A2AHttpServer",
                            "Voltaic.A2A.A2AJson",
                            "Voltaic.A2A.A2AProtocol",
                            "Voltaic.A2A.A2AProtocolException",
                            "Voltaic.A2A.A2ARequestContext",
                            "Voltaic.A2A.A2ATaskUpdater",
                            "Voltaic.A2A.AgentCapabilities",
                            "Voltaic.A2A.AgentCard",
                            "Voltaic.A2A.AgentCardSignature",
                            "Voltaic.A2A.AgentExtension",
                            "Voltaic.A2A.AgentInterface",
                            "Voltaic.A2A.AgentProvider",
                            "Voltaic.A2A.AgentSkill",
                            "Voltaic.A2A.AgentTask",
                            "Voltaic.A2A.ApiKeySecurityScheme",
                            "Voltaic.A2A.Artifact",
                            "Voltaic.A2A.AuthenticationInfo",
                            "Voltaic.A2A.CancelTaskRequest",
                            "Voltaic.A2A.CreateTaskPushNotificationConfigRequest",
                            "Voltaic.A2A.DeleteTaskPushNotificationConfigRequest",
                            "Voltaic.A2A.GetExtendedAgentCardRequest",
                            "Voltaic.A2A.GetTaskPushNotificationConfigRequest",
                            "Voltaic.A2A.GetTaskRequest",
                            "Voltaic.A2A.HttpAuthSecurityScheme",
                            "Voltaic.A2A.IA2AAgentHandler",
                            "Voltaic.A2A.IA2ATaskStore",
                            "Voltaic.A2A.InMemoryA2ATaskStore",
                            "Voltaic.A2A.ListTaskPushNotificationConfigRequest",
                            "Voltaic.A2A.ListTaskPushNotificationConfigResponse",
                            "Voltaic.A2A.ListTasksRequest",
                            "Voltaic.A2A.ListTasksResponse",
                            "Voltaic.A2A.Message",
                            "Voltaic.A2A.MutualTlsSecurityScheme",
                            "Voltaic.A2A.OAuth2SecurityScheme",
                            "Voltaic.A2A.OAuthFlow",
                            "Voltaic.A2A.OAuthFlows",
                            "Voltaic.A2A.OpenIdConnectSecurityScheme",
                            "Voltaic.A2A.Part",
                            "Voltaic.A2A.PartContentCase",
                            "Voltaic.A2A.PushNotificationConfig",
                            "Voltaic.A2A.Role",
                            "Voltaic.A2A.SecurityRequirement",
                            "Voltaic.A2A.SecurityScheme",
                            "Voltaic.A2A.SecuritySchemeCase",
                            "Voltaic.A2A.SendMessageConfiguration",
                            "Voltaic.A2A.SendMessageRequest",
                            "Voltaic.A2A.SendMessageResponse",
                            "Voltaic.A2A.SendMessageResponseCase",
                            "Voltaic.A2A.StreamResponse",
                            "Voltaic.A2A.StreamResponseCase",
                            "Voltaic.A2A.SubscribeToTaskRequest",
                            "Voltaic.A2A.TaskArtifactUpdateEvent",
                            "Voltaic.A2A.TaskPushNotificationConfig",
                            "Voltaic.A2A.TaskState",
                            "Voltaic.A2A.TaskStatus",
                            "Voltaic.A2A.TaskStatusUpdateEvent",
                            "Voltaic.Core.AuthenticationResult",
                            "Voltaic.Core.ClientConnectedEventArgs",
                            "Voltaic.Core.ClientConnection",
                            "Voltaic.Core.ClientConnectionTypeEnum",
                            "Voltaic.Core.ClientDisconnectedEventArgs",
                            "Voltaic.Core.IJsonRpcErrorProvider",
                            "Voltaic.Core.JsonRpcClient",
                            "Voltaic.Core.JsonRpcError",
                            "Voltaic.Core.JsonRpcRequest",
                            "Voltaic.Core.JsonRpcRequestEventArgs",
                            "Voltaic.Core.JsonRpcResponse",
                            "Voltaic.Core.JsonRpcResponseEventArgs",
                            "Voltaic.Core.JsonRpcServer",
                            "Voltaic.Core.MessageFraming",
                            "Voltaic.Core.RequestSentEventArgs",
                            "Voltaic.Core.ResponseReceivedEventArgs",
                            "Voltaic.Mcp.McpAnnotations",
                            "Voltaic.Mcp.McpAudioContent",
                            "Voltaic.Mcp.McpBlobResourceContents",
                            "Voltaic.Mcp.McpCancelledNotification",
                            "Voltaic.Mcp.McpClient",
                            "Voltaic.Mcp.McpClientCapabilities",
                            "Voltaic.Mcp.McpCompleteRequest",
                            "Voltaic.Mcp.McpCompleteResult",
                            "Voltaic.Mcp.McpCompletion",
                            "Voltaic.Mcp.McpCompletionArgument",
                            "Voltaic.Mcp.McpCompletionContext",
                            "Voltaic.Mcp.McpCompletionReference",
                            "Voltaic.Mcp.McpEmbeddedResourceContent",
                            "Voltaic.Mcp.McpGetPromptResult",
                            "Voltaic.Mcp.McpHttpClient",
                            "Voltaic.Mcp.McpHttpServer",
                            "Voltaic.Mcp.McpIcon",
                            "Voltaic.Mcp.McpImageContent",
                            "Voltaic.Mcp.McpImplementation",
                            "Voltaic.Mcp.McpListChangedCapability",
                            "Voltaic.Mcp.McpListPromptsResult",
                            "Voltaic.Mcp.McpListResourceTemplatesResult",
                            "Voltaic.Mcp.McpListResourcesResult",
                            "Voltaic.Mcp.McpListToolsResult",
                            "Voltaic.Mcp.McpLogMessageNotification",
                            "Voltaic.Mcp.McpPaginatedResult",
                            "Voltaic.Mcp.McpProgressNotification",
                            "Voltaic.Mcp.McpPrompt",
                            "Voltaic.Mcp.McpPromptArgument",
                            "Voltaic.Mcp.McpPromptMessage",
                            "Voltaic.Mcp.McpProtocol",
                            "Voltaic.Mcp.McpProtocolException",
                            "Voltaic.Mcp.McpReadResourceResult",
                            "Voltaic.Mcp.McpResource",
                            "Voltaic.Mcp.McpResourceCapability",
                            "Voltaic.Mcp.McpResourceLinkContent",
                            "Voltaic.Mcp.McpResourceTemplate",
                            "Voltaic.Mcp.McpResult",
                            "Voltaic.Mcp.McpServer",
                            "Voltaic.Mcp.McpServerCapabilities",
                            "Voltaic.Mcp.McpSessionLifecycleState",
                            "Voltaic.Mcp.McpSetLogLevelRequest",
                            "Voltaic.Mcp.McpTcpClient",
                            "Voltaic.Mcp.McpTcpServer",
                            "Voltaic.Mcp.McpTextContent",
                            "Voltaic.Mcp.McpTextResourceContents",
                            "Voltaic.Mcp.McpToolCallResult",
                            "Voltaic.Mcp.McpWebsocketsClient",
                            "Voltaic.Mcp.McpWebsocketsServer",
                            "Voltaic.Mcp.ToolDefinition"
                        };

                        TestAssert.Equal(String.Join(",", expected.OrderBy(name => name, StringComparer.Ordinal)), String.Join(",", actual));
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "DisposableTypes", "Disposable public types remain covered by disposal tests", ct =>
                    {
                        string[] disposableTypes = typeof(JsonRpcClient).Assembly.GetExportedTypes()
                            .Where(type => type.Namespace != null && type.Namespace.StartsWith("Voltaic.", StringComparison.Ordinal) && typeof(IDisposable).IsAssignableFrom(type))
                            .Select(type => type.Namespace + "." + type.Name)
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .ToArray();

                        string[] expected =
                        {
                            "Voltaic.A2A.A2ACardResolver",
                            "Voltaic.A2A.A2AClient",
                            "Voltaic.A2A.A2AGrpcClient",
                            "Voltaic.A2A.A2AGrpcServer",
                            "Voltaic.A2A.A2AHttpJsonClient",
                            "Voltaic.A2A.A2AHttpServer",
                            "Voltaic.Core.ClientConnection",
                            "Voltaic.Core.JsonRpcClient",
                            "Voltaic.Core.JsonRpcServer",
                            "Voltaic.Mcp.McpClient",
                            "Voltaic.Mcp.McpHttpClient",
                            "Voltaic.Mcp.McpHttpServer",
                            "Voltaic.Mcp.McpServer",
                            "Voltaic.Mcp.McpTcpClient",
                            "Voltaic.Mcp.McpTcpServer",
                            "Voltaic.Mcp.McpWebsocketsClient",
                            "Voltaic.Mcp.McpWebsocketsServer"
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
                        AssertEvents<A2AGrpcServer>("Log");
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
