# Voltaic Problems

## Streamable HTTP `/mcp` interoperability is broken for external clients

Status: Open
Severity: High
Area: `McpHttpServer`, `ClientConnection`, streamable HTTP tests, streamable HTTP client

### Summary

Voltaic's HTTP JSON-RPC path works, but the streamable HTTP path on `/mcp` is not interoperable with external MCP clients that expect the SSE side of the transport to become active immediately.

This was reproduced against a live Docker-hosted `RestDb.McpServer` that uses Voltaic under the hood. The observed pattern was:

1. `POST /mcp` `initialize` succeeds and returns `Mcp-Session-Id`.
2. `POST /mcp` `notifications/initialized` succeeds.
3. `POST /mcp` `tools/list` succeeds.
4. `GET /mcp` with the valid `Mcp-Session-Id` and `Accept: text/event-stream` returns **zero bytes** for at least 5 seconds.

Clients such as Codex use streamable HTTP and expect the event stream to become live immediately. When the SSE side remains silent, the transport is treated as dead and the client tears the channel down during startup.

### Why this matters

The current behavior makes it possible for Voltaic-based servers to appear healthy when tested with plain HTTP request/response flows, while still failing with real streamable HTTP MCP clients.

In practice, this shows up as:

- successful `initialize`
- successful `notifications/initialized`
- transport shutdown before the first real follow-up operation such as `tools/list`

### Reproduction

#### Reproduce with any Voltaic-based `/mcp` endpoint

1. Send `POST /mcp` with `initialize`.
2. Capture the returned `Mcp-Session-Id` header.
3. Open `GET /mcp` with:
   - `Accept: text/event-stream`
   - `Mcp-Session-Id: <session-id>`
4. Wait 5 seconds.

Expected:

- response headers become observable immediately
- at least one SSE frame is emitted immediately, such as `: connected\n\n`
- or a heartbeat is emitted quickly enough that the client knows the stream is alive

Actual:

- no bytes are emitted at all for at least 5 seconds

#### Minimal raw client expectation

A plain `HttpClient` or `curl` process should be able to:

1. complete `initialize`
2. open `/mcp`
3. observe some SSE bytes quickly
4. continue using the same session for `notifications/initialized` and `tools/list`

That currently does not happen reliably.

### Root causes

#### Root cause 1: no immediate SSE prelude is sent on `/mcp` or `/events`

In `src/Voltaic/McpHttpServer.cs`, both of these methods:

- `HandleMcpRequestAsync`
- `HandleSseRequestAsync`

set SSE headers and then enter a loop waiting on the session notification queue.

No bytes are written immediately after the stream is established.

Current behavior:

- first bytes are only written when a queued server notification exists, or when the keep-alive path runs

For streamable HTTP clients, this is too late.

#### Root cause 2: `ClientConnection.DequeueAsync` swallows timeout cancellation

In `src/Voltaic/ClientConnection.cs`, `DequeueAsync` catches `OperationCanceledException` and returns `null`.

That breaks the intended keep-alive behavior in `McpHttpServer`.

The server code currently does this pattern:

1. create a linked CTS
2. `CancelAfter(TimeSpan.FromSeconds(30))`
3. `await connection.DequeueAsync(timeoutCts.Token)`
4. catch `OperationCanceledException` and send `: keep-alive\n\n`

But `DequeueAsync` does not rethrow the timeout cancellation. It returns `null` instead.

That means:

- the caller's `catch (OperationCanceledException)` never executes
- the keep-alive branch is skipped
- the stream can remain silent forever if no notifications are queued

This is a real logic bug, not just an interoperability preference.

### Required fixes

#### Fix 1: send an immediate SSE prelude on stream establishment

In both `HandleMcpRequestAsync` and `HandleSseRequestAsync`:

1. after setting:
   - `ContentType = "text/event-stream"`
   - `Cache-Control = "no-cache"`
   - `SendChunked = true`
2. immediately write a small SSE comment or event
3. immediately flush the output stream

Recommended payload:

```text
: connected


```

or, if a named event is preferred:

```text
event: ready
data: {}


```

Requirements:

- must be emitted before waiting on the notification queue
- must be flushed immediately
- must work for both `/mcp` and `/events`

