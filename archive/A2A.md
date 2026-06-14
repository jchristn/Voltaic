# Voltaic, MCP, and A2A

> Status: historical design opinion captured before the v0.4.0 A2A implementation. The accepted implementation plan and completion state live in `A2A_PLAN.md`; current user-facing usage lives in `README.md`; current test coverage lives in `src/Test.Shared/API_COVERAGE.md`.

## Short opinion

Yes, it would be valuable to refactor Voltaic so MCP-specific API lives under `Voltaic.Mcp` and to add a peer `Voltaic.A2A` area. The value is not that MCP and A2A should be made to look identical. They should not. The value is that Voltaic already has useful JSON-RPC, session, transport, SSE, WebSocket, framing, and handler-registration machinery, and A2A can reuse that lower-level machinery without turning Voltaic into a clone of the official A2A .NET SDK.

The important boundary is this: `Voltaic.Mcp` and `Voltaic.A2A` should share transport and dispatch infrastructure, not domain semantics.

MCP is tool/resource/prompt oriented. A2A is agent/message/task/artifact oriented. A clean Voltaic design should make both easy to host and consume, but should not hide the fact that they solve different problems.

## Original Voltaic shape at time of analysis

At the time this opinion was written, Voltaic was a single package and a flat `Voltaic` namespace containing:

- JSON-RPC primitives: `JsonRpcRequest`, `JsonRpcResponse`, `JsonRpcError`, `JsonRpcClient`, `JsonRpcServer`, request/response event args, pending request types, and message framing.
- MCP protocol models: `Mcp*Models`, `ToolDefinition`, `McpCapabilities`, `McpProtocol`, `McpProtocolException`, list result types, and utility notification models.
- MCP endpoint logic: internal `McpEndpoint`, which owns tool/resource/prompt/completion registries, initialization, pagination, schema checks, and protocol method handling.
- MCP transports: stdio, HTTP Streamable HTTP/SSE, TCP, and WebSocket clients and servers.

That is practical, but the namespace is already doing too much. Adding A2A into the same flat namespace would make the public API harder to scan and would blur the distinction between JSON-RPC infrastructure, MCP protocol surface, and A2A protocol surface.

The current implementation also repeats a lot of server registration and request-processing shape across transports. That duplication is tolerable for MCP alone, but it becomes a maintenance problem if A2A is added as another protocol family.

## Relevant A2A facts

From the current A2A project and specification:

- A2A is an open protocol for interoperability between opaque agentic applications: https://github.com/a2aproject/A2A
- A2A v1.0 defines agent discovery through Agent Cards, plus operations for message sending, streaming message sending, task lookup/listing/cancellation/subscription, push notification config, and extended agent cards: https://a2a-protocol.org/latest/specification/
- A2A supports JSON-RPC 2.0 over HTTP(S), HTTP+JSON/REST, gRPC, streaming over SSE, and asynchronous push notifications.
- Agent Cards are discoverable at `/.well-known/agent-card.json` and describe identity, skills, capabilities, supported interfaces, and security requirements.
- The official .NET SDK already provides a full implementation with `A2AClient`, `A2ACardResolver`, `A2AServer`, `IAgentHandler`, task stores, event queues, response helpers, and ASP.NET Core integration: https://github.com/a2aproject/a2a-dotnet
- A2A and MCP are explicitly complementary. MCP standardizes how agents use tools/resources; A2A standardizes how agents collaborate with other agents: https://a2a-protocol.org/latest/topics/a2a-and-mcp/

That last point should drive the design. A2A should not be modeled as "MCP with different method names."

## Recommendation

The implemented v0.4.0 variant uses `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A` inside one NuGet package. Some option text below mentions `Voltaic.JsonRpc`; that was an evaluated design alternative, not the final namespace choice.

Refactor toward this conceptual layout:

```text
Voltaic
  Shared package identity and possibly compatibility aliases

Voltaic.JsonRpc
  JSON-RPC request/response/error models
  Method dispatch
  Request/response events
  Pending request management
  Message framing

Voltaic.Transports or Voltaic.Core
  HTTP/SSE helpers
  WebSocket helpers
  TCP helpers
  stdio helpers
  session/connection abstractions

Voltaic.Mcp
  MCP models
  MCP protocol constants and errors
  MCP endpoint/registry
  MCP clients and servers

Voltaic.A2A
  A2A models
  Agent card resolver
  A2A client
  A2A endpoint/server
  task store abstractions
  streaming event helpers

Voltaic.Agent or Voltaic.Capabilities
  Optional cross-protocol authoring layer
  Only for capabilities that can be intentionally exposed through both MCP and A2A
```

