# Voltaic — Multi-Version MCP Support Plan (v0.6.0)

Target release: **v0.5.1 → v0.6.0** (minor, additive, backward compatible)
Scope: MCP client **and** server, protocol models, transports, extensions, samples, tests, documentation.
Authority: this plan is written against the live specification at `modelcontextprotocol.io/specification/*` and complies with `C:\code\agents\requirements` (`CODE_STYLE.md`, `REPOSITORY_REQUIREMENTS.md`, `BACKEND_TEST_ARCHITECTURE.md`, `WRITING_DOCUMENTS.md`, `AUTHENTICATION.md`).

This is a working implementation plan. It is meant to be edited in place as work proceeds — check the boxes, fill the Status/Notes columns, and strike or annotate anything the spec forces you to change.

---

## How to use this document

Every task carries an ID (`T-###`) and a checkbox. Test cases carry an ID (`TC-###`) and a status cell. Update them as you go.

Progress markers:

- `[ ]` not started
- `[~]` in progress
- `[x]` complete and verified (built warning-free + covered by a passing test)
- `[!]` blocked — record the blocker in Notes

A task is not `[x]` until its code builds clean on **both** `net8.0` and `net10.0` and its associated test cases pass through the console, xUnit, and NUnit runners.

The wire-level details that were open in the first draft are now resolved against canonical sources — the `2026-07-28` schema page, the MRTR pattern page, the `2025-11-25` changelog, and the `ext-tasks` `schema/draft/schema.ts`. Concrete codes and field names are inlined below. The one remaining `[PENDING SCHEMA]` is MCP Apps + EMA (T-084), which the plan deliberately limits to capability negotiation in v0.6.0; confirm those two extension surfaces against their own specs before building past negotiation. See Section 11 for the resolved-source list.

---

## 1. Objective

Voltaic today negotiates two MCP revisions — `2025-11-25` (default) and `2025-03-26` — inside a single handshake-based, session-oriented HTTP transport. The goal of v0.6.0 is to support the full set of publicly listed MCP protocol revisions, on both the client and the server, and to add explicit positive and negative test coverage for each one.

The hard constraint is backward compatibility. Nothing that works against `2025-03-26` or `2025-11-25` today may change behavior. Every new revision is added as an additive path selected by a version resolver, and the older handshake transport keeps running untouched beside the new stateless transport.

## 2. Supported version matrix

Five revisions are in scope. They split into two eras, and that split drives the whole architecture.

| Revision | Era | Transport shape | Status in Voltaic today | Headline features to honor |
|----------|-----|-----------------|-------------------------|----------------------------|
| `2024-11-05` | Handshake (init-based) | HTTP+SSE two-endpoint (GET SSE + POST, `endpoint` event); stdio. JSON-RPC batching allowed. No `MCP-Protocol-Version` header. | **Not implemented** | Original base protocol, `initialize`/`initialized`, HTTP+SSE transport (now Deprecated but still supported) |
| `2025-03-26` | Handshake | Streamable HTTP introduced: `Mcp-Session-Id`, GET SSE stream, DELETE termination, resumable via `Last-Event-ID`. | **Implemented** (compat) | Streamable HTTP, sessions, server-initiated requests on SSE |
| `2025-06-18` | Handshake | Streamable HTTP + sessions. `MCP-Protocol-Version` header **required** on HTTP. Batching **removed**. | **Not implemented** | Structured tool output, elicitation, resource links, `title`, completion `context`, OAuth Resource Server + RFC 8707, lifecycle SHOULD→MUST |
| `2025-11-25` | Handshake | Streamable HTTP + sessions (current default). | **Implemented** (default) | Carries 2025-06-18 forward and adds: icons metadata on tools/resources/templates/prompts; **experimental in-core tasks** (`tasks/get`, `tasks/result`, `tasks/list`, `tasks/cancel`, `notifications/tasks/status`, `execution.taskSupport`); OIDC discovery; incremental scope consent via `WWW-Authenticate`; OAuth Client ID Metadata Documents (recommended); standards-based `ElicitResult`/`EnumSchema` (titled/untitled, single/multi-select enums, default values); URL-mode elicitation; sampling tool-calling (`tools`/`toolChoice`); `Implementation.description`; JSON Schema 2020-12 as default dialect; input-validation errors returned as Tool Execution Errors (not Protocol Errors); HTTP 403 for invalid `Origin`; pollable SSE streams (server may disconnect at will) |
| `2026-07-28` | **Stateless** (per-request `_meta`) | Single POST-only MCP endpoint. No sessions, no GET stream, no DELETE, no resumability. | **Not implemented** | `server/discover`, per-request version+capability negotiation, MRTR, header routing (`Mcp-Method`/`Mcp-Name`/`Mcp-Param-*`), cacheable lists, Tasks/Apps/EMA extensions |

The current constants live in `src/Voltaic/Mcp/McpProtocol.cs` (`LatestProtocolVersion = "2025-11-25"`, `ProtocolVersion20250326`). Everything in Section 5 builds on that file.

## 3. Architecture strategy

### 3.1 Two eras, one server

Revisions `2024-11-05` through `2025-11-25` are *initialization-based*: the client sends `initialize`, the server replies with negotiated version and capabilities, and (on Streamable HTTP) a session is minted. `2026-07-28` is *stateless*: every request is self-describing via `_meta`, there is no handshake, and `server/discover` is an optional single-shot capability query. A backward-compatible server has to serve both without being told up front which the caller speaks.

The design keeps the existing session machinery in `McpHttpServer` intact for the handshake era and adds a parallel stateless handler beside it. The dispatch core in `McpEndpoint` (tool/resource/prompt/completion registries and their `ListTools`/`CallToolAsync`/etc. methods) is already stateless and is reused verbatim by both paths. Only the request envelope forks.

