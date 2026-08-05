# DOM Removal Migration (v1.0.0, breaking)

Goal: remove every use of `System.Text.Json` DOM types — `JsonElement`, `JsonDocument`, `JsonNode`, `JsonObject`, `JsonArray` — from the entire codebase (production, tests, samples), along with any `var` and tuples (already at zero). This is a **breaking change** because the public MCP/JSON-RPC handler contract changes shape.

`JsonSerializer` (serialize/deserialize to typed models and strings) is retained — it is not a DOM type.

## New handler contract

Handlers currently receive `JsonElement?` request params. They will instead receive `McpParameters?` (JSON-RPC) — a DOM-free wrapper carrying the raw params JSON with `Deserialize<T>()`, `RawJson`, and `HasValue`. Internal dispatch parses into typed param models (`McpCallToolParams`, `McpReadResourceParams`, …) via `JsonSerializer`.

Delegate changes (public, breaking):
- `Func<JsonElement?, object>` → `Func<McpParameters?, object>`
- `Func<JsonElement?, Task<object>>` → `Func<McpParameters?, Task<object>>`
- `Func<JsonElement?, CancellationToken, Task<object>>` → `Func<McpParameters?, CancellationToken, Task<object>>`

## Baseline

Last green commit before migration: `b2b8e96` (313/313 on net8.0 + net10.0). Commit only when green.

## Production files to convert (JsonElement/DOM hit counts)

- [ ] `src/Voltaic/Mcp/McpEndpoint.cs` (46) — dispatch + schema validator
- [ ] `src/Voltaic/Mcp/McpServer.cs` (21) — stdio server
- [ ] `src/Voltaic/Mcp/McpWebsocketsServer.cs` (19)
- [ ] `src/Voltaic/Mcp/McpHttpServer.cs` (19)
- [ ] `src/Voltaic/Mcp/McpTcpServer.cs` (12)
- [ ] `src/Voltaic/Core/JsonRpcServer.cs` (11)
- [ ] `src/Voltaic/A2A/A2AModels.cs` (11)
- [ ] `src/Voltaic/A2A/A2AHttpServer.cs` (8)
- [ ] `src/Voltaic/A2A/A2AGrpcWire.cs` (8)
- [x] `src/Voltaic/Mcp/McpWebsocketsClient.cs` (3) — result/id parsing converted (commit pending)
- [x] `src/Voltaic/Core/JsonRpcClient.cs` (3) — result/id parsing converted
- [x] `src/Voltaic/Mcp/McpClient.cs` (2) — result/id parsing converted
- [x] `src/Voltaic/Mcp/McpHttpClient.cs` (1) — result parsing converted
- [ ] `src/Voltaic/A2A/A2AClient.cs` (1)

## Foundation

- [ ] `McpParameters` DOM-free params wrapper
- [ ] Internal typed param models for MCP methods
- [ ] DOM-free JSON-schema validation (replace the JsonElement validator in McpEndpoint)

## Tests + samples

- [ ] All `src/Test.Shared/*` suites (127 DOM hits) — handler lambdas + result inspection via typed models / `RpcResponseHelpers`
- [ ] `src/Test.*` and `src/Sample.*` apps

## Release

- [ ] Bump to `1.0.0`; CHANGELOG breaking-change note; README handler-API migration guide
