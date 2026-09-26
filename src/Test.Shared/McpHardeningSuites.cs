namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the requirements closed in v2.1.11: clients request the configured version on every connection, keep
    /// reading while application code runs after a call, and reconnect at once; the last coalesced progress update
    /// always arrives; a cancellation carrying client <c>_meta</c> applies; a running request keeps its session; a
    /// zero replay buffer keeps event IDs; per-client rate limits; embedded dialects, relative root IDs, and exact numbers.
    /// </summary>
    public static class McpHardeningSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(400);
        private const string _InitializeResult = "{\"protocolVersion\":\"VERSION\",\"capabilities\":{},\"serverInfo\":{\"name\":\"raw\",\"version\":\"1\"}}";

        /// <summary>
        /// Client and stream cases.
        /// </summary>
        public static TestSuiteDescriptor Clients()
        {
            const string suiteId = "McpClients.Hardening";
            return new TestSuiteDescriptor(
                suiteId,
                "Client version, receive loop, reconnection, and progress",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ReconnectRequestsConfiguredVersion", "After a session negotiated an older version, the next connection requests the configured version again", async ct =>
                    {
                        using McpTcpClient client = new McpTcpClient { PingIntervalMs = 0 };
                        using (RawTcpPeer older = RawTcpPeer.Listen())
                        {
                            Task accepting = older.AcceptAsync(ct);
                            Task<bool> connecting = client.ConnectAsync("127.0.0.1", older.Port, ct);
                            await accepting.ConfigureAwait(false);
                            JsonProbe first = TestJson.ParseRoot((await older.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                            await older.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + first.Get("id").Int() + ",\"result\":" + _InitializeResult.Replace("VERSION", "2024-11-05") + "}", ct).ConfigureAwait(false);
                            TestAssert.True(await connecting.ConfigureAwait(false), "The first connection succeeds.");
                            TestAssert.Equal("2024-11-05", client.ProtocolVersion, "The older version was negotiated.");
                            client.Disconnect();
                        }

                        using RawTcpPeer newer = RawTcpPeer.Listen();
                        Task acceptingNewer = newer.AcceptAsync(ct);
                        Task<bool> reconnecting = client.ConnectAsync("127.0.0.1", newer.Port, ct);
                        await acceptingNewer.ConfigureAwait(false);
                        JsonProbe second = TestJson.ParseRoot((await newer.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                        TestAssert.Equal("2025-11-25", second.Get("params").Get("protocolVersion").String(), "The new initialize requests the configured (latest) version.");
                        await newer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + second.Get("id").Int() + ",\"result\":" + _InitializeResult.Replace("VERSION", "2025-11-25") + "}", ct).ConfigureAwait(false);
                        TestAssert.True(await reconnecting.ConfigureAwait(false), "The second connection succeeds.");
                    }),

                    Case(suiteId, "CodeAfterAnAwaitedCallDoesNotBlockReceiving", "Application code that blocks after an awaited call does not stop the client from reading responses", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using McpTcpClient client = new McpTcpClient { PingIntervalMs = 0 };
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false), "The client connects.");
                        await client.CallAsync<object?>("ping", null, 10000, ct).ConfigureAwait(false);

                        // Blocking here used to block the receive loop, which ran this continuation.
                        bool answered = client.CallAsync<object?>("ping", null, 10000, ct).Wait(5000, ct);
                        TestAssert.True(answered, "A second call made synchronously after an awaited call completes.");
                    }),

                    Case(suiteId, "ImmediateReconnectWorks", "Disconnect followed at once by ConnectAsync succeeds every time", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using McpTcpClient client = new McpTcpClient { PingIntervalMs = 0 };
                        for (int attempt = 0; attempt < 10; attempt++)
                        {
                            client.Disconnect();
                            TestAssert.True(await client.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false), $"Connection {attempt} succeeds.");
                            await client.CallAsync<object?>("ping", null, 10000, ct).ConfigureAwait(false);
                        }
                    }),

                    Case(suiteId, "LastProgressUpdateAlwaysArrives", "With a long progress interval and no total, the server sends the first and the last update, and the client delivers the last one it held back", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.ProgressIntervalMs = 60000;
                            s.RegisterTool("busy", "Reports progress", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                McpToolCallContext call = McpToolCallContext.Current!;
                                for (int step = 1; step <= 5; step++) await call.ReportProgressAsync(step, null, null, token).ConfigureAwait(false);
                                return "done";
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient raw = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await raw.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"busy\",\"arguments\":{},\"_meta\":{\"progressToken\":\"p\"}}}").ConfigureAwait(false);
                        List<double> sent = new List<double>();
                        while (true)
                        {
                            JsonProbe message = McpStrictSuites.Next(raw);
                            if (message.Has("id")) break;
                            sent.Add(message.Get("params").Get("progress").Double());
                        }

                        TestAssert.True(sent.Count == 2 && sent[0] == 1 && sent[1] == 5, $"The server sends the first and the last update: {String.Join(",", sent)}");

                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0, ProgressIntervalMs = 60000 };
                        List<double> delivered = new List<double>();
                        client.NotificationReceived += (sender, notification) =>
                        {
                            if (notification.Method != "notifications/progress") return;
                            lock (delivered) delivered.Add(JsonProbe.From(notification.Params).Get("progress").Double());
                        };
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client connects.");
                        await accepting.ConfigureAwait(false);
                        Task<object?> call = client.CallAsync<object?>("work", new { _meta = new { progressToken = "t" } }, 10000, ct);
                        JsonProbe request = TestJson.ParseRoot((await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                        for (int step = 1; step <= 3; step++)
                        {
                            await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":\"t\",\"progress\":" + step + "}}", ct).ConfigureAwait(false);
                        }

                        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + request.Get("id").Int() + ",\"result\":{}}", ct).ConfigureAwait(false);
                        await call.ConfigureAwait(false);
                        lock (delivered) TestAssert.True(delivered.Count == 2 && delivered[0] == 1 && delivered[1] == 3, $"The client delivers the first and the last update: {String.Join(",", delivered)}");
                    }),

                    Case(suiteId, "CancellationWithClientMetaApplies", "A notifications/cancelled whose _meta carries clientInfo cancels its stateless request on a stream transport", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("slow", "Waits", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                        {
                            await Task.Delay(3000, token).ConfigureAwait(false);
                            return "done";
                        })).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}," + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":5,\"_meta\":{\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}}").ConfigureAwait(false);
                        await Task.Delay(3500, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/list\",\"params\":{" + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.Equal(6, McpStrictSuites.Next(client).Get("id").Int(), "The cancelled request got no response.");
                        TestAssert.True(client.Receive(_Quiet) == null, "Nothing else arrives.");
                    }),
                });
        }

        /// <summary>
        /// Streamable HTTP and server feature cases.
        /// </summary>
        public static TestSuiteDescriptor Servers()
        {
            const string suiteId = "McpServers.Hardening";
            return new TestSuiteDescriptor(
                suiteId,
                "Sessions, replay, rate limits, dialects, and numbers",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "RunningRequestKeepsItsSession", "A session whose request runs longer than the session timeout is not expired; an idle one is", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s =>
                        {
                            s.SessionTimeoutSeconds = 10;
                            s.RegisterTool("long", "Runs long", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await Task.Delay(TimeSpan.FromSeconds(17), token).ConfigureAwait(false);
                                return "finished";
                            });
                        }).ConfigureAwait(false);
                        string busy = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        string idle = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        RpcResult result = await McpHttpTestRequests.SendAsync(fixture, "tools/call", 1, new { name = "long", arguments = new { } }, busy, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal("finished", result.Result.Get("content")[0].Get("text").String(), $"The long request completes: {result.Body}");
                        RpcResult expired = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 2, null, idle, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, expired.StatusCode, "The idle session expired.");
                    }),

                    Case(suiteId, "ZeroReplayBufferKeepsEventIds", "With SseReplayBufferSize 0, a session's POST response stream still carries event IDs and the priming event", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s =>
                        {
                            s.SseReplayBufferSize = 0;
                            s.RegisterTool("steps", "Reports progress", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await McpToolCallContext.Current!.ReportProgressAsync(1, 2, null, token).ConfigureAwait(false);
                                return "done";
                            });
                        }).ConfigureAwait(false);
                        string session = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        RpcResult result = await McpHttpTestRequests.SendAsync(fixture, "tools/call", 1, new { name = "steps", arguments = new { }, _meta = new { progressToken = "p" } }, session, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.True(result.Body.Contains("id: ", StringComparison.Ordinal), $"The stream carries event IDs: {result.Body}");
                    }),

                    Case(suiteId, "RateLimitsApplyPerClient", "Tool calls, completions, and log messages over the per-client limits are refused, and the limits validate their range", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RateLimits.ToolCallsPerSecond = 3;
                            s.RateLimits.LogMessagesPerSecond = 2;
                            s.RegisterTool("chatty", "Logs five times", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                for (int i = 0; i < 5; i++) await McpToolCallContext.Current!.LogAsync("info", "line " + i, null, token).ConfigureAwait(false);
                                return "ok";
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"lvl\",\"method\":\"logging/setLevel\",\"params\":{\"level\":\"debug\"}}").ConfigureAwait(false);
                        McpStrictSuites.Next(client);

                        int logs = 0;
                        int limited = 0;
                        for (int call = 0; call < 5; call++)
                        {
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + (100 + call) + ",\"method\":\"tools/call\",\"params\":{\"name\":\"chatty\",\"arguments\":{}}}").ConfigureAwait(false);
                            while (true)
                            {
                                JsonProbe message = McpStrictSuites.Next(client);
                                if (!message.Has("id"))
                                {
                                    if (message.Get("method").String() == "notifications/message") logs++;
                                    continue;
                                }

                                if (message.Get("result").Has("isError") && message.Get("result").Get("isError").Bool()) limited++;
                                break;
                            }
                        }

                        TestAssert.True(limited >= 2, $"Tool calls over 3 per second are refused ({limited} of 5).");
                        TestAssert.True(logs <= 3, $"Log messages are limited ({logs} sent).");

                        McpRateLimits limits = new McpRateLimits();
                        TestAssert.Equal(100, limits.ToolCallsPerSecond, "The default tool call limit is 100 per second.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => limits.CompletionsPerSecond = -1, "A negative limit is rejected.");
                        limits.ToolCallsPerSecond = 0;
                        TestAssert.Equal(0, limits.ToolCallsPerSecond, "0 disables the limit.");
                    }),

                    Case(suiteId, "EmbeddedDialectsAndRelativeIds", "An embedded resource is validated in its own $schema dialect, an unsupported embedded dialect is rejected, and a relative root $id resolves", async ct =>
                    {
                        const string mixed = "{\"type\":\"object\",\"properties\":{\"a\":{\"$id\":\"https://example.com/a\",\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"$ref\":\"#/definitions/n\",\"type\":\"string\",\"definitions\":{\"n\":{\"type\":\"number\"}}}}}";
                        const string relative = "{\"$id\":\"args.json\",\"type\":\"object\",\"$defs\":{\"s\":{\"type\":\"string\"}},\"properties\":{\"a\":{\"$ref\":\"args.json#/$defs/s\"}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("mixed", "Embedded draft-07", McpStrictSuites.Schema(mixed), args => "ok");
                            s.RegisterTool("relative", "Relative root id", McpStrictSuites.Schema(relative), args => "ok");
                        }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "mixed", "{\"a\":5}").ConfigureAwait(false), "draft-07 ignores the type beside $ref: a number passes.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "mixed", "{\"a\":\"x\"}").ConfigureAwait(false), "The $ref target (a number) is enforced.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "relative", "{\"a\":5}").ConfigureAwait(false), "The relative root $id resolves and is enforced.");

                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        TestAssert.Throws<ArgumentException>(
                            () => server.RegisterTool("old", "2019-09 inside", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"a\":{\"$id\":\"https://example.com/b\",\"$schema\":\"https://json-schema.org/draft/2019-09/schema\"}}}"), args => "x"),
                            "An unsupported embedded dialect is rejected.");
                    }),

                    Case(suiteId, "NumbersAreComparedExactly", "Bounds and multipleOf use the exact JSON number, whatever its precision or range", async ct =>
                    {
                        const string schema = "{\"type\":\"object\",\"properties\":{\"min\":{\"minimum\":0},\"max\":{\"maximum\":100},\"xmax\":{\"exclusiveMaximum\":1},\"three\":{\"multipleOf\":3},\"tenth\":{\"multipleOf\":0.1}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("n", "Numbers", McpStrictSuites.Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"min\":-0.00000000000000000000000000000001}").ConfigureAwait(false), "A tiny negative is below 0.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"max\":100.0000000000000000000000000000001}").ConfigureAwait(false), "A value just above 100 exceeds the maximum.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", "{\"xmax\":0.99999999999999999999999999999999}").ConfigureAwait(false), "A value just below 1 is below the exclusive maximum.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"three\":1e400}").ConfigureAwait(false), "1e400 is not a multiple of 3.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", "{\"three\":3e400}").ConfigureAwait(false), "3e400 is a multiple of 3.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "n", "{\"tenth\":0.3}").ConfigureAwait(false), "0.3 is a multiple of 0.1 (exactly, not in binary floating point).");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "n", "{\"tenth\":0.35}").ConfigureAwait(false), "0.35 is not a multiple of 0.1.");
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "hardening", "conformance" });
        }
    }
}