### 3.2 The version + mode resolver (the new critical path)

A single resolver runs before dispatch and returns a resolved revision plus an era/mode. Precedence, highest first:

1. `MCP-Protocol-Version` HTTP header, when present.
2. `_meta["io.modelcontextprotocol/protocolVersion"]` in the request body.
3. Structural cues: method `initialize`/`notifications/initialized`, or an `Mcp-Session-Id` header → handshake era; presence of `Mcp-Method`/`Mcp-Name` routing headers → stateless era.
4. Default: handshake era at `2025-03-26` (the spec-sanctioned fallback for pre-`2025-06-18` clients that send no header).

On `2026-07-28`, if the header and the body `_meta` version disagree, reject with `400` + `HeaderMismatch` (`-32020`). If the requested version is unknown/unsupported, reject with `UnsupportedProtocolVersionError` listing supported versions. The resolved version is stamped on the response.

### 3.3 Capability projection

`McpEndpoint.BuildCapabilities()` (currently one shape) becomes version-parameterized. It advertises logging and other deprecated bits to handshake-era clients, and projects the `2026-07-28` capability object (including `extensions`) for stateless clients. The registry backing it does not change.

### 3.4 Per-transport reach

Statelessness is an HTTP concern. `McpClient` (stdio), `McpTcpServer/Client`, and the WebSocket transports keep their handshake flow for every revision; they need only the new model fields, the version constant, and the `server/discover` probe for stdio backward-compatibility detection. The `2026-07-28` stateless request path is implemented only for Streamable HTTP (`McpHttpServer`/`McpHttpClient`).

## 4. Semantic versioning and release metadata

- [ ] **T-001** Bump `<Version>` in `src/Voltaic/Voltaic.csproj` from `0.5.1` to `0.6.0`.
- [ ] **T-002** Rewrite `<PackageReleaseNotes>` to summarize multi-version MCP support (keep prior-release history per the existing convention).
- [ ] **T-003** Update `<Description>`/`<PackageTags>` only if new surface warrants (e.g. add `tasks`, `discovery`).

The bump is a minor version because every change is additive: no public signature is removed or altered, and existing default behavior (`2025-11-25`, handshake, sessions) is unchanged. If any task in this plan forces a breaking change to a public type, stop and escalate — that would push the release to a different version decision.

## 5. Compliance mapping

The plan is bound to the requirement documents as follows, and each implementation task inherits these rules:

- **`CODE_STYLE.md`** — namespace-first with usings inside the namespace (system/Microsoft alphabetical, then others); `_PascalCase` private fields; no `var`; no tuples (use named model classes for every new result/descriptor); XML docs on all public members with `<exception>`, nullability, thread-safety, and default/min/max notes; `.ConfigureAwait(false)`; `CancellationToken` on new async methods; one class or one enum per file; no `Console.WriteLine` in `src/Voltaic`; specific/custom exception types (`McpProtocolException`). New protocol errors extend the existing `McpProtocolException` factory rather than throwing generic exceptions.
- **`REPOSITORY_REQUIREMENTS.md`** — all code stays under `src/`; `README.md`, `CHANGELOG.md`, `LICENSE.md` maintained. Voltaic is a library, not a container service, so `.dockerignore`/`DOCKERHUB_README.md`/compose files are out of scope (no Docker artifacts in this repo); confirm this exemption still holds at release (**T-902**).
- **`BACKEND_TEST_ARCHITECTURE.md`** — all test logic expressed as Touchstone `TestCaseDescriptor`s in `Test.Shared` with zero console output; consumed unchanged by `Test.Automated` (console), `Test.Xunit`, and `Test.Nunit`. Assertions throw on failure. New suites register through the shared `All` aggregator so every runner picks them up automatically.
- **`WRITING_DOCUMENTS.md`** — applies to the README prose (Section for docs). The plan itself and the CHANGELOG are implementation/maintenance files. README edits get the human-voice review pass before release.
- **`AUTHENTICATION.md`** — Voltaic is a protocol library; it does not own the platform AAA/RBAC model described there. MCP-level auth hardening (Section P6) provides protocol hooks (`AuthenticationHandler`, OAuth Resource Server metadata, RFC 8707/9207 client behavior). Full tenant/credential/session/RBAC enforcement remains the responsibility of the host application that embeds Voltaic; the plan documents that boundary rather than implementing a database.

## 6. Work breakdown

Phases are ordered by dependency. Within a phase, tasks may proceed in parallel unless noted. Each phase ends with a Definition of Done (DoD).

### P0 — Foundations (version registry, resolver, errors)

- [ ] **T-010** Add version constants for `2024-11-05`, `2025-06-18`, `2026-07-28` in `McpProtocol.cs`; set `LatestProtocolVersion = "2026-07-28"`; keep `2025-11-25`/`2025-03-26`.
- [ ] **T-011** Introduce a version registry describing each revision: value, era (`Handshake`/`Stateless`), transport capabilities (sessions, GET-SSE, batching allowed, header-required), and deprecation flags. One class per file; era is its own enum file (`McpProtocolEra.cs`).
- [ ] **T-012** Rework `IsSupportedVersion`/`NegotiateVersion` to consult the registry and to return a resolved version + era rather than a bare string. Preserve the existing throw-on-unsupported contract for handshake callers.
- [ ] **T-013** Implement the resolver (Section 3.2) as a dedicated class consumed by `McpHttpServer`. Deterministic precedence table; unit-tested in isolation.
- [ ] **T-014** Add protocol error factories to `McpProtocolException.cs`, with the confirmed JSON-RPC codes and `data` payloads:
  - `UnsupportedProtocolVersionError` — code **`-32022`**, `data: { supported: string[]; requested: string }`, HTTP `400`.
  - `HeaderMismatchError` — code **`-32020`**, no `data` (message only), HTTP `400`.
  - `MissingRequiredClientCapabilityError` — code **`-32021`**, `data: { requiredCapabilities: ClientCapabilities }`, HTTP `400`.
  - Method-not-found — code **`-32601`**, returned with HTTP `404` on the stateless endpoint.
