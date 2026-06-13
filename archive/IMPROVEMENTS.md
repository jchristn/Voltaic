# Voltaic v0.3.0 MCP Improvement Plan - Archived

Archived after the v0.3.0 implementation and release verification pass.

## Final Scope

- Target release: `v0.3.0`
- Target MCP baseline: `2025-11-25`
- Stable baseline check: the official MCP documentation still marks `2025-11-25` as the latest stable version; `2026-07-28` is a release-candidate/future revision.
- Supported target frameworks: `net8.0` and `net10.0`

## Completed Product Work

- Updated package metadata, version, release notes, tags, README, CHANGELOG, CLAUDE guidance, sample code, and API coverage matrix.
- Added shared MCP protocol support for tools, resources, prompts, completions, capabilities, pagination, schema validation, protocol errors, logging level handling, cancellation notifications, progress notifications, resource subscriptions, and utility models.
- Kept existing public APIs and compatibility paths while adding richer v0.3.0 models and registration overloads.
- Tightened Streamable HTTP behavior for `/mcp`, required `Accept` headers, `MCP-Protocol-Version`, notification `202 Accepted` responses, terminated-session `404` responses, SSE delivery, and session headers.
- Preserved legacy `/rpc` and `/events` compatibility endpoints.
- Kept TCP and WebSocket MCP transports available as Voltaic extension transports and aligned their shared MCP method behavior.
- Expanded `Sample.McpServer` with v0.3.0 tool, structured-output, resource, template, prompt, and completion examples.
- Expanded Touchstone coverage to 253 deterministic cases projected through console, xUnit, and NUnit runners.

## Verification

- `dotnet restore src/Voltaic.sln` - passed.
- `dotnet build src/Voltaic.sln -c Release` - passed with 0 warnings and 0 errors.
- `dotnet run --project src/Test.Automated/Test.Automated.csproj -c Release --framework net8.0` - 253 passed.
- `dotnet run --project src/Test.Automated/Test.Automated.csproj -c Release --framework net10.0` - 253 passed after rerunning one transient fixture-startup failure.
- `dotnet run --project src/Test.Automated/Test.Automated.csproj -c Release --framework net8.0 -- --results artifacts/test-results/voltaic-touchstone.json` - 253 passed and exported JSON results.
- `dotnet test src/Test.Xunit/Test.Xunit.csproj -c Release --framework net8.0 --no-build` - 253 passed.
- `dotnet test src/Test.Xunit/Test.Xunit.csproj -c Release --framework net10.0 --no-build` - 253 passed.
- `dotnet test src/Test.Nunit/Test.Nunit.csproj -c Release --framework net8.0 --no-build` - 253 passed.
- `dotnet test src/Test.Nunit/Test.Nunit.csproj -c Release --framework net10.0 --no-build` - 253 passed.
- `dotnet pack src/Voltaic/Voltaic.csproj -c Release --no-build` - produced `Voltaic.0.3.0.nupkg` and `Voltaic.0.3.0.snupkg`.

## Deferred Future Work

The following work is intentionally outside the v0.3.0 release scope and should be tracked in a future plan:

- Full roots, sampling, and elicitation server-to-client request orchestration.
- Full JSON Schema 2020-12 validation beyond Voltaic's lightweight required/type/object checks.
- Long-running stress and soak suites for many sessions, cancellation races, and queue pressure.
- Optional OAuth/resource metadata helpers beyond the existing `AuthenticationHandler` integration point.
