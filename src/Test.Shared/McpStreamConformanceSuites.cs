namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the MCP lifecycle and JSON-RPC rules on the stream transports (checked on TCP, which shares the processor
    /// with stdio and WebSocket): initialization first and only once, the JSON-RPC envelope rules, batches only under
    /// 2025-03-26, concurrent requests (ping answered while a tool runs), cancellation, newline and Content-Length
    /// framing, and the client side (automatic initialize, cancellation notices, batches from the server).
    /// </summary>
    public static class McpStreamConformanceSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Stream transport conformance cases.
        /// </summary>
        public static TestSuiteDescriptor StreamConformance()
        {
            const string suiteId = "McpStreams.Conformance";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP lifecycle, JSON-RPC, concurrency, and cancellation on stream transports",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "RequestsBeforeInitializeAreRejected", "Before initialize, requests other than ping and server/discover get -32600; after it they are served", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}").ConfigureAwait(false);
                        JsonProbe early = Next(client);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}").ConfigureAwait(false);
                        JsonProbe ping = Next(client);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}").ConfigureAwait(false);
                        JsonProbe after = Next(client);

                        TestAssert.Equal(-32600, early.Get("error").Get("code").Int(), "tools/list before initialize is rejected.");
                        TestAssert.True(ping.Get("result").IsObject, "ping is allowed before initialize.");
                        TestAssert.True(after.Get("result").Has("tools"), "tools/list is served after initialize.");
                    }),

                    Case(suiteId, "InitializeValidatesParamsAndRunsOnce", "initialize without capabilities or clientInfo gets -32602; a second initialize gets -32600", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}").ConfigureAwait(false);
                        JsonProbe incomplete = Next(client);
                        string first = await client.InitializeAsync("2025-06-18", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"x\",\"version\":\"1\"}}}").ConfigureAwait(false);
                        JsonProbe again = Next(client);

                        TestAssert.Equal(-32602, incomplete.Get("error").Get("code").Int(), "Missing initialize fields are invalid params.");
                        TestAssert.Equal("2025-06-18", TestJson.ParseRoot(first).Get("result").Get("protocolVersion").String(), "The first valid initialize negotiates.");
                        TestAssert.Equal(-32600, again.Get("error").Get("code").Int(), "initialize may be sent only once.");
                    }),

                    Case(suiteId, "InvalidEnvelopesAreRejected", "A null id, a missing or wrong jsonrpc member, a non-string method, and a non-object message get -32600; params that are not an object get -32602; client responses are ignored", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);

                        string[] invalid =
                        {
                            "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}",
                            "{\"id\":4,\"method\":\"ping\"}",
                            "{\"jsonrpc\":\"1.0\",\"id\":5,\"method\":\"ping\"}",
                            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":42}",
                            "\"just a string\""
                        };
                        foreach (string message in invalid)
                        {
                            await client.SendAsync(message).ConfigureAwait(false);
                            string? reply = client.Receive(_Wait);
                            TestAssert.True(reply != null, $"No reply to: {message} (closed: {client.IsClosed})");
                            TestAssert.Equal(-32600, TestJson.ParseRoot(reply!).Get("error").Get("code").Int(), $"Invalid request: {message}");
                        }

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\",\"params\":[1,2]}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, Next(client).Get("error").Get("code").Int(), "Array params are invalid for MCP.");

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"server-ping\",\"result\":{}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"ping\"}").ConfigureAwait(false);
                        TestAssert.Equal(8, Next(client).Get("id").Int(), "A client response gets no answer; the next request does.");
                    }),

                    Case(suiteId, "BatchesFollowTheNegotiatedVersion", "Under 2025-03-26 a batch is answered with an array (initialize may not be batched); under 2025-11-25 it is rejected; an empty batch is invalid", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using RawLineClient old = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await old.InitializeAsync("2025-03-26", ct).ConfigureAwait(false);
                        await old.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/whatever\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}]").ConfigureAwait(false);
                        JsonProbe batch = Next(old);
                        await old.SendAsync("[]").ConfigureAwait(false);
                        JsonProbe empty = Next(old);
                        await old.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"initialize\",\"params\":{}}]").ConfigureAwait(false);
                        JsonProbe batchedInitialize = Next(old);

                        using RawLineClient current = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await current.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await current.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]").ConfigureAwait(false);
                        JsonProbe rejected = Next(current);

                        TestAssert.Equal(2, batch.Length, "Both requests are answered in one array; the notification is not.");
                        TestAssert.Equal(-32600, empty.Get("error").Get("code").Int(), "An empty batch is invalid.");
                        TestAssert.Equal(-32600, batchedInitialize[0].Get("error").Get("code").Int(), "initialize must not be batched.");
                        TestAssert.Equal(-32600, rejected.Get("error").Get("code").Int(), "2025-11-25 has no batching.");
                    }),

                    Case(suiteId, "PingIsAnsweredWhileAToolRuns", "Requests run concurrently: a ping sent while a slow tool runs is answered immediately", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"slow\",\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{\"ms\":3000}}}").ConfigureAwait(false);
                        Stopwatch watch = Stopwatch.StartNew();
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"ping\",\"method\":\"ping\"}").ConfigureAwait(false);
                        JsonProbe first = Next(client);

                        TestAssert.Equal("ping", first.Get("id").String(), "The ping is answered first.");
                        TestAssert.True(watch.ElapsedMilliseconds < 2000, $"The ping did not wait for the tool ({watch.ElapsedMilliseconds} ms).");
                        TestAssert.Equal("slow", Next(client).Get("id").String(), "The tool's response follows.");
                    }),

                    Case(suiteId, "CancelledRequestStopsWithoutResponse", "notifications/cancelled cancels the handler's token and suppresses its response; initialize cannot be cancelled", async ct =>
                    {
                        SlowToolProbe probe = new SlowToolProbe();
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct, probe).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"work\",\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{\"ms\":10000}}}").ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"work\",\"reason\":\"user gave up\"}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"after\",\"method\":\"ping\"}").ConfigureAwait(false);

                        JsonProbe next = Next(client);
                        TestAssert.Equal("after", next.Get("id").String(), "The cancelled request gets no response.");
                        TestAssert.True(await WaitUntilAsync(() => probe.Cancelled, ct).ConfigureAwait(false), "The handler's token was cancelled.");
                        TestAssert.True(client.Receive(TimeSpan.FromMilliseconds(500)) == null, "Nothing more arrives for the cancelled request.");
                    }),

                    Case(suiteId, "NewlineAndContentLengthFramingAreAccepted", "McpTcpServer accepts newline-delimited JSON (the stdio framing) and Content-Length framing, and closes a connection whose first line is not JSON", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using McpTcpClient newline = new McpTcpClient();
                        using McpTcpClient framed = new McpTcpClient { NewlineDelimited = false };
                        TestAssert.True(await newline.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false), "A newline-delimited client connects and initializes.");
                        TestAssert.True(await framed.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false), "A Content-Length client connects and initializes.");
                        TestAssert.True(JsonProbe.From(await newline.CallAsync<object?>("tools/list", new { }, token: ct).ConfigureAwait(false)).Has("tools"), "Newline framing works.");
                        TestAssert.True(JsonProbe.From(await framed.CallAsync<object?>("tools/list", new { }, token: ct).ConfigureAwait(false)).Has("tools"), "Content-Length framing works.");

                        using TcpClient http = new TcpClient();
                        await http.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false);
                        NetworkStream stream = http.GetStream();
                        byte[] request = Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: localhost\r\nContent-Length: 40\r\n\r\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n");
                        await stream.WriteAsync(request, 0, request.Length, ct).ConfigureAwait(false);
                        byte[] buffer = new byte[256];
                        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(_Wait);
                        int read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                        TestAssert.Equal(0, read, "A cross-protocol HTTP request is dropped without an answer.");
                    }),

                    Case(suiteId, "ClientsInitializeAutomatically", "McpTcpClient and McpWebsocketsClient perform initialize on connect (the result is exposed); with AutoInitialize false the server rejects other requests", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using McpTcpClient automatic = new McpTcpClient();
                        automatic.ClientCapabilities["experimental"] = new { };
                        TestAssert.True(await automatic.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false), "The client connects.");
                        TestAssert.True(automatic.InitializeResult.HasValue, "The initialize result is exposed.");
                        TestAssert.Equal(McpProtocol.NewestHandshakeProtocolVersion, automatic.ProtocolVersion, "The negotiated version is stored.");

                        using McpTcpClient manual = new McpTcpClient { AutoInitialize = false };
                        TestAssert.True(await manual.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false), "The client connects without initializing.");
                        TestAssert.False(manual.InitializeResult.HasValue, "No handshake happened.");
                        Exception? rejected = await CaptureAsync(() => manual.CallAsync<object?>("tools/list", new { }, token: ct)).ConfigureAwait(false);
                        TestAssert.True(rejected != null && rejected.Message.Contains("-32600"), $"The server requires initialize first: {rejected?.Message}");
                        await manual.InitializeAsync(ct).ConfigureAwait(false);
                        TestAssert.True(JsonProbe.From(await manual.CallAsync<object?>("tools/list", new { }, token: ct).ConfigureAwait(false)).Has("tools"), "After InitializeAsync the server serves requests.");

                        await using WebSocketMcpFixture websocket = await WebSocketMcpFixture.StartAsync(ct).ConfigureAwait(false);
                        using McpWebsocketsClient wsClient = await websocket.ConnectClientAsync(ct).ConfigureAwait(false);
                        TestAssert.True(wsClient.InitializeResult.HasValue, "The WebSocket client initialized on connect.");
                    }),

                    Case(suiteId, "ClientSendsCancelledOnTimeout", "A McpTcpClient call that times out sends notifications/cancelled, and the server cancels the handler", async ct =>
                    {
                        SlowToolProbe probe = new SlowToolProbe();
                        await using TcpJsonRpcFixture fixture = await StartAsync(ct, probe).ConfigureAwait(false);
                        using McpTcpClient client = new McpTcpClient();
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false), "The client connects.");

                        Exception? timedOut = await CaptureAsync(() => client.CallAsync<object?>("tools/call", new { name = "slow", arguments = new { ms = 10000 } }, 300, ct)).ConfigureAwait(false);
                        TestAssert.True(timedOut is OperationCanceledException, $"The call times out: {timedOut?.GetType().Name}");
                        TestAssert.True(await WaitUntilAsync(() => probe.Cancelled, ct).ConfigureAwait(false), "The server cancelled the handler after the client's notification.");
                    }),

                    Case(suiteId, "ClientsAnswerBatchesFromTheServer", "On 2025-03-26 a batch of server requests is answered with one array; notifications in it are raised", async ct =>
                    {
                        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                        listener.Start();
                        try
                        {
                            using McpTcpClient client = new McpTcpClient { AutoInitialize = false, ProtocolVersion = McpProtocol.ProtocolVersion20250326 };
                            List<string> notifications = new List<string>();
                            client.NotificationReceived += (sender, notification) => { lock (notifications) notifications.Add(notification.Method); };
                            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(ct).AsTask();
                            TestAssert.True(await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, ct).ConfigureAwait(false), "The client connects.");
                            using TcpClient server = await accepting.ConfigureAwait(false);
                            NetworkStream stream = server.GetStream();
                            byte[] batch = Encoding.UTF8.GetBytes("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/hello\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"unknown\"}]\n");
                            await stream.WriteAsync(batch, 0, batch.Length, ct).ConfigureAwait(false);

                            using StreamReader reader = new StreamReader(stream);
                            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeout.CancelAfter(_Wait);
                            string? answer = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                            JsonProbe answers = TestJson.ParseRoot(answer!);
                            TestAssert.Equal(2, answers.Length, $"Both requests are answered in one array: {answer}");
                            TestAssert.True(await WaitUntilAsync(() => { lock (notifications) return notifications.Contains("notifications/hello"); }, ct).ConfigureAwait(false), "The notification is raised.");
                        }
                        finally
                        {
                            listener.Stop();
                        }
                    }),

                    Case(suiteId, "ClientsRefuseBatchesWithoutBatching", "On 2025-06-18 and later a batch from the server gets one -32600 error (id null) and none of its requests run", async ct =>
                    {
                        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                        listener.Start();
                        try
                        {
                            using McpTcpClient client = new McpTcpClient { AutoInitialize = false, ProtocolVersion = McpProtocol.ProtocolVersion20250618 };
                            int handled = 0;
                            client.RegisterRequestHandler("custom/work", (_, _) => { Interlocked.Increment(ref handled); return Task.FromResult<object?>(new { }); });
                            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(ct).AsTask();
                            TestAssert.True(await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, ct).ConfigureAwait(false), "The client connects.");
                            using TcpClient server = await accepting.ConfigureAwait(false);
                            NetworkStream stream = server.GetStream();
                            byte[] batch = Encoding.UTF8.GetBytes("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"custom/work\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}]\n");
                            await stream.WriteAsync(batch, 0, batch.Length, ct).ConfigureAwait(false);

                            using StreamReader reader = new StreamReader(stream);
                            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeout.CancelAfter(_Wait);
                            string? answer = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                            JsonProbe refusal = TestJson.ParseRoot(answer!);
                            TestAssert.Equal(-32600, refusal.Get("error").Get("code").Int(), $"The batch is refused: {answer}");
                            TestAssert.True(refusal.Has("id") && refusal.Get("id").IsNull, "The refusal carries a null id.");
                            await Task.Delay(200, ct).ConfigureAwait(false);
                            TestAssert.Equal(0, Volatile.Read(ref handled), "No request in the refused batch runs.");
                        }
                        finally
                        {
                            listener.Stop();
                        }
                    }),

                    Case(suiteId, "StdioServerKeepsStdoutClean", "Console output from a tool on the stdio server goes to stderr, so stdout carries only MCP messages", async ct =>
                    {
                        using McpClient client = new McpClient();
                        await McpStdioIntegrationSuites.LaunchTestServerAsync(client, ct).ConfigureAwait(false);
                        JsonProbe result = JsonProbe.From(await client.CallAsync<object?>("tools/call", new { name = "chatty", arguments = new { } }, 15000, ct).ConfigureAwait(false));
                        JsonProbe list = JsonProbe.From(await client.CallAsync<object?>("tools/list", new { }, 15000, ct).ConfigureAwait(false));

                        TestAssert.Equal("done", result.Get("content")[0].Get("text").String(), "The tool's result arrives intact.");
                        TestAssert.True(list.Has("tools"), "Later messages still parse.");
                        client.Shutdown();
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "stream", "conformance" });
        }

        private static Task<TcpJsonRpcFixture> StartAsync(CancellationToken token, SlowToolProbe? probe = null)
        {
            SlowToolProbe slow = probe ?? new SlowToolProbe();
            return TcpJsonRpcFixture.StartMcpTcpAsync(token, server =>
            {
                server.RegisterTool("slow", "Waits", new { type = "object", properties = new { ms = new { type = "integer" } } }, async (RpcParameters? args, CancellationToken token) =>
                {
                    try
                    {
                        await Task.Delay((int)(args?.GetInt64("ms") ?? 1000), token).ConfigureAwait(false);
                        return "finished";
                    }
                    catch (OperationCanceledException)
                    {
                        slow.Cancelled = true;
                        throw;
                    }
                });
            });
        }

        private static JsonProbe Next(RawLineClient client)
        {
            string? line = client.Receive(_Wait);
            if (line == null) throw new TimeoutException("No message arrived.");
            return TestJson.ParseRoot(line);
        }

        private static async Task<Exception?> CaptureAsync(Func<Task> action)
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