- [x] **T-015** `2025-11-25`-vs-`2025-06-18` deltas confirmed from `/specification/2025-11-25/changelog` (see the version matrix row and Section 7.4). Record them in T-011's registry: icons metadata; experimental in-core tasks; OIDC discovery; incremental scope consent; OAuth CIMD; standards-based elicitation enums + default values + URL-mode elicitation; sampling tool-calling; `Implementation.description`; JSON Schema 2020-12 default dialect; tool input-validation errors as Tool Execution Errors; `403` on invalid `Origin`; pollable SSE.

**DoD P0:** resolver + registry unit-tested; `McpProtocol.NegotiateVersion` returns correct era for all five versions; no downstream consumers broken.

### P1 — Protocol model additions (shared, additive)

All additions are nullable / `JsonIgnoreCondition.WhenWritingNull` and serialize only when the resolved version supports them.

- [ ] **T-020** `_meta` well-known keys as typed helpers: `io.modelcontextprotocol/protocolVersion`, `io.modelcontextprotocol/clientInfo` (`{name,version}`), `io.modelcontextprotocol/clientCapabilities` (incl. `extensions`), `io.modelcontextprotocol/serverInfo`. Back onto the existing `_meta` dictionary in `McpCommonModels.cs`.
- [ ] **T-021** `DiscoverResult` model (`McpDiscoverModels.cs`): `resultType`, `supportedVersions[]`, `capabilities`, `instructions`, `ttlMs`, `cacheScope`, plus `_meta.serverInfo`.
- [ ] **T-022** MRTR models (confirmed against the MRTR pattern page). `InputRequiredResult`: `resultType: "input_required"`; optional `inputRequests` (an `InputRequests` map: string key → one of `ElicitRequest` / `CreateMessageRequest` / `ListRootsRequest`, each `{method, params}`); optional `requestState` (opaque server string the client must echo verbatim and never inspect). Every `InputRequiredResult` must carry at least one of `inputRequests`/`requestState`. Retry params carry `inputResponses` (an `InputResponses` map keyed to the `inputRequests` keys: `ElicitResult` / `CreateMessageResult` / `ListRootsResult`) plus the echoed `requestState`. Model these as named classes (no tuples). `InputRequiredResult` is valid only on `tools/call`, `resources/read`, `prompts/get`.
- [ ] **T-023** Cacheable-list fields (`ttlMs`, `cacheScope`) on `McpListToolsResult`, `McpListPromptsResult`, `McpListResourcesResult`, `McpListResourceTemplatesResult`, and the `resources/read` result (`McpListResults.cs`, `McpResourceModels.cs`). Configurable defaults via public endpoint properties, not constants.
- [ ] **T-024** `title` field on tool/resource/prompt models (2025-06-18+); keep `name` as the programmatic id. `McpToolModels.cs`, `McpResourceModels.cs`, `McpPromptModels.cs`.
- [ ] **T-025** `context` field on `McpCompleteRequest` (`McpCompletionModels.cs`), 2025-06-18+.
- [ ] **T-026** Resource links in tool-call results (2025-06-18+): confirm/extend `McpToolCallResult`/content models in `McpContentModels.cs`.
- [ ] **T-027** Verify existing structured tool output (`McpToolCallResult.StructuredContent` + `OutputSchema` validation in `McpEndpoint.CallToolAsync`) satisfies 2025-06-18; gate its *advertisement* by version.
- [ ] **T-028** `x-mcp-header` input-schema annotation support model + validation rules (primitive types only, statically reachable via `properties`, case-insensitive uniqueness, `Mcp-Param-{Name}` mapping, Base64 sentinel `=?base64?...?=`). `McpToolModels.cs` + a header-encoding helper.
Tasks appear in **two distinct shapes** and both are in scope; keep them in separate model files.

- [ ] **T-029a** `2025-11-25` experimental in-core tasks (`McpTaskModels.cs`): `Task` (`taskId`, `status`, `statusMessage?`, `createdAt` ISO-8601, `lastUpdatedAt` ISO-8601, `ttl` ms, `pollInterval?` ms), `CreateTaskResult` (wraps `task`), task-request param `task: { ttl? }`, tool `execution.taskSupport` (`required`/`optional`/`forbidden`), the request-category `tasks` capability (`tasks.list`, `tasks.cancel`, `tasks.requests.tools.call`, `tasks.requests.sampling.createMessage`, `tasks.requests.elicitation.create`), and the `io.modelcontextprotocol/related-task` `_meta` key.
- [ ] **T-029b** `2026-07-28` `io.modelcontextprotocol/tasks` extension (`McpTasksExtensionModels.cs`), confirmed against `ext-tasks` `schema/draft/schema.ts`: `TaskStatus` = `working`/`input_required`/`completed`/`failed`/`cancelled`; base `Task` = `{ taskId: string; status: TaskStatus; statusMessage?: string; ttlMs: number | null; pollIntervalMs?: number }`; status-specific subtypes (`WorkingTask`, `InputRequiredTask` adds `inputRequests: InputRequests`, `CompletedTask`, `FailedTask`, `CancelledTask`) unioned as `DetailedTask`; `CreateTaskResult = Result & Task` with `resultType: "task"`; `tasks/get` → `Result & DetailedTask` (`resultType: "task"`); `tasks/update` = `{ taskId, inputResponses: InputResponses }` → empty `Result` (`resultType: "complete"`); `tasks/cancel` = `{ taskId }` → empty `Result` (`resultType: "complete"`); `notifications/tasks` carrying a `DetailedTask`; subscription via `subscriptions/listen` with optional `taskIds[]` and an acknowledged-`taskIds[]` reply; extension capability value is `{}` (`Record<string, never>`). `InputRequests`/`InputResponses` are the same types as MRTR (T-022).

