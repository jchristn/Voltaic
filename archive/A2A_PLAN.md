# A2A Implementation Plan

Status legend:

- `[ ]` Not started
- `[~]` In progress
- `[x]` Complete
- `[!]` Blocked or intentionally out of scope

## Scope Decisions

- `[x]` Versioning: this is pre-1.0; increment the minor package version from `0.3.0` to `0.4.0`.
- `[x]` Packaging: keep one NuGet package named `Voltaic`.
- `[x]` Namespaces: refactor public APIs into `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A`.
- `[x]` Source layout: mirror the public API split with `src/Voltaic/Core`, `src/Voltaic/Mcp`, and `src/Voltaic/A2A`.
- `[x]` Dependencies: keep the library dependency-light and avoid ASP.NET Core.
- `[x]` A2A API style: use Voltaic's direct client/server style rather than framework-driven hosting.
- `[x]` Compatibility oracle: treat the official `a2a-dotnet` SDK as the behavioral and wire-format compatibility oracle.
- `[x]` gRPC binding: implemented dependency-light A2A gRPC with the Watson7 NuGet package for the server transport and `HttpClient` for the client transport. Does not use ASP.NET Core.

## Reference Baseline

- `[x]` A2A spec repository inspected from `https://github.com/a2aproject/A2A`.
  - Reference commit: `69dd57cb7ec8f83b7d93855d166869a72f01a1eb`.
- `[x]` Official .NET SDK inspected from `https://github.com/a2aproject/a2a-dotnet`.
  - Reference commit: `8fe65cfaa65a72b2d63bc9bef2e2d32fddc12a18`.
- `[x]` Current A2A method names confirmed as v1 PascalCase JSON-RPC names:
  - `SendMessage`
  - `SendStreamingMessage`
  - `GetTask`
  - `ListTasks`
  - `CancelTask`
  - `SubscribeToTask`
  - `CreateTaskPushNotificationConfig`
  - `GetTaskPushNotificationConfig`
  - `ListTaskPushNotificationConfig`
  - `DeleteTaskPushNotificationConfig`
  - `GetExtendedAgentCard`
- `[x]` Current Agent Card discovery path confirmed as `/.well-known/agent-card.json`.
- `[x]` Current A2A version header confirmed as `A2A-Version: 1.0`.

## Phase 1 - Namespace Refactor

- `[x]` Move JSON-RPC and transport-neutral types to `Voltaic.Core`.
  - `JsonRpcRequest`
  - `JsonRpcResponse`
  - `JsonRpcError`
  - `JsonRpcClient`
  - `JsonRpcServer`
  - `JsonRpcRequestEventArgs`
  - `JsonRpcResponseEventArgs`
  - `RequestSentEventArgs`
  - `ResponseReceivedEventArgs`
  - `ClientConnection`
  - `ClientConnectionTypeEnum`
  - `ClientConnectedEventArgs`
  - `ClientDisconnectedEventArgs`
  - `ClientPendingRequest`
  - `ServerPendingRequest`
  - `MessageFraming`
  - `AuthenticationResult`
- `[x]` Move JSON-RPC and transport-neutral source files under `src/Voltaic/Core`.
- `[x]` Move all MCP-specific types to `Voltaic.Mcp`.
  - `Mcp*`
  - `ToolDefinition`
  - `McpEndpoint`
- `[x]` Move MCP source files under `src/Voltaic/Mcp`.
- `[x]` Add `using Voltaic.Core;` where MCP files consume JSON-RPC/core types.
- `[x]` Update test projects and sample projects to import `Voltaic.Core` and/or `Voltaic.Mcp`.
- `[x]` Update documentation examples to use the new namespaces.
- `[x]` Confirm the solution builds after namespace movement before adding A2A.

## Phase 2 - Shared Core Hardening

- `[x]` Keep shared JSON-RPC message serialization stable after namespace movement.
- `[x]` Keep TCP framing behavior stable.
- `[x]` Preserve existing MCP stdio, HTTP, TCP, and WebSocket behavior.
- `[x]` Avoid introducing shared abstractions that encode MCP semantics.
- `[x]` Add or update API inventory tests to assert the namespace layout.

## Phase 3 - A2A Models

- `[x]` Add `Voltaic.A2A.A2AProtocol` constants.
  - Protocol version `1.0`
  - Version header `A2A-Version`
  - Agent Card path `/.well-known/agent-card.json`
  - Extended Agent Card path `/extendedAgentCard`
  - JSON-RPC method constants
  - REST path constants
