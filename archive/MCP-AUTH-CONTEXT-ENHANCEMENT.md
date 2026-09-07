# Voltaic enhancement: carry the authenticated caller into MCP tool/method handlers

## Problem / use case

Armada runs a multi-tenant, multi-user control plane and exposes its capabilities as MCP tools
through `Voltaic.Mcp.McpHttpServer`. It needs **per-user authorization inside each MCP tool
handler**: when a tool such as `enumerate` or `create_mission` runs, the handler must know which
tenant/user is calling so it can scope reads and gate writes (a regular user sees only their own
records; a tenant admin sees the whole tenant; a global admin sees everything).

Today that is impossible with Voltaic 0.6.1:

- `McpHttpServer.AuthenticationHandler` (`Func<HttpListenerRequest, Task<AuthenticationResult>>`)
  can authenticate the HTTP request and produce an `AuthenticationResult` with `Principal` and
  `Claims` (see `src/Voltaic/Core/AuthenticationResult.cs`). Good — the host can identify the caller.
- **But the authenticated identity is never handed to the tool handler.** A tool handler receives
  only `RpcParameters` (the tool arguments' raw JSON) — see the handler delegate
  `Func<RpcParameters?, CancellationToken, Task<object>>` and the invocation at
  `src/Voltaic/Mcp/McpEndpoint.cs:254` (`await tool.Handler(toolArguments, token)`).

So the host can authenticate the connection but cannot authorize per-caller inside the handler.
The ask is a first-class, supported way for a tool/method handler to see the authenticated
caller for the current request.

## Key fact that makes this cheap

The request flows through a single async call chain, with **no queue/thread hop between
authentication and handler dispatch** on the request/response path:

```
McpHttpServer.HandleRequestAsync (~line 985)
  -> AuthenticationHandler(context.Request)                 // src/Voltaic/Mcp/McpHttpServer.cs:1035
  -> HandlePreReadRpcRequestAsync / HandleMcpRequestAsync   // :1061 / :1068  (same async flow)
     -> _Methods[request.Method](paramsElement, token)      // :2058  (single method choke point)
        -> McpEndpoint.CallToolAsync                          // registered as "tools/call"
           -> tool.Handler(toolArguments, token)             // src/Voltaic/Mcp/McpEndpoint.cs:254
```

Because this is one awaited chain, an `AsyncLocal<T>` set immediately after a successful
`AuthenticationResult` will flow all the way into `tool.Handler`. That yields a minimal,
fully backward-compatible enhancement (no handler-signature changes required).

## Recommended design

Implement **Design A** (ambient request context) as the core primitive. Optionally add
**Design B** (explicit context parameter) on top for ergonomics. Design A alone unblocks Armada.

### Design A - ambient request context via AsyncLocal (required)

1. New public type in `Voltaic.Core` (new file `src/Voltaic/Core/RpcCallContext.cs`):

   ```csharp
   namespace Voltaic.Core
   {
       using System.Collections.Generic;
       using System.Threading;

       /// <summary>
       /// Ambient, request-scoped context describing the authenticated caller for the JSON-RPC / MCP
       /// request currently being handled on this async flow. Populated by the transport immediately
       /// after a successful AuthenticationHandler result and cleared when the request completes.
       /// Handlers read <see cref="Current"/> to authorize per-caller. Null when no AuthenticationHandler
       /// is configured or the request was allowed pre-auth (e.g. ping).
       /// </summary>
       public sealed class RpcCallContext
       {
           private static readonly AsyncLocal<RpcCallContext?> _Current = new AsyncLocal<RpcCallContext?>();

           /// <summary>Gets the context for the current request, or null.</summary>
           public static RpcCallContext? Current => _Current.Value;

           /// <summary>Authenticated principal (opaque host-defined string), or null.</summary>
           public string? Principal { get; }

           /// <summary>Host-defined claims copied from the AuthenticationResult (e.g. tenantId, userId, roles).</summary>
           public IReadOnlyDictionary<string, string> Claims { get; }

           /// <summary>Creates a context. Intended for transport use.</summary>
           public RpcCallContext(string? principal, IReadOnlyDictionary<string, string>? claims)
           {
               Principal = principal;
               Claims = claims ?? new Dictionary<string, string>();
           }

           /// <summary>Sets the ambient context for the current async flow. Returns an IDisposable that restores the prior value.</summary>
           public static System.IDisposable Push(RpcCallContext? context)
           {
               RpcCallContext? prior = _Current.Value;
               _Current.Value = context;
               return new Scope(prior);
           }

           private sealed class Scope : System.IDisposable
           {
               private readonly RpcCallContext? _Prior;
               private bool _Disposed;
               public Scope(RpcCallContext? prior) { _Prior = prior; }
               public void Dispose() { if (!_Disposed) { _Current.Value = _Prior; _Disposed = true; } }
           }
       }
   }
   ```

2. In `McpHttpServer.HandleRequestAsync`, right after a successful auth result
   (`src/Voltaic/Mcp/McpHttpServer.cs`, immediately after line 1056 where `authResult.IsAuthenticated`
   is confirmed) wrap the remainder of request handling in the ambient scope:

   ```csharp
   using (RpcCallContext.Push(new RpcCallContext(authResult.Principal, authResult.Claims)))
   {
       if (requestBody != null)
       {
           await HandlePreReadRpcRequestAsync(context, path, requestBody, token).ConfigureAwait(false);
           return;
       }
       // ... fall through to the existing McpPath / RpcPath / EventsPath dispatch below
   }
   ```

   Ensure BOTH dispatch paths that can run after auth (the buffered-body `HandlePreReadRpcRequestAsync`
   path AND the fall-through `HandleMcpRequestAsync` / `HandleRpcRequestAsync` block at lines 1066-1073)
   execute inside the `using`. The `IDisposable` restore keeps `AsyncLocal` clean across pooled
   HttpListener continuations.

3. Do the equivalent in the other transports that support `AuthenticationHandler` and dispatch to the
   same `_Methods` map / `CallToolAsync` (`McpTcpServer`, `McpWebsocketsServer`, and the RPC handlers
   in `McpServer` if applicable). Armada only uses `McpHttpServer`, so HTTP is the priority; the others
   are for parity so the ambient is never silently absent on a transport that authenticated the caller.

Backward compatibility: no existing signature changes; when no `AuthenticationHandler` is set,
`RpcCallContext.Current` is null and every current handler behaves exactly as before.

### Design B - explicit context parameter (optional, ergonomic)

If you prefer an explicit handler signature over an ambient, add overloads that receive the context:

```csharp
public void RegisterTool(string name, string description, object inputSchema,
    Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>> handler);
```

Internally these can simply read `RpcCallContext.Current` at invocation time and pass it through, so
Design B is a thin wrapper over Design A. Keep all existing overloads intact.

## What Armada will do once this ships (confirms the contract)

- Set `McpHttpServer.AuthenticationHandler` to read `Authorization` / `X-Token` / `X-Api-Key` from
  `HttpListenerRequest.Headers`, resolve them to an Armada identity, and return an
  `AuthenticationResult { IsAuthenticated = true, Principal = <userId>, Claims = { tenantId, userId,
  isAdmin, isTenantAdmin, authMethod } }` (or `IsAuthenticated = false` with `StatusCode = 401`).
- In its tool adapter (`RegisterAdaptedTool`), read `RpcCallContext.Current`, rebuild an
  `AuthContext` from `Claims`, and thread it into the ~55 tool handlers that currently use a default
  tenant-admin context. Handlers then use the same `ScopedVisibility` / scoped DB overloads already
  used by the REST layer.

So the only thing Voltaic must own is **carrying `AuthenticationResult` (Principal + Claims) to the
handler invocation for the current request**. Credential parsing and the claims schema stay in the host.

## Acceptance criteria

1. `Voltaic.Core.RpcCallContext.Current` is non-null and reflects the `AuthenticationResult` returned
   by `AuthenticationHandler` when a tool/method handler runs for an authenticated request over
   `McpHttpServer` (verify inside a `tools/call` handler).
2. `RpcCallContext.Current` is null for pre-auth-bypassed requests (e.g. `ping`) and when no
   `AuthenticationHandler` is configured.
3. Concurrent requests on different async flows never observe each other's context (AsyncLocal isolation).
4. The ambient is cleared/restored after each request (no leakage across pooled continuations).
5. All existing `RegisterTool` / `RegisterMethod` overloads compile and behave unchanged.

## File references (Voltaic repo, as of this writing)

- `src/Voltaic/Mcp/McpHttpServer.cs:1035` - `AuthenticationHandler` invocation.
- `src/Voltaic/Mcp/McpHttpServer.cs:1056-1073` - post-auth dispatch (wrap here).
- `src/Voltaic/Mcp/McpHttpServer.cs:2058` - single method choke point (`_Methods[request.Method](...)`).
- `src/Voltaic/Mcp/McpEndpoint.cs:221` / `:254` - `CallToolAsync` and the `tool.Handler(...)` call.
- `src/Voltaic/Core/AuthenticationResult.cs` - existing result type (`Principal`, `Claims`, `IsAuthenticated`, `StatusCode`, `ErrorMessage`).
- `src/Voltaic/Core/RpcParameters.cs` - existing handler parameter (unchanged).
