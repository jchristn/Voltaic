# Fix: MCP clients that request 2026-07-28 get a session they cannot use

| | |
|---|---|
| Status | Implemented in v1.1.0 (library, tests, docs, live check). Isis follow-ups I1-I4 open |
| Reported by | Isis agent-memory platform (`C:\Code\AgentMemory`), 2026-09-24 |
| Affects | Voltaic 0.6.0 through 0.7.1 (confirmed on 0.6.1 and 0.7.1) |
| Target release | v1.1.0, approved by the maintainer on 2026-09-24 (v1.0.0 was published to NuGet in error) |
| Owner | _unassigned_ |
| Last updated | 2026-09-24 |

## How to use this document

This is a working plan, not a record. Each task has a checkbox, an ID, and a place for notes, so whoever picks it up
can annotate as they go without rewriting the plan. Use these marks:

- `[ ]`: not started
- `[~]`: in progress (add your initials and the date)
- `[x]`: done (add the date, and the commit hash where one applies)
- `[-]`: dropped (say why in the note)

Record decisions and surprises in the [Progress log](#11-progress-log) at the bottom rather than editing earlier
sections. When a task's approach changes, strike the old text with `~~...~~` and write the new approach underneath,
so the history stays readable.

---

## 1. The problem

Claude Code 2.1.281, the current release, connects to any Voltaic MCP server over Streamable HTTP and then sees
**zero tools**. The connection itself succeeds. The failure only shows up in the client's debug log:

```
MCP server "isis": Connection established with capabilities: {"hasTools":true, ...}
MCP server "isis": tools/list failed (Invalid result for tools/list: missing required resultType -
servers implementing protocol revision 2026-07-28 MUST include it ...)
```

To the user the failure is invisible. The agent quietly has no tools, and it tends to hallucinate tool-call text
instead. Isis found this while building an agent-in-the-loop benchmark, and the live Isis deployment has the same
problem for anyone on current Claude Code. Any Voltaic-hosted MCP server is exposed to the same thing as clients move
to the 2026-07-28 revision.

### 1.1 Reproduction

1. Start any `McpHttpServer` (the Isis MCP server, `Sample.McpServer`, or `Test.McpHttpServer`).
2. Point Claude Code at it with `--mcp-config` and capture a debug log:
   `claude -p --debug-file debug.log --strict-mcp-config --mcp-config mcp.json`.
3. Search `debug.log` for `tools/list failed`.

The same thing can be seen directly on the wire. An `initialize` request that asks for the newest revision gets it
echoed back:

```
POST /mcp   {"method":"initialize","params":{"protocolVersion":"2026-07-28", ...}}
->          {"result":{"protocolVersion":"2026-07-28","capabilities":{...},"serverInfo":{...}}}
```

After that, `tools/list` returns a result with no `resultType`, and the client rejects it.

### 1.2 Root cause

There are two defects. A third gap decides which of the two a given server hits.

**Defect A: the handshake agrees to a stateless-era revision.** `McpEndpoint.Initialize`
(`src/Voltaic/Mcp/McpEndpoint.cs`, L62-90) calls `McpProtocol.NegotiateVersion`, which echoes any supported version
(`McpProtocol.cs`, L189-202). Voltaic's own registry models `2026-07-28` as `McpProtocolEra.Stateless` with
`supportsSessions: false` (`McpProtocol.cs`, L111-118). Stateless-era clients discover capabilities through
`server/discover`, not `initialize`. So when `initialize` negotiates `2026-07-28`, the server promises a revision on
a session-based handshake that the revision does not define. The client believes it is talking 2026-07-28 and holds
the server to that revision's rules. The server's session path never applies them.

**Defect B: 2026-07-28 results never carry `resultType`.** Every final result is missing the `resultType` that the
2026-07-28 revision requires. That includes `tools/list`, `resources/list`, `prompts/list`, and a completed
`tools/call`. The field exists only on `McpDiscoverResult` (`"complete"`), `McpInputRequiredResult`
(`"input_required"`), and the tasks-extension models (`"task"`, `"complete"`). `McpResult` and
`McpPaginatedResult` (`McpCommonModels.cs`) have no such property, and nothing adds it on dispatch. That holds on
the stateless path too (`McpHttpServer.HandleStatelessPostAsync`, L1489-1577). The existing tests lock in the
omission: `McpStatelessClientSuites.cs` L79 and `McpStatelessTransportSuites.cs` L215 assert `ResultType == null` for
final tool results.

**Gap C: servers with an `AuthenticationHandler` take a different path.** When an `AuthenticationHandler` is set and a
POST has a body, `McpHttpServer` sends the request to `HandlePreReadRpcRequestAsync` (L1847-1930). That path skips the
version resolver, stateless routing, `_SessionVersions` capture, and the batching gate that `HandleMcpRequestAsync`
applies (L1201-1353). Isis authenticates every request, so it always takes this path. The practical consequence for
the fix: anything placed only in `HandleMcpRequestAsync` would not reach Isis, or any other server that uses
authentication, which is most real deployments.

Here is the failing sequence against Isis, end to end:

1. `initialize` asks for `2026-07-28`, and Defect A echoes it.
2. The client then sends `tools/list` with `MCP-Protocol-Version: 2026-07-28` and the session id.
3. Because of Gap C, the pre-read path only checks that the header names a supported version. It does, so it
   dispatches normally.
4. Because of Defect B, the result has no `resultType`, and the client rejects it.

### 1.3 Why a consumer cannot work around it

`McpHttpServer.ProtocolVersion` (L85-89) only sets the version used when a client asks for none. It does not cap
negotiation. `McpEndpoint` is internal, and the `AuthenticationHandler` only sees the request, not the body. Isis
tried the 0.7.1 upgrade and the behavior was identical. The fix has to be in Voltaic.

---

## 2. Goals and non-goals

The main goal is that a current Claude Code client connected to a Voltaic server lists and calls tools, with or
without an `AuthenticationHandler`, on every MCP transport. Behind that sits a correctness goal: whatever revision
Voltaic claims to speak, its responses satisfy that revision's rules.

The fix should also stay small and additive for library consumers. Nobody who registers tools, resources, or prompts
should need to change code. The one visible behavior change is that `initialize` no longer returns `2026-07-28`, and
that response was already unusable.

Goals:

- `initialize` never negotiates a stateless-era revision on any transport.
- Every final result served under a stateless-era revision carries `resultType: "complete"`. Existing
  `input_required` and `task` results keep their values.
- The authenticated (pre-read) HTTP path applies the same version resolution and era handling as the
  unauthenticated path.
- The version cap is a configurable public member, not a hard-coded constant, per CODE_STYLE.md.

Non-goals:

- Stateless-era support on stdio, TCP, or WebSocket. Those transports are handshake-only today and stay that way.
- A general redesign of `McpVersionResolver` precedence.
- Adding `resultType` to results of custom methods registered with `RegisterMethod` that do not return an `McpResult`
  type. Voltaic cannot safely modify arbitrary handler objects without a JSON DOM, which the codebase deliberately
  avoids (`archive/DOM_REMOVAL_MIGRATION.md`). Section 3.2 documents this limitation.

---

## 3. Solution

### 3.1 Fix A: cap handshake negotiation at the handshake era

`McpProtocol` gets an era-aware negotiation method, and every `initialize` handler uses it:

```csharp
/// <summary>
/// Negotiates the protocol version for a session-based handshake (the initialize request).
/// A stateless-era revision cannot be negotiated through initialize, so a request for one is answered with
/// <paramref name="maximumHandshakeVersion"/>, the newest handshake-era revision the server will speak.
/// </summary>
/// <param name="requestedVersion">The client-requested version. May be null or blank.</param>
/// <param name="maximumHandshakeVersion">The newest handshake-era version to agree to.</param>
/// <returns>The version to use for the session.</returns>
/// <exception cref="ArgumentException">Thrown when either version is unsupported, or when
/// <paramref name="maximumHandshakeVersion"/> is not a handshake-era revision.</exception>
public static string NegotiateHandshakeVersion(string? requestedVersion, string maximumHandshakeVersion)
```

The rules:

| Client requests | Server answers |
|---|---|
| Nothing (null or blank) | `LatestProtocolVersion` (unchanged behavior) |
| A handshake-era version at or below the cap | That version (unchanged behavior) |
| A handshake-era version above the cap | The cap |
| A stateless-era version (`2026-07-28`) | The cap. **This is the fix** |
| An unknown version | `McpProtocolException.UnsupportedVersion` (unchanged behavior) |

The client still gets a valid answer when it asks for a newer revision: it receives the newest version the server
supports for this style of connection, which is how version negotiation is meant to work. A client that supports the
answered version continues with it, and one that does not can disconnect cleanly instead of failing silently.
Claude Code is expected to continue on 2025-11-25, since most MCP servers it connects to today speak that revision
or an older one.
Treat that as an assumption until the manual interop check (T15) confirms it.

The cap is configurable per server. CODE_STYLE.md asks for a public member with a validated backing field rather than
a constant:

```csharp
/// <summary>
/// Gets or sets the newest protocol revision the server will agree to during an initialize handshake.
/// Default is the newest handshake-era revision in <see cref="McpProtocol.SupportedVersions"/> (currently
/// 2025-11-25). Lower it to pin older clients to an earlier revision. A stateless-era revision is rejected.
/// </summary>
/// <exception cref="ArgumentException">Thrown when the value is not a supported handshake-era revision.</exception>
public string MaximumHandshakeProtocolVersion { get; set; }
```

Add the property to `McpHttpServer`, `McpServer` (stdio), `McpTcpServer`, and `McpWebsocketsServer`. Each passes it
to its `McpEndpoint`. Add a companion static `McpProtocol.NewestHandshakeProtocolVersion`, derived from the registry
rather than hard-coded, so the default moves automatically when a new handshake-era revision is added.

`McpProtocol.NegotiateVersion` stays as it is, because it is public API. Its XML documentation should say it is
era-agnostic and point handshake callers to `NegotiateHandshakeVersion`.

### 3.2 Fix B: stamp `resultType` on results served under a stateless-era revision

`McpResult` gets the field, serialized only when set:

```csharp
/// <summary>
/// Gets or sets the result type required by stateless-era revisions (2026-07-28): "complete" for a final result,
/// "input_required" for a Multi Round-Trip request, or "task" for a created task. Null for handshake-era revisions,
/// where it is not part of the protocol and is omitted from the wire.
/// </summary>
[JsonPropertyName("resultType")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? ResultType { get; set; } = null;
```

Where a model already declares `ResultType` (`McpDiscoverResult`, `McpInputRequiredResult`, the tasks-extension
models), remove the duplicate declaration if it derives from `McpResult`. If it does not derive from `McpResult`, keep
the declaration as it is. Check each one and record which case applies in task L4.

After dispatch, one internal helper sets the value on the response:

```csharp
// McpResultTypeStamper (internal static class, its own file)
internal static void ApplyForVersion(JsonRpcResponse response, string negotiatedVersion)
```

When `McpProtocol.GetEra(negotiatedVersion) == McpProtocolEra.Stateless` and `response.Result` is an `McpResult`
whose `ResultType` is null, it sets `ResultType = "complete"`. It never overwrites an existing value, so
`input_required` and `task` pass through. It never touches error responses.

The results that are anonymous `new { }` objects today are the empty results from `Ping`, subscribe, unsubscribe,
`SetLogLevel`, `Cancelled`, and `Initialized` (`McpEndpoint.cs`, L121 and L475-518). They become a typed
`McpEmptyResult : McpResult` in its own file, so they can be stamped too. It serializes to `{}` under handshake-era
revisions, exactly as today.

Call `ApplyForVersion` from `HandleStatelessPostAsync`, from the session path whenever the resolved version is
stateless-era, and from the unified authenticated path (Fix C).

A handler registered with `RegisterMethod` that returns something other than an `McpResult` is left alone. Document
this in the XML docs for `RegisterMethod` and in the README. The built-in MCP surface is fully covered.

### 3.3 Fix C: one MCP POST pipeline, with or without authentication

Refactor `HandlePreReadRpcRequestAsync` and the POST branch of `HandleMcpRequestAsync` onto a shared private method,
for example `ProcessMcpPostAsync(HttpListenerContext context, string requestBody, CancellationToken token)`. It runs,
in order:

1. Accept-header check
2. `ExtractStatelessSignals`
3. `McpVersionResolver.Resolve`
4. The stateless route
5. `ValidateProtocolVersionHeader`
6. Session create or lookup, and the terminated-session check
7. The batching gate
8. `ProcessRpcRequestAsync`
9. `_SessionVersions` capture on `initialize`
10. `McpResultTypeStamper.ApplyForVersion`
11. Response writing

Authentication stays where it is: before the shared method, with the `RpcCallContext` scope from v0.7.0 wrapped
around the shared call so handlers still see the caller. The `ping` pre-auth bypass (L1810) is unchanged.

Session-path requests need a version to stamp with, so use the value captured in `_SessionVersions[sessionId]`. If
the request's `MCP-Protocol-Version` header names a different supported version than the session negotiated, keep
today's behavior: the header wins, and the resolver may route the request to the stateless handler. Add a test that
pins this down (T9).

### 3.4 Other transports

`McpServer` (stdio, L511), `McpTcpServer` (L464), and `McpWebsocketsServer` (L768) all call `_Endpoint.Initialize`.
Fix A covers them through `McpEndpoint`, and they need no stamping because they cannot negotiate a stateless-era
revision once Fix A is in. Each still gets the `MaximumHandshakeProtocolVersion` property and one negotiation test.

### 3.5 Client side

`McpHttpClient` already treats a missing `resultType` as final (`TryParseInputRequired`, L867-881), so it keeps
working against both old and new servers. No behavior change is needed.

Once the stamping lands, the stateless client tests change from expecting `null` to expecting `"complete"` (T5). The
client should keep tolerating a missing field, because older servers exist.

---

## 4. Library tasks

| ID | Task | Files | Status | Notes |
|---|---|---|---|---|
| L1 | Add `McpProtocol.NewestHandshakeProtocolVersion` (derived from `_Registry`, newest `Handshake`-era entry) with XML docs | `src/Voltaic/Mcp/McpProtocol.cs` | [ ] | |
| L2 | Add `McpProtocol.NegotiateHandshakeVersion(requested, maximum)` per the table in 3.1, with `<exception>` docs. Update the `NegotiateVersion` XML docs to point handshake callers at it | `McpProtocol.cs` | [ ] | |
| L3 | Add `MaximumHandshakeProtocolVersion` (validated backing field `_MaximumHandshakeProtocolVersion`; setter throws `ArgumentException` for unknown or stateless-era values) to `McpHttpServer`, `McpServer`, `McpTcpServer`, `McpWebsocketsServer`; flow it into `McpEndpoint` | the four server files, `McpEndpoint.cs` | [ ] | |
| L4 | `McpEndpoint.Initialize` uses `NegotiateHandshakeVersion(initialize.ProtocolVersion, MaximumHandshakeProtocolVersion)`. Add `ResultType` to `McpResult`; reconcile the existing `ResultType` declarations on the discover, MRTR, and task models | `McpEndpoint.cs`, `McpCommonModels.cs` (or wherever `McpResult` lives), `McpDiscoverModels.cs`, `McpMrtrModels.cs`, `McpTasksExtensionModels.cs` | [ ] | Record which models derive from `McpResult` |
| L5 | Add `McpEmptyResult : McpResult` (own file) and replace the anonymous `new { }` results in `McpEndpoint` | `McpEmptyResult.cs` (new), `McpEndpoint.cs` | [ ] | Must still serialize to `{}` under handshake revisions |
| L6 | Add internal `McpResultTypeStamper.ApplyForVersion` (own file) | `McpResultTypeStamper.cs` (new) | [ ] | Never overwrite; never touch errors |
| L7 | Extract the shared MCP POST pipeline (3.3); route both the authenticated and unauthenticated paths through it; keep the auth-first ordering and `RpcCallContext` scope; call the stamper | `McpHttpServer.cs` | [ ] | Largest change; review carefully |
| L8 | Call the stamper in `HandleStatelessPostAsync` | `McpHttpServer.cs` | [ ] | |
| L9 | XML docs on `RegisterMethod`/`RegisterTool` overloads: custom non-`McpResult` returns are not stamped | the server files | [ ] | |
| L10 | Build clean with **zero warnings** on net8.0 and net10.0 (`dotnet build src/Voltaic.sln -c Release`) | | [ ] | |

Each change follows `C:\Code\agents\requirements\CODE_STYLE.md` and Voltaic's `CLAUDE.md`:

- usings inside the namespace
- no `var`, no tuples
- `_PascalCase` private fields
- XML docs on public members only
- one class per file for new types
- `ConfigureAwait(false)`
- specific exception types with contextual messages, documented with `<exception>`
- no `Console.WriteLine`
- no em-dashes in code or comments

---

## 5. Tests

The suites are Touchstone descriptors in `src/Test.Shared`, registered in `VoltaicSuites.cs`, and run by
`Test.Automated`, `Test.Xunit`, and `Test.Nunit` on net8.0 and net10.0, per
`C:\Code\agents\requirements\BACKEND_TEST_ARCHITECTURE.md`. Two rules from that document matter here. Shared test code
must not write to the console. In-process HTTP servers must bind and be called on `127.0.0.1`, never `localhost`.

The most important new test is T3. It reproduces the exact Claude Code sequence against a server **with an
`AuthenticationHandler`**, because that is the path real deployments take and the one the existing suites never
exercise with a stateless-era request.

| ID | Test (suite) | Asserts | Status | Notes |
|---|---|---|---|---|
| T1 | `ProtocolNegotiationHandshakeCap` (`McpProtocol.Models`) | Every row of the 3.1 table, including an unknown version throwing | [ ] | Pure unit test |
| T2 | `NewestHandshakeVersionDerivedFromRegistry` (`McpProtocol.Models`) | Equals `2025-11-25` today and is a handshake-era entry | [ ] | |
| T3 | `InitializeRequesting20260728NegotiatesHandshake` (new suite `McpHttp.Server.Negotiation`), run twice: without and **with** an `AuthenticationHandler` | `initialize` asking for `2026-07-28` returns `2025-11-25`; a following `tools/list` (with session id and `MCP-Protocol-Version: 2025-11-25`) returns the tools | [ ] | Regression test for the Isis failure |
| T4 | `MaximumHandshakeProtocolVersionHonoredAndValidated` (same suite) | With the cap set to `2025-06-18`, a `2025-11-25` request negotiates `2025-06-18`; setting `2026-07-28` or `1900-01-01` throws `ArgumentException` | [ ] | |
| T5 | Update `McpStatelessClientSuites.cs` L79 and `McpStatelessTransportSuites.cs` L215 | Final results now carry `ResultType == "complete"` | [ ] | These currently assert `null` |
| T6 | `StatelessListResultsCarryResultType` (`McpVersion.Stateless`) | `tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`, `completion/complete` all return `resultType: "complete"` | [ ] | |
| T7 | `StatelessInputRequiredNotOverwritten` / `StatelessTaskResultNotOverwritten` (`McpVersion.Stateless`) | MRTR and task results keep `input_required` / `task` | [ ] | |
| T8 | `HandshakeResultsOmitResultType` (`McpVersion.Matrix`, each handshake version) | No `resultType` key on the wire for 2024-11-05 through 2025-11-25; empty results still serialize to `{}` | [ ] | Guards Fix B's scope |
| T9 | `SessionHeaderSelectingStatelessRoutesAndStamps` | A session request carrying `MCP-Protocol-Version: 2026-07-28` is routed per the resolver and its result is stamped | [ ] | Pins the 3.3 edge case |
| T10 | `AuthenticatedPathMatchesUnauthenticated` (new suite `McpHttp.Server.AuthParity`) | For the same request set (handshake, stateless, batch on 2025-03-26, terminated session), the responses are identical except for auth failures | [ ] | Guards Fix C |
| T11 | `CallContextStillFlowsAfterPipelineRefactor` (`McpHttp.Server.CallContext`) | The existing v0.7.0 caller-context cases still pass through the shared pipeline | [ ] | Existing cases should cover this; add one if not |
| T12 | `InitializeRequesting20260728NegotiatesHandshake` for stdio, TCP, and WebSocket (`McpTransportParitySuites.cs`) | Same negotiation result as HTTP | [ ] | |
| T13 | Custom `RegisterMethod` returning a plain object under stateless is returned unmodified | Documents the 3.2 limitation | [ ] | |
| T14 | Full suite green on all runners and both frameworks; update the case count in README and `CLAUDE.md` (currently "337" in README and "352" in `CLAUDE.md`, already inconsistent) | | [ ] | |
| T15 | **Manual interop check** with Claude Code 2.1.281 or later against `Sample.McpServer` and against a server with an `AuthenticationHandler`: `claude -p --debug-file d.log --strict-mcp-config --mcp-config cfg.json`; `d.log` shows no `tools/list failed`, and the model calls a tool | Record the Claude Code version and the result in the progress log | [ ] | |

Run commands, from `CLAUDE.md`:

```bash
dotnet run --project src/Test.Automated/Test.Automated.csproj -c Release --framework net8.0
dotnet run --project src/Test.Automated/Test.Automated.csproj -c Release --framework net10.0
dotnet test src/Test.Xunit/Test.Xunit.csproj -c Release --framework net8.0
dotnet test src/Test.Xunit/Test.Xunit.csproj -c Release --framework net10.0
dotnet test src/Test.Nunit/Test.Nunit.csproj -c Release --framework net8.0
dotnet test src/Test.Nunit/Test.Nunit.csproj -c Release --framework net10.0
```

---

## 6. Documentation

Voltaic's docs currently lag the code: the README says v0.7.0, and `CLAUDE.md` says v0.6.0. Since this fix changes
what the negotiation section promises, update those passages rather than appending a note under stale text. Existing
Voltaic prose uses em-dashes in several places. New and edited text must not (CODE_STYLE.md, WRITING_DOCUMENTS.md),
and any sentence touched by this work should be rewritten without them.

| ID | Document | Change | Status | Notes |
|---|---|---|---|---|
| D1 | `README.md` L13-15 (overview) | State the rule plainly: `initialize` negotiates at most the newest handshake-era revision (configurable with `MaximumHandshakeProtocolVersion`), and 2026-07-28 is reached only through the stateless request path. Fix the stale "v0.7.0" | [ ] | |
| D2 | `README.md` "MCP Endpoint Requirements" (around L50, `initialize` negotiation) | Replace the echo behavior with the 3.1 table; add a short example of setting the cap | [ ] | |
| D3 | `README.md` stateless section and "MCP Client (Streamable HTTP)" (around L846) | Document `resultType` on stateless results; note that clients should treat a missing `resultType` as final for compatibility with older servers | [ ] | |
| D4 | `README.md` custom methods | Document that custom non-`McpResult` returns are not stamped (3.2) | [ ] | |
| D5 | `README.md` "Building" (around L1167) | Correct the test case count (T14) | [ ] | |
| D6 | `CHANGELOG.md` | New entry at the top under the approved version heading (see R1). Draft below | [ ] | |
| D7 | `src/Voltaic/Voltaic.csproj` `<PackageReleaseNotes>` | Prepend a short summary for the approved version, following the existing "vX.Y.Z: ... Prior release, vA.B.C: ..." pattern, without em-dashes | [ ] | Do not change `<Version>` without approval |
| D8 | `CLAUDE.md` project overview | Replace "Voltaic v0.6.0 recognizes..." with the current version and the negotiation rule; update the case count | [ ] | |
| D9 | `src/Test.Shared/API_COVERAGE.md` | Add `NegotiateHandshakeVersion`, `NewestHandshakeProtocolVersion`, `MaximumHandshakeProtocolVersion` (×4 servers), `McpResult.ResultType`, `McpEmptyResult` | [ ] | |
| D10 | `archive/UPDATED_MCP_VERSION.md` | Add a dated status note that handshake negotiation is capped and stateless results are stamped, with a link to this plan | [ ] | |
| D11 | `Sample.McpServer` | Add a commented line showing `MaximumHandshakeProtocolVersion` | [ ] | Optional |

Draft CHANGELOG entry. Replace `vX.Y.Z` with the approved version:

```markdown
## vX.Y.Z
- Fixed MCP clients that request the stateless `2026-07-28` revision in `initialize` (for example Claude Code 2.1.x)
  receiving a session whose results they reject. `initialize` now negotiates at most the newest handshake-era
  revision (`2025-11-25`), which clients accept and fall back to. The stateless revision is reached only through the
  stateless request path
- Added `McpProtocol.NegotiateHandshakeVersion`, `McpProtocol.NewestHandshakeProtocolVersion`, and a configurable
  `MaximumHandshakeProtocolVersion` on `McpHttpServer`, `McpServer`, `McpTcpServer`, and `McpWebsocketsServer`
- Results served under a stateless-era revision now carry the required `resultType` (`"complete"` for final
  results; `input_required` and `task` results are unchanged). `McpResult` gained an optional `ResultType`, omitted
  on the wire for handshake-era revisions. Empty results are now the typed `McpEmptyResult`
- `McpHttpServer` now runs the same version resolution, stateless routing, session tracking, and batching rules for
  authenticated requests (`AuthenticationHandler` set) as for unauthenticated ones
- Behavior change: `initialize` no longer echoes `2026-07-28`. Clients that asked for it previously got a session
  they could not use
```

---

## 7. Compliance with `C:\Code\agents\requirements`

| Requirement | Where it applies | How this plan meets it |
|---|---|---|
| `CODE_STYLE.md` | All code in L1-L10 | Listed under section 4. The configurable cap replaces a would-be constant; new types get their own files; `<exception>` docs on the new public methods and setters |
| `VERSIONING.md` §4 (agents never change versions) | R1 | No task changes `<Version>` or tags a release. The version is a maintainer decision (section 8) |
| `VERSIONING.md` §3 (0.x.y labeled alpha) | R1 | Raised as a question for the maintainer, because current Voltaic releases (`0.7.1`) are not alpha-labeled |
| `BACKEND_TEST_ARCHITECTURE.md` | T1-T14 | Touchstone descriptors in `Test.Shared`; the three runners; no console output in shared code; `127.0.0.1` loopback; exit codes from the runners |
| `REPOSITORY_REQUIREMENTS.md` | D1-D8 | README and CHANGELOG exist and are updated. `REST_API.md`/`MCP_API.md` apply to products that expose a server surface; Voltaic is a library, and its README is its API reference |
| `WRITING_DOCUMENTS.md` / `CODE_STYLE.md` (no em-dashes) | All new text | No em-dashes in code, comments, docs, release notes, or commit messages written for this work |

---

## 8. Release

| ID | Task | Status | Notes |
|---|---|---|---|
| R1 | **Maintainer decision: release version.** Suggested: a PATCH release, `0.7.2` (the fix is backward compatible for every client that could previously use a session). Per `VERSIONING.md` §3, confirm whether it should carry the `-alpha` label. Do not edit `<Version>` until this is approved in writing | [ ] | Approved version: ______ by ______ on ______ |
| R2 | Apply the approved version to `<Version>`, the CHANGELOG heading (D6), and the release notes (D7) | [ ] | |
| R3 | `dotnet pack src/Voltaic/Voltaic.csproj -c Release`; confirm the package and symbol package build | [ ] | |
| R4 | Publish to NuGet | [ ] | Maintainer |

---

## 9. Downstream: Isis

Isis is the reason this fix was found, and it is the easiest way to prove it end to end. Once the release is
published:

| ID | Task (in `C:\Code\AgentMemory`) | Status | Notes |
|---|---|---|---|
| I1 | Bump `Isis.McpServer` from Voltaic 0.7.1 to the released version | [ ] | |
| I2 | Add an Isis `McpSuite` case: `initialize` requesting `2026-07-28` through the authenticated Isis MCP server returns `2025-11-25`, and `tools/list` returns the Isis tools | [ ] | |
| I3 | Re-run the Isis agent benchmark (`benchmarks/agent/tasks-isis.json`), which is blocked on this bug, and record the with-memory vs. without-memory results in `benchmarks/RESULTS.md` | [ ] | |
| I4 | Redeploy the live Isis MCP server and confirm with Claude Code that the tools appear | [ ] | |

---

## 10. Acceptance criteria

The fix is done when all of these hold:

1. Claude Code 2.1.281 or later, connected to a Voltaic `McpHttpServer` with and without an `AuthenticationHandler`,
   lists the server's tools and calls one successfully (T15). Its debug log has no `tools/list failed` line.
2. `initialize` never returns a stateless-era revision on any transport (T1, T3, T12).
3. Every built-in result served under a stateless-era revision has the correct `resultType`, and handshake-era wire
   output is byte-for-byte unchanged apart from the negotiated version (T6-T8).
4. The authenticated and unauthenticated HTTP paths produce identical protocol behavior (T10).
5. All Touchstone cases pass on `Test.Automated`, `Test.Xunit`, and `Test.Nunit`, on net8.0 and net10.0, and the
   solution builds with zero warnings (L10, T14).
6. Documentation and release notes are updated (D1-D10). The version is changed only after written approval (R1).

---

## 11. Progress log

Add a row whenever something is decided, blocked, or finished. Newest at the bottom.

| Date | Who | Entry |
|---|---|---|
| 2026-09-24 | Isis investigation | Plan written. Root cause confirmed against Voltaic 0.6.1 and 0.7.1 with Claude Code 2.1.281 (debug log shows `tools/list failed ... missing required resultType`). The live Isis deployment negotiates `2026-07-28` on `initialize` |
| 2026-09-24 | Claude (Voltaic session) | Live baseline with Claude Code 2.1.281 disproved part of section 1: Claude Code never sends `initialize`. It opens with a stateless `server/discover`, picks `2026-07-28` from `supportedVersions`, then sends stateless `tools/list`. So Defect A is real but is not the path Claude Code takes; Defect B (stamping) is the fix that matters. |
| 2026-09-24 | Claude (Voltaic session) | Found Defect D, not in the plan: under `2026-07-28` cacheable results must carry numeric `ttlMs` and `cacheScope` (`public`/`private`); Claude Code rejected `tools/list` for that after `resultType` was fixed. Fixed by stamping defaults `ttlMs: 0`, `cacheScope: "private"` when unset (configured `ListCacheTtlMs`/`ListCacheScope` win). The stamper became `McpStatelessResultStamper` and reverts after serialization so shared result instances do not leak fields into handshake responses. |
| 2026-09-24 | Claude (Voltaic session) | Found Defect E: `server/discover` advertised `listChanged`, so Claude Code polled `subscriptions/listen`, which Voltaic does not implement ("Method not found"). `server/discover` now omits `listChanged`/`subscribe`. Gap C was worse than described: on authenticated servers every stateless request created a never-reused session. |
| 2026-09-24 | Claude (Voltaic session) | Fix C implemented as a smaller refactor than 3.3: the auth step passes its pre-read body into the existing `HandleMcpRequestAsync`/`HandleRpcRequestAsync`, and the duplicate `HandlePreReadRpcRequestAsync`/`HandlePreAuthRpcRequestAsync` were deleted. `initialize` always takes the handshake path even with a `2026-07-28` header. |
| 2026-09-24 | Claude (Voltaic session) | Tests: new suites `McpHttp.Server.Negotiation`, `McpVersion.StatelessResults`, `McpHttp.Server.AuthParity`, plus stdio/TCP/WebSocket parity cases. 405 cases pass on console, xUnit, NUnit under net8.0 and net10.0. Mutation checks: disabling stamping fails 9 cases; bypassing stateless routing on the auth path fails 6. |
| 2026-09-24 | Claude (Voltaic session) | T15 live check, Claude Code 2.1.281: stateless 2026-07-28 with auth, without auth, wrong token (clean 401), legacy server without `server/discover` (falls back to `initialize` on 2025-11-25), and `Sample.McpServer`: tools listed and called in every positive case, no `tools/list failed` lines. Released as v1.1.0; the alpha label question is moot at 1.x. |
| | | |