- `[x]` Add `A2AErrorCode` and `A2AProtocolException`.
- `[x]` Add Agent Card and discovery models.
  - `AgentCard`
  - `AgentCapabilities`
  - `AgentInterface`
  - `AgentProvider`
  - `AgentSkill`
  - `AgentExtension`
  - `AgentCardSignature`
- `[x]` Add security declaration models.
  - `SecurityScheme`
  - `ApiKeySecurityScheme`
  - `HttpAuthSecurityScheme`
  - `OAuth2SecurityScheme`
  - `OpenIdConnectSecurityScheme`
  - `MutualTlsSecurityScheme`
  - `OAuthFlows`
  - OAuth flow models
  - `SecurityRequirement`
- `[x]` Add message/content models.
  - `Message`
  - `Role`
  - `Part`
  - `PartContentCase`
  - `Artifact`
- `[x]` Add task lifecycle models.
  - `AgentTask`
  - `TaskStatus`
  - `TaskState`
  - `TaskStatusUpdateEvent`
  - `TaskArtifactUpdateEvent`
  - `StreamResponse`
  - `SendMessageResponse`
- `[x]` Add request/response models.
  - `SendMessageRequest`
  - `SendMessageConfiguration`
  - `GetTaskRequest`
  - `ListTasksRequest`
  - `ListTasksResponse`
  - `CancelTaskRequest`
  - `SubscribeToTaskRequest`
  - `GetExtendedAgentCardRequest`
- `[x]` Add push notification models.
  - `PushNotificationConfig`
  - `AuthenticationInfo`
  - `TaskPushNotificationConfig`
  - `CreateTaskPushNotificationConfigRequest`
  - `GetTaskPushNotificationConfigRequest`
  - `ListTaskPushNotificationConfigRequest`
  - `ListTaskPushNotificationConfigResponse`
  - `DeleteTaskPushNotificationConfigRequest`
- `[x]` Add convenience constructors/factories for common text/data/file parts where they do not obscure wire shape.
- `[x]` Ensure JSON uses web/camelCase naming, null omission, enum wire names, and official field names.
- `[x]` Place A2A source files under `src/Voltaic/A2A`.

## Phase 4 - A2A Client

- `[x]` Add `A2ACardResolver`.
  - Fetch public Agent Card from `/.well-known/agent-card.json`.
  - Allow custom `HttpClient`.
  - Validate base URL input.
- `[x]` Add direct-style `A2AClient`.
  - Constructor from endpoint URI and optional `HttpClient`.
  - Send `A2A-Version: 1.0`.
  - JSON-RPC request/response handling.
  - A2A error mapping.
  - SSE parsing for streaming methods without `System.Net.ServerSentEvents`.
- `[x]` Add client methods:
  - `SendMessageAsync`
  - `SendStreamingMessageAsync`
  - `GetTaskAsync`
  - `ListTasksAsync`
  - `CancelTaskAsync`
  - `SubscribeToTaskAsync`
  - `CreateTaskPushNotificationConfigAsync`
  - `GetTaskPushNotificationConfigAsync`
  - `ListTaskPushNotificationConfigAsync`
  - `DeleteTaskPushNotificationConfigAsync`
  - `GetExtendedAgentCardAsync`
- `[x]` Add dependency-free HTTP+JSON REST client support following the official SDK's separate REST-client shape.

## Phase 5 - A2A Server

- `[x]` Add direct-style `A2AHttpServer` using `HttpListener`.
  - No ASP.NET Core dependency.
  - Serve public Agent Card.
  - Serve optional extended Agent Card.
  - Handle JSON-RPC endpoint.
  - Handle HTTP+JSON REST endpoints.
  - Handle SSE streaming responses.
  - Expose optional authentication hook similar to `McpHttpServer.AuthenticationHandler`.
  - Support CORS configuration.
  - Support graceful stop/dispose.
- `[x]` Add `IA2AAgentHandler`.
  - Execute message requests.
  - Handle cancellation.
  - Direct/simple handler shape for Voltaic users.
- `[x]` Add request context and event queue helpers.
  - `A2ARequestContext`
  - `A2AAgentEventQueue`
  - `A2ATaskUpdater`
- `[x]` Add task store abstraction.
  - `IA2ATaskStore`
  - `InMemoryA2ATaskStore`
  - CRUD/list behavior.
  - History trimming behavior.
- `[x]` Add task projection logic for message, task, status update, and artifact update events.
- `[x]` Add live task subscription support.
- `[x]` Add return-immediately handling.
- `[x]` Add push notification configuration storage and APIs.
  - Multiple configs per task.
  - Create/get/list/delete.
  - Capability error if disabled.
