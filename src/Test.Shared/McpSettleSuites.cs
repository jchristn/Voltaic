namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the requirements closed in v2.1.13: a cancellation for an unknown ID is ignored, a cancellation read
    /// while a batch runs drops its elements' responses (server and client), HTTP error bodies never carry a null id,
    /// the rate limits apply per client across sessions and connections, queued notifications do not keep a session
    /// alive, the HTTP client starts a new session after a 404 on a cancellation, WebSocket MaxMessageSize is enforced,
    /// InitializeResult is cleared on reconnect, input responses are validated, and only the MRTR methods may ask for
    /// input.
    /// </summary>
    public static class McpSettleSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Cases.
        /// </summary>
        public static TestSuiteDescriptor Cases()
        {
            const string suiteId = "Mcp.Settle";
            return new TestSuiteDescriptor(
                suiteId,
                "Cancellation, limits, sessions, message size, and MRTR validation",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "UnknownIdCancellationIsIgnored", "A cancellation for an ID the server has not seen is ignored, so a later request with that ID is answered", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":50}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"s-51\"}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":50,\"method\":\"tools/list\"}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Get("result").Has("tools"), "The numeric ID is answered.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"s-51\",\"method\":\"tools/list\",\"params\":{" + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Get("result").Has("tools"), "The string ID is answered on the stateless revision too.");
                    }),

                    Case(suiteId, "BatchCancellationDropsElements", "A cancellation read while a 2025-03-26 batch runs drops the responses of the elements it names, including refused ones", async ct =>
                    {
                        using ManualResetEventSlim release = new ManualResetEventSlim(false);
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("gate", "Waits", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await Task.Run(() => release.Wait(_Wait)).ConfigureAwait(false);
                                return "open";
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-03-26", ct).ConfigureAwait(false);
                        await client.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"g\",\"method\":\"tools/call\",\"params\":{\"name\":\"gate\",\"arguments\":{}}},"
                            + "{\"jsonrpc\":\"2.0\",\"id\":\"i\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}},"
                            + "{\"jsonrpc\":\"2.0\",\"id\":\"s\",\"method\":\"tools/list\",\"params\":{" + McpStrictSuites.Meta("{}") + "}},"
                            + "{\"jsonrpc\":\"2.0\",\"id\":\"p\",\"method\":\"ping\"}]").ConfigureAwait(false);
                        foreach (string id in new[] { "i", "s", "p" })
                        {
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"" + id + "\"}}").ConfigureAwait(false);
                        }

                        // Notifications are handled in order, so once this ping is answered the cancellations were read.
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"marker\",\"method\":\"ping\"}").ConfigureAwait(false);
                        TestAssert.Equal("marker", McpStrictSuites.Next(client).Get("id").String(), "The batch is still running.");
                        release.Set();
                        JsonProbe batch = McpStrictSuites.Next(client);
                        TestAssert.Equal(1, batch.Length, $"Only the uncancelled element is answered: {batch}");
                        TestAssert.Equal("g", batch[0].Get("id").String(), "The gate call is answered.");
                    }),

                    Case(suiteId, "ClientBatchCancellationDropsElements", "A client drops the responses of batch elements the server cancelled while the batch ran, including ones it would reject", async ct =>
                    {
                        using ManualResetEventSlim release = new ManualResetEventSlim(false);
                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0, ProtocolVersion = "2025-03-26" };
                        client.RegisterRequestHandler("custom/gate", async (RpcParameters? args, CancellationToken token) =>
                        {
                            await Task.Run(() => release.Wait(_Wait)).ConfigureAwait(false);
                            return new Dictionary<string, object?>();
                        });
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client connects.");
                        await accepting.ConfigureAwait(false);
                        await peer.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"g\",\"method\":\"custom/gate\"},{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"unknown/method\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}]", ct).ConfigureAwait(false);
                        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":1}}", ct).ConfigureAwait(false);
                        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2}}", ct).ConfigureAwait(false);
                        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"marker\",\"method\":\"ping\"}", ct).ConfigureAwait(false);
                        string? marker = await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false);
                        TestAssert.True(marker != null && TestJson.ParseRoot(marker).Get("id").String() == "marker", $"The batch is still running: {marker}");
                        release.Set();
                        string? line = await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false);
                        TestAssert.True(line != null, "The batch is answered.");
                        JsonProbe batch = TestJson.ParseRoot(line!);
                        TestAssert.Equal(1, batch.Length, $"Only the uncancelled element is answered: {line}");
                        TestAssert.Equal("g", batch[0].Get("id").String(), "The gate request is answered.");
                    }),

                    Case(suiteId, "RejectedNotificationErrorHasNoId", "A stateless notification rejected for an Mcp-Method mismatch gets a 400 error body without an id", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "requestId", 1 }, { "_meta", McpHttpTestRequests.StatelessMeta() } };
                        Dictionary<string, string> headers = new Dictionary<string, string>
                        {
                            { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                            { McpProtocol.MethodHeader, "tools/list" }
                        };
                        RpcResult result = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("notifications/cancelled", null, parameters), headers, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, result.StatusCode, "The mismatch is rejected.");
                        TestAssert.False(result.Root.Has("id"), $"The error carries no id: {result.Body}");
                        TestAssert.Equal(-32020, result.Error.Get("code").Int(), "The header mismatch code is used.");
                    }),

                    Case(suiteId, "RateLimitsSpanSessions", "Every HTTP session of one client, and every TCP connection from one address, shares the tool call limit", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s =>
                        {
                            s.RateLimits.ToolCallsPerSecond = 3;
                            s.RegisterTool("t", "Tool", new { type = "object" }, args => "ok");
                        }).ConfigureAwait(false);
                        int limited = 0;
                        for (int session = 0; session < 3; session++)
                        {
                            string id = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                            for (int call = 0; call < 3; call++)
                            {
                                RpcResult result = await McpHttpTestRequests.SendAsync(fixture, "tools/call", call + 10, new { name = "t", arguments = new { } }, id, "2025-11-25", null, ct).ConfigureAwait(false);
                                if (result.Result.Has("isError") && result.Result.Get("isError").Bool()) limited++;
                            }
                        }

                        TestAssert.True(limited > 0, "Nine calls over three sessions exceed one client's limit of three per second.");

                        await using TcpJsonRpcFixture tcp = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RateLimits.ToolCallsPerSecond = 3;
                            s.RegisterTool("t", "Tool", new { type = "object" }, args => "ok");
                        }).ConfigureAwait(false);
                        int tcpLimited = 0;
                        for (int connection = 0; connection < 3; connection++)
                        {
                            using RawLineClient client = await McpStrictSuites.InitializedAsync(tcp, ct).ConfigureAwait(false);
                            for (int call = 0; call < 3; call++)
                            {
                                if (await McpStrictSuites.IsErrorAsync(client, "t", "{}").ConfigureAwait(false)) tcpLimited++;
                            }
                        }

                        TestAssert.True(tcpLimited > 0, "Nine calls over three TCP connections exceed one address's limit.");
                    }),

                    Case(suiteId, "CancellationAfterSessionLossStartsNewSession", "When a cancellation gets 404, McpHttpClient starts a new session without resending the cancellation", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        string lost = client.SessionId!;
                        await McpHttpTestRequests.DeleteSessionAsync(fixture, lost, null, ct).ConfigureAwait(false);
                        await client.NotifyAsync("notifications/cancelled", new { requestId = 99 }, 0, ct).ConfigureAwait(false);
                        TestAssert.True(!String.IsNullOrEmpty(client.SessionId) && client.SessionId != lost, $"A new session was started: {client.SessionId}");
                    }),

                    Case(suiteId, "WebSocketServerEnforcesMaxMessageSize", "A WebSocket message larger than MaxMessageSize, sent in small frames, closes the connection with 1009", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, s => s.MaxMessageSize = 4096).ConfigureAwait(false);
                        using ClientWebSocket socket = new ClientWebSocket();
                        await socket.ConnectAsync(new Uri(fixture.Url), ct).ConfigureAwait(false);
                        byte[] frame = Encoding.UTF8.GetBytes(new string(' ', 2000));
                        try
                        {
                            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\""), WebSocketMessageType.Text, false, ct).ConfigureAwait(false);
                            for (int i = 0; i < 3; i++)
                            {
                                await socket.SendAsync(frame, WebSocketMessageType.Text, false, ct).ConfigureAwait(false);
                            }

                            await socket.SendAsync(Encoding.UTF8.GetBytes("\"}}"), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                        }
                        catch (WebSocketException)
                        {
                        }

                        byte[] buffer = new byte[8192];
                        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(_Wait);
                        WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
                        TestAssert.Equal(WebSocketMessageType.Close, received.MessageType, "The server closed the connection.");
                        TestAssert.Equal(WebSocketCloseStatus.MessageTooBig, received.CloseStatus ?? WebSocketCloseStatus.Empty, "With status 1009.");
                    }),

                    Case(suiteId, "WebSocketClientEnforcesMaxMessageSize", "McpWebsocketsClient closes the connection when a server message exceeds its MaxMessageSize", async ct =>
                    {
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, s => s.RegisterTool("big", "Big result", new { type = "object" }, args => new string('x', 20000))).ConfigureAwait(false);
                        using McpWebsocketsClient client = new McpWebsocketsClient { MaxMessageSize = 4096, PingIntervalMs = 0 };
                        TestAssert.True(await client.ConnectAsync(fixture.Url, ct).ConfigureAwait(false), "The client connects.");
                        bool failed = false;
                        try
                        {
                            await client.CallAsync<object?>("tools/call", new { name = "big", arguments = new { } }, 5000, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is InvalidOperationException || ex is OperationCanceledException || ex is WebSocketException)
                        {
                            failed = true;
                        }

                        TestAssert.True(failed, "The oversized response is not accepted.");
                        TestAssert.False(client.IsConnected, "The client closed the connection.");
                    }),

                    Case(suiteId, "InitializeResultIsClearedOnReconnect", "InitializeResult describes the current connection only", async ct =>
                    {
                        await using TcpJsonRpcFixture first = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using McpTcpClient client = new McpTcpClient { PingIntervalMs = 0 };
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", first.Port, ct).ConfigureAwait(false), "The client connects.");
                        TestAssert.True(client.InitializeResult.HasValue, "The handshake result is kept.");
                        client.Disconnect();

                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        client.AutoInitialize = false;
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client reconnects.");
                        await accepting.ConfigureAwait(false);
                        TestAssert.False(client.InitializeResult.HasValue, "The previous server's result is gone.");

                        await using WebSocketMcpFixture ws = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpWebsocketsClient wsClient = await ws.ConnectClientAsync(ct).ConfigureAwait(false);
                        TestAssert.True(wsClient.InitializeResult.HasValue, "The WebSocket handshake result is kept.");
                        wsClient.Disconnect();
                        wsClient.AutoInitialize = false;
                        TestAssert.True(await wsClient.ConnectAsync(ws.Url, ct).ConfigureAwait(false), "The WebSocket client reconnects.");
                        TestAssert.False(wsClient.InitializeResult.HasValue, "The WebSocket client's previous result is gone.");
                    }),

                    Case(suiteId, "InputResponsesAreValidated", "Recognizable inputResponses values must match their result type; a wrong-typed requestState gets -32602 without .NET details", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("t", "Tool", new { type = "object" }, args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        string meta = McpStrictSuites.Meta("{\"elicitation\":{}}");
                        foreach (string bad in new[] { "{\"q\":{\"action\":\"maybe\"}}", "{\"q\":{\"roots\":5}}" })
                        {
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"t\",\"arguments\":{},\"inputResponses\":" + bad + ",\"requestState\":\"s\"," + meta + "}}").ConfigureAwait(false);
                            TestAssert.Equal(-32602, McpStrictSuites.Next(client).Get("error").Get("code").Int(), $"Rejected: {bad}");
                        }

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"t\",\"arguments\":{},\"inputResponses\":{\"q\":{\"action\":\"accept\",\"content\":{\"a\":1}},\"r\":{\"roots\":[]}},\"requestState\":\"s\"," + meta + "}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("result"), "Valid input responses are accepted.");

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"t\",\"arguments\":{},\"requestState\":5," + meta + "}}").ConfigureAwait(false);
                        JsonProbe error = McpStrictSuites.Next(client).Get("error");
                        TestAssert.Equal(-32602, error.Get("code").Int(), "A wrong-typed requestState is invalid params.");
                        string message = error.Get("message").String() ?? String.Empty;
                        TestAssert.False(message.Contains("System.", StringComparison.Ordinal) || message.Contains("BytePosition", StringComparison.Ordinal), $"No .NET details: {message}");
                    }),

                    Case(suiteId, "OnlyMrtrMethodsRequestInput", "An application method that returns an input-required result on 2026-07-28 gets -32603; -32021 always carries requiredCapabilities", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterMethod("custom/ask", args => new McpInputRequiredResult { RequestState = "s" });
                            s.RegisterTool("needs", "Needs a capability", new { type = "object" }, (RpcParameters? args, CancellationToken token) => throw new McpProtocolException(-32021, "Needs roots."));
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"custom/ask\",\"params\":{" + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.Equal(-32603, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "Only tools/call, resources/read, and prompts/get may request input.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"needs\",\"arguments\":{}," + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        JsonProbe error = McpStrictSuites.Next(client).Get("error");
                        TestAssert.Equal(-32021, error.Get("code").Int(), "The capability error is sent.");
                        TestAssert.True(error.Get("data").Has("requiredCapabilities"), "Its data carries requiredCapabilities.");
                    }),

                    Case(suiteId, "PromptArgumentSurrogateIsInvalidParams", "A prompt argument with an unpaired surrogate gets -32602, not an internal error", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterPrompt("p", "Prompt", new[] { new McpPromptArgument { Name = "x" } }, args => new McpGetPromptResult())).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"prompts/get\",\"params\":{\"name\":\"p\",\"arguments\":{\"x\":\"\\ud800\"}}}").ConfigureAwait(false);
                        string line = client.Receive(_Wait) ?? String.Empty;
                        TestAssert.Equal(-32602, TestJson.ParseRoot(line).Get("error").Get("code").Int(), $"Invalid Unicode text is invalid params: {line}");
                    }),

                    Case(suiteId, "SurrogatesOutsideTheSchemaAreRejected", "A tool argument string with an unpaired surrogate is rejected also where the schema does not look", async ct =>
                    {
                        bool ran = false;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("t", "Tool", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"s\":{\"type\":\"string\"}}}"), args => { ran = true; return "ok"; })).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "t", "{\"o\":[\"\\udfff\"]}").ConfigureAwait(false), "A nested value is rejected.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "t", "{\"\\ud800\":1}").ConfigureAwait(false), "A property name is rejected.");
                        TestAssert.False(ran, "The tool did not run.");
                    }),

                    Case(suiteId, "UriTemplatePrefixesFollowRfc6570", "A :n prefix counts decoded characters, applies to query expressions, and cannot be combined with explode", async ct =>
                    {
                        Dictionary<string, string>? seen = null;
                        Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<McpReadResourceResult>> read = (uri, variables, token) =>
                        {
                            seen = new Dictionary<string, string>(variables);
                            return Task.FromResult(new McpReadResourceResult { Contents = new List<object> { new McpTextResourceContents { Uri = uri, MimeType = "text/plain", Text = "ok" } } });
                        };
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterResourceTemplate(new McpResourceTemplate { UriTemplate = "pre/{v:3}", Name = "p", MimeType = "text/plain" }, read);
                            s.RegisterResourceTemplate(new McpResourceTemplate { UriTemplate = "q{?a:2,b}", Name = "q", MimeType = "text/plain" }, read);
                        }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/read\",\"params\":{\"uri\":\"pre/%C3%A9%C3%A9x\"}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("result"), "Three encoded characters fit a :3 prefix.");
                        TestAssert.Equal("\u00e9\u00e9x", seen?["v"], "The decoded value is extracted.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/read\",\"params\":{\"uri\":\"pre/%C3%A9%C3%A9xy\"}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("error"), "Four characters do not.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/read\",\"params\":{\"uri\":\"q?a=xyz&b=1\"}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("error"), "A query value longer than its prefix does not match.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"resources/read\",\"params\":{\"uri\":\"q?a=xy&b=1\"}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("result"), "A query value within its prefix matches.");

                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        TestAssert.Throws<ArgumentException>(() => server.RegisterResourceTemplate(new McpResourceTemplate { UriTemplate = "ex/{v:2*}", Name = "x", MimeType = "text/plain" }, read), "A prefix with explode is not a valid template.");
                    }),

                    Case(suiteId, "LongNumbersValidateInLinearTime", "Numbers with 200,000 trailing zeros validate quickly and exactly under minimum, enum, and uniqueItems", async ct =>
                    {
                        const string schema = "{\"type\":\"object\",\"properties\":{\"m\":{\"minimum\":1},\"e\":{\"enum\":[1,2]},\"u\":{\"uniqueItems\":true},\"x\":{\"maximum\":1}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("n", "Numbers", McpStrictSuites.Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        string zeros = new string('0', 200000);
                        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", "{\"m\":1" + zeros + "}").ConfigureAwait(false), "A huge integer is above the minimum.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"e\":1" + zeros + "}").ConfigureAwait(false), "A huge integer is not in the enum.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", "{\"e\":1." + zeros + "}").ConfigureAwait(false), "1.000... equals 1.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"x\":1." + zeros + "1}").ConfigureAwait(false), "1.000...1 is above the maximum.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"u\":[1" + zeros + ",1e200000]}").ConfigureAwait(false), "Equal huge numbers are not unique.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", "{\"u\":[1e400,1e401,-1e400,1e-400]}").ConfigureAwait(false), "Distinct huge and tiny numbers are unique.");
                        TestAssert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Validation stayed fast: {elapsed.Elapsed}.");
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "settle", "conformance" });
        }
    }
}
