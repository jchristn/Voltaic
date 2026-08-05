# DOM Removal Migration (v0.6.0, breaking — pre-1.0 ALPHA)

> Voltaic is pre-1.0 ALPHA; the README states surfaces are likely to shift, so this breaking
> change lands under v0.6.0 rather than forcing a 1.0.0. Semver-stable begins at 1.0.0.

Goal: remove every use of `System.Text.Json` DOM types — `JsonElement`, `JsonDocument`, `JsonNode`,
`JsonObject`, `JsonArray` — from the codebase, along with any `var` and tuples. `JsonSerializer` and
the streaming `Utf8JsonReader` are retained; they are not DOM types.

## New handler contract

Handlers previously received `JsonElement?` request params. They now receive
`Voltaic.Core.RpcParameters?` — a DOM-free wrapper carrying the raw params JSON with `Deserialize<T>()`,
`RawJson`, `HasValue`, and scalar accessors (`GetString`/`GetDouble`/`GetInt64`/`GetBoolean`/
`ContainsProperty`) built on `Utf8JsonReader`. Internal dispatch parses into typed param models.

Delegate change (public, breaking): `Func<JsonElement?, …>` → `Func<RpcParameters?, …>`.

## Done (green: full solution builds warning-free on net8.0 + net10.0, 313/313 tests)

- Foundation: `Voltaic.Core.RpcParameters`, `RpcMethodInvocation` (replaces a value tuple),
  typed param models (`McpDispatchModels`, `RpcBuiltInModels`), and a DOM-free schema validator
  (`McpSchemaValidator` + `JsonValueInfo` + `McpJsonValueKind`).
- Core: `JsonRpcServer` (incl. removing its value tuple), `JsonRpcClient`.
- MCP: `McpEndpoint` (dispatch + validator), `McpServer`, `McpHttpServer`, `McpTcpServer`,
  `McpWebsocketsServer`, `McpClient`, `McpHttpClient`, `McpWebsocketsClient`.
- Tests: all MCP/JSON-RPC `Test.Shared` suites (handler lambdas + response inspection via typed
  models / `RpcResponseHelpers`).
- Apps: `Sample.McpServer`, `Test.McpServer`, `Test.McpHttpServer`, `Test.McpWebsocketsServer`,
  `Test.JsonRpcServer`.
- `var` and tuples: zero across the codebase.

## Remaining

- A2A production: `A2AModels.cs`, `A2AHttpServer.cs`, `A2AGrpcWire.cs`, `A2AClient.cs` (~28 hits).
- A2A test suites: `A2AProtocolSuites.cs`, `A2ACompatibilitySuites.cs` (still compile; convert with
  A2A production).
- Doc-comment mentions of `JsonElement` in `RpcParameters.cs` / `McpJsonValueKind.cs` (explanatory,
  not usage).

## Release

Staying on `v0.6.0` (pre-1.0 ALPHA); no version bump. CHANGELOG note to be added when A2A lands.
