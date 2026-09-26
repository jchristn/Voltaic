namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the stateless <c>2026-07-28</c> revision on the stream transports (stdio, TCP, WebSocket): a request
    /// whose <c>_meta</c> names the revision is served statelessly (with <c>resultType</c>, <c>ttlMs</c>, and
    /// <c>cacheScope</c>), <c>server/discover</c> answers the dual-era probe, unsupported versions get <c>-32022</c>,
    /// and handshake-era requests on the same connection are unchanged.
    /// </summary>
    public static class McpStreamTransportStatelessSuites
    {
        private static readonly string[] _Transports = { "stdio", "tcp", "websocket" };

        /// <summary>
        /// Stateless-era cases run on every stream transport.
        /// </summary>
        public static TestSuiteDescriptor StatelessOnStreams()
        {
            const string suiteId = "McpStreams.Stateless";
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            foreach (string transport in _Transports)
            {
                string t = transport;

                cases.Add(Case(suiteId, $"DiscoverAnswersProbe_{t}", $"server/discover answers the 2026-07-28 probe on {t}", async ct =>
                {
                    await using StreamCaller caller = await StreamCaller.StartAsync(t, ct).ConfigureAwait(false);
                    JsonElement result = await caller.CallAsync("server/discover", Meta(), ct).ConfigureAwait(false);

                    List<string> versions = result.GetProperty("supportedVersions").EnumerateArray().Select(item => item.GetString()!).ToList();
                    TestAssert.True(versions.Contains(McpProtocol.ProtocolVersion20260728) && versions.Contains(McpProtocol.ProtocolVersion20251125), $"Both eras are advertised on {t}.");
                    TestAssert.Equal("complete", result.GetProperty("resultType").GetString(), "Discover carries resultType.");
                    TestAssert.True(result.TryGetProperty("ttlMs", out _) && result.TryGetProperty("cacheScope", out _), "Discover is cacheable.");
                    TestAssert.True(result.GetProperty("capabilities").TryGetProperty("tools", out JsonElement tools) && !tools.TryGetProperty("listChanged", out _), "listChanged is not promised on the stateless surface.");
                }));

                cases.Add(Case(suiteId, $"StatelessRequestsCarryResultFields_{t}", $"Stateless tools/list and tools/call on {t} carry resultType, and list results carry ttlMs and cacheScope", async ct =>
                {
                    await using StreamCaller caller = await StreamCaller.StartAsync(t, ct).ConfigureAwait(false);
                    JsonElement list = await caller.CallAsync("tools/list", Meta(), ct).ConfigureAwait(false);
                    JsonElement call = await caller.CallAsync("tools/call", Meta(new Dictionary<string, object?> { { "name", "echo" }, { "arguments", new { message = "stateless" } } }), ct).ConfigureAwait(false);
                    JsonElement ping = await caller.CallAsync("ping", Meta(), ct).ConfigureAwait(false);

                    TestAssert.Equal("complete", list.GetProperty("resultType").GetString());
                    TestAssert.Equal("private", list.GetProperty("cacheScope").GetString(), "The conservative cache default applies.");
                    TestAssert.Equal(0, list.GetProperty("ttlMs").GetInt64());
                    TestAssert.True(list.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "echo"), "Tools are listed.");
                    TestAssert.Equal("complete", call.GetProperty("resultType").GetString());
                    TestAssert.True(call.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("stateless"), "The tool ran.");
                    TestAssert.Equal("complete", ping.GetProperty("resultType").GetString(), "ping carries resultType.");
                }));

                cases.Add(Case(suiteId, $"StatelessInvalidArgumentsAreToolErrors_{t}", $"Invalid arguments on a stateless request on {t} are a tool execution error with resultType", async ct =>
                {
                    await using StreamCaller caller = await StreamCaller.StartAsync(t, ct).ConfigureAwait(false);
                    JsonElement call = await caller.CallAsync("tools/call", Meta(new Dictionary<string, object?> { { "name", "echo" }, { "arguments", new { } } }), ct).ConfigureAwait(false);

                    TestAssert.True(call.GetProperty("isError").GetBoolean(), "isError is true.");
                    TestAssert.Equal("complete", call.GetProperty("resultType").GetString());
                }));

                cases.Add(Case(suiteId, $"UnsupportedMetaVersionIsRejected_{t}", $"A _meta protocol version the server does not implement gets -32022 on {t}", async ct =>
                {
                    await using StreamCaller caller = await StreamCaller.StartAsync(t, ct).ConfigureAwait(false);
                    Exception? error = await CaptureAsync(() => caller.CallAsync("tools/list", Meta(null, "1999-01-01"), ct)).ConfigureAwait(false);

                    TestAssert.NotNull(error, "The request is rejected.");
                    TestAssert.True(error!.Message.Contains("-32022"), $"The error is UnsupportedProtocolVersion: {error.Message}");
                }));

                cases.Add(Case(suiteId, $"HandshakeRequestsUnchanged_{t}", $"On {t}, initialize (even with a 2026-07-28 _meta) and requests without _meta keep handshake-era semantics on the same connection", async ct =>
                {
                    await using StreamCaller caller = await StreamCaller.StartAsync(t, ct).ConfigureAwait(false);
                    Dictionary<string, object?> initializeParams = Meta(new Dictionary<string, object?>
                    {
                        { "protocolVersion", "2026-07-28" },
                        { "capabilities", new { } },
                        { "clientInfo", new { name = "mixed", version = "1" } }
                    });
                    JsonElement initialize = await caller.CallAsync("initialize", initializeParams, ct).ConfigureAwait(false);
                    JsonElement legacyList = await caller.CallAsync("tools/list", new Dictionary<string, object?>(), ct).ConfigureAwait(false);
                    JsonElement statelessList = await caller.CallAsync("tools/list", Meta(), ct).ConfigureAwait(false);
                    JsonElement legacyAgain = await caller.CallAsync("tools/list", new Dictionary<string, object?>(), ct).ConfigureAwait(false);

                    TestAssert.Equal(McpProtocol.NewestHandshakeProtocolVersion, initialize.GetProperty("protocolVersion").GetString(), "initialize negotiates a handshake version.");
                    TestAssert.False(legacyList.TryGetProperty("resultType", out _), "A handshake-era result has no resultType.");
                    TestAssert.Equal("complete", statelessList.GetProperty("resultType").GetString(), "A stateless request on the same connection is stateless.");
                    TestAssert.False(legacyAgain.TryGetProperty("resultType", out _), "Stateless fields do not leak into later handshake-era results.");
                }));
            }

            cases.Add(Case(suiteId, "ReusedResultInstanceIsReverted", "A result instance a handler reuses carries stateless fields only in the stateless response (TCP and WebSocket)", async ct =>
            {
                McpToolCallResult shared = McpToolCallResult.FromText("shared");
                foreach (string t in new[] { "tcp", "websocket" })
                {
                    await using StreamCaller caller = await StreamCaller.StartAsync(t, ct, registerTool: (name, schema, handler) => handler(name, schema, _ => shared)).ConfigureAwait(false);
                    JsonElement stateless = await caller.CallAsync("tools/call", Meta(new Dictionary<string, object?> { { "name", "shared" }, { "arguments", new { } } }), ct).ConfigureAwait(false);
                    JsonElement legacy = await caller.CallAsync("tools/call", new Dictionary<string, object?> { { "name", "shared" }, { "arguments", new { } } }, ct).ConfigureAwait(false);

                    TestAssert.Equal("complete", stateless.GetProperty("resultType").GetString(), $"The stateless response is stamped on {t}.");
                    TestAssert.False(legacy.TryGetProperty("resultType", out _), $"The reused instance is reverted on {t}.");
                    TestAssert.True(shared.ResultType == null, "The handler's instance is left unchanged.");
                }
            }));

            return new TestSuiteDescriptor(suiteId, "MCP 2026-07-28 on stdio, TCP, and WebSocket", cases);
        }

        private static Dictionary<string, object?> Meta(Dictionary<string, object?>? parameters = null, string version = McpProtocol.ProtocolVersion20260728)
        {
            Dictionary<string, object?> result = parameters != null ? new Dictionary<string, object?>(parameters) : new Dictionary<string, object?>();
            result["_meta"] = McpHttpTestRequests.StatelessMeta(version);
            return result;
        }

        private static async Task<Exception?> CaptureAsync(Func<Task<JsonElement>> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "stateless", "transport" });
        }

        /// <summary>
        /// Sends JSON-RPC requests over one stream transport and returns the raw result, so tests can inspect
        /// stateless-era fields. stdio launches the Test.McpServer subprocess; TCP and WebSocket host a server in-process.
        /// </summary>
        private sealed class StreamCaller : IAsyncDisposable
        {
            private Func<string, object?, CancellationToken, Task<JsonElement>> _Call = (_, _, _) => Task.FromResult(default(JsonElement));
            private readonly List<IAsyncDisposable> _AsyncDisposables = new List<IAsyncDisposable>();
            private readonly List<IDisposable> _Disposables = new List<IDisposable>();

            public static async Task<StreamCaller> StartAsync(string transport, CancellationToken token, Action<string, object, Func<string, object, Func<RpcParameters?, object>, bool>>? registerTool = null)
            {
                StreamCaller caller = new StreamCaller();
                object schema = new { type = "object" };

                switch (transport)
                {
                    case "tcp":
                        TcpJsonRpcFixture tcp = await TcpJsonRpcFixture.StartMcpTcpAsync(token, server =>
                        {
                            registerTool?.Invoke("shared", schema, (name, s, handler) => { server.RegisterTool(name, "Reused result", s, handler); return true; });
                        }, includeDiagnosticTools: true).ConfigureAwait(false);
                        caller._AsyncDisposables.Add(tcp);
                        JsonRpcClient tcpClient = await tcp.ConnectClientAsync(token).ConfigureAwait(false);
                        caller._Disposables.Add(tcpClient);
                        caller._Call = (method, parameters, ct) => tcpClient.CallAsync<JsonElement>(method, parameters, token: ct);
                        break;

                    case "websocket":
                        WebSocketMcpFixture ws = await WebSocketMcpFixture.StartAsync(token, server =>
                        {
                            registerTool?.Invoke("shared", schema, (name, s, handler) => { server.RegisterTool(name, "Reused result", s, handler); return true; });
                        }, includeDiagnosticTools: true).ConfigureAwait(false);
                        caller._AsyncDisposables.Add(ws);
                        McpWebsocketsClient wsClient = await ws.ConnectClientAsync(token).ConfigureAwait(false);
                        caller._Disposables.Add(wsClient);
                        caller._Call = (method, parameters, ct) => wsClient.CallAsync<JsonElement>(method, parameters, token: ct);
                        break;

                    case "stdio":
                        McpClient stdio = new McpClient();
                        caller._Disposables.Add(stdio);
                        await McpStdioIntegrationSuites.LaunchTestServerAsync(stdio, token).ConfigureAwait(false);
                        caller._Call = (method, parameters, ct) => stdio.CallAsync<JsonElement>(method, parameters, token: ct);
                        break;

                    default:
                        throw new ArgumentException($"Unknown transport '{transport}'.", nameof(transport));
                }

                return caller;
            }

            public Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken token)
            {
                return _Call(method, parameters, token);
            }

            public async ValueTask DisposeAsync()
            {
                foreach (IDisposable disposable in _Disposables) disposable.Dispose();
                foreach (IAsyncDisposable disposable in _AsyncDisposables) await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