**DoD P1:** all models serialize/deserialize round-trip in a model suite; no field leaks into a version that must not carry it.

### P2 — Server: stateless Streamable HTTP (2026-07-28)

New handler in `McpHttpServer`, selected by the resolver; existing session handler untouched.

- [ ] **T-030** Single POST-only stateless path on the MCP endpoint: read `_meta`, dispatch through `McpEndpoint`, return single `application/json` object or a per-request SSE stream.
- [ ] **T-031** Enforce `MCP-Protocol-Version` header presence and header/body match; emit `HeaderMismatch` (`-32020`) / `UnsupportedProtocolVersionError` per spec.
- [ ] **T-032** Header routing validation: require `Mcp-Method` on all requests; require `Mcp-Name` on `tools/call`/`resources/read`/`prompts/get`; validate against body (`params.name`/`params.uri`), decoding Base64 sentinel first.
- [ ] **T-033** `Mcp-Param-{Name}` validation: decode, compare to body value at the annotated property path, reject mismatches with `-32020`.
- [ ] **T-034** `server/discover` handler returning `DiscoverResult` (T-021) with `supportedVersions`, projected capabilities (incl. `extensions`), `serverInfo`, `instructions`, `ttlMs`, `cacheScope`.
- [ ] **T-035** MRTR emission: let a tool/prompt handler return `InputRequiredResult`; accept the retried call carrying `inputResponses`; correlate and resume. Opt-in handler contract — existing `object`-returning handlers are unaffected.
- [ ] **T-036** `subscriptions/listen` handler: long-lived SSE response stream, first emit `notifications/subscriptions/acknowledged`, then deliver only opted-in change notifications (`tools/list_changed`, `resources/updated`, `notifications/tasks`). Keep-alive via SSE comment lines; `X-Accel-Buffering: no`.
- [ ] **T-037** Notification POST → `202 Accepted` (no body); request-scoped notifications (`progress`, `message`) flow only on the originating request's SSE stream, never on the listen stream.
- [ ] **T-038** Stateless refusals: `GET`/`DELETE` on the MCP endpoint → `405`; ignore `Mcp-Session-Id` and `Last-Event-ID` (do not mint/echo); unknown method → `404` + `-32601`.
- [ ] **T-039** `Origin` header validation → `403` on invalid; localhost-bind guidance surfaced via config, not forced.
- [ ] **T-040** Cancellation = client closing the request's SSE stream; stop work, send nothing further.
- [ ] **T-041** Cacheable list results: populate `ttlMs`/`cacheScope` on list + read + discover responses.

**DoD P2:** a `2026-07-28` client completes discover → list → call → MRTR → subscribe against the stateless path; every negative case in TC-500x returns the specified status/error.

### P3 — Server: handshake-era transports (2024-11-05 / 2025-03-26 / 2025-06-18 / 2025-11-25)

- [ ] **T-050** HTTP+SSE two-endpoint transport for `2024-11-05`: GET opens SSE and emits the `endpoint` event first; POST to the returned endpoint; allow JSON-RPC batching. Kept as a Deprecated-but-supported path.
- [ ] **T-051** Retain existing Streamable-HTTP session path for `2025-03-26`/`2025-11-25` (sessions, GET SSE, DELETE, `202`), now selected via the resolver rather than assumed.
- [ ] **T-052** Add `2025-06-18` behavior: require `MCP-Protocol-Version` header on subsequent HTTP requests; **reject JSON-RPC batches**; enforce lifecycle ordering (SHOULD→MUST) so calls before `initialized` error; advertise structured output, elicitation, resource links, `title`, completion `context`.
- [ ] **T-053** Version-gate `McpEndpoint.BuildCapabilities()` per revision (Section 3.3), including deprecated-feature suppression for stateless callers.
- [ ] **T-054** Ensure per-version negotiation in `initialize` echoes the negotiated version and never advertises a feature the negotiated version predates.
- [ ] **T-055** `2025-11-25` experimental in-core tasks (T-029a): the request-category `tasks` capability, `tools/list` `execution.taskSupport`, `task`-augmented `tools/call`, `tasks/get` / `tasks/result` / `tasks/list` (paginated) / `tasks/cancel`, `notifications/tasks/status`, `related-task` `_meta`, and the terminal-cancel `-32602` rule. Distinct from the P5 stateless extension — do not share method handlers (`tasks/result`+`tasks/list` here vs `tasks/update` there; `ttl` vs `ttlMs`).
- [ ] **T-056** Icons metadata on tools/resources/templates/prompts; `Implementation.description`; JSON Schema 2020-12 as the default validation dialect for `2025-11-25`; return tool input-validation failures as Tool Execution Errors (not Protocol Errors).

**DoD P3:** each handshake revision negotiates, lists, and calls correctly; batching is accepted on ≤`2025-03-26`/`2024-11-05` and rejected on ≥`2025-06-18`; the pre-existing `2025-11-25`/`2025-03-26` behavior is byte-for-byte unchanged for clients that send today's requests.

### P4 — Client