- `[x]` Add extended Agent Card capability enforcement.
- `[x]` Add REST binding handlers.
  - `POST /message:send`
  - `POST /message:stream`
  - `GET /tasks/{id}`
  - `GET /tasks`
  - `POST /tasks/{id}:cancel`
  - `POST /tasks/{id}:subscribe`
  - `POST /tasks/{id}/pushNotificationConfigs`
  - `GET /tasks/{id}/pushNotificationConfigs/{configId}`
  - `GET /tasks/{id}/pushNotificationConfigs`
  - `DELETE /tasks/{id}/pushNotificationConfigs/{configId}`
  - `GET /extendedAgentCard`

## Phase 5b - A2A gRPC Binding

- `[x]` Add dependency-light production package references for gRPC transport.
  - `Watson` NuGet package for HTTP/2 server hosting.
  - `Google.Protobuf` for official-compatible protobuf messages.
  - `Grpc.Tools` as a private build-time code generator.
- `[x]` Add official-compatible A2A protobuf contract under `Voltaic.A2A.Grpc`.
  - Source proto lives under `src/Voltaic/A2A/Protos`.
  - `A2AService` service path `lf.a2a.v1.A2AService`.
  - Unary RPCs for message, task, push config, and extended Agent Card APIs.
  - Server-streaming RPCs for message streaming and task subscription.
- `[x]` Add internal gRPC wire helpers.
  - gRPC frame encode/decode.
  - HTTP/2 request configuration.
  - gRPC status/trailer mapping.
  - Model conversion between Voltaic A2A models and generated protobuf messages.
- `[x]` Add public `A2AGrpcClient`.
  - Direct Voltaic method names matching `A2AClient`.
  - `HttpClient` transport with HTTP/2 gRPC framing.
  - Unary and streaming response handling.
- `[x]` Add public `A2AGrpcServer`.
  - Watson HTTP/2 cleartext listener.
  - Public Agent Card and extended Agent Card JSON endpoints.
  - gRPC service path dispatch to the existing A2A endpoint behavior.
  - gRPC trailers/status responses for protocol errors.
- `[x]` Add gRPC protocol tests.
  - Unary send/get/list.
  - Streaming message responses.
  - Rich message parts and metadata round-trip.
  - Live task subscription and cancellation.
  - Push notification config create/get/list/delete.
  - Extended Agent Card.
  - Error mapping.
  - Authentication block behavior with public Agent Card discovery.
- `[x]` Update samples/manual harnesses to advertise or exercise gRPC where practical.
  - `Sample.A2AServer` starts gRPC on `port + 1`.
  - `Test.A2AServer` starts gRPC on `port + 1`.
  - `Test.A2AClient` exercises discovered `GRPC` interfaces.
- `[x]` Update README, CHANGELOG, and API coverage notes for gRPC.
- `[x]` Run full verification after gRPC additions.

## Phase 6 - Test Infrastructure

- `[x]` Add A2A shared Touchstone suites to `Test.Shared`.
- `[x]` Include A2A suites in `VoltaicSuites.All`.
- `[x]` Add A2A model serialization tests.
- `[x]` Add A2A client validation tests.
- `[x]` Add A2A server validation tests.
- `[x]` Add A2A JSON-RPC protocol tests.
- `[x]` Add A2A SSE streaming tests.
- `[x]` Add A2A REST binding tests.
- `[x]` Add A2A Agent Card discovery tests.
- `[x]` Add A2A task lifecycle tests.
- `[x]` Add A2A push notification configuration tests.
- `[x]` Add A2A extended Agent Card tests.
- `[x]` Add A2A return-immediately tests.
- `[x]` Add A2A HTTP+JSON client tests.
- `[x]` Add namespace/API surface tests for `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A`.
- `[x]` Add source-layout test enforcing `src/Voltaic/Core`, `src/Voltaic/Mcp`, `src/Voltaic/A2A`, and `src/Voltaic/A2A/Protos`.
- `[x]` Ensure console Touchstone runner executes the new suites.
- `[x]` Ensure xUnit and NUnit Touchstone runners execute the new suites.

## Phase 7 - Official SDK Compatibility Tests

