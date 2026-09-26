namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the requirements closed in v2.1.7: JSON Schema dialects and 2020-12 keywords, late cancellations and ID
    /// reuse, stateless notifications on handshake connections, disconnect-as-cancellation and exact status codes on
    /// HTTP, client session recovery on every path, the <c>/rpc</c> lifecycle, protocol-version header checks,
    /// resource metadata behind a proxy, periodic pings, nested sampling capabilities, per-version client
    /// capabilities, and the stricter envelope, result, broadcast, and stateless body rules.
    /// </summary>
    public static class McpComplianceSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// JSON Schema dialect and keyword cases.
        /// </summary>
        public static TestSuiteDescriptor Schemas()
        {
            const string suiteId = "Mcp.SchemaDialects";
            return new TestSuiteDescriptor(
                suiteId,
                "JSON Schema dialects, unevaluated keywords, and references",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "UnevaluatedPropertiesAndItemsAreEnforced", "unevaluatedProperties and unevaluatedItems reject what no other keyword (including passing subschemas) evaluated", async ct =>
                    {
                        string schema = "{\"type\":\"object\",\"allOf\":[{\"properties\":{\"a\":{\"type\":\"string\"}}}],\"properties\":{\"list\":{\"type\":\"array\",\"prefixItems\":[{\"type\":\"integer\"}],\"unevaluatedItems\":false}},\"unevaluatedProperties\":false}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("strict", "Strict", Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);

                        TestAssert.False(await IsErrorAsync(client, "strict", "{\"a\":\"x\",\"list\":[1]}").ConfigureAwait(false), "Evaluated properties and items pass.");
                        TestAssert.True(await IsErrorAsync(client, "strict", "{\"a\":\"x\",\"extra\":1}").ConfigureAwait(false), "An unevaluated property is rejected.");
                        TestAssert.True(await IsErrorAsync(client, "strict", "{\"list\":[1,2]}").ConfigureAwait(false), "An unevaluated item is rejected.");
                    }),

                    Case(suiteId, "AnchorsIdsAndDynamicRefsResolve", "$ref resolves $anchor names and embedded $id resources, and $dynamicRef resolves $dynamicAnchor", async ct =>
                    {
                        string schema = "{\"$id\":\"https://example.com/root\",\"type\":\"object\",\"properties\":{"
                            + "\"a\":{\"$ref\":\"#name\"},"
                            + "\"b\":{\"$ref\":\"item.json\"},"
                            + "\"c\":{\"$dynamicRef\":\"#node\"}},"
                            + "\"$defs\":{\"n\":{\"$anchor\":\"name\",\"type\":\"string\"},\"i\":{\"$id\":\"item.json\",\"type\":\"integer\"},\"d\":{\"$dynamicAnchor\":\"node\",\"type\":\"boolean\"}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("refs", "References", Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);

                        TestAssert.False(await IsErrorAsync(client, "refs", "{\"a\":\"x\",\"b\":1,\"c\":true}").ConfigureAwait(false), "Values matching the referenced schemas pass.");
                        TestAssert.True(await IsErrorAsync(client, "refs", "{\"a\":1}").ConfigureAwait(false), "The $anchor target is enforced.");
                        TestAssert.True(await IsErrorAsync(client, "refs", "{\"b\":\"x\"}").ConfigureAwait(false), "The $id target is enforced.");
                        TestAssert.True(await IsErrorAsync(client, "refs", "{\"c\":\"x\"}").ConfigureAwait(false), "The $dynamicAnchor target is enforced.");
                    }),

                    Case(suiteId, "DialectsAreHonored", "2020-12 and draft-07 are accepted (draft-07 dependencies enforced); other dialects and unresolvable references are rejected at registration", ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        server.RegisterTool("modern", "2020-12", Schema("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}"), args => "ok");
                        server.RegisterTool("seven", "draft-07", Schema("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"dependencies\":{\"a\":[\"b\"]}}"), args => "ok");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("four", "draft-04", Schema("{\"$schema\":\"http://json-schema.org/draft-04/schema#\",\"type\":\"object\"}"), args => "x"), "draft-04 is not supported.");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("remote", "Remote", Schema("{\"type\":\"object\",\"properties\":{\"a\":{\"$ref\":\"https://example.com/other.json\"}}}"), args => "x"), "External references are rejected.");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("dangling", "Dangling", Schema("{\"type\":\"object\",\"properties\":{\"a\":{\"$ref\":\"#/$defs/missing\"}}}"), args => "x"), "Dangling pointers are rejected.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "Draft07DependenciesAreEnforced", "draft-07 dependencies (array and schema forms) are enforced", async ct =>
                    {
                        string schema = "{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"dependencies\":{\"a\":[\"b\"],\"c\":{\"required\":[\"d\"]}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("deps", "Dependencies", Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.True(await IsErrorAsync(client, "deps", "{\"a\":1}").ConfigureAwait(false), "a requires b.");
                        TestAssert.True(await IsErrorAsync(client, "deps", "{\"c\":1}").ConfigureAwait(false), "c requires d through a schema.");
                        TestAssert.False(await IsErrorAsync(client, "deps", "{\"a\":1,\"b\":2,\"c\":3,\"d\":4}").ConfigureAwait(false), "Satisfied dependencies pass.");
                    }),
                });
        }

        /// <summary>
        /// Stream transport cases.
        /// </summary>
        public static TestSuiteDescriptor Streams()
        {
            const string suiteId = "McpStreams.Compliance";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP stream transport compliance",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "LateCancelDoesNotHitReusedId", "A cancellation that arrives after the response is ignored, so a later request reusing the ID (allowed on 2026-07-28) is answered", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{" + Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.Equal(1, Next(client).Get("id").Int(), "The first request is answered.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":1," + Meta("{}") + "}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{" + Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("result").Has("tools"), "The reused ID is answered.");
                    }),

                    Case(suiteId, "StatelessNotificationsIgnoreTheHandshake", "On a connection initialized at 2024-11-05, a 2026-07-28 request's progress keeps its message", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("work", "Progress", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                        {
                            await McpToolCallContext.Current!.ReportProgressAsync(1, null, "half", token).ConfigureAwait(false);
                            return "ok";
                        })).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2024-11-05", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"work\",\"arguments\":{},\"_meta\":{\"progressToken\":\"p\",\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}}}").ConfigureAwait(false);
                        TestAssert.Equal("half", Next(client).Get("params").Get("message").String(), "The stateless request's progress is not downgraded.");
                    }),

                    Case(suiteId, "ServersAndClientsPingPeriodically", "An initialized TCP session is pinged by the server, and McpTcpClient pings its server, at PingIntervalMs", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.PingIntervalMs = 200).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        JsonProbe ping = Next(client);
                        TestAssert.Equal("ping", ping.Get("method").String(), "The server sends ping.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"" + ping.Get("id").String() + "\",\"result\":{}}").ConfigureAwait(false);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => ((McpTcpServer)fixture.Server).PingIntervalMs = -1, "A negative interval is rejected.");

                        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                        listener.Start();
                        try
                        {
                            using McpTcpClient pinging = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 200 };
                            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(ct).AsTask();
                            TestAssert.True(await pinging.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, ct).ConfigureAwait(false), "The client connects.");
                            using TcpClient server = await accepting.ConfigureAwait(false);
                            using StreamReader reader = new StreamReader(server.GetStream());
                            Task init = pinging.InitializeAsync(ct);
                            JsonProbe initialize = JsonProbe.Parse((await ReadLineAsync(reader, ct).ConfigureAwait(false))!);
                            await WriteLineAsync(server.GetStream(), "{\"jsonrpc\":\"2.0\",\"id\":" + initialize.Get("id").Int() + ",\"result\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"serverInfo\":{\"name\":\"raw\",\"version\":\"1\"}}}", ct).ConfigureAwait(false);
                            await init.ConfigureAwait(false);
                            string? line;
                            bool sawPing = false;
                            while ((line = await ReadLineAsync(reader, ct).ConfigureAwait(false)) != null)
                            {
                                if (JsonProbe.Parse(line).Get("method").String() == "ping")
                                {
                                    sawPing = true;
                                    break;
                                }
                            }

                            TestAssert.True(sawPing, "The client sends ping after initialize.");
                        }
                        finally
                        {
                            listener.Stop();
                        }
                    }),

                    Case(suiteId, "SamplingContextNeedsCapability", "A sampling input request with includeContext needs sampling.context (-32021 naming it)", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("ask", "Sampling with context", new { type = "object" }, args => Task.FromResult<object>(new McpInputRequiredResult
                        {
                            InputRequests = new Dictionary<string, McpInputRequest> { { "s", new McpInputRequest { Method = "sampling/createMessage", Params = new { messages = new object[0], maxTokens = 5, includeContext = "thisServer" } } } }
                        }))).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"ask\",\"arguments\":{}," + Meta("{\"sampling\":{}}") + "}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("error").Get("data").Get("requiredCapabilities").Get("sampling").Has("context"), "sampling.context is required.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"ask\",\"arguments\":{}," + Meta("{\"sampling\":{\"context\":{}}}") + "}}").ConfigureAwait(false);
                        TestAssert.Equal("input_required", Next(client).Get("result").Get("resultType").String(), "A declared context capability passes.");
                    }),

                    Case(suiteId, "ClientsDeclareOnlyCapabilitiesTheVersionDefines", "A client with an elicitation handler requesting 2024-11-05 does not declare elicitation; requesting 2025-11-25 it does", async ct =>
                    {
                        foreach (string version in new[] { "2024-11-05", "2025-11-25" })
                        {
                            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                            listener.Start();
                            try
                            {
                                using McpTcpClient client = new McpTcpClient { AutoInitialize = false, ProtocolVersion = version };
                                client.RegisterRequestHandler("elicitation/create", (parameters, token) => Task.FromResult<object?>(new { action = "decline" }));
                                Task<TcpClient> accepting = listener.AcceptTcpClientAsync(ct).AsTask();
                                TestAssert.True(await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, ct).ConfigureAwait(false), "The client connects.");
                                using TcpClient server = await accepting.ConfigureAwait(false);
                                using StreamReader reader = new StreamReader(server.GetStream());
                                _ = client.InitializeAsync(ct);
                                JsonProbe initialize = JsonProbe.Parse((await ReadLineAsync(reader, ct).ConfigureAwait(false))!);
                                bool declared = initialize.Get("params").Get("capabilities").Has("elicitation");
                                TestAssert.Equal(version != "2024-11-05", declared, $"elicitation declared for {version}: {declared}");
                            }
                            finally
                            {
                                listener.Stop();
                            }
                        }
                    }),

                    Case(suiteId, "EnvelopeAndResultRulesAreStrict", "Fractional IDs are -32600, notification methods sent as requests are -32601, and non-object results are -32603", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterMethod("text", (RpcParameters? args, CancellationToken token) => Task.FromResult<object>("plain"))).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"ping\"}").ConfigureAwait(false);
                        TestAssert.Equal(-32600, Next(client).Get("error").Get("code").Int(), "A fractional ID is invalid.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"notifications/initialized\"}").ConfigureAwait(false);
                        TestAssert.Equal(-32601, Next(client).Get("error").Get("code").Int(), "A notification method is not a request.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"text\"}").ConfigureAwait(false);
                        TestAssert.Equal(-32603, Next(client).Get("error").Get("code").Int(), "A string result is not an MCP result.");
                    }),

                    Case(suiteId, "ConcurrentInitializeRunsOnce", "Two initialize requests sent together produce one success and one -32600", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        string initialize = "\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"x\",\"version\":\"1\"}}}";
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1," + initialize).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2," + initialize).ConfigureAwait(false);
                        List<JsonProbe> answers = new List<JsonProbe> { Next(client), Next(client) };
                        TestAssert.Equal(1, answers.Count(a => a.Has("result")), "Exactly one initialize succeeds.");
                        TestAssert.Equal(-32600, answers.First(a => a.Has("error")).Get("error").Get("code").Int(), "The other is refused.");
                    }),

                    Case(suiteId, "BroadcastsAndListChangesReachInitializedSessions", "BroadcastNotificationAsync and automatic list_changed reach initialized sessions only", async ct =>
                    {
                        McpTcpServer? server = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => server = s).ConfigureAwait(false);
                        using RawLineClient ready = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        using RawLineClient pending = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await pending.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}").ConfigureAwait(false);
                        Next(pending);

                        await server!.BroadcastNotificationAsync("custom/event", new { n = 1 }, ct).ConfigureAwait(false);
                        TestAssert.Equal("custom/event", Next(ready).Get("method").String(), "The initialized session receives the broadcast.");
                        server.RegisterTool("late", "Registered later", new { type = "object" }, args => "ok");
                        TestAssert.Equal("notifications/tools/list_changed", Next(ready).Get("method").String(), "Registering a tool announces the change.");
                        server.UnregisterTool("late");
                        TestAssert.Equal("notifications/tools/list_changed", Next(ready).Get("method").String(), "Removing a tool announces the change.");
                        TestAssert.True(pending.Receive(_Quiet) == null, "A connection that has not initialized receives nothing.");
                    }),

                    Case(suiteId, "TcpDisconnectClosesTheConnection", "McpTcpClient.Disconnect closes the socket, so the server sees the client leave", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        TaskCompletionSource<bool> left = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        fixture.Server.ClientDisconnected += (sender, client) => left.TrySetResult(true);
                        using McpTcpClient client = new McpTcpClient();
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false), "The client connects.");
                        client.Disconnect();
                        TestAssert.True(await left.Task.WaitAsync(_Wait, ct).ConfigureAwait(false), "The server saw the disconnect.");
                    }),

                    Case(suiteId, "StdioShutdownSettingsAreValidated", "ShutdownGracePeriodMs and TerminateGracePeriodMs are configurable and validated", ct =>
                    {
                        using McpClient client = new McpClient { ShutdownGracePeriodMs = 1000, TerminateGracePeriodMs = 500 };
                        TestAssert.Equal(1000, client.ShutdownGracePeriodMs, "The grace period is stored.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => client.ShutdownGracePeriodMs = -1, "Negative values are rejected.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => client.TerminateGracePeriodMs = 300001, "Values above the maximum are rejected.");
                        return Task.CompletedTask;
                    }),
                });
        }

        /// <summary>
        /// Streamable HTTP cases.
        /// </summary>
        public static TestSuiteDescriptor Http()
        {
            const string suiteId = "McpHttp.Compliance";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP Streamable HTTP compliance",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "LateCapabilityErrorKeepsStatus400", "A stateless response that completes within the default keep-alive interval is not streamed, so a -32021 raised after a slow tool still gets HTTP 400", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("slowask", "Slow URL elicitation", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                        {
                            await Task.Delay(2500, token).ConfigureAwait(false);
                            return new McpInputRequiredResult
                            {
                                InputRequests = new Dictionary<string, McpInputRequest> { { "u", new McpInputRequest { Method = "elicitation/create", Params = new { mode = "url", message = "Sign in", url = "https://example.com", elicitationId = "e" } } } }
                            };
                        })).ConfigureAwait(false);
                        RpcResult result = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/call", 1, new Dictionary<string, object?> { { "name", "slowask" }, { "arguments", new { } } }, "slowask", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(15000, fixture.Server.ResponseKeepAliveMs, "Keep-alives start after 15 seconds by default.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, result.StatusCode, $"The late -32021 keeps status 400: {result.Body}");
                        TestAssert.Equal(-32021, result.Error.Get("code").Int(), "The error is -32021.");
                    }),

                    Case(suiteId, "WriteToClosedStreamCancelsTheRequest", "When a stateless client has closed its stream, the next progress write cancels the handler instead of failing it", async ct =>
                    {
                        SlowToolProbe probe = new SlowToolProbe();
                        TaskCompletionSource<bool> firstSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("reporter", "Reports until cancelled", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                        {
                            McpToolCallContext call = McpToolCallContext.Current!;
                            try
                            {
                                for (int i = 1; i <= 200; i++)
                                {
                                    await call.ReportProgressAsync(i).ConfigureAwait(false);
                                    if (i == 1) firstSent.TrySetResult(true);
                                    await Task.Delay(100, token).ConfigureAwait(false);
                                }
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                probe.Cancelled = true;
                                throw;
                            }

                            return "finished";
                        })).ConfigureAwait(false);

                        Dictionary<string, object?> meta = McpHttpTestRequests.StatelessMeta();
                        meta["progressToken"] = "p";
                        using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/"))
                        {
                            request.Content = new StringContent(McpHttpTestRequests.BuildBody("tools/call", 1, new Dictionary<string, object?> { { "name", "reporter" }, { "arguments", new { } }, { "_meta", meta } }), Encoding.UTF8, "application/json");
                            request.Headers.Accept.ParseAdd("application/json");
                            request.Headers.Accept.ParseAdd("text/event-stream");
                            request.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728);
                            request.Headers.TryAddWithoutValidation(McpProtocol.MethodHeader, "tools/call");
                            request.Headers.TryAddWithoutValidation(McpProtocol.NameHeader, "reporter");
                            using HttpResponseMessage response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            await firstSent.Task.WaitAsync(_Wait, ct).ConfigureAwait(false);
                        }

                        TestAssert.True(await WaitUntilAsync(() => probe.Cancelled, ct).ConfigureAwait(false), "The handler was cancelled after the client closed the stream.");
                    }),

                    Case(suiteId, "ClientRecoversSessionOnEveryPath", "McpHttpClient starts a new session when a notification or its GET stream finds the session gone", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient { PingIntervalMs = 0 };
                        List<string> log = new List<string>();
                        client.Log += (sender, message) => { lock (log) log.Add("C " + (message.Length > 120 ? message.Substring(0, 120) : message)); };
                        fixture.Server.Log += (sender, message) => { lock (log) log.Add("S " + (message.Length > 120 ? message.Substring(0, 120) : message)); };
                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        string first = client.SessionId!;
                        fixture.Server.RemoveSession(first);
                        await client.NotifyAsync("notifications/roots/list_changed", null, 10000, ct).ConfigureAwait(false);
                        string second = client.SessionId!;
                        TestAssert.True(second != first && fixture.Server.GetActiveSessions().Contains(second), "A notification that found the session gone started a new one.");

                        await client.StartSseAsync(ct).ConfigureAwait(false);
                        TestAssert.True(await WaitUntilAsync(() => client.IsSseConnected, ct).ConfigureAwait(false), "The GET stream is open.");
                        fixture.Server.RemoveSession(second);
                        TestAssert.True(await WaitUntilAsync(() => client.SessionId != null && client.SessionId != second, ct).ConfigureAwait(false), "The GET stream's 404 started a new session. Log: " + String.Join(" | ", log.Skip(Math.Max(0, log.Count - 25))));
                    }),

                    Case(suiteId, "LegacyEndpointsCanBeDisabled", "EnableLegacyEndpoints = false leaves only the Streamable HTTP endpoint", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.EnableLegacyEndpoints = false).ConfigureAwait(false);
                        RpcResult rpc = await fixture.PostJsonRpcAsync("/rpc/", "ping", null, 1, null, ct).ConfigureAwait(false);
                        RpcResult mcp = await McpHttpTestRequests.InitializeAsync(fixture, null, null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, rpc.StatusCode, "/rpc is not served.");
                        TestAssert.True(mcp.SessionId != null, "/mcp still works.");
                    }),

                    Case(suiteId, "ProtocolVersionHeaderMustMatchTheSession", "A header naming a version above the cap, another version than the session's, or 2026-07-28 on a session GET gets 400", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.MaximumHandshakeProtocolVersion = "2025-06-18").ConfigureAwait(false);
                        string session = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-06-18", null, ct).ConfigureAwait(false)).SessionId!;
                        RpcResult aboveCap = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 1, new { }, session, "2025-11-25", null, ct).ConfigureAwait(false);
                        RpcResult otherVersion = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 2, new { }, session, "2025-03-26", null, ct).ConfigureAwait(false);
                        RpcResult matching = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 3, new { }, session, "2025-06-18", null, ct).ConfigureAwait(false);
                        using HttpRequestMessage get = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/mcp/");
                        get.Headers.Accept.ParseAdd("text/event-stream");
                        get.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
                        get.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, "2026-07-28");
                        using HttpResponseMessage stream = await fixture.SendRawAsync(get, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, aboveCap.StatusCode, "A version above the cap is rejected.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, otherVersion.StatusCode, "A version other than the session's is rejected.");
                        TestAssert.Equal(HttpStatusCode.OK, matching.StatusCode, "The negotiated version is accepted.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, stream.StatusCode, "2026-07-28 on a session GET is rejected.");
                    }),

                    Case(suiteId, "ResourceMetadataComesFromTheResourceIdentifier", "Challenges advertise the metadata URL derived from ProtectedResourceMetadata.Resource, which is also served", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s =>
                        {
                            s.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "https://mcp.example.com/api/mcp", AuthorizationServers = new List<string> { "https://auth.example.com" } };
                            s.AuthenticationHandler = _ => Task.FromResult(new AuthenticationResult { IsAuthenticated = false, StatusCode = 401 });
                        }).ConfigureAwait(false);
                        using HttpRequestMessage post = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json") };
                        post.Headers.Accept.ParseAdd("application/json");
                        post.Headers.Accept.ParseAdd("text/event-stream");
                        using HttpResponseMessage unauthorized = await fixture.SendRawAsync(post, ct).ConfigureAwait(false);
                        string challenge = unauthorized.Headers.WwwAuthenticate.ToString();
                        using HttpResponseMessage document = await fixture.Client.GetAsync($"{fixture.BaseUrl}{McpProtocol.ProtectedResourceMetadataPath}/api/mcp", ct).ConfigureAwait(false);

                        TestAssert.True(challenge.Contains("resource_metadata=\"https://mcp.example.com" + McpProtocol.ProtectedResourceMetadataPath + "/api/mcp\""), $"The public URL is advertised: {challenge}");
                        TestAssert.Equal(HttpStatusCode.OK, document.StatusCode, "The metadata is served at the resource's path.");
                    }),

                    Case(suiteId, "StatelessBodiesMustBeSingleMessages", "A stateless batch or a client-sent response gets 400 -32600", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        Dictionary<string, string> headers = new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 }, { McpProtocol.MethodHeader, "tools/list" } };
                        RpcResult batch = await McpHttpTestRequests.SendAsync(fixture, "[" + McpHttpTestRequests.BuildBody("tools/list", 1, new { _meta = McpHttpTestRequests.StatelessMeta() }) + "]", headers, true, ct).ConfigureAwait(false);
                        RpcResult response = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 } }, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, batch.StatusCode, "A batch is rejected.");
                        TestAssert.Equal(-32600, batch.Error.Get("code").Int(), "It is an invalid request.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode, "A client response is rejected.");
                    }),

                    Case(suiteId, "PrimingFollowsTheSessionVersion", "GET streams start with a priming event on 2025-11-25 sessions and not on 2025-06-18 sessions", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        foreach (string version in new[] { "2025-06-18", "2025-11-25" })
                        {
                            string session = (await McpHttpTestRequests.InitializeAsync(fixture, version, null, ct).ConfigureAwait(false)).SessionId!;
                            using HttpRequestMessage get = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/mcp/");
                            get.Headers.Accept.ParseAdd("text/event-stream");
                            get.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
                            get.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, version);
                            using HttpResponseMessage stream = await fixture.Client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            using StreamReader reader = new StreamReader(await stream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                            fixture.Server.SendNotificationToSession(session, "custom/hello", new { });
                            string? line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
                            bool primed = line != null && line.StartsWith("id:", StringComparison.Ordinal) && (await ReadLineAsync(reader, ct).ConfigureAwait(false))!.StartsWith("retry:", StringComparison.Ordinal);
                            TestAssert.Equal(version == "2025-11-25", primed, $"Priming on {version}: {primed}");

                            // Ending the session ends its stream at once.
                            fixture.Server.RemoveSession(session);
                        }
                    }),

                    Case(suiteId, "StatelessClientRetriesLaterRequestsAfterMinus32022", "When a later stateless request gets -32022, McpHttpClient switches to a listed version and sends it again", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer { IssueSession = false };
                        List<string> versions = new List<string>();
                        server.RequestHandler = (method, idJson, body, response, token) =>
                        {
                            string version = JsonProbe.Parse(body).Get("params").Get("_meta").Get(McpProtocol.MetaProtocolVersionKey).String()!;
                            lock (versions) versions.Add(method + "@" + version);
                            string json = method == "server/discover"
                                ? "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":{\"supportedVersions\":[\"2026-07-28\",\"2099-01-01\"],\"capabilities\":{},\"serverInfo\":{\"name\":\"f\",\"version\":\"1\"},\"resultType\":\"complete\"}}"
                                : version == "2099-01-01"
                                    ? "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"error\":{\"code\":-32022,\"message\":\"Unsupported\",\"data\":{\"supported\":[\"2026-07-28\"],\"requested\":\"2099-01-01\"}}}"
                                    : "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":{\"tools\":[],\"resultType\":\"complete\"}}";
                            return FakeStreamableHttpServer.WriteJsonAsync(response, json, token);
                        };
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStatelessAsync(server.BaseUrl, "/mcp", "2099-01-01", true, ct).ConfigureAwait(false), "The client connects.");
                        JsonRpcResponse listed = await client.SendStatelessAsync("tools/list", null, null, ct).ConfigureAwait(false);
                        TestAssert.True(listed.Error == null, $"The retried request succeeds: {listed.Error?.Message}");
                        lock (versions) TestAssert.True(versions.Contains("tools/list@2026-07-28"), $"The retry used a listed version: {String.Join(",", versions)}");
                    }),

                    Case(suiteId, "InsufficientScopeCodeIsOutsideReservedRanges", "McpInsufficientScopeException uses an application-defined code outside the JSON-RPC reserved range", ct =>
                    {
                        TestAssert.True(McpInsufficientScopeException.ErrorCode > -32000 || McpInsufficientScopeException.ErrorCode < -32768, "The code is outside -32768..-32000.");
                        TestAssert.Equal(403, new McpInsufficientScopeException("s").ToJsonRpcError().Code, "The error carries the code.");
                        return Task.CompletedTask;
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "compliance", "conformance" });
        }

        private static JsonElement Schema(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static string Meta(string capabilities)
        {
            return "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":" + capabilities + "}";
        }

        private static async Task<RawLineClient> InitializedAsync(TcpJsonRpcFixture fixture, CancellationToken token)
        {
            RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, token).ConfigureAwait(false);
            await client.InitializeAsync("2025-11-25", token).ConfigureAwait(false);
            return client;
        }

        private static int _NextId = 100;

        private static async Task<bool> IsErrorAsync(RawLineClient client, string tool, string arguments)
        {
            int id = Interlocked.Increment(ref _NextId);
            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}").ConfigureAwait(false);
            JsonProbe result = Next(client).Get("result");
            return result.Has("isError") && result.Get("isError").Bool();
        }

        private static JsonProbe Next(RawLineClient client)
        {
            string? line = client.Receive(_Wait);
            if (line == null) throw new TimeoutException("No message arrived.");
            return TestJson.ParseRoot(line);
        }

        private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_Wait);
            return await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }

        private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow + _Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            return condition();
        }
    }
}
