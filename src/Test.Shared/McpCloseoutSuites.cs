namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the findings of the v2.1.13 review, closed before release: refused batches finish their IDs, stdio splits
    /// messages only at LF, clients answer empty and invalid batches, WebSocket peers refuse binary messages, input
    /// responses are validated by type and unrecognized ones ignored, handler result types and application errors are
    /// brought into 2026-07-28's shape, header numbers compare exactly, <c>subscriptions/listen</c>, session limits,
    /// error ids per revision, and 405 <c>Allow</c> headers.
    /// </summary>
    public static class McpCloseoutSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Cases.
        /// </summary>
        public static TestSuiteDescriptor Cases()
        {
            const string suiteId = "Mcp.Closeout";
            return new TestSuiteDescriptor(
                suiteId,
                "Findings of the v2.1.13 review",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "RefusedBatchFinishesItsIds", "A batch refused on 2025-11-25 leaves no reserved IDs: a later cancellation for one of them is ignored", async ct =>
                    {
                        ConcurrentQueue<string> log = new ConcurrentQueue<string>();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.Log += (sender, message) => log.Enqueue(message)).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"leak\",\"method\":\"ping\"}]").ConfigureAwait(false);
                        TestAssert.Equal(-32600, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "The batch is refused.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"leak\"}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"after\",\"method\":\"ping\"}").ConfigureAwait(false);
                        McpStrictSuites.Next(client);
                        TestAssert.False(log.Any(line => line.Contains("Cancelled request \"leak\"", StringComparison.Ordinal)), "The refused element's ID was not left reserved.");
                    }),

                    Case(suiteId, "StdioSplitsOnlyAtLineFeed", "A bare carriage return inside a stdio message is JSON whitespace, not a message boundary", async ct =>
                    {
                        await using McpRawStreamChannel channel = McpRawStreamChannel.StartStdio();
                        await channel.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}", ct).ConfigureAwait(false);
                        TestAssert.True(NextWithId(channel, "\"init\"") != null, "The server initializes.");
                        await channel.SendAsync("{\"jsonrpc\":\"2.0\",\r\"id\":7,\"method\":\"ping\"}", ct).ConfigureAwait(false);
                        JsonProbe? pong = NextWithId(channel, "7");
                        TestAssert.True(pong != null && pong.Has("result"), "The message with an embedded CR is one request.");
                    }),

                    Case(suiteId, "ClientsAnswerEmptyAndInvalidBatches", "A 2025-03-26 client answers an empty batch with one -32600 and each invalid element with -32600", async ct =>
                    {
                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0, ProtocolVersion = "2025-03-26" };
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client connects.");
                        await accepting.ConfigureAwait(false);

                        await peer.SendAsync("[]", ct).ConfigureAwait(false);
                        JsonProbe empty = TestJson.ParseRoot((await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                        TestAssert.Equal(-32600, (empty.IsArray ? empty[0] : empty).Get("error").Get("code").Int(), "An empty batch is an invalid request.");

                        await peer.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"p\",\"method\":\"ping\"},2]", ct).ConfigureAwait(false);
                        JsonProbe mixed = TestJson.ParseRoot((await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                        TestAssert.Equal(2, mixed.Length, "The ping and the invalid element are both answered.");
                        TestAssert.True(mixed.EnumerateArray().Any(item => item.Has("error") && item.Get("error").Get("code").Int() == -32600), "The invalid element gets -32600.");
                        TestAssert.True(mixed.EnumerateArray().Any(item => item.Has("result")), "The ping is answered.");
                    }),

                    Case(suiteId, "WebSocketRefusesBinaryMessages", "A binary WebSocket message closes the connection with 1003 instead of being dropped", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        using ClientWebSocket socket = new ClientWebSocket();
                        await socket.ConnectAsync(new Uri(fixture.Url), ct).ConfigureAwait(false);
                        await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(_Wait);
                        WebSocketReceiveResult received = await socket.ReceiveAsync(new byte[1024], timeout.Token).ConfigureAwait(false);
                        TestAssert.Equal(WebSocketMessageType.Close, received.MessageType, "The server closed the connection.");
                        TestAssert.Equal(WebSocketCloseStatus.InvalidMessageType, received.CloseStatus ?? WebSocketCloseStatus.Empty, "With status 1003.");
                    }),

                    Case(suiteId, "InputResponsesAreValidatedByType", "Recognizable input responses must match their result schema; unrecognized ones are ignored; handshake-era calls ignore inputResponses", async ct =>
                    {
                        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? seen = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("t", "Tool", new { type = "object" }, args =>
                        {
                            seen = McpToolCallContext.Current?.InputResponses;
                            return "ok";
                        })).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        string meta = McpStrictSuites.Meta("{\"elicitation\":{},\"sampling\":{},\"roots\":{}}");
                        string[] malformed =
                        {
                            "{\"q\":{\"action\":\"maybe\"}}",
                            "{\"q\":{\"action\":\"accept\",\"content\":[1,2]}}",
                            "{\"q\":{\"action\":\"accept\",\"content\":{\"a\":{\"nested\":1}}}}",
                            "{\"q\":{\"role\":\"robot\",\"model\":\"m\",\"content\":{\"type\":\"text\",\"text\":\"x\"}}}",
                            "{\"q\":{\"role\":\"assistant\",\"content\":{\"type\":\"text\",\"text\":\"x\"}}}",
                            "{\"q\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":{\"text\":\"x\"}}}",
                            "{\"q\":{\"roots\":[5]}}",
                            "{\"q\":{\"roots\":[{\"name\":\"x\"}]}}"
                        };
                        int id = 1;
                        foreach (string bad in malformed)
                        {
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id++ + ",\"method\":\"tools/call\",\"params\":{\"name\":\"t\",\"arguments\":{},\"inputResponses\":" + bad + ",\"requestState\":\"s\"," + meta + "}}").ConfigureAwait(false);
                            TestAssert.Equal(-32602, McpStrictSuites.Next(client).Get("error").Get("code").Int(), $"Rejected: {bad}");
                        }

                        string valid = "{\"e\":{\"action\":\"accept\",\"content\":{\"s\":\"x\",\"n\":1,\"b\":true,\"l\":[\"a\"]}},\"s\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}]},\"r\":{\"roots\":[{\"uri\":\"file:///a\"}]},\"zz\":\"ignored\",\"yy\":{\"foo\":1}}";
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":100,\"method\":\"tools/call\",\"params\":{\"name\":\"t\",\"arguments\":{},\"inputResponses\":" + valid + ",\"requestState\":\"s\"," + meta + "}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("result"), "Valid responses with unrecognized extras are accepted.");
                        TestAssert.True(seen != null && seen.Count == 3 && !seen.ContainsKey("zz") && !seen.ContainsKey("yy"), "The handler sees only the recognized responses.");

                        using RawLineClient legacy = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await legacy.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"t\",\"arguments\":{},\"inputResponses\":{\"q\":5}}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(legacy).Has("result"), "A handshake-era call ignores inputResponses, which its revision does not define.");
                    }),

                    Case(suiteId, "HandlerResultTypesAreChecked", "Under 2026-07-28 a tool result with an unsupported resultType, or an incomplete input_required, is -32603", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("task", "Task", new { type = "object" }, args => new McpToolCallResult { ResultType = "task", Content = new List<object> { new McpTextContent { Text = "x" } } });
                            s.RegisterTool("bare", "Bare input required", new { type = "object" }, args => new McpToolCallResult { ResultType = "input_required", Content = new List<object> { new McpTextContent { Text = "x" } } });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        foreach (string tool in new[] { "task", "bare" })
                        {
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"" + tool + "\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}," + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                            TestAssert.Equal(-32603, McpStrictSuites.Next(client).Get("error").Get("code").Int(), $"The {tool} result is refused.");
                        }
                    }),

                    Case(suiteId, "ApplicationErrorsFitTheStatelessSchema", "Under 2026-07-28, -32021 and -32022 carry their required data and reserved codes become -32603", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterMethod("custom/cap", (Func<RpcParameters?, object>)(args => throw new McpProtocolException(-32021, "Needs roots.", new { foo = 1 })));
                            s.RegisterMethod("custom/version", (Func<RpcParameters?, object>)(args => throw new McpProtocolException(-32022, "Unsupported.")));
                            s.RegisterMethod("custom/url", (Func<RpcParameters?, object>)(args => throw new McpProtocolException(-32042, "URL elicitation required.")));
                            s.RegisterMethod("custom/session", (Func<RpcParameters?, object>)(args => throw McpProtocolException.SessionNotFound()));
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        string meta = McpStrictSuites.Meta("{}");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"custom/cap\",\"params\":{" + meta + "}}").ConfigureAwait(false);
                        JsonProbe cap = McpStrictSuites.Next(client).Get("error");
                        TestAssert.True(cap.Get("code").Int() == -32021 && cap.Get("data").Has("requiredCapabilities"), "-32021 data carries requiredCapabilities.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"custom/version\",\"params\":{" + meta + "}}").ConfigureAwait(false);
                        JsonProbe version = McpStrictSuites.Next(client).Get("error");
                        TestAssert.True(version.Get("data").Has("supported") && version.Get("data").Has("requested"), "-32022 data carries supported and requested.");
                        foreach (string method in new[] { "custom/url", "custom/session" })
                        {
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"" + method + "\",\"method\":\"" + method + "\",\"params\":{" + meta + "}}").ConfigureAwait(false);
                            TestAssert.Equal(-32603, McpStrictSuites.Next(client).Get("error").Get("code").Int(), $"A reserved code from {method} becomes -32603.");
                        }
                    }),

                    Case(suiteId, "SubscriptionsListenStreamsRequestedNotifications", "subscriptions/listen acknowledges first, delivers only requested notifications tagged with its ID, ends silently on cancel, and gracefully on stop", async ct =>
                    {
                        TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("t", "Tool", new { type = "object" }, args => "ok")).ConfigureAwait(false);
                        McpTcpServer server = (McpTcpServer)fixture.Server;
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        string meta = McpStrictSuites.Meta("{}");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"sub\",\"method\":\"subscriptions/listen\",\"params\":{\"notifications\":{\"toolsListChanged\":true,\"resourceSubscriptions\":[\"r://x\"]}," + meta + "}}").ConfigureAwait(false);
                        JsonProbe ack = McpStrictSuites.Next(client);
                        TestAssert.Equal("notifications/subscriptions/acknowledged", ack.Get("method").String(), "The acknowledgment comes first.");
                        TestAssert.Equal("sub", ack.Get("params").Get("_meta").Get(McpProtocol.MetaSubscriptionIdKey).String(), "It carries the subscription ID.");
                        TestAssert.True(ack.Get("params").Get("notifications").Get("toolsListChanged").Bool(), "The requested list change is honored.");
                        TestAssert.False(ack.Get("params").Get("notifications").Has("promptsListChanged"), "Nothing unrequested is acknowledged.");

                        server.RegisterPrompt("p", "Prompt", null, args => new McpGetPromptResult());
                        server.RegisterTool("u", "Another", new { type = "object" }, args => "ok");
                        await server.NotifyResourceUpdatedAsync("r://y", ct).ConfigureAwait(false);
                        await server.NotifyResourceUpdatedAsync("r://x", ct).ConfigureAwait(false);
                        // Automatic list changes are sent asynchronously, so the two notifications may arrive in either order.
                        List<JsonProbe> delivered = new List<JsonProbe> { McpStrictSuites.Next(client), McpStrictSuites.Next(client) };
                        JsonProbe? listChanged = delivered.FirstOrDefault(message => message.Get("method").String() == "notifications/tools/list_changed");
                        JsonProbe? updated = delivered.FirstOrDefault(message => message.Get("method").String() == "notifications/resources/updated");
                        TestAssert.True(listChanged != null, "The tool list change arrives.");
                        TestAssert.Equal("sub", listChanged!.Get("params").Get("_meta").Get(McpProtocol.MetaSubscriptionIdKey).String(), "Tagged with the subscription ID.");
                        TestAssert.True(updated != null && updated.Get("params").Get("uri").String() == "r://x", "Only the subscribed resource's update arrives.");
                        TestAssert.True(client.Receive(_Quiet) == null, "The prompt change and the unsubscribed resource send nothing.");

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"sub2\",\"method\":\"subscriptions/listen\",\"params\":{\"notifications\":{\"toolsListChanged\":true}," + meta + "}}").ConfigureAwait(false);
                        TestAssert.Equal("sub2", McpStrictSuites.Next(client).Get("params").Get("_meta").Get(McpProtocol.MetaSubscriptionIdKey).String(), "A second subscription is acknowledged.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"sub\"}}").ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        server.RegisterTool("v", "Third", new { type = "object" }, args => "ok");
                        JsonProbe onlySecond = McpStrictSuites.Next(client);
                        TestAssert.Equal("sub2", onlySecond.Get("params").Get("_meta").Get(McpProtocol.MetaSubscriptionIdKey).String(), "The cancelled subscription sends nothing more, and got no response.");
                        TestAssert.True(client.Receive(_Quiet) == null, "Nothing else arrives.");

                        await fixture.DisposeAsync().ConfigureAwait(false);
                        string? closing = client.Receive(_Wait);
                        TestAssert.True(closing != null && TestJson.ParseRoot(closing).Get("id").String() == "sub2" && TestJson.ParseRoot(closing).Has("result"), $"Stopping the server ends the subscription with a completion result: {closing}");
                    }),

                    Case(suiteId, "SubscriptionsListenIsStatelessOnly", "subscriptions/listen on a handshake-era session is -32601", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"subscriptions/listen\",\"params\":{\"notifications\":{\"toolsListChanged\":true}}}").ConfigureAwait(false);
                        TestAssert.Equal(-32601, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "The method does not exist in the handshake revisions.");
                    }),

                    Case(suiteId, "HttpClientListens", "McpHttpClient.ListenAsync receives the acknowledgment and requested notifications over Streamable HTTP until cancelled", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects statelessly.");
                        using BlockingCollection<string> received = new BlockingCollection<string>();
                        client.NotificationReceived += (sender, notification) => received.Add(notification.Method);
                        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        Task<JsonRpcResponse> listening = client.ListenAsync(new McpSubscriptionFilter { ToolsListChanged = true }, stop.Token);
                        TestAssert.Equal("notifications/subscriptions/acknowledged", Take(received), "The acknowledgment arrives.");
                        fixture.Server.RegisterTool("late", "Late", new { type = "object" }, args => "ok");
                        TestAssert.Equal("notifications/tools/list_changed", Take(received), "The list change arrives on the stream.");
                        stop.Cancel();
                        bool cancelled = false;
                        try
                        {
                            await listening.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                        }

                        TestAssert.True(cancelled, "Cancelling ends the subscription.");
                    }),

                    Case(suiteId, "HttpHeaderNumbersCompareExactly", "An Mcp-Param integer header equal to 42 only after rounding does not match 42", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("n", "Numbers", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"integer\",\"x-mcp-header\":\"N\"}}}"), args => "ok")).ConfigureAwait(false);
                        foreach (KeyValuePair<string, HttpStatusCode> header in new Dictionary<string, HttpStatusCode> { { "42.00000000000000000000000000001", HttpStatusCode.BadRequest }, { "42.000", HttpStatusCode.OK }, { "4.2e1", HttpStatusCode.OK } })
                        {
                            Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "name", "n" }, { "arguments", new { n = 42 } }, { "_meta", McpHttpTestRequests.StatelessMeta() } };
                            Dictionary<string, string> headers = new Dictionary<string, string>
                            {
                                { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                                { McpProtocol.MethodHeader, "tools/call" },
                                { McpProtocol.NameHeader, "n" },
                                { "Mcp-Param-N", header.Key }
                            };
                            RpcResult result = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/call", 1, parameters), headers, true, ct).ConfigureAwait(false);
                            TestAssert.Equal(header.Value, result.StatusCode, $"{header.Key}: {result.Body}");
                        }
                    }),

                    Case(suiteId, "HttpSessionLimits", "A client over MaxSessionsPerClient loses its least recently active session; a server at MaxSessions refuses initialize with 503", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => { s.MaxSessionsPerClient = 2; s.MaxSessions = 3; }).ConfigureAwait(false);
                        string first = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        string second = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        string third = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        RpcResult oldest = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 1, null, first, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, oldest.StatusCode, "The oldest session was ended.");
                        RpcResult newest = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 2, null, third, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, newest.StatusCode, "The newest session works.");
                        TestAssert.True(second != third, "Sessions are distinct.");

                        await using HttpMcpTestServerFixture full = await HttpMcpTestServerFixture.StartAsync(ct, s => s.MaxSessions = 1).ConfigureAwait(false);
                        await McpHttpTestRequests.InitializeAsync(full, "2025-11-25", null, ct).ConfigureAwait(false);
                        RpcResult refused = await McpHttpTestRequests.InitializeAsync(full, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode, "A full server refuses new sessions.");
                    }),

                    Case(suiteId, "HttpErrorIdsFollowTheRevision", "An id that is not a string or integer is never echoed; an unreadable id is null before 2025-11-25 and omitted from then on", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult objectId = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"id\":{\"a\":1},\"method\":\"tools/list\"}", new Dictionary<string, string>(), true, ct).ConfigureAwait(false);
                        TestAssert.True(objectId.Root.Has("id") && objectId.Root.Get("id").IsNull, $"A header-less (2025-03-26) error has id null, never the object: {objectId.Body}");
                        RpcResult modern = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"tools/list\"}", new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, "2025-11-25" } }, true, ct).ConfigureAwait(false);
                        TestAssert.False(modern.Root.Has("id"), $"From 2025-11-25 the unreadable id is omitted: {modern.Body}");
                    }),

                    Case(suiteId, "PatternBudgetIsPerValue", "All the pattern matches of one value share one step budget, so many strings cost no more than one of the same size", async ct =>
                    {
                        const string schema = "{\"type\":\"object\",\"properties\":{\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"not\":{\"pattern\":\"[a-z]+\\\\d\"}}}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("tags", "Tags", McpStrictSuites.Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        string one = "\"" + new string('a', 7000) + "\"";
                        string arguments = "{\"tags\":[" + String.Join(",", Enumerable.Repeat(one, 142)) + "]}";
                        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                        bool rejected = await McpStrictSuites.IsErrorAsync(client, "tags", arguments).ConfigureAwait(false);
                        TestAssert.True(rejected, "The shared budget runs out, so the value fails validation.");
                        TestAssert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"It stays within one value's budget: {elapsed.Elapsed}.");
                    }),

                    Case(suiteId, "NumbersHaveBoundedExponents", "A number whose exponent has more than 1,000 digits fails validation; a schema with one, or with a multipleOf of more than 1,000 digits, is rejected; multipleOf on a million-digit value is fast", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("n", "Numbers", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"m\":{\"minimum\":1},\"k\":{\"multipleOf\":7}}}"), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"m\":1e" + new string('9', 1001) + "}").ConfigureAwait(false), "A 1,001-digit exponent is not supported.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", "{\"m\":1e" + new string('9', 1000) + "}").ConfigureAwait(false), "A 1,000-digit exponent compares exactly.");
                        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", "{\"k\":7" + new string('0', 1000000) + "}").ConfigureAwait(false), "7 followed by a million zeros is a multiple of 7.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"k\":1" + new string('0', 1000000) + "}").ConfigureAwait(false), "1 followed by a million zeros is not.");
                        TestAssert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"multipleOf on huge values is fast: {elapsed.Elapsed}.");

                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("big", "Big exponent", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"x\":{\"maximum\":1e" + new string('9', 1001) + "}}}"), args => "x"), "A schema bound with a 1,001-digit exponent is rejected.");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("div", "Big divisor", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"x\":{\"multipleOf\":" + new string('7', 1001) + "}}}"), args => "x"), "A multipleOf with 1,001 digits is rejected.");
                    }),

                    Case(suiteId, "DeepArgumentsAreParsed", "Tool arguments nested 150 levels deep are parsed and validated (the JSON reader allows 256 levels)", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("deep", "Deep", new { type = "object" }, args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        string nested = String.Concat(Enumerable.Repeat("{\"a\":", 150)) + "1" + new string('}', 150);
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "deep", "{\"x\":" + nested + "}").ConfigureAwait(false), "Deep arguments reach the tool.");
                    }),

                    Case(suiteId, "HttpMethodNotAllowedListsMethods", "A 405 carries an Allow header", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpRequestMessage put = new HttpRequestMessage(HttpMethod.Put, fixture.BaseUrl + "/mcp/") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                        using HttpResponseMessage response = await fixture.Client.SendAsync(put, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode, "PUT is not allowed.");
                        TestAssert.True(response.Content.Headers.Allow.Contains("POST") || (response.Headers.TryGetValues("Allow", out IEnumerable<string>? allow) && allow.Any(value => value.Contains("POST", StringComparison.Ordinal))), "The Allow header lists POST.");
                    }),
                });
        }

        private static string? Take(BlockingCollection<string> received)
        {
            return received.TryTake(out string? item, _Wait) ? item : null;
        }

        // The next response whose raw id is the given JSON text, skipping notifications and server requests.
        private static JsonProbe? NextWithId(IMcpRawChannel channel, string idJson)
        {
            DateTime deadline = DateTime.UtcNow + _Wait;
            while (DateTime.UtcNow < deadline)
            {
                string? line = channel.Receive(deadline - DateTime.UtcNow);
                if (line == null) return null;
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(line);
                System.Text.Json.JsonElement root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object || root.TryGetProperty("method", out System.Text.Json.JsonElement _)) continue;
                if (root.TryGetProperty("id", out System.Text.Json.JsonElement id) && id.GetRawText() == idJson) return TestJson.ParseRoot(line);
            }

            return null;
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "closeout", "conformance" });
        }
    }
}