- [ ] **T-060** `McpHttpClient`: version selection; stateless mode emitting `_meta` (protocolVersion/clientInfo/clientCapabilities), `MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name`, and `Mcp-Param-*` (with Base64 sentinel encoding) per request.
- [ ] **T-061** `McpHttpClient`: `server/discover` call + typed `DiscoverResult`; choose a mutually supported version; on `UnsupportedProtocolVersionError`, retry with an advertised version.
- [ ] **T-062** `McpHttpClient`: MRTR retry loop — on `InputRequiredResult`, gather inputs and re-POST the original request with `inputResponses`.
- [ ] **T-063** `McpHttpClient`: `subscriptions/listen` consumer for change notifications; backward-compatible fallback to the legacy GET SSE stream when talking to a handshake-era server.
- [ ] **T-064** `McpHttpClient`: reject tool definitions whose `x-mcp-header` annotations violate constraints (exclude from `tools/list`), logging a warning via the existing `Log` event.
- [ ] **T-065** `McpClient` (stdio): `server/discover` first as the backward-compat probe; fall back to `initialize` handshake when the server is legacy.
- [ ] **T-066** `McpTcpClient` / `McpWebsocketsClient`: add new model fields + version selection; keep handshake flow (no statelessness required).
- [ ] **T-067** Preserve `McpHttpClient.SetRequestHeader` (v0.5.0 auth headers) across all new request paths.

**DoD P4:** clients interoperate with a Voltaic server on every supported version; the stdio probe correctly distinguishes modern vs legacy servers.

### P5 — Tasks extension for `2026-07-28` (`io.modelcontextprotocol/tasks`)

This is the stateless-era extension (T-029b), separate from the `2025-11-25` in-core tasks in P3/T-055.

- [ ] **T-070** Negotiation: read `extensions` in the per-request `io.modelcontextprotocol/clientCapabilities` `_meta`; advertise the `io.modelcontextprotocol/tasks` extension (value `{}`) in `server/discover` capabilities. Never return a task to a client that did not declare support (guarded).
- [ ] **T-071** `CreateTaskResult` (`Result & Task`, `resultType: "task"`) emission from a supported request when the server elects async execution; task durably created before the response is sent.
- [ ] **T-072** `tasks/get` → `Result & DetailedTask` (status-specific: `InputRequiredTask.inputRequests`, `CompletedTask` result, `FailedTask` error); `tasks/update` `{ taskId, inputResponses }` → empty ack (`resultType: "complete"`, ignore unknown/satisfied keys); `tasks/cancel` `{ taskId }` → empty ack (cooperative).
- [ ] **T-073** `notifications/tasks` (carrying a `DetailedTask`) delivered over `subscriptions/listen`; honor the optional `taskIds[]` subscription filter and emit the acknowledged-`taskIds[]` reply.
- [ ] **T-074** Durable task store abstraction + in-memory default (mirror the A2A `IA2ATaskStore`/`InMemoryA2ATaskStore` pattern already in the repo). Respect `ttlMs` (nullable = unlimited) and generate cryptographically strong `taskId`s; bind tasks to the auth context when one exists.
- [ ] **T-075** Client-side task polling honoring `pollIntervalMs`, `input_required` handling via `tasks/update`, durable `taskId` persistence hook.

**DoD P5:** full task lifecycle (`working`→`input_required`→`completed`, plus `failed`/`cancelled`) exercised end-to-end; capability-guard proven (no task to a non-declaring client).

### P6 — Authorization hardening (protocol hooks)

Voltaic supplies protocol-level auth behavior and hooks; it does not implement the platform AAA/RBAC store (Section 5, `AUTHENTICATION.md` boundary).

- [ ] **T-080** OAuth Resource Server metadata (2025-06-18+): serve/consume protected-resource metadata for authorization-server discovery; document the hosting app's responsibility.
- [ ] **T-081** RFC 8707 Resource Indicators on the client token flow (2025-06-18+).
- [ ] **T-082** RFC 9207 `iss` validation on the client before redeeming authorization codes (2026-07-28).
- [ ] **T-083** Dynamic Client Registration `application_type` support and the DCR→CIMD (Client ID Metadata Documents) direction; document deprecation of DCR.
- [ ] **T-084** `[PENDING SCHEMA]` MCP Apps + EMA (Enterprise Managed Authorization) extension surfaces: negotiate via `extensions`; scope depth deliberately minimal for v0.6.0 (advertise + accept), with a Future Work note (Section 10). Confirm shapes against the extensions specs before building beyond negotiation.

**DoD P6:** client performs issuer validation and resource indication where the negotiated version requires it; the library/host boundary is documented in README + XML docs.

### P7 — Samples and manual test apps

- [ ] **T-090** `Sample.McpServer`: advertise all supported versions; expose a stateless `2026-07-28` endpoint alongside the existing handshake endpoint; add a Tasks-enabled tool.
- [ ] **T-091** `Test.McpHttpServer` / `Test.McpHttpClient`: add a stateless-mode switch and a `server/discover` demo.
- [ ] **T-092** Keep `Sample.*`/`Test.*` console output free of any change to `src/Voltaic` logging rules (console allowed only in sample/test apps).

**DoD P7:** samples run against every version from the CLI as documented in `CLAUDE.md`.

### P8 — Documentation

- [ ] **T-100** README: supported-version matrix, stateless-vs-handshake explanation, client/server usage per version, migration notes. Apply the `WRITING_DOCUMENTS.md` human-voice pass before release.
- [ ] **T-101** CHANGELOG: a `v0.6.0` entry itemizing every additive change (models, transports, extensions, tests), in the established style.
- [ ] **T-102** Update the public API coverage matrix in `Test.Shared` docs to include the new surface.
- [ ] **T-103** Update `CLAUDE.md` project overview (currently cites `v0.4.0` / protocol `2025-11-25`) to reflect `v0.6.0` and the five supported revisions.
- [ ] **T-104** Ensure every new public member has XML docs; regenerate `Voltaic.xml`.