This guarantees that:

- response headers become observable immediately
- clients know the stream is alive before any server notification exists

#### Fix 2: stop swallowing timeout cancellation in `ClientConnection.DequeueAsync`

Current implementation catches `OperationCanceledException` and returns `null`.

That must change.

Recommended approaches:

Option A:

- remove the `catch (OperationCanceledException)` entirely
- let the caller decide how to handle timeout vs shutdown

Option B:

- add a second dequeue method, for example `WaitForNotificationAsync`, that preserves cancellation semantics
- keep the existing method only if some callers truly need the null-on-cancel behavior

Whichever approach is chosen, the SSE loop in `McpHttpServer` must be able to distinguish:

- queue timeout: send keep-alive
- server shutdown / request cancellation: exit cleanly

#### Fix 3: make the keep-alive path actually run

After Fix 2, the existing keep-alive logic should start working as intended.

Recommended behavior:

- if no notification is queued within the heartbeat interval, emit `: keep-alive\n\n`
- flush immediately
- continue waiting

Suggested heartbeat interval:

- configurable, default 15 or 30 seconds

#### Fix 4: add a real external-client interoperability test

The current streamable HTTP coverage is not sufficient because it mostly validates Voltaic against itself.

A required test should:

1. start `McpHttpServer`
2. `POST /mcp initialize` using raw `HttpClient`
3. open `GET /mcp` using raw `HttpClient` with `ResponseHeadersRead`
4. assert that at least one byte arrives quickly, for example within 1 second
5. then send `notifications/initialized`
6. then send `tools/list`
7. assert the stream remains open and usable

Important: this test must not use `McpHttpClient` for the SSE validation step.

It should validate the wire behavior directly.

#### Fix 5: add a keep-alive regression test

Add a test that:

1. initializes a session
2. opens `GET /mcp`
3. does not enqueue any notifications
4. waits slightly longer than the heartbeat interval
5. asserts that the client receives a keep-alive SSE comment

This test should fail on the current implementation and pass after Fix 2 + Fix 3.

#### Fix 6: tighten `McpHttpClient` streamable HTTP validation

`src/Voltaic/McpHttpClient.cs` has an important blind spot:

- `ConnectStreamableAsync` only validates the POST side of `/mcp`
- it does not prove that the SSE stream is live

That means Voltaic can report "connected" even when streamable HTTP is not actually usable by external clients.

Recommended improvements:

1. document clearly that `ConnectStreamableAsync` only establishes the RPC/session side unless SSE is also started
2. consider adding an option to verify SSE liveness during connect
3. consider making `StartSseAsync` verify that the stream actually produces headers or bytes within a bounded time

### Test gaps in current coverage

`src/Test.Automated/Program.cs` already contains many streamable HTTP tests, including:

- `POST /mcp`
- `GET /mcp` SSE establishment
- session persistence
- auth handling
- custom `mcpPath`

However, the current gaps are:

1. no assertion that the SSE stream emits any bytes immediately after connect
2. no raw-client assertion that response headers/body become visible before the first server notification
3. no regression test for the null-on-cancel behavior in `ClientConnection.DequeueAsync`
4. the built-in `McpHttpClient` can mask server-side stream liveness issues because `ConnectStreamableAsync` validates only the POST path

### Recommended implementation sequence

1. Fix `ClientConnection.DequeueAsync` so timeout cancellation is visible to the caller.
2. Add an immediate SSE prelude in both `HandleMcpRequestAsync` and `HandleSseRequestAsync`.
3. Verify keep-alive comments are emitted when the queue is idle.
4. Add raw `HttpClient` interoperability tests that assert immediate stream liveness.
5. Strengthen `McpHttpClient` and/or its tests so it cannot report streamable HTTP success when the SSE side is dead.

### Acceptance criteria

This issue is fixed when all of the following are true:

- `POST /mcp initialize` succeeds
- `GET /mcp` with the returned session ID emits bytes immediately
- `notifications/initialized` succeeds on the same session
- `tools/list` succeeds on the same session
- idle sessions receive keep-alive comments on schedule
- raw `HttpClient` tests pass without relying on `McpHttpClient`
- external clients such as Codex can complete MCP startup over streamable HTTP without transport shutdown