- `[x]` Add compatibility tests using official `a2a-dotnet` as oracle where practical.
- `[x]` Compare Voltaic model serialization against official SDK JSON for representative payloads.
- `[x]` Verify official-style JSON-RPC request payloads are accepted by Voltaic server.
- `[x]` Verify Voltaic client request payloads match official SDK method names and envelope shape.
- `[x]` Verify SSE event framing matches official SDK expectations.
- `[x]` Keep official SDK dependency out of the production library.
- `[!]` If an official package reference is too unstable for normal CI, gate oracle tests with an opt-in tag/env var and document how to run them.
  - NuGet exact-match search for package `A2A` returned no package in this environment, so normal compatibility coverage uses the inspected official source commit as the oracle instead of a package reference.

## Phase 8 - Samples

- `[x]` Add `Sample.A2AServer`.
  - Public Agent Card.
  - JSON-RPC endpoint.
  - REST endpoint.
  - Streaming task updates.
  - Push notification config demo.
  - Extended Agent Card demo.
- `[x]` Add `Test.A2AClient` manual client.
- `[x]` Add `Test.A2AServer` manual server if separate from sample.
  - Manual harness with Agent Card, JSON-RPC, HTTP+JSON, streaming, extended Agent Card, task inspection, and push config commands.
- `[x]` Add solution entries for new sample/test projects.

## Phase 9 - Documentation

- `[x]` Update `README.md`.
  - New namespace guidance.
  - A2A overview.
  - A2A server example.
  - A2A client example.
  - Agent Card discovery.
  - Streaming/SSE behavior.
  - HTTP+JSON REST binding.
  - Push notification configuration behavior.
  - Extended Agent Card behavior.
  - Interop stance with official SDK.
  - Dependency stance and no ASP.NET Core note.
- `[x]` Update `CHANGELOG.md`.
  - Version `0.4.0`.
  - Namespace refactor.
  - A2A feature list.
  - Test coverage expansion.
  - Breaking pre-1.0 namespace note.
- `[x]` Update package metadata in `Voltaic.csproj`.
  - Version.
  - Description.
  - Tags.
  - Release notes.
- `[x]` Update `CLAUDE.md` repository guidance.
- `[x]` Update or add API coverage notes.

## Phase 10 - Verification

- `[x]` Run `dotnet build src/Voltaic.sln`.
  - Latest gRPC-era run: `dotnet build src\Voltaic.sln --no-restore`
  - Result: 0 warnings, 0 errors.
- `[x]` Run console Touchstone suite through `Test.Automated`.
  - `dotnet run --project src\Test.Automated\Test.Automated.csproj --framework net10.0`
  - Latest expanded full run: 274 passed, 0 failed.
  - Latest expanded A2A tag run: 21 passed, 0 failed.
- `[x]` Run xUnit tests.
  - `dotnet test src\Test.Xunit\Test.Xunit.csproj --framework net10.0 --no-build`
  - Latest expanded run: 274 passed, 0 failed.
- `[x]` Run NUnit tests.
  - `dotnet test src\Test.Nunit\Test.Nunit.csproj --framework net10.0 --no-build`
  - Latest expanded run: 274 passed, 0 failed.
- `[x]` Run A2A manual sample smoke tests.
  - Started `Sample.A2AServer` on port `8123` and called it with `Test.A2AClient`.
  - Verified Agent Card discovery, JSON-RPC send, JSON-RPC streaming, HTTP+JSON send, push config creation, and extended Agent Card.
- `[x]` Run A2A manual harness smoke tests.
  - Started `Test.A2AServer` on port `8124` and called it with `Test.A2AClient`.
  - Verified Agent Card discovery, JSON-RPC send, JSON-RPC streaming, HTTP+JSON send, push config creation, and extended Agent Card.
  - Latest gRPC-era smoke: started `Test.A2AServer` on port `8234`, which exposed gRPC on `8235`, and called it with `Test.A2AClient`.
  - Verified JSON-RPC send/streaming, HTTP+JSON send, push config creation, gRPC send/streaming, and extended Agent Card.
- `[x]` Run official SDK compatibility tests if available in this environment.
  - Official NuGet package was unavailable by exact package search; source-derived oracle tests ran in the normal Touchstone matrix.
- `[x]` Inspect generated `Voltaic.xml` for namespace/documentation correctness.
  - Verified generated XML uses `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A` member names with no obsolete one-level `Voltaic.*` members.
- `[x]` Review `git diff` for accidental unrelated changes.
  - `git diff --check` passes.
  - Content review used `git diff --ignore-space-at-eol`; remaining changes are namespace movement, A2A implementation/tests/samples/docs, package metadata, and generated XML.

## Running Notes

- `[x]` Phase 1 code namespace refactor builds; documentation namespace updates remain in Phase 9.
- `[x]` Keep this file updated as each phase progresses.
