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

## A2A (done)

- Production: `A2AModels` (`object?` / `Dictionary<string, object?>`), `A2AGrpcWire` (Struct<->JSON via
  `Utf8JsonReader`), `A2AHttpServer`, `A2AClient` — all converted; the library round-trips arbitrary
  JSON (including caller-supplied deserialized values) through `JsonSerializer`.

## Tests + demo apps (done)

- Added `JsonProbe` (Test.Shared) — a `Utf8JsonReader`-based navigable JSON view for assertions
  (`Get`/`[i]`/`String`/`Int`/`Long`/`Double`/`Bool`/`Has`/`TryGet`/`EnumerateArray`/`IsObject`/`IsArray`/`From`).
- Converted every result-inspection site across the Test.Shared suites and the demo client apps
  (`Test.McpClient`, `Test.McpHttpClient`, `Test.JsonRpcClient`, `Test.McpWebsocketsClient`).

## Status: complete

A `grep` for `JsonElement`/`JsonDocument`/`JsonNode`/`JsonObject`/`JsonArray` across all of `src` returns
**zero** matches. No bare `JsonValueKind`. `var` and tuples are zero. The only remaining JSON reader
types are the streaming `Utf8JsonReader`/`JsonTokenType` (not DOM) and the project's own
`McpJsonValueKind`/`JsonValueInfo`/`JsonProbe`/`RpcParameters` helpers. Full solution builds
warning-free on net8.0 and net10.0; console suite 313/313 on both.

## Release

Staying on `v0.6.0` (pre-1.0 ALPHA); no version bump.
