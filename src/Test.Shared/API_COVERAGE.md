# Voltaic Public API Coverage Matrix

This matrix tracks the Touchstone descriptors in `src/Test.Shared`. `Covered` means the public type has direct descriptor coverage for its main success and failure paths. `Partial` means meaningful descriptors exist, but deeper integration or long-running/stress scenarios remain release work.

Public APIs are grouped under `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A`.

| API family | Status | Descriptor suites |
|---|---:|---|
| `AuthenticationResult` | Covered | `ModelApi.Supporting.*`, `ModelApi.Mcp.Matrix.AuthenticationResultCustomValues` |
| `ClientConnectedEventArgs` | Covered | `ModelApi.Supporting.*`, `JsonRpc.Tcp.Integration.ClientAndServerEvents`, `McpHttp.Client.Matrix.DisconnectRaisesEvent` |
| `ClientConnection` | Covered | `ClientConnection.*`, `ClientConnection.Matrix.*` |
| `ClientConnectionTypeEnum` | Covered | `ClientConnection.Matrix.TypedConstructorAllEnumValues`, `ModelApi.Mcp.Matrix.ClientConnectionTypeEnumAllValues` |
| `ClientDisconnectedEventArgs` | Covered | `ModelApi.Supporting.*`, `McpHttp.Client.Matrix.DisconnectRaisesEvent` |
| `JsonRpcClient` | Covered | `PublicApi.Clients.Validation.*`, `JsonRpc.Tcp.Integration.*` |
| `JsonRpcError` | Covered | `ModelApi.JsonRpc.*`, `ModelApi.JsonRpc.Matrix.*` |
| `JsonRpcRequest` | Covered | `ModelApi.JsonRpc.*`, `ModelApi.JsonRpc.Matrix.*`, transport integration suites |
| `JsonRpcRequestEventArgs` | Covered | `ModelApi.Supporting.JsonRpcEventArgs`, `JsonRpc.Tcp.Integration.ClientNotificationRaisesServerRequestReceived` |
| `JsonRpcResponse` | Covered | `ModelApi.JsonRpc.*`, `ModelApi.JsonRpc.Matrix.*`, transport integration suites |
| `JsonRpcResponseEventArgs` | Covered | `ModelApi.Supporting.JsonRpcEventArgs`, `JsonRpc.Tcp.Integration.ClientAndServerEvents` |
| `JsonRpcServer` | Covered | `PublicApi.Servers.Validation.*`, `JsonRpc.Tcp.Integration.*` |
| `IJsonRpcErrorProvider` | Covered | `ApiSurface.Inventory.ExportedTypesAreTracked`, protocol exception mapping suites |
| `A2AProtocol` | Covered | `A2A.Protocol.*`, `A2A.Compatibility.*` |
| `A2AJson` | Covered | `A2A.Protocol.AgentCardSerializesV1Shape`, `A2A.Protocol.TaskStateAndRoleUseA2AWireNames`, `A2A.Compatibility.*` |
| `A2AErrorCode` | Covered | `A2A.Protocol.*`, `A2A.Compatibility.*`, protocol error paths |
| `A2AProtocolException` | Covered | `A2A.Protocol.*`, protocol error paths |
| `A2ACardResolver` | Covered | `A2A.Protocol.AgentCardResolverFetchesWellKnownCard`, `ApiSurface.Inventory.DisposableTypes` |
| `A2AClient` | Covered | `A2A.Protocol.JsonRpcSendMessageAndGetTask`, `A2A.Protocol.JsonRpcStreamingMessageUsesSse`, `A2A.Protocol.PushNotificationConfigCrud`, `A2A.Protocol.ExtendedAgentCardJsonRpcAndRest`, `A2A.Protocol.ReturnImmediatelyPersistsSubmittedTask`, `A2A.Compatibility.JsonRpcClientEnvelopeMatchesOfficialSdk` |
| `A2AHttpJsonClient` | Covered | `A2A.Protocol.HttpJsonClientCoversRestBinding`, `A2A.Compatibility.HttpJsonClientRoutesMatchOfficialSdk`, `A2A.Compatibility.HttpJsonListTasksQueryMatchesOfficialSdk`, `A2A.Compatibility.HttpJsonClientParsesOfficialRestSse` |
| `A2AGrpcClient` | Covered | `A2A.Protocol.GrpcClientServerCoversA2AService`, `A2A.Protocol.GrpcErrorMapsToA2AProtocolException`, `A2A.Protocol.GrpcPreservesRichMessagePartsAndMetadata`, `A2A.Protocol.GrpcSubscribeAndCancelTask`, `A2A.Protocol.GrpcAuthenticationBlocksRpcButAllowsAgentCard` |
| `A2AHttpServer` | Covered | `A2A.Protocol.*`, official-style JSON-RPC and HTTP+JSON compatibility cases |
| `A2AGrpcServer` | Covered | `A2A.Protocol.GrpcClientServerCoversA2AService`, `A2A.Protocol.GrpcErrorMapsToA2AProtocolException`, `A2A.Protocol.GrpcPreservesRichMessagePartsAndMetadata`, `A2A.Protocol.GrpcSubscribeAndCancelTask`, `A2A.Protocol.GrpcAuthenticationBlocksRpcButAllowsAgentCard`, sample/manual harness coverage |
| `IA2AAgentHandler` | Covered | `A2A.Protocol.*`, sample/test handlers |
| `A2ARequestContext` | Covered | `A2A.Protocol.*`, handler integration coverage |
| `A2AAgentEventQueue` | Covered | `A2A.Protocol.*`, handler integration coverage |
| `A2ATaskUpdater` | Covered | `A2A.Protocol.*`, task lifecycle and streaming coverage |
| `IA2ATaskStore` / `InMemoryA2ATaskStore` | Covered | `A2A.Protocol.JsonRpcSendMessageAndGetTask`, `A2A.Protocol.HttpJsonClientCoversRestBinding`, list/get/task lifecycle coverage |
| A2A Agent Card models | Covered | `A2A.Protocol.AgentCardSerializesV1Shape`, `A2A.Protocol.AgentCardResolverFetchesWellKnownCard`, extended-card cases |
| A2A security models | Covered | `ApiSurface.Inventory.ExportedTypesAreTracked`, Agent Card serialization surface |
| A2A message/content models | Covered | `A2A.Protocol.TaskStateAndRoleUseA2AWireNames`, send/stream/task integration cases |
| A2A task lifecycle models | Covered | `A2A.Protocol.JsonRpcSendMessageAndGetTask`, `A2A.Protocol.JsonRpcStreamingMessageUsesSse`, `A2A.Protocol.ReturnImmediatelyPersistsSubmittedTask`, `A2A.Protocol.GrpcSubscribeAndCancelTask`, HTTP+JSON and gRPC client/server cases |
| A2A push notification config models | Covered | `A2A.Protocol.PushNotificationConfigCrud`, `A2A.Protocol.GrpcClientServerCoversA2AService`, `A2A.Protocol.OfficialRestPushConfigBodyAccepted`, `A2A.Compatibility.HttpJsonClientRoutesMatchOfficialSdk` |
| `McpClient` | Covered | `PublicApi.Clients.Validation.McpClient*`, `McpStdio.Integration.*` |
| `McpHttpClient` | Covered | `PublicApi.Clients.Validation.McpHttpClient*`, `McpHttp.Client.Matrix.*` |
| `McpHttpServer` | Covered | `PublicApi.Servers.Validation.McpHttpServer*`, `McpHttp.Protocol.*`, `McpHttp.Streamable.Matrix.*`, `McpHttp.Registry.Matrix.*` |
| `McpServer` | Covered | `McpServer.Api.*`, `PublicApi.Servers.Validation.McpServer*` |
| `McpTcpClient` | Covered | `PublicApi.Clients.Validation.McpTcpClientIsJsonRpcClient`, `McpTcp.Parity.*` |
| `McpTcpServer` | Covered | `PublicApi.Servers.Validation.McpTcpServer*`, `McpTcp.Parity.*` |
| `McpWebsocketsClient` | Covered | `PublicApi.Clients.Validation.McpWebsocketsClient*`, `McpWebSocket.Parity.*` |
| `McpWebsocketsServer` | Covered | `PublicApi.Servers.Validation.McpWebsocketsServer*`, `McpWebSocket.Parity.*` |
| `MessageFraming` | Covered | `MessageFraming.*`, `MessageFraming.Edge.*` |
| `RequestSentEventArgs` | Covered | `ModelApi.Supporting.RequestSentEventArgs`, `JsonRpc.Tcp.Integration.ClientAndServerEvents`, `McpHttp.Client.Matrix.ResponseEvents` |
| `ResponseReceivedEventArgs` | Covered | `ModelApi.Supporting.ResponseReceivedEventArgs`, `JsonRpc.Tcp.Integration.ClientAndServerEvents`, `McpHttp.Client.Matrix.ResponseEvents` |
| `ToolDefinition` | Covered | `ModelApi.Supporting.ToolDefinitionDefaults`, `McpProtocol.Content.ToolDefinitionSerialization`, `ModelApi.Mcp.Matrix.ToolDefinitionOmittedOptionalFields`, `McpHttp.Registry.Matrix.ToolsListMetadata` |
| `McpProtocol` | Covered | `McpProtocol.Models.*`, `ModelApi.Mcp.Matrix.Protocol*`, HTTP/TCP/WebSocket initialize suites |
| `McpProtocolException` | Covered | `ModelApi.Mcp.Matrix.ProtocolException*`, `JsonRpc.Tcp.Integration.McpProtocolExceptionMapsToProtocolError`, HTTP invalid-params suites |
| `McpImplementation` | Covered | `McpProtocol.Models.ImplementationSerialization`, `ModelApi.Mcp.Matrix.ImplementationOmittedNulls` |
| `McpIcon` | Covered | `McpProtocol.Models.ImplementationSerialization`, `ModelApi.Mcp.Matrix.IconAllFields` |
| `McpAnnotations` | Covered | `McpProtocol.Content.ToolDefinitionSerialization`, `ModelApi.Mcp.Matrix.AnnotationsAllHints` |
| `McpResult` | Covered | `ModelApi.Mcp.Matrix.ResultMeta`, result model suites |
| `McpPaginatedResult` | Covered | `ModelApi.Mcp.Matrix.PaginatedResultNextCursor`, pagination protocol suites |
| `McpListToolsResult` | Covered | `ModelApi.Mcp.Matrix.ListToolsResult`, `McpHttp.Registry.Matrix.ToolsListPagination` |
| `McpListResourcesResult` | Covered | `ModelApi.Mcp.Matrix.ListResourcesResult`, `McpHttp.Registry.Matrix.ResourcesListPagination` |
| `McpListResourceTemplatesResult` | Covered | `ModelApi.Mcp.Matrix.ListResourceTemplatesResult`, `McpHttp.Registry.Matrix.ResourceTemplatesPagination` |
| `McpListPromptsResult` | Covered | `ModelApi.Mcp.Matrix.ListPromptsResult`, `McpHttp.Registry.Matrix.PromptsListPagination` |
| `McpClientCapabilities` | Covered | `ModelApi.Mcp.Matrix.ClientCapabilitiesAllFields` |
| `McpServerCapabilities` | Covered | `McpProtocol.Models.CapabilitiesSerialization`, `ModelApi.Mcp.Matrix.ServerCapabilitiesAllFields` |
| `McpListChangedCapability` | Covered | capability model suites |
| `McpResourceCapability` | Covered | capability model suites |
| `McpTextContent` | Covered | `McpProtocol.Content.ContentSerialization`, `ModelApi.Mcp.Matrix.TextContentMetaAndAnnotations` |
| `McpImageContent` | Covered | `McpProtocol.Content.ContentSerialization`, `ModelApi.Mcp.Matrix.ImageContentAnnotations`, `McpHttp.Registry.Matrix.ToolsCallFullResult` |
| `McpAudioContent` | Covered | `McpProtocol.Content.ContentSerialization`, `ModelApi.Mcp.Matrix.AudioContentDefaults` |
| `McpEmbeddedResourceContent` | Covered | `McpProtocol.Content.ContentSerialization` |
| `McpResourceLinkContent` | Covered | `McpProtocol.Content.ContentSerialization` |
| `McpResource` | Covered | `McpProtocol.Content.ResourceSerialization`, `ModelApi.Mcp.Matrix.ResourceFullMetadata`, HTTP/TCP resource suites |
| `McpResourceTemplate` | Covered | `McpProtocol.Content.ResourceSerialization`, `ModelApi.Mcp.Matrix.ResourceTemplateFullMetadata`, HTTP/TCP template suites |
| `McpTextResourceContents` | Covered | `McpProtocol.Content.ResourceSerialization`, `ModelApi.Mcp.Matrix.TextResourceContentsOmittedMimeType`, HTTP/TCP resource suites |
| `McpBlobResourceContents` | Covered | `McpProtocol.Content.ResourceSerialization`, `ModelApi.Mcp.Matrix.BlobResourceContents` |
| `McpReadResourceResult` | Covered | `ModelApi.Mcp.Matrix.ReadResourceResultDefaults`, HTTP/TCP resource suites |
| `McpPrompt` | Covered | `McpProtocol.Content.PromptSerialization`, `ModelApi.Mcp.Matrix.PromptOmittedArguments`, HTTP/TCP prompt suites |
| `McpPromptArgument` | Covered | `McpProtocol.Content.PromptSerialization`, `ModelApi.Mcp.Matrix.PromptArgument*`, prompt validation suites |
| `McpPromptMessage` | Covered | `McpProtocol.Content.PromptSerialization`, `ModelApi.Mcp.Matrix.PromptMessageDefaults` |
| `McpGetPromptResult` | Covered | `McpProtocol.Content.PromptSerialization`, `ModelApi.Mcp.Matrix.GetPromptResultMetadata`, HTTP/TCP prompt suites |
| `McpToolCallResult` | Covered | `McpProtocol.Content.ToolCallResultFactories`, `ModelApi.Mcp.Matrix.ToolCallResult*`, `McpHttp.Registry.Matrix.ToolsCall*` |
| `McpCompleteRequest` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization`, `McpHttp.Registry.Matrix.CompletionCompletePrompt` |
| `McpCompletionReference` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization`, `McpHttp.Registry.Matrix.CompletionCompletePrompt` |
| `McpCompletionArgument` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization`, `McpHttp.Registry.Matrix.CompletionCompletePrompt` |
| `McpCompletionContext` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization` |
| `McpCompleteResult` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization`, `McpHttp.Registry.Matrix.CompletionCompletePrompt` |
| `McpCompletion` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization`, `McpHttp.Registry.Matrix.CompletionCompletePrompt` |
| `McpSessionLifecycleState` | Covered | `ApiSurface.Inventory.ExportedTypesAreTracked` |
| `McpCancelledNotification` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization`, HTTP/TCP/WebSocket notification helper coverage |
| `McpProgressNotification` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization`, HTTP/TCP/WebSocket notification helper coverage |
| `McpSetLogLevelRequest` | Covered | `ApiSurface.Inventory.ExportedTypesAreTracked`, `McpHttp.Registry.Matrix.LoggingSetLevel` |
| `McpLogMessageNotification` | Covered | `McpProtocol.Content.CompletionAndUtilitySerialization`, HTTP/TCP/WebSocket notification helper coverage |
| Public type inventory | Covered | `ApiSurface.Inventory.*` |

Remaining high-value future work is intentionally outside the v0.4.0 package scope: full MCP roots/sampling/elicitation request orchestration, long-running stress/soak suites, and full JSON Schema 2020-12 validation beyond Voltaic's lightweight required/type checks.