**DoD P8:** README is accurate against the shipped code (CODE_STYLE "analyze the README and ensure it is accurate"); CHANGELOG and package release notes agree.

### P9 — Release readiness

- [ ] **T-110** Full solution builds warning-free on `net8.0` and `net10.0` (`dotnet build src/Voltaic.sln -c Release`).
- [ ] **T-111** All Touchstone suites green through console, xUnit, and NUnit on both frameworks; export JSON results.
- [ ] **T-112** `dotnet pack` produces a valid `0.6.0` package with README/LICENSE/symbols.
- [ ] **T-902** Confirm the Docker-artifact exemption (library, not service) still applies; if a container sample is added later, revisit `REPOSITORY_REQUIREMENTS.md` items 2/4/9.

**DoD P9:** tagged, packable, green on both TFMs, docs accurate.

## 7. Testing plan

The core requirement: **explicit positive and negative Touchstone suites for each supported MCP version**, on both client and server. All descriptors live in `Test.Shared` (no console output), register through the shared `All` aggregator, and therefore run identically through `Test.Automated` (console), `Test.Xunit`, and `Test.Nunit` on `net8.0` and `net10.0`. Positive cases assert that a version does what it must; negative cases assert that it rejects what it must — a version is only proven when both directions hold.

Suite registry to add (one suite pair per version, plus cross-cutting suites):

| Suite ID | Purpose |
|----------|---------|
| `Mcp.Version.20241105.Positive` / `.Negative` | 2024-11-05 handshake + HTTP+SSE transport |
| `Mcp.Version.20250326.Positive` / `.Negative` | 2025-03-26 Streamable HTTP + sessions |
| `Mcp.Version.20250618.Positive` / `.Negative` | 2025-06-18 structured output, elicitation, header-required, no-batch |
| `Mcp.Version.20251125.Positive` / `.Negative` | 2025-11-25 current default |
| `Mcp.Version.20260728.Positive` / `.Negative` | 2026-07-28 stateless core + MRTR + header routing + tasks |
| `Mcp.Version.Negotiation` | resolver precedence, mixed cues, fallback |
| `Mcp.Version.Interop` | one running server, many clients across versions |

Case-status legend for the tables below: `☐` not written · `◐` written, not passing · `☑` passing on all runners.

### 7.1 `2024-11-05`

| ID | Direction | Case | Expected |
|----|-----------|------|----------|
| TC-1001 | + | Negotiate via `initialize` with `2024-11-05` | server echoes `2024-11-05` |
| TC-1002 | + | HTTP+SSE: GET opens stream, first event is `endpoint` | `endpoint` event received |
| TC-1003 | + | POST to advertised endpoint; `tools/list` + `tools/call` | success results |
| TC-1004 | + | JSON-RPC **batch** request | accepted (batching legal here) |
| TC-1005 | + | Request with no `MCP-Protocol-Version` header | accepted |
| TC-1006 | − | Negotiate an unsupported version string | error, supported list surfaced |
| TC-1007 | − | Request an `elicitation`/resource-link feature (added 2025-06-18) | not advertised / rejected |
| TC-1008 | − | `Mcp-Session-Id` semantics expected by caller | server does not mint sessions on this transport |

### 7.2 `2025-03-26`

| ID | Direction | Case | Expected |
|----|-----------|------|----------|
| TC-2001 | + | `initialize` `2025-03-26` mints `Mcp-Session-Id` | session header returned |
| TC-2002 | + | Subsequent request carries session; GET SSE stream opens | server-initiated messages delivered |
| TC-2003 | + | Notification POST | `202 Accepted` |
| TC-2004 | + | DELETE terminates session | subsequent reuse → `404` |
| TC-2005 | + | Batch request | accepted (still legal) |
| TC-2006 | − | POST missing `Accept: application/json, text/event-stream` | `406` |
| TC-2007 | − | Reuse a terminated session | `404` |
| TC-2008 | − | Unsupported version | error |
| TC-2009 | − | Require 2025-06-18 elicitation | not available |

### 7.3 `2025-06-18`

| ID | Direction | Case | Expected |
|----|-----------|------|----------|
| TC-3001 | + | Negotiate `2025-06-18` | echoed |
| TC-3002 | + | `MCP-Protocol-Version` header honored on subsequent HTTP request | accepted |
| TC-3003 | + | Structured tool output validates against `outputSchema` | typed result returned |
| TC-3004 | + | Elicitation capability negotiated | advertised |
| TC-3005 | + | Resource link in tool result; `title` on tool/resource/prompt; completion `context` | present and correct |
| TC-3006 | − | **JSON-RPC batch** request | rejected (batching removed) |
| TC-3007 | − | HTTP subsequent request omitting `MCP-Protocol-Version` | rejected |
| TC-3008 | − | Call before `initialized` (lifecycle SHOULD→MUST) | rejected |
| TC-3009 | − | Structured output violating `outputSchema` | validation error |
| TC-3010 | − | Unsupported version | error |

### 7.4 `2025-11-25`