I would not put a large cross-protocol abstraction at the center first. Build the neutral JSON-RPC/transport core, keep MCP and A2A as peers, then add optional adapters where the mapping is honest.

## Why this is valuable

The namespace split makes the library easier to understand. Users looking for MCP see MCP. Users looking for A2A see A2A. Users looking for lower-level JSON-RPC are not forced through either protocol.

The shared infrastructure reduces duplicate bugs. MCP Streamable HTTP and A2A JSON-RPC/SSE are not the same transport contract, but both need careful request dispatch, response serialization, cancellation, event streaming, logging hooks, and connection lifecycle behavior. Those pieces should be reusable.

The positioning is coherent. Voltaic can become a small .NET protocol toolkit for agentic systems: JSON-RPC first, MCP for tools/resources/prompts, A2A for agent collaboration. That is a natural extension of what Voltaic already is.

The abstraction can be deliberately lighter than the official A2A SDK. The official SDK should remain the answer for full ASP.NET Core hosting, richer task orchestration, and complete binding coverage. Voltaic can focus on "small, direct, dependency-light client/server primitives" in the same style as the existing MCP API.

## What not to do

Do not make `McpEndpoint` generic enough to become `A2AEndpoint`. MCP endpoint state is about initialization, capabilities, tools, resources, prompts, completions, logging, and resource subscriptions. A2A endpoint state is about agent identity, skills, messages, tasks, statuses, artifacts, histories, streaming events, and optional push notifications. They deserve separate endpoint classes.

Do not expose every A2A agent as an MCP tool automatically. Some A2A skills may be safely wrapped as MCP tools when they are stateless, schema-friendly, and produce bounded responses. Many A2A interactions are long-running, multi-turn, authorization-sensitive, and stateful. Automatic wrapping would create misleading APIs.

Do not reproduce the official A2A SDK class-for-class. That project already covers full A2A v1.0, JSON-RPC and REST bindings, streaming, ASP.NET Core integration, task stores, and higher-level helpers. Voltaic should be a smaller implementation with clear interop goals.

Do not add A2A into the current flat `Voltaic` namespace. That would make the public API noisy and would make a future namespace cleanup more painful.

## Design options

### Option 1: Namespace cleanup only

Move MCP types under `Voltaic.Mcp`, move JSON-RPC types under `Voltaic.JsonRpc`, and leave A2A out for now.

Pros:

- Clarifies the existing API.
- Reduces naming clutter before adding another protocol.
- Gives time to extract a neutral core.

Cons:

- Delivers no new A2A capability.
- Still needs a compatibility plan because namespace moves are source-breaking.

This is useful groundwork, but it does not answer the bigger opportunity.

### Option 2: Add `Voltaic.A2A` beside the current API

Keep existing MCP types in `Voltaic`, add new A2A types in `Voltaic.A2A`, and reuse only obvious JSON-RPC classes.

Pros:

- Fastest route to A2A experimentation.
- Lowest immediate disruption for existing users.
- Lets A2A prove itself before a public namespace migration.

Cons:

- Leaves MCP in the wrong namespace.
- Encourages more duplicated transport and endpoint code.
- Creates an asymmetric public API: A2A is organized, MCP is not.

This is acceptable for a spike, but it is not the design I would want to keep.

### Option 3: Shared core plus protocol namespaces

Extract protocol-neutral dispatch and transport helpers, move MCP into `Voltaic.Mcp`, and add A2A under `Voltaic.A2A`.

Pros:

- Best long-term shape.
- Keeps protocol semantics separate while sharing useful infrastructure.
- Makes package positioning clearer.
- Creates a natural place for future protocol integrations.

Cons:

- Requires careful migration work.
- Likely deserves a major version boundary.
- Needs strong tests because both MCP and A2A would rely on the shared core.

This is the recommended option.

### Option 4: High-level unified agent abstraction first

Create a new abstraction such as `VoltaicCapability`, `VoltaicAgent`, or `VoltaicOperation`, then generate both MCP and A2A surfaces from it.

Pros:

- Attractive developer story for simple cases.
- Could let one handler be exposed as an MCP tool and advertised as an A2A skill.

Cons:

- Easy to overfit to the lowest common denominator.
- Risks hiding important protocol differences.
- Hard to get right before the A2A implementation exists.

This should be a later layer, not the foundation.

## Suggested A2A scope for Voltaic

Start with a deliberately small `Voltaic.A2A` surface:

- `A2AClient`
- `A2AServer` or `A2AEndpoint`
- `A2ACardResolver`
- `AgentCard`, `AgentSkill`, `AgentCapabilities`, `AgentInterface`
- `A2AMessage`, `A2APart`, `A2AArtifact`
- `A2ATask`, `A2ATaskStatus`, `A2ATaskState`
- `A2AStreamingEvent`
- `IA2AAgentHandler`
- `IA2ATaskStore`
- `InMemoryA2ATaskStore`

Initial operations should cover:

- Resolve public Agent Card from `/.well-known/agent-card.json`.
- Send a message.
- Send a streaming message over SSE.
- Get a task.
- Cancel a task.
- Subscribe to task events if streaming support is declared.
- Expose an authenticated hook for extended Agent Card later.

Defer these until the basics are solid:

- gRPC binding.
- Full REST binding parity if JSON-RPC is the initial focus.
- Durable task stores beyond in-memory.
- Push notification registration and webhook delivery.
- Agent card signing.
- Full OAuth helper flows.
- ASP.NET Core integration, unless split into a separate package.

This gives Voltaic a useful A2A implementation without trying to be the official SDK.

## Cross-protocol abstraction

The shared abstraction should be explicit and opt-in. A possible later shape:

```csharp
public sealed class VoltaicCapability
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public object? InputSchema { get; init; }
    public object? OutputSchema { get; init; }
    public bool ExposeAsMcpTool { get; init; }
    public bool AdvertiseAsA2ASkill { get; init; }
    public Func<VoltaicInvocationContext, CancellationToken, Task<VoltaicInvocationResult>> InvokeAsync { get; init; }
}
```

MCP could map `ExposeAsMcpTool` capabilities to `tools/list` and `tools/call`.

A2A could map `AdvertiseAsA2ASkill` capabilities to `AgentCard.skills`, while still routing actual messages through an A2A handler that understands tasks, context IDs, multi-turn state, and artifacts.

This distinction matters. In MCP, the capability is usually directly invoked. In A2A, a skill is discovery metadata for an agent that may plan, ask follow-up questions, use tools, and produce task artifacts over time.

## Compatibility strategy

Moving public types from `Voltaic` to `Voltaic.Mcp` is source-breaking. There are three realistic strategies:

1. Major-version break: move the types, update documentation, and accept that users change `using Voltaic;` to `using Voltaic.Mcp;` and `using Voltaic.Core;`.
2. Transitional aliases/wrappers: introduce new namespaces, keep root `Voltaic.McpServer`, `Voltaic.McpHttpClient`, etc. as `[Obsolete]` forwarding types where possible, and remove them in a later major version.
3. Package split: keep `Voltaic` as a compatibility meta-package and introduce `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A` packages.

For this project, I would prefer a transitional release followed by a major cleanup. The current API appears young enough that a clean break may be acceptable, but wrappers would reduce friction for users already on v0.3.0.

One caution: wrapper compatibility is uneven. Classes can often inherit or forward, but static classes, enums, and model collections are harder to alias cleanly in C#. If compatibility is important, design the migration before moving files.

## Package strategy

Single package:

- `Voltaic` contains JSON-RPC, MCP, and A2A.
- Easiest for users.
- Keeps the current packaging model.
- Risk: package grows and gains dependencies users may not need.

Split packages:

- `Voltaic.Core`
- `Voltaic.Mcp`
- `Voltaic.A2A`
- Optional `Voltaic.AspNetCore`
- Cleaner dependencies and clearer ownership.
- More packaging overhead.

I would start as a single package only if A2A stays dependency-light and avoids ASP.NET Core dependencies. If ASP.NET Core hosting is added, put it in a separate package.

## Testing and interop expectations

For MCP, preserve the existing shared Touchstone-style suite and run it through every transport after the namespace/core refactor.

For A2A, add interop tests against the official .NET SDK where practical:

- Voltaic client to official A2A server.
- Official A2A client to Voltaic server.
- Agent Card discovery compatibility.
- JSON-RPC request/response compatibility.
- SSE streaming compatibility.
- Task lifecycle compatibility for basic states.

Do not consider `Voltaic.A2A` credible until it can exchange at least basic messages and streaming task updates with the official SDK.

## Final take

The valuable refactor is:

- `Voltaic.Core` for shared JSON-RPC primitives.
- `Voltaic.Mcp` for MCP.
- `Voltaic.A2A` for A2A.
- A small shared transport/dispatch core.
- Optional cross-protocol capability adapters later.

The risky refactor is:

- A single generic "agent protocol" abstraction that tries to make MCP tools and A2A tasks feel the same.

Voltaic should make both MCP and A2A easy, but it should preserve their different mental models. That gives the library a strong niche: smaller and more direct than full agent frameworks, broader than MCP-only libraries, and still interoperable with the official A2A ecosystem.
