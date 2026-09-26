namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers MCP requirements at the edges of the protocol: UTF-8 split across WebSocket reads, cancellation that
    /// arrives before its request or while a handler keeps reporting progress, progress-token collisions between
    /// clients, clients cancelling server requests, null results, typed-params errors, structuredContent shape, RFC 6570
    /// template forms, nested client capabilities for input requests, the stateless <c>server/discover</c> rule, and on
    /// HTTP the capped <c>-32022</c> list, <c>Mcp-Param</c> validation for any value type, <c>resource_metadata</c> in
    /// every challenge, and resumable POST response streams.
    /// </summary>
    public static class McpSpecEdgeSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Stream transport edge cases.
        /// </summary>
        public static TestSuiteDescriptor Streams()
        {
            const string suiteId = "McpStreams.EdgeCases";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP edge-case requirements on stream transports",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "WebSocketKeepsUtf8SplitAcrossReads", "Raw UTF-8 messages larger than the receive buffer arrive intact at McpWebsocketsServer and McpWebsocketsClient, although reads split multi-byte characters", async ct =>
                    {
                        // Voltaic escapes non-ASCII when it serializes, so raw peers send the literal UTF-8 other SDKs send.
                        string text = "a" + String.Concat(Enumerable.Repeat("世界", 5000));
                        await using WebSocketMcpFixture fixture = await WebSocketMcpFixture.StartAsync(ct, server =>
                        {
                            server.MaxMessageSize = 4096;
                            server.RegisterTool("echo_text", "Echoes text", new { type = "object", properties = new { text = new { type = "string" } } }, args => args?.GetString("text") ?? "");
                        }).ConfigureAwait(false);
                        using (ClientWebSocket raw = new ClientWebSocket())
                        {
                            await raw.ConnectAsync(new Uri(fixture.Url), ct).ConfigureAwait(false);
                            await SendTextAsync(raw, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw\",\"version\":\"1\"}}}", ct).ConfigureAwait(false);
                            await ReceiveTextAsync(raw, ct).ConfigureAwait(false);
                            await SendTextAsync(raw, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", ct).ConfigureAwait(false);
                            await SendTextAsync(raw, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"echo_text\",\"arguments\":{\"text\":\"" + text + "\"}}}", ct).ConfigureAwait(false);
                            JsonProbe echoed = JsonProbe.Parse(await ReceiveTextAsync(raw, ct).ConfigureAwait(false));
                            TestAssert.Equal(text, echoed.Get("result").Get("content")[0].Get("text").String(), "The server decoded the split characters correctly.");
                        }

                        int port = TestPorts.GetFreePort();
                        using HttpListener listener = new HttpListener();
                        listener.Prefixes.Add($"http://localhost:{port}/");
                        listener.Start();
                        Task serve = Task.Run(async () =>
                        {
                            HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                            HttpListenerWebSocketContext socket = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                            JsonProbe initialize = JsonProbe.Parse(await ReceiveTextAsync(socket.WebSocket, ct).ConfigureAwait(false));
                            await SendTextAsync(socket.WebSocket, "{\"jsonrpc\":\"2.0\",\"id\":" + initialize.Get("id").Int() + ",\"result\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"serverInfo\":{\"name\":\"raw\",\"version\":\"1\"}}}", ct).ConfigureAwait(false);
                            await ReceiveTextAsync(socket.WebSocket, ct).ConfigureAwait(false);
                            JsonProbe call = JsonProbe.Parse(await ReceiveTextAsync(socket.WebSocket, ct).ConfigureAwait(false));
                            await SendTextAsync(socket.WebSocket, "{\"jsonrpc\":\"2.0\",\"id\":" + call.Get("id").Int() + ",\"result\":{\"text\":\"" + text + "\"}}", ct).ConfigureAwait(false);
                            await Task.Delay(500, ct).ConfigureAwait(false);
                        }, ct);
                        using McpWebsocketsClient client = new McpWebsocketsClient { MaxMessageSize = 4096 };
                        TestAssert.True(await client.ConnectAsync($"ws://localhost:{port}/", ct).ConfigureAwait(false), "The client connects to the raw server.");
                        JsonProbe received = JsonProbe.From(await client.CallAsync<object?>("custom/text", new { }, 15000, ct).ConfigureAwait(false));
                        TestAssert.Equal(text, received.Get("text").String(), "The client decoded the split characters correctly.");
                        await serve.ConfigureAwait(false);
                    }),

                    Case(suiteId, "CancelBeforeRequestSuppressesIt", "A notifications/cancelled that arrives before its request makes the server skip that request and send no response", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"early\"}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"early\",\"method\":\"tools/list\"}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"after\",\"method\":\"ping\"}").ConfigureAwait(false);
                        TestAssert.Equal("after", Next(client).Get("id").String(), "The pre-cancelled request got no response.");
                        TestAssert.True(client.Receive(_Quiet) == null, "Nothing else arrives.");
                    }),

                    Case(suiteId, "NoProgressAfterCancellation", "A handler that keeps reporting progress after its request is cancelled sends nothing more", async ct =>
                    {
                        TaskCompletionSource<bool> finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct, server =>
                        {
                            server.RegisterTool("stubborn", "Ignores cancellation", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                McpToolCallContext call = McpToolCallContext.Current!;
                                await call.ReportProgressAsync(1).ConfigureAwait(false);
                                await Task.Delay(600).ConfigureAwait(false);
                                await call.ReportProgressAsync(2).ConfigureAwait(false);
                                await call.LogAsync("error", "late").ConfigureAwait(false);
                                finished.TrySetResult(true);
                                return "done";
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"stubborn\",\"arguments\":{},\"_meta\":{\"progressToken\":\"p\"}}}").ConfigureAwait(false);
                        TestAssert.Equal("notifications/progress", Next(client).Get("method").String(), "The first progress arrives.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":7}}").ConfigureAwait(false);
                        await finished.Task.WaitAsync(_Wait, ct).ConfigureAwait(false);
                        TestAssert.True(client.Receive(_Quiet) == null, "No progress, log, or response follows the cancellation.");
                    }),

                    Case(suiteId, "ServerProgressNeverGuessesBetweenClients", "Server-level NotifyProgressAsync sends to a handler's own request, and to nobody when two clients use the same token", async ct =>
                    {
                        McpTcpServer? server = null;
                        TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct, s =>
                        {
                            server = s;
                            s.RegisterTool("wait", "Waits", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await release.Task.ConfigureAwait(false);
                                return "done";
                            });
                            s.RegisterTool("self", "Reports through the server", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await s.NotifyProgressAsync(1, 5, null, null, token).ConfigureAwait(false);
                                return "done";
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient first = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        using RawLineClient second = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await first.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await second.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);

                        await first.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"wait\",\"arguments\":{},\"_meta\":{\"progressToken\":1}}}").ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        await second.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"self\",\"arguments\":{},\"_meta\":{\"progressToken\":1}}}").ConfigureAwait(false);
                        TestAssert.Equal("notifications/progress", Next(second).Get("method").String(), "The handler's own client gets its progress.");
                        TestAssert.Equal(1, Next(second).Get("id").Int(), "Then its result.");
                        TestAssert.True(first.Receive(_Quiet) == null, "The other client with the same token gets nothing.");

                        await second.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"wait\",\"arguments\":{},\"_meta\":{\"progressToken\":1}}}").ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        await server!.NotifyProgressAsync(1, 1, null, null, ct).ConfigureAwait(false);
                        TestAssert.True(first.Receive(_Quiet) == null && second.Receive(_Quiet) == null, "An ambiguous token reaches nobody.");
                        release.TrySetResult(true);
                    }),

                    Case(suiteId, "ClientsHonorCancellationOfServerRequests", "When the server cancels a request it sent, the client cancels the handler and sends no response", async ct =>
                    {
                        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                        listener.Start();
                        try
                        {
                            TaskCompletionSource<bool> handlerCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            using McpTcpClient client = new McpTcpClient { AutoInitialize = false };
                            client.RegisterRequestHandler("sampling/createMessage", async (parameters, token) =>
                            {
                                try
                                {
                                    await Task.Delay(10000, token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    handlerCancelled.TrySetResult(true);
                                    throw;
                                }

                                return new { };
                            });
                            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(ct).AsTask();
                            TestAssert.True(await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, ct).ConfigureAwait(false), "The client connects.");
                            using TcpClient server = await accepting.ConfigureAwait(false);
                            NetworkStream stream = server.GetStream();
                            await WriteLineAsync(stream, "{\"jsonrpc\":\"2.0\",\"id\":\"s1\",\"method\":\"sampling/createMessage\",\"params\":{}}", ct).ConfigureAwait(false);
                            await Task.Delay(200, ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"s1\"}}", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, "{\"jsonrpc\":\"2.0\",\"id\":\"s2\",\"method\":\"ping\"}", ct).ConfigureAwait(false);

                            using StreamReader reader = new StreamReader(stream);
                            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeout.CancelAfter(_Wait);
                            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                            TestAssert.Equal("s2", JsonProbe.Parse(line!).Get("id").String(), $"Only the ping is answered: {line}");
                            TestAssert.True(await handlerCancelled.Task.WaitAsync(_Wait, ct).ConfigureAwait(false), "The handler's token was cancelled.");
                        }
                        finally
                        {
                            listener.Stop();
                        }
                    }),

                    Case(suiteId, "NullResultIsEmptyObject", "A method handler that returns null answers with result {}, never a response without result", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct, server => server.RegisterMethod("nothing", (RpcParameters? args, CancellationToken token) => Task.FromResult<object>(null!))).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"nothing\"}").ConfigureAwait(false);
                        JsonProbe response = Next(client);
                        TestAssert.True(response.Has("result") && response.Get("result").IsObject && response.Get("result").Length == 0, "The result is {}.");
                    }),

                    Case(suiteId, "WronglyTypedParamsAreInvalidParams", "Params members of the wrong JSON type are -32602, not internal errors", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct, server => server.RegisterPrompt("p", "Prompt", null, args => new McpGetPromptResult())).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        string[] requests =
                        {
                            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":5}}",
                            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/read\",\"params\":{\"uri\":5}}",
                            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\",\"params\":{\"cursor\":5}}",
                            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"logging/setLevel\",\"params\":{\"level\":1}}",
                            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"completion/complete\",\"params\":{\"ref\":null,\"argument\":{\"name\":\"x\",\"value\":\"\"}}}",
                            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"t\",\"arguments\":{},\"requestState\":5}}"
                        };
                        foreach (string request in requests)
                        {
                            await client.SendAsync(request).ConfigureAwait(false);
                            TestAssert.Equal(-32602, Next(client).Get("error").Get("code").Int(), $"Invalid params: {request}");
                        }
                    }),

                    Case(suiteId, "StructuredContentMustBeAnObjectBefore20260728", "A non-object structuredContent is removed for 2025-11-25 sessions (the text block keeps the data) and kept on 2026-07-28", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct, server => server.RegisterTool("list", "Returns a list", new { type = "object" }, args => McpToolCallResult.FromStructured(new[] { 1, 2 }))).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list\",\"arguments\":{}}}").ConfigureAwait(false);
                        JsonProbe handshake = Next(client).Get("result");
                        TestAssert.False(handshake.Has("structuredContent"), "An array is not a valid 2025-11-25 structuredContent.");
                        TestAssert.Equal("[1,2]", handshake.Get("content")[0].Get("text").String(), "The text block carries the data.");

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"list\",\"arguments\":{}," + StatelessMetaJson("{}") + "}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("result").Get("structuredContent").IsArray, "2026-07-28 allows any JSON value.");
                    }),

                    Case(suiteId, "UriTemplatesCoverRfc6570Forms", "Reserved expansion accepts commas, exploded path segments match, and dotted variable names register", async ct =>
                    {
                        List<string> read = new List<string>();
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct, server =>
                        {
                            server.RegisterResourceTemplate("file:///{+path}", "files", "text/plain", uri => { lock (read) read.Add(uri); return new McpReadResourceResult(); });
                            server.RegisterResourceTemplate("repo://x{/segments*}", "repo", "text/plain", uri => { lock (read) read.Add(uri); return new McpReadResourceResult(); });
                            server.RegisterResourceTemplate("user://{user.name}", "user", "text/plain", uri => { lock (read) read.Add(uri); return new McpReadResourceResult(); });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        foreach (string uri in new[] { "file:///a,b.txt", "repo://x/a/b", "user://jane" })
                        {
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/read\",\"params\":{\"uri\":\"" + uri + "\"}}").ConfigureAwait(false);
                            TestAssert.True(Next(client).Get("result").IsObject, $"{uri} matches its template.");
                        }

                        TestAssert.Equal(3, read.Count, "Every read reached a handler.");
                    }),

                    Case(suiteId, "InputRequestsNeedNestedCapabilities", "URL-mode elicitation needs elicitation.url and tool-enabled sampling needs sampling.tools (-32021 naming them); declared ones pass", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct, server =>
                        {
                            server.RegisterTool("ask_url", "URL elicitation", new { type = "object" }, args => Task.FromResult<object>(InputRequired("elicitation/create", new { mode = "url", message = "Sign in", url = "https://example.com", elicitationId = "e1" })));
                            server.RegisterTool("ask_model", "Tool sampling", new { type = "object" }, args => Task.FromResult<object>(InputRequired("sampling/createMessage", new { messages = new object[0], maxTokens = 10, tools = new[] { new { name = "t", inputSchema = new { type = "object" } } } })));
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"ask_url\",\"arguments\":{}," + StatelessMetaJson("{\"elicitation\":{}}") + "}}").ConfigureAwait(false);
                        JsonProbe urlError = Next(client).Get("error");
                        TestAssert.Equal(-32021, urlError.Get("code").Int(), "Form-only elicitation cannot receive a URL request.");
                        TestAssert.True(urlError.Get("data").Get("requiredCapabilities").Get("elicitation").Has("url"), "The data names elicitation.url.");

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"ask_url\",\"arguments\":{}," + StatelessMetaJson("{\"elicitation\":{\"url\":{}}}") + "}}").ConfigureAwait(false);
                        TestAssert.Equal("input_required", Next(client).Get("result").Get("resultType").String(), "A declared URL mode passes.");

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"ask_model\",\"arguments\":{}," + StatelessMetaJson("{\"sampling\":{}}") + "}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("error").Get("data").Get("requiredCapabilities").Get("sampling").Has("tools"), "Tool-enabled sampling needs sampling.tools.");
                    }),

                    Case(suiteId, "DiscoverRequiresStatelessMeta", "server/discover without a _meta protocol version is invalid params; with one it is served", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\"}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, Next(client).Get("error").Get("code").Int(), "server/discover needs the stateless _meta.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"server/discover\",\"params\":{" + StatelessMetaJson("{}") + "}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("result").Has("supportedVersions"), "With the _meta it is served.");
                    }),

                    Case(suiteId, "StreamClientsRequestOnlyHandshakeVersions", "Setting a stream client's ProtocolVersion to a version initialize cannot negotiate throws", ct =>
                    {
                        using McpTcpClient tcp = new McpTcpClient();
                        using McpClient stdio = new McpClient();
                        using McpWebsocketsClient websocket = new McpWebsocketsClient();
                        TestAssert.Throws<ArgumentException>(() => tcp.ProtocolVersion = McpProtocol.ProtocolVersion20260728, "TCP rejects 2026-07-28.");
                        TestAssert.Throws<ArgumentException>(() => stdio.ProtocolVersion = "1999-01-01", "stdio rejects unknown versions.");
                        TestAssert.Throws<ArgumentException>(() => websocket.ProtocolVersion = McpProtocol.ProtocolVersion20260728, "WebSocket rejects 2026-07-28.");
                        tcp.ProtocolVersion = "2025-03-26";
                        TestAssert.Equal("2025-03-26", tcp.ProtocolVersion, "Handshake versions are accepted.");
                        return Task.CompletedTask;
                    }),
                });
        }

        /// <summary>
        /// Streamable HTTP edge cases.
        /// </summary>
        public static TestSuiteDescriptor Http()
        {
            const string suiteId = "McpHttp.EdgeCases";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP edge-case requirements on Streamable HTTP",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "UnsupportedVersionListsOnlyAcceptedVersions", "The -32022 supported list honors MaximumHandshakeProtocolVersion", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.MaximumHandshakeProtocolVersion = "2025-06-18").ConfigureAwait(false);
                        Dictionary<string, string> headers = new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, "1900-01-01" }, { McpProtocol.MethodHeader, "tools/list" } };
                        RpcResult result = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/list", 1, new { _meta = McpHttpTestRequests.StatelessMeta("1900-01-01") }), headers, true, ct).ConfigureAwait(false);
                        List<string?> supported = result.Error.Get("data").Get("supported").EnumerateArray().Select(v => v.String()).ToList();
                        TestAssert.Equal(-32022, result.Error.Get("code").Int(), $"Unsupported version: {result.Body}");
                        TestAssert.False(supported.Contains("2025-11-25"), $"A version above the cap is not offered: {String.Join(",", supported)}");
                        TestAssert.True(supported.Contains("2025-06-18") && supported.Contains("2026-07-28"), "Accepted versions are listed.");
                    }),

                    Case(suiteId, "ParamHeadersAreValidatedForAnyValueType", "An annotated argument of another JSON type must still be mirrored and match; objects cannot be mirrored", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server => server.RegisterTool("regional", "Regional", Schema("{\"type\":\"object\",\"properties\":{\"region\":{\"type\":\"string\",\"x-mcp-header\":\"Region\"}}}"), args => "ok")).ConfigureAwait(false);
                        RpcResult missing = await CallRegionalAsync(fixture, new { region = 5 }, null, ct).ConfigureAwait(false);
                        RpcResult wrong = await CallRegionalAsync(fixture, new { region = 5 }, "us-west1", ct).ConfigureAwait(false);
                        RpcResult mirrored = await CallRegionalAsync(fixture, new { region = 5 }, "5", ct).ConfigureAwait(false);
                        RpcResult objectValue = await CallRegionalAsync(fixture, new { region = new { a = 1 } }, "x", ct).ConfigureAwait(false);

                        TestAssert.Equal(-32020, missing.Error.Get("code").Int(), "A value without its header is rejected.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode, "A header that differs from a numeric body value is rejected.");
                        TestAssert.Equal(-32020, wrong.Error.Get("code").Int(), "It is HeaderMismatch.");
                        TestAssert.Equal(HttpStatusCode.OK, mirrored.StatusCode, "A mirrored number passes the header check.");
                        TestAssert.True(mirrored.Result.Get("isError").Bool(), "Input validation then reports the wrong type.");
                        TestAssert.Equal(-32020, objectValue.Error.Get("code").Int(), "An object cannot be mirrored.");

                        using McpHttpClient client = new McpHttpClient();
                        client.ClientCapabilities["elicitation"] = new { };
                        TestAssert.True(await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The stateless client connects.");
                        await client.SendStatelessAsync("tools/list", null, null, ct).ConfigureAwait(false);
                        JsonRpcResponse viaClient = await client.SendStatelessAsync("tools/call", new Dictionary<string, object?> { { "name", "regional" }, { "arguments", new { region = 5 } } }, null, ct).ConfigureAwait(false);
                        TestAssert.True(viaClient.Error == null, $"McpHttpClient mirrors a value of another type: {viaClient.Error?.Message}");
                    }),

                    Case(suiteId, "ChallengesAlwaysNameResourceMetadata", "A handler's BearerChallenge and InsufficientScope get resource_metadata added when ProtectedResourceMetadata is set", async ct =>
                    {
                        bool scopeFailure = false;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "http://localhost/mcp", AuthorizationServers = new List<string> { "https://auth.example.com" } };
                            server.AuthenticationHandler = _ => Task.FromResult(scopeFailure ? AuthenticationResult.InsufficientScope("files:read") : AuthenticationResult.BearerChallenge(null, "invalid_token"));
                        }).ConfigureAwait(false);
                        using HttpResponseMessage unauthorized = await PostAsync(fixture, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", ct).ConfigureAwait(false);
                        scopeFailure = true;
                        using HttpResponseMessage forbidden = await PostAsync(fixture, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", ct).ConfigureAwait(false);

                        string challenge = unauthorized.Headers.WwwAuthenticate.ToString();
                        string scopeChallenge = forbidden.Headers.WwwAuthenticate.ToString();
                        TestAssert.True(challenge.Contains("error=\"invalid_token\"") && challenge.Contains("resource_metadata=\""), $"401: {challenge}");
                        TestAssert.True(scopeChallenge.Contains("insufficient_scope") && scopeChallenge.Contains("resource_metadata=\""), $"403: {scopeChallenge}");
                    }),

                    Case(suiteId, "PostStreamsArePrimedAndResumable", "A 2025-11-25 POST response stream starts with a priming event, and after a disconnect GET with Last-Event-ID replays the rest and ends after the response", async ct =>
                    {
                        TaskCompletionSource<bool> dropped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, server =>
                        {
                            server.RegisterTool("long", "Reports progress, then finishes", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                McpToolCallContext call = McpToolCallContext.Current!;
                                await call.ReportProgressAsync(1).ConfigureAwait(false);
                                await dropped.Task.ConfigureAwait(false);
                                await call.ReportProgressAsync(2).ConfigureAwait(false);
                                return "finished";
                            });
                        }).ConfigureAwait(false);
                        string session = (await fixture.InitializeSessionAsync(ct).ConfigureAwait(false))!;

                        string primingId;
                        using (HttpRequestMessage post = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/"))
                        {
                            post.Content = new StringContent(McpHttpTestRequests.BuildBody("tools/call", 9, new { name = "long", arguments = new { }, _meta = new { progressToken = "t" } }), Encoding.UTF8, "application/json");
                            post.Headers.Accept.ParseAdd("application/json");
                            post.Headers.Accept.ParseAdd("text/event-stream");
                            post.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
                            post.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, "2025-11-25");
                            using HttpResponseMessage response = await fixture.Client.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            using StreamReader reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                            List<string> firstEvent = await ReadEventAsync(reader, ct).ConfigureAwait(false);
                            primingId = firstEvent.First(line => line.StartsWith("id:", StringComparison.Ordinal)).Substring(3).Trim();
                            TestAssert.True(firstEvent.Any(line => line.StartsWith("data:", StringComparison.Ordinal) && line.Substring(5).Trim().Length == 0), $"The first event is a priming event: {String.Join("|", firstEvent)}");
                            List<string> progress = await ReadEventAsync(reader, ct).ConfigureAwait(false);
                            TestAssert.True(progress.Any(line => line.StartsWith("id:", StringComparison.Ordinal)) && progress.Any(line => line.Contains("notifications/progress")), "Progress carries an event ID.");
                        }

                        // The connection is gone; the handler continues and its remaining messages are logged.
                        dropped.TrySetResult(true);
                        await Task.Delay(300, ct).ConfigureAwait(false);

                        using HttpRequestMessage resume = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/mcp/");
                        resume.Headers.Accept.ParseAdd("text/event-stream");
                        resume.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
                        resume.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, "2025-11-25");
                        resume.Headers.TryAddWithoutValidation("Last-Event-ID", primingId);
                        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(_Wait);
                        using HttpResponseMessage resumed = await fixture.Client.SendAsync(resume, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                        string body = await resumed.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                        List<JsonProbe> messages = body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal) && line.Length > 6).Select(line => JsonProbe.Parse(line.Substring(5).Trim())).ToList();
                        TestAssert.True(messages.Any(m => m.Has("method") && m.Get("params").Get("progress").Double() == 1), $"The missed progress is replayed: {body}");
                        TestAssert.Equal(9, messages.Last().Get("id").Int(), "The stream ends with the response.");
                        TestAssert.Equal("finished", messages.Last().Get("result").Get("content")[0].Get("text").String(), "The response is the tool result.");
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "edge-cases", "conformance" });
        }

        private static Task<TcpJsonRpcFixture> StartTcpAsync(CancellationToken token, Action<McpTcpServer>? configure = null)
        {
            return TcpJsonRpcFixture.StartMcpTcpAsync(token, configure);
        }

        private static McpInputRequiredResult InputRequired(string method, object parameters)
        {
            return new McpInputRequiredResult
            {
                InputRequests = new Dictionary<string, McpInputRequest> { { "ask", new McpInputRequest { Method = method, Params = parameters } } }
            };
        }

        private static string StatelessMetaJson(string capabilities)
        {
            return "\"_meta\":{\"" + McpProtocol.MetaProtocolVersionKey + "\":\"2026-07-28\",\"" + McpProtocol.MetaClientInfoKey + "\":{\"name\":\"edge\",\"version\":\"1\"},\"" + McpProtocol.MetaClientCapabilitiesKey + "\":" + capabilities + "}";
        }

        private static JsonElement Schema(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static Task<RpcResult> CallRegionalAsync(HttpMcpTestServerFixture fixture, object arguments, string? regionHeader, CancellationToken token)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                { McpProtocol.MethodHeader, "tools/call" },
                { McpProtocol.NameHeader, "regional" }
            };
            if (regionHeader != null) headers["Mcp-Param-Region"] = regionHeader;
            Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "name", "regional" }, { "arguments", arguments }, { "_meta", McpHttpTestRequests.StatelessMeta() } };
            return McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/call", 1, parameters), headers, true, token);
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpMcpTestServerFixture fixture, string body, CancellationToken token)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            return await fixture.SendRawAsync(request, token).ConfigureAwait(false);
        }

        private static async Task<List<string>> ReadEventAsync(StreamReader reader, CancellationToken token)
        {
            List<string> lines = new List<string>();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_Wait);
            while (true)
            {
                string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line == null) return lines;
                if (line.Length == 0)
                {
                    if (lines.Count > 0 && !lines.All(l => l.StartsWith(":", StringComparison.Ordinal))) return lines;
                    lines.Clear();
                    continue;
                }

                lines.Add(line);
            }
        }

        private static async Task SendTextAsync(WebSocket socket, string text, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }

        private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken token)
        {
            using MemoryStream message = new MemoryStream();
            byte[] buffer = new byte[65536];
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_Wait);
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
            }
        }

        private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        }

        private static JsonProbe Next(RawLineClient client)
        {
            string? line = client.Receive(_Wait);
            if (line == null) throw new TimeoutException("No message arrived.");
            return TestJson.ParseRoot(line);
        }
    }
}