| ID | Direction | Case | Expected |
|----|-----------|------|----------|
| TC-4001 | + | Negotiate `2025-11-25` (current default path) | echoed; existing behavior unchanged |
| TC-4002 | + | 2025-06-18 feature set carried forward | advertised |
| TC-4003 | + | Streamable HTTP session + GET SSE + DELETE | success |
| TC-4004 | + | Icons metadata on tool/resource/template/prompt; `Implementation.description` | present |
| TC-4005 | + | Experimental in-core tasks: `execution.taskSupport` tool → `task`-augmented `tools/call` → `CreateTaskResult` → `tasks/get`→`completed`→`tasks/result`; `tasks/list` paginates; `tasks/cancel` | full experimental lifecycle |
| TC-4006 | + | Standards-based elicitation: titled/untitled single- and multi-select enums + default values; URL-mode elicitation | accepted |
| TC-4007 | + | Sampling tool-calling (`tools`/`toolChoice`); JSON Schema 2020-12 validation dialect | honored |
| TC-4008 | − | JSON-RPC batch request | rejected |
| TC-4009 | − | Missing required `MCP-Protocol-Version` header | rejected |
| TC-4010 | − | Terminated-session reuse | `404` |
| TC-4011 | − | Unsupported version | error |
| TC-4012 | − | `tasks/cancel` on an already-terminal task | `-32602` (Invalid params) |
| TC-4013 | − | Tool input-validation failure | returned as a Tool Execution Error (`isError`), **not** a protocol error |
| TC-4014 | − | Invalid `Origin` on Streamable HTTP | `403` |

### 7.5 `2026-07-28`

| ID | Direction | Case | Expected |
|----|-----------|------|----------|
| TC-5001 | + | `server/discover` returns `resultType:"complete"`, `supportedVersions`, capabilities, `_meta.serverInfo`, `ttlMs`, `cacheScope` | valid DiscoverResult |
| TC-5002 | + | `tools/call` with `_meta` (protocolVersion/clientInfo/clientCapabilities) + matching `MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name` | success |
| TC-5003 | + | Tool with `x-mcp-header` → `Mcp-Param-{Name}` mirrored + validated (incl. Base64 sentinel for non-ASCII) | header accepted, matches body |
| TC-5004 | + | Streaming response: SSE with `notifications/progress` then final response | ordered, stream closes |
| TC-5005 | + | Notification POST | `202 Accepted`, no body |
| TC-5006 | + | MRTR: server returns `InputRequiredResult` (`resultType:"input_required"`, `inputRequests` + `requestState`); client retries with a **new id**, echoed `requestState`, and `inputResponses` | final result |
| TC-5007 | + | `subscriptions/listen` → `acknowledged` then opted-in change notifications | stream stays open, filtered |
| TC-5008 | + | Cacheable `tools/list`/`resources/read`/`server/discover` carry `ttlMs`/`cacheScope` | present |
| TC-5009 | + | Tasks extension declared → `CreateTaskResult` (`resultType:"task"`, `Task{taskId,status,ttlMs,pollIntervalMs}`); `tasks/get`→`DetailedTask`; `input_required`→`tasks/update{inputResponses}`; `completed` result; `tasks/cancel` | full extension lifecycle |
| TC-5010 | + | Valid `Origin` | accepted |
| TC-5011 | − | Missing `MCP-Protocol-Version` header | `400`, version error |
| TC-5012 | − | Header/body protocol-version mismatch | `400` `HeaderMismatch` `-32020` |
| TC-5013 | − | `Mcp-Name` ≠ `params.name` (after Base64 decode) | `400` `-32020` |
| TC-5014 | − | Missing `Mcp-Method` header | `400` |
| TC-5015 | − | Unsupported version in `_meta` | `400` `UnsupportedProtocolVersionError` + `supported` |
| TC-5016 | − | `GET` to MCP endpoint | `405` |
| TC-5017 | − | `DELETE` to MCP endpoint | `405` |
| TC-5018 | − | Unknown method | `404` + `-32601` |
| TC-5019 | − | `Mcp-Param-*` present but mismatched body value | `400` `-32020` |
| TC-5020 | − | Invalid `Origin` | `403` |
| TC-5021 | − | POST `Accept` missing `text/event-stream` | `406` |
| TC-5022 | − | Server returns a task to a client that did **not** declare the tasks extension | must not happen — server guard rejects/omits |
| TC-5023 | − | Missing a required client capability | `MissingRequiredClientCapabilityError` |
| TC-5024 | − | `Mcp-Session-Id` / `Last-Event-ID` sent by a legacy client | ignored, not echoed |
| TC-5025 | − | Server includes an `inputRequests` entry for a capability the client did not declare (e.g. `elicitation/create` without elicitation) | must not happen — server omits it |
| TC-5026 | − | `InputRequiredResult` on an unsupported method (anything other than `tools/call`/`resources/read`/`prompts/get`) | must not happen — server guard |
| TC-5027 | − | Client retries MRTR with a tampered/absent `requestState` | server rejects state that fails integrity check |
| TC-5028 | − | `InputRequiredResult` carrying neither `inputRequests` nor `requestState` | invalid — server must include at least one |

### 7.6 Negotiation and interop

| ID | Direction | Case | Expected |
|----|-----------|------|----------|
| TC-6001 | + | Resolver: `MCP-Protocol-Version` header wins over body `_meta` | header value chosen |
| TC-6002 | + | Resolver: body `_meta` used when no header | body value chosen |
| TC-6003 | + | Resolver: `initialize` method → handshake era | handshake path |
| TC-6004 | + | Resolver: no cues → default `2025-03-26` handshake | fallback path |
| TC-6005 | − | Resolver: header/body disagree on `2026-07-28` | `HeaderMismatch` `-32020` |
| TC-6101 | + | One server: a `2025-11-25` handshake client and a `2026-07-28` stateless client concurrently | both succeed independently |
| TC-6102 | + | stdio client probe: `server/discover` first, fall back to `initialize` for a legacy server | correct era detected |
| TC-6103 | + | Client hits `UnsupportedProtocolVersionError`, retries with an advertised version | second attempt succeeds |

### 7.7 Coverage rule

- [ ] **T-200** Every version suite pair (positive + negative) is registered in the shared `All` aggregator.
- [ ] **T-201** No `Console.Write*` in `Test.Shared`; assertions throw.
- [ ] **T-202** Suites pass through `Test.Automated`, `Test.Xunit`, `Test.Nunit` on `net8.0` and `net10.0`.
- [ ] **T-203** Record the final case count and update the figure cited in `CLAUDE.md` (currently 274).

