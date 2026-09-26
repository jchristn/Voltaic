# Voltaic Public API Coverage Matrix

This matrix tracks the Touchstone descriptors in `src/Test.Shared`. `Covered` means the public type has direct descriptor coverage for its main success and failure paths. `Partial` means meaningful descriptors exist, but deeper integration or long-running/stress scenarios remain release work.

Public APIs are grouped under `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A`. Library source files mirror that grouping under `src/Voltaic/Core`, `src/Voltaic/Mcp`, and `src/Voltaic/A2A`; `ApiSurface.Inventory.SourceLayoutMatchesPublicNamespaces` enforces that no C# source files drift back into the project root and that A2A proto definitions stay under `src/Voltaic/A2A/Protos`.

| API family | Status | Descriptor suites |
|---|---:|---|
| `AuthenticationResult` | Covered | `ModelApi.Supporting.*`, `ModelApi.Mcp.Matrix.AuthenticationResultCustomValues`, `Security.Policies.AuthenticationResultHeadersAreNeverNull`, `Security.Policies.BearerChallengeFormatsRfc6750`, `Security.HttpServers.AuthenticationFailureCarriesResultHeaders`, `Security.HttpServers.A2AAuthenticationFailureCarriesResultHeaders`, `Security.WebSocket.AuthenticationRejectsMissingOrWrongCredentials` (`Headers`, `BearerChallenge`) |
| `OriginPolicy` | Covered | `Security.Policies.*` (defaults, lookalike and opaque origins, allowlist normalization, wildcard, `AllowLoopbackOrigins`, `OriginValidator`), `Security.HttpServers.*`, `Security.WebSocket.*` (server enforcement) |
| `LoopbackAddresses` | Covered | `Security.Policies.LoopbackAddressesRecognizeLoopbackOnly`, `Security.HttpServers.LoopbackRestrictionDefaultsFollowHostname`, `Security.HttpServers.RemoteClientWithSpoofedHostIsRejected`, `Security.WebSocket.RemoteClientWithSpoofedHostIsRejected` |
| `RpcCallContext` | Covered | `McpHttp.Server.CallContext.*` (ambient and explicit-context propagation, unauthenticated null, ping bypass, concurrent isolation) |
| `ClientConnectedEventArgs` | Covered | `ModelApi.Supporting.*`, `JsonRpc.Tcp.Integration.ClientAndServerEvents`, `McpHttp.Client.Matrix.DisconnectRaisesEvent` |
| `ClientConnection` | Covered | `ClientConnection.*`, `ClientConnection.Matrix.*`, `McpHttp.Sessions.MarkActivityUpdatesLastActivity`, `McpHttp.Sessions.RequestsKeepSessionsAlive` (`MarkActivity`), `Security.WebSocket.ValidCredentialsConnectAndCallerFlowsIntoHandlers` (`Caller`) |
| `ClientConnectionTypeEnum` | Covered | `ClientConnection.Matrix.TypedConstructorAllEnumValues`, `ModelApi.Mcp.Matrix.ClientConnectionTypeEnumAllValues` |
| `ClientDisconnectedEventArgs` | Covered | `ModelApi.Supporting.*`, `McpHttp.Client.Matrix.DisconnectRaisesEvent` |
| `JsonRpcClient` | Covered | `PublicApi.Clients.Validation.*`, `JsonRpc.Tcp.Integration.*`, `McpClients.ServerRequests.TcpClientAnswersServerRequests`, `McpClients.ServerRequests.RequestHandlerRegistrationValidates` (`RegisterRequestHandler`, `UnregisterRequestHandler`) |
| `JsonRpcError` | Covered | `ModelApi.JsonRpc.*`, `ModelApi.JsonRpc.Matrix.*` |
| `JsonRpcRequest` | Covered | `ModelApi.JsonRpc.*`, `ModelApi.JsonRpc.Matrix.*`, transport integration suites |
| `JsonRpcRequestEventArgs` | Covered | `ModelApi.Supporting.JsonRpcEventArgs`, `JsonRpc.Tcp.Integration.ClientNotificationRaisesServerRequestReceived` |
| `JsonRpcResponse` | Covered | `ModelApi.JsonRpc.*`, `ModelApi.JsonRpc.Matrix.*`, transport integration suites |
| `JsonRpcResponseEventArgs` | Covered | `ModelApi.Supporting.JsonRpcEventArgs`, `JsonRpc.Tcp.Integration.ClientAndServerEvents` |
| `JsonRpcServer` | Covered | `PublicApi.Servers.Validation.*`, `JsonRpc.Tcp.Integration.*`, `Mcp.DiagnosticTools.JsonRpcServerDiagnosticsOptIn` (`includeDiagnosticMethods`) |
| `IJsonRpcErrorProvider` | Covered | `ApiSurface.Inventory.ExportedTypesAreTracked`, protocol exception mapping suites |
| `A2AProtocol` | Covered | `A2A.Protocol.*`, `A2A.Compatibility.*` |
| `A2AJson` | Covered | `A2A.Protocol.AgentCardSerializesV1Shape`, `A2A.Protocol.TaskStateAndRoleUseA2AWireNames`, `A2A.Compatibility.*` |
| `A2AErrorCode` | Covered | `A2A.Protocol.*`, `A2A.Compatibility.*`, protocol error paths |
| `A2AProtocolException` | Covered | `A2A.Protocol.*`, protocol error paths |
| `A2ACardResolver` | Covered | `A2A.Protocol.AgentCardResolverFetchesWellKnownCard`, `ApiSurface.Inventory.DisposableTypes` |
| `A2AClient` | Covered | `A2A.Protocol.JsonRpcSendMessageAndGetTask`, `A2A.Protocol.JsonRpcStreamingMessageUsesSse`, `A2A.Protocol.PushNotificationConfigCrud`, `A2A.Protocol.ExtendedAgentCardJsonRpcAndRest`, `A2A.Protocol.ReturnImmediatelyPersistsSubmittedTask`, `A2A.Compatibility.JsonRpcClientEnvelopeMatchesOfficialSdk`, `A2A.Hardening.PushConfigErrorsFollowSpec` (JSON-RPC error bodies on any HTTP status) |
| `A2AHttpJsonClient` | Covered | `A2A.Protocol.HttpJsonClientCoversRestBinding`, `A2A.Compatibility.HttpJsonClientRoutesMatchOfficialSdk`, `A2A.Compatibility.HttpJsonListTasksQueryMatchesOfficialSdk`, `A2A.Compatibility.HttpJsonClientParsesOfficialRestSse`, `A2A.Hardening.JsonRpcAndRestHideInternalErrorDetails` (`google.rpc.Status` errors with `ErrorInfo` reasons, legacy bodies) |
| `A2AGrpcClient` | Covered | `A2A.Protocol.GrpcClientServerCoversA2AService`, `A2A.Protocol.GrpcErrorMapsToA2AProtocolException`, `A2A.Protocol.GrpcPreservesRichMessagePartsAndMetadata`, `A2A.Protocol.GrpcSubscribeAndCancelTask`, `A2A.Protocol.GrpcAuthenticationBlocksRpcButAllowsAgentCard` |
| `A2AHttpServer` | Covered | `A2A.Protocol.*`, official-style JSON-RPC and HTTP+JSON compatibility cases, `Security.HttpServers.A2A*` (`OriginPolicy`, CORS echo, `RestrictToLoopbackClients`, auth failure headers), `A2A.Hardening.*` (push delivery, `PushNotificationUrlValidator`, `PushNotificationTimeoutMs`, `PushNotificationMaxAttempts`, push config errors, restart after `Stop`) |
| `A2AGrpcServer` | Covered | `A2A.Protocol.GrpcClientServerCoversA2AService`, `A2A.Protocol.GrpcErrorMapsToA2AProtocolException`, `A2A.Protocol.GrpcPreservesRichMessagePartsAndMetadata`, `A2A.Protocol.GrpcSubscribeAndCancelTask`, `A2A.Protocol.GrpcAuthenticationBlocksRpcButAllowsAgentCard`, sample/manual harness coverage, `A2A.Hardening.Grpc*` (extended card authentication, `OriginPolicy`, `RestrictToLoopbackClients`, generic internal errors, restart after `Stop`), `A2A.Hardening.GrpcServerDeliversPushWithItsSettings`, `A2A.Hardening.GrpcLocalhostListensOnBothLoopbackAddresses`, `A2A.Hardening.GrpcStopReleasesEveryListener`, `A2A.Hardening.GrpcLocalhostStartsWhenIpv6PortIsTaken` |
| `IA2AAgentHandler` | Covered | `A2A.Protocol.*`, sample/test handlers |
| `A2ARequestContext` | Covered | `A2A.Protocol.*`, handler integration coverage, `A2A.Protocol.AuthenticatedCallerReachesAgentContext`, `A2A.Protocol.GrpcAuthenticatedCallerReachesAgentContext`, `A2A.Protocol.UnauthenticatedRequestHasNullCallerInContext` (Principal/Claims propagation) |
| `A2AAgentEventQueue` | Covered | `A2A.Protocol.*`, handler integration coverage |
| `A2ATaskUpdater` | Covered | `A2A.Protocol.*`, task lifecycle and streaming coverage |
| `IA2ATaskStore` / `InMemoryA2ATaskStore` | Covered | `A2A.Protocol.JsonRpcSendMessageAndGetTask`, `A2A.Protocol.HttpJsonClientCoversRestBinding`, list/get/task lifecycle coverage, `A2A.Hardening.TaskStoreHandlesNulls` |
| A2A Agent Card models | Covered | `A2A.Protocol.AgentCardSerializesV1Shape`, `A2A.Protocol.AgentCardResolverFetchesWellKnownCard`, extended-card cases |
| A2A security models | Covered | `ApiSurface.Inventory.ExportedTypesAreTracked`, Agent Card serialization surface |
| A2A message/content models | Covered | `A2A.Protocol.TaskStateAndRoleUseA2AWireNames`, send/stream/task integration cases |
| A2A task lifecycle models | Covered | `A2A.Protocol.JsonRpcSendMessageAndGetTask`, `A2A.Protocol.JsonRpcStreamingMessageUsesSse`, `A2A.Protocol.ReturnImmediatelyPersistsSubmittedTask`, `A2A.Protocol.GrpcSubscribeAndCancelTask`, HTTP+JSON and gRPC client/server cases |
| A2A push notification config models | Covered | `A2A.Protocol.PushNotificationConfigCrud`, `A2A.Protocol.GrpcClientServerCoversA2AService`, `A2A.Protocol.OfficialRestPushConfigBodyAccepted`, `A2A.Compatibility.HttpJsonClientRoutesMatchOfficialSdk`, `A2A.Hardening.PushConfigJsonMatchesV1AndReadsLegacy` (flat v1.0 shape, legacy nested shape, `id`/`configId`), `A2A.Hardening.ListPushConfigsAcceptsBothMethodNames` |
| `McpClient` | Covered | `PublicApi.Clients.Validation.McpClient*`, `McpStdio.Integration.*` (including `PingAsync`), `McpClients.ServerRequests.StdioClientAnswersServerRequests`, `McpClients.ServerRequests.RequestHandlerRegistrationValidates` |
| `McpHttpClient` | Covered | `PublicApi.Clients.Validation.McpHttpClient*`, `McpHttp.Client.Matrix.*`, `Mcp.DiagnosticTools.HttpClientAcceptsLegacyPong`, `Mcp.DiagnosticTools.HttpClientPingSurfacesErrors` (`PingAsync`), `Mcp.DiagnosticTools.HttpClientConnectFailsWhenInitializeFails`, `McpHttp.Sessions.ClientHandshakeNegotiatesVersionAndSendsInitialized`, `McpHttp.Sessions.LegacyClientConnectsAndReceivesEventsNotifications` (initialize handshake on connect), `Mcp.SpecConformance.HttpClientReadsSseResponses` (SSE POST responses), `Mcp.SpecConformance.MrtrHandlerSeesInputResponsesAndRequestState` (`CallToolStatelessAsync`), `McpClients.ServerRequests.Http*` (answers server requests, capability declaration, `AutoReconnectSse`, `SseReconnectDelayMs`, `SseMaxReconnectAttempts`, POST stream resumption), `McpClients.ServerRequests.StatelessClientIgnoresServerRequests`, `McpHttp.SseResumability.VoltaicClientResumesVoltaicServer`, `Mcp.HeaderParameters.VoltaicClientMirrorsHeaders`, `Mcp.HeaderParameters.ClientDropsInvalidToolDefinitions` |
| `McpHttpServer` | Covered | `PublicApi.Servers.Validation.McpHttpServer*`, `McpHttp.Protocol.*`, `McpHttp.Streamable.Matrix.*`, `McpHttp.Registry.Matrix.*`, `McpHttp.Server.Negotiation.*` (`MaximumHandshakeProtocolVersion`), `McpHttp.Server.AuthParity.*` (authenticated and unauthenticated requests share one pipeline), `McpVersion.StatelessResults.*`, `Mcp.DiagnosticTools.*` (protocol methods always registered, opt-in diagnostic tools, protocol `ping` result, `UnregisterTool`, tools reachable only through `tools/call`), `Mcp.SchemaValidation.*` (`additionalProperties`, `patternProperties`), `McpHttp.Sessions.*` (initialize-only sessions, 400/404 rules, principal binding, `RequireInitializedSessions`), `Security.HttpServers.*` (`OriginPolicy`, CORS echo, `RestrictToLoopbackClients`, JSON content type, auth failure headers), `Mcp.SpecConformance.*` (202 for client responses, header-less `2025-03-26`, stateless `_meta` requirement, `ProtectedResourceMetadata`), `McpHttp.SseResumability.*` (`SseReplayBufferSize`, `SseRetryIntervalMs`, priming, `Last-Event-ID`), `Mcp.HeaderParameters.*` (x-mcp-header validation), `McpHttp.Sessions.RelaxedModeIssuesSessionsForSuccessfulRequests` and `RequireInitializedSessionsDefaultsToTrue` (obsolete `RequireInitializedSessions`), `Mcp.SpecConformance.ToolHandlerExceptionsAreToolExecutionErrors` |
| `McpProtectedResourceMetadata` | Covered | `Mcp.SpecConformance.ProtectedResourceMetadataIsServedWithoutAuth`, `Mcp.SpecConformance.ProtectedResourceMetadataAbsentOrInvalid` |
| `McpServer` | Covered | `McpServer.Api.*`, `PublicApi.Servers.Validation.McpServer*`, `McpStdio.*.InitializeRequesting20260728NegotiatesHandshake`, `McpHttp.Server.Negotiation.MaximumHandshakeProtocolVersionValidatedOnEveryServer`, `Mcp.DiagnosticTools.UnregisterToolOnEveryServer`, `McpStreams.Stateless.*` (stdio: `server/discover`, stateless result fields, `-32022`, handshake unchanged) |
| `McpToolException` | Covered | `Mcp.SpecConformance.ToolHandlerExceptionsAreToolExecutionErrors`, `Mcp.SpecConformance.IncludeToolExceptionMessagesShowsDetails` (constructor validation, `IncludeToolExceptionMessages` on every MCP server) |
| `McpToolCallContext` | Covered | `Mcp.SpecConformance.MrtrHandlerSeesInputResponsesAndRequestState`, `Mcp.SpecConformance.MrtrWorksOnTcp`, `Mcp.SpecConformance.ToolCallContextIsScopedToToolHandlers` (null outside tool calls, constructor validation), `Mcp.SpecConformance.InputRequiredOnHandshakeEraBecomesToolError` (`CanRequestInput`) |
| `McpTcpClient` | Covered | `PublicApi.Clients.Validation.McpTcpClientIsJsonRpcClient`, `McpTcp.Parity.*`, `McpClients.ServerRequests.TcpClientAnswersServerRequests` (built-in `ping`) |
| `McpTcpServer` | Covered | `PublicApi.Servers.Validation.McpTcpServer*`, `McpTcp.Parity.*` (including `InitializeRequesting20260728NegotiatesHandshake` and `MaximumHandshakeProtocolVersionHonored`), `Mcp.DiagnosticTools.TcpDefaultAndOptIn`, `Mcp.DiagnosticTools.UnregisterToolOnEveryServer`, `McpStreams.Stateless.*` (TCP), `Mcp.SpecConformance.MrtrWorksOnTcp` |
| `McpWebsocketsClient` | Covered | `PublicApi.Clients.Validation.McpWebsocketsClient*`, `McpWebSocket.Parity.*` (including `PingAsync`), `Security.WebSocket.ValidCredentialsConnectAndCallerFlowsIntoHandlers`, `Security.WebSocket.SetRequestHeaderRemovalAppliesToNextConnect` (`SetRequestHeader`), `McpClients.ServerRequests.WebSocketClientAnswersServerRequests` |
| `McpWebsocketsServer` | Covered | `PublicApi.Servers.Validation.McpWebsocketsServer*`, `McpWebSocket.Parity.*` (including `InitializeRequesting20260728NegotiatesHandshake` and `MaximumHandshakeProtocolVersionHonored`), `Mcp.DiagnosticTools.WebSocketDiagnosticsAreTools`, `Mcp.DiagnosticTools.UnregisterToolOnEveryServer`, `Security.WebSocket.*` (`AuthenticationHandler`, `OriginPolicy`, `RestrictToLoopbackClients`, caller context), `McpStreams.Stateless.*` (WebSocket) |
| `MessageFraming` | Covered | `MessageFraming.*`, `MessageFraming.Edge.*`, `Security.Framing.*` (strict LSP header grammar, HTTP requests dropped by `JsonRpcServer` and `McpTcpServer`) |
| `RequestSentEventArgs` | Covered | `ModelApi.Supporting.RequestSentEventArgs`, `JsonRpc.Tcp.Integration.ClientAndServerEvents`, `McpHttp.Client.Matrix.ResponseEvents` |
| `ResponseReceivedEventArgs` | Covered | `ModelApi.Supporting.ResponseReceivedEventArgs`, `JsonRpc.Tcp.Integration.ClientAndServerEvents`, `McpHttp.Client.Matrix.ResponseEvents` |
| `ToolDefinition` | Covered | `ModelApi.Supporting.ToolDefinitionDefaults`, `McpProtocol.Content.ToolDefinitionSerialization`, `ModelApi.Mcp.Matrix.ToolDefinitionOmittedOptionalFields`, `McpHttp.Registry.Matrix.ToolsListMetadata` |
| `McpProtocol` | Covered | `McpProtocol.Models.*`, `ModelApi.Mcp.Matrix.Protocol*`, HTTP/TCP/WebSocket initialize suites, `McpHttp.Server.Negotiation.*` (`NegotiateHandshakeVersion`, `NewestHandshakeProtocolVersion`, `IsHandshakeVersion`), `McpVersion.*.ResolveDefaultFallback` and `Mcp.SpecConformance.*` (`HeaderlessProtocolVersion`, `ProtectedResourceMetadataPath`), `Mcp.HeaderParameters.*` (`ParamHeaderPrefix`, `HeaderAnnotationKeyword`) |
| `McpProtocolException` | Covered | `ModelApi.Mcp.Matrix.ProtocolException*`, `JsonRpc.Tcp.Integration.McpProtocolExceptionMapsToProtocolError`, HTTP invalid-params suites, `McpHttp.Sessions.SessionErrorFactoriesUseSpecCodes` (`SessionNotFound`, `SessionRequired`) |
| `McpImplementation` | Covered | `McpProtocol.Models.ImplementationSerialization`, `ModelApi.Mcp.Matrix.ImplementationOmittedNulls` |
| `McpIcon` | Covered | `McpProtocol.Models.ImplementationSerialization`, `ModelApi.Mcp.Matrix.IconAllFields` |
| `McpAnnotations` | Covered | `McpProtocol.Content.ToolDefinitionSerialization`, `ModelApi.Mcp.Matrix.AnnotationsAllHints` |
| `McpResult` | Covered | `ModelApi.Mcp.Matrix.ResultMeta`, result model suites, `McpVersion.StatelessResults.*` (`ResultType` stamping, preservation, and revert) |
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
| `McpEmptyResult` | Covered | `McpVersion.StatelessResults.CustomMcpResultIsStamped`, `McpVersion.StatelessResults.HandshakeResultsOmitStatelessFields`, `McpVersion.StatelessResults.EveryBuiltInResultCarriesResultType` |
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
| Source layout inventory | Covered | `ApiSurface.Inventory.SourceLayoutMatchesPublicNamespaces` |

Remaining high-value future work is intentionally outside the v0.4.0 package scope: full MCP roots/sampling/elicitation request orchestration, long-running stress/soak suites, and full JSON Schema 2020-12 validation beyond Voltaic's lightweight required/type checks.