## 8. Risks and open items

The wire-detail dependencies that were open in the first draft are closed. Error codes (`-32020`/`-32021`/`-32022`/`-32601`), the MRTR `InputRequiredResult`/`InputRequests`/`InputResponses`/`requestState` shapes, the `2025-11-25` changelog deltas, and both task models (the `2025-11-25` in-core `Task` with `ttl`/`tasks/result`/`tasks/list`, and the `2026-07-28` extension `Task` with `ttlMs`/`tasks/update`) are pinned to canonical sources in Section 11. The residual verification is small: when implementing, diff the exact optional/required flags and any `_meta`-nested fields against `ext-tasks` `schema/draft/schema.ts` and the `2026-07-28` `schema.ts`, since those TypeScript files are the normative types and this plan captures them at the field-name level.

One correction surfaced during verification and is now reflected throughout: tasks exist in **two incompatible shapes**. `2025-11-25` ships an experimental in-core `tasks` capability; `2026-07-28` replaces it with the `io.modelcontextprotocol/tasks` extension. They differ in field names (`ttl`/`pollInterval` vs `ttlMs`/`pollIntervalMs`), methods (`tasks/result` + `tasks/list` vs `tasks/update`), and notifications (`notifications/tasks/status` vs `notifications/tasks`). Do not share handlers between them (T-055 vs P5).

MCP Apps and EMA remain the only `[PENDING SCHEMA]` items. The plan deliberately limits v0.6.0 to negotiating them via the `extensions` capability and accepting their presence, rather than implementing their full surface, and records the remainder as Future Work. If a consumer needs deep Apps/EMA support sooner, that becomes its own scoped effort against those extensions' own specifications.

## 9. Definition of done (release gate)

The release is done when all five version suite pairs pass both directions through all three runners on both target frameworks; a single running server serves a handshake-era client and a stateless client at the same time (TC-6101); the pre-existing `2025-03-26`/`2025-11-25` behavior is unchanged for today's requests; the README, CHANGELOG, `CLAUDE.md`, and package release notes agree with the shipped surface; and `dotnet build -c Release` is warning-free with a valid `0.6.0` package.

## 10. Future work (explicitly out of v0.6.0)

Deep MCP Apps rendering and Enterprise Managed Authorization enforcement land beyond negotiation. Full CIMD issuance (as opposed to client-side consumption and DCR compatibility) is a separate track. Resumable SSE and legacy HTTP+SSE removal follow the spec's twelve-month deprecation windows and are not triggered by this release.

## 11. Resolved sources

The wire-level facts in this plan were verified against these canonical sources; consult them (not this plan) as the authority when a task reaches implementation.

| Item | Source | Confirmed facts |
|------|--------|-----------------|
| Version list + negotiation | `/specification/versioning` | Current = `2026-07-28`; `_meta` key `io.modelcontextprotocol/protocolVersion`; `server/discover` mandatory |
| Error codes (T-014) | `/specification/2026-07-28/schema` | `HeaderMismatch` `-32020` (no data); `MissingRequiredClientCapabilityError` `-32021` (`data.requiredCapabilities`); `UnsupportedProtocolVersionError` `-32022` (`data.supported`/`data.requested`); method-not-found `-32601` |
| Streamable HTTP + header routing | `/specification/2026-07-28/basic/transports/streamable-http` | POST-only; no sessions/GET/DELETE (→`405`); `Mcp-Method`/`Mcp-Name`/`Mcp-Param-{Name}`; Base64 sentinel `=?base64?…?=`; `Origin`→`403`; `X-Accel-Buffering: no` |
| `server/discover` (T-021) | `/specification/2026-07-28/server/discover` | `DiscoverResult{resultType:"complete", supportedVersions[], capabilities, instructions, ttlMs, cacheScope, _meta.serverInfo}` |
| MRTR (T-022) | `/specification/2026-07-28/basic/patterns/mrtr` | `InputRequiredResult{resultType:"input_required", inputRequests?, requestState?}`; `InputRequests`/`InputResponses` maps; valid only on `tools/call`/`resources/read`/`prompts/get`; `requestState` integrity + replay rules |
| 2025-06-18 delta | `/specification/2025-06-18/changelog` | Structured output, elicitation, resource links, `title`, completion `context`, header-required, batching removed, SHOULD→MUST |
| 2025-11-25 delta (T-015) | `/specification/2025-11-25/changelog` | Icons, experimental in-core tasks, OIDC, incremental scope consent, CIMD, elicitation enums/URL-mode, sampling tool-calling, JSON Schema 2020-12, validation-as-tool-error, `403` Origin |
| 2025-11-25 tasks (T-029a) | `/specification/2025-11-25/basic/utilities/tasks` | `Task{taskId,status,statusMessage?,createdAt,lastUpdatedAt,ttl,pollInterval?}`; `tasks/get`/`tasks/result`/`tasks/list`/`tasks/cancel`; `notifications/tasks/status`; `execution.taskSupport`; `related-task` |
| 2026-07-28 tasks extension (T-029b) | `ext-tasks` `schema/draft/schema.ts` | `Task{taskId,status,statusMessage?,ttlMs:number\|null,pollIntervalMs?}`; `DetailedTask` union; `CreateTaskResult=Result&Task`; `tasks/get`/`tasks/update{inputResponses}`/`tasks/cancel`; `notifications/tasks`; capability `{}` |
| MCP Apps + EMA (T-084) | **unresolved** — `/extensions/apps/overview`, EMA spec | `[PENDING SCHEMA]` — negotiate-only in v0.6.0 |
