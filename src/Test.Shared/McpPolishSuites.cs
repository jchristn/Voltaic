namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the requirements closed in v2.1.12: no cancellation reaches a later connection, the stdio client shuts down a server whose
    /// stdout closed, a failed explicit initialize closes the connection, the log limit applies per client on HTTP, and a
    /// rejected notification's error carries no id.
    /// </summary>
    public static class McpPolishSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Cases.
        /// </summary>
        public static TestSuiteDescriptor Cases()
        {
            const string suiteId = "Mcp.Polish";
            return new TestSuiteDescriptor(
                suiteId,
                "Cancellation of rejected requests, reconnection, shutdown, and limits",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "NoCancellationReachesALaterConnection", "When the connection drops, a waiting call fails at once and its cancellation never reaches the next connection", async ct =>
                    {
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0 };
                        using (RawTcpPeer first = RawTcpPeer.Listen())
                        {
                            Task accepting = first.AcceptAsync(ct);
                            TestAssert.True(await client.ConnectAsync("127.0.0.1", first.Port, ct).ConfigureAwait(false), "The client connects.");
                            await accepting.ConfigureAwait(false);
                            Task<object?> call = client.CallAsync<object?>("work", null, 4000, ct);
                            await first.ReceiveAsync(_Wait, ct).ConfigureAwait(false);
                            first.Dispose();

                            Stopwatch elapsed = Stopwatch.StartNew();
                            bool failed = false;
                            try
                            {
                                await call.ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is IOException || error is OperationCanceledException)
                            {
                                failed = error is IOException;
                            }

                            TestAssert.True(failed, "The call fails with an IOException when the connection drops.");
                            TestAssert.True(elapsed.ElapsedMilliseconds < 3000, $"It fails at once ({elapsed.ElapsedMilliseconds} ms), not after its timeout.");
                        }

                        using RawTcpPeer second = RawTcpPeer.Listen();
                        Task acceptingSecond = second.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", second.Port, ct).ConfigureAwait(false), "The client reconnects.");
                        await acceptingSecond.ConfigureAwait(false);
                        string? stray = await second.ReceiveAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        TestAssert.True(stray == null, $"Nothing reaches the new connection: {stray}");
                    }),

                    Case(suiteId, "StdioClientShutsDownAServerWhoseStdoutClosed", "Shutdown ends the server process even after the server closed its stdout", async ct =>
                    {
                        string pidFile = Path.Combine(Path.GetTempPath(), "voltaic-stdout-" + Guid.NewGuid().ToString("N") + ".pid");
                        using McpClient client = new McpClient { AutoInitialize = false, PingIntervalMs = 0, ShutdownGracePeriodMs = 500, TerminateGracePeriodMs = 500 };
                        await McpStdioIntegrationSuites.LaunchTestServerAsync(client, ct, "--close-stdout", pidFile).ConfigureAwait(false);
                        DateTime deadline = DateTime.UtcNow + _Wait;
                        while (!File.Exists(pidFile) && DateTime.UtcNow < deadline) await Task.Delay(50, ct).ConfigureAwait(false);
                        int pid = Int32.Parse(File.ReadAllText(pidFile).Trim());
                        while (client.IsConnected && DateTime.UtcNow < deadline) await Task.Delay(50, ct).ConfigureAwait(false);
                        TestAssert.False(client.IsConnected, "The client saw stdout close.");

                        client.Shutdown();
                        bool exited;
                        try
                        {
                            using Process server = Process.GetProcessById(pid);
                            exited = server.WaitForExit(5000);
                        }
                        catch (ArgumentException)
                        {
                            exited = true;
                        }

                        File.Delete(pidFile);
                        TestAssert.True(exited, "The server process was shut down.");
                    }),

                    Case(suiteId, "FailedExplicitInitializeDisconnects", "When InitializeAsync fails, it throws InvalidOperationException and the client disconnects", async ct =>
                    {
                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0 };
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client connects.");
                        await accepting.ConfigureAwait(false);
                        Task initializing = client.InitializeAsync(ct);
                        JsonProbe request = TestJson.ParseRoot((await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                        await peer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + request.Get("id").Int() + ",\"result\":{\"protocolVersion\":\"1999-01-01\",\"capabilities\":{},\"serverInfo\":{\"name\":\"odd\",\"version\":\"1\"}}}", ct).ConfigureAwait(false);
                        bool threw = false;
                        try
                        {
                            await initializing.ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                            threw = true;
                        }

                        TestAssert.True(threw, "InitializeAsync throws InvalidOperationException.");
                        TestAssert.False(client.IsConnected, "The client disconnected.");
                    }),

                    Case(suiteId, "LogLimitAppliesPerHttpClient", "The log message limit is shared by every stateless request of one HTTP client", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s =>
                        {
                            s.RateLimits.LogMessagesPerSecond = 3;
                            s.RegisterTool("chatty", "Logs five times", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                for (int i = 0; i < 5; i++) await McpToolCallContext.Current!.LogAsync("info", "line " + i, null, token).ConfigureAwait(false);
                                return "ok";
                            });
                        }).ConfigureAwait(false);
                        string meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{},\"io.modelcontextprotocol/logLevel\":\"debug\"}";
                        Dictionary<string, string> headers = new Dictionary<string, string>
                        {
                            { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                            { McpProtocol.MethodHeader, "tools/call" },
                            { McpProtocol.NameHeader, "chatty" }
                        };
                        RpcResult[] results = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
                            McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"id\":" + i + ",\"method\":\"tools/call\",\"params\":{\"name\":\"chatty\",\"arguments\":{}," + meta + "}}", headers, true, ct))).ConfigureAwait(false);
                        int logs = results.Sum(result => CountOccurrences(result.Body, "notifications/message"));
                        TestAssert.True(logs <= 5, $"Four requests of one client share the limit ({logs} log messages sent).");
                    }),

                    Case(suiteId, "SessionsStayAliveWhileUsed", "A session stays alive after a request longer than its timeout, and while an idle GET stream is open", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s =>
                        {
                            s.SessionTimeoutSeconds = 10;
                            s.RegisterTool("long", "Runs long", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await Task.Delay(TimeSpan.FromSeconds(13), token).ConfigureAwait(false);
                                return "finished";
                            });
                        }).ConfigureAwait(false);
                        string busy = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        string streaming = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        using HttpRequestMessage open = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/mcp");
                        open.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, streaming);
                        open.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, "2025-11-25");
                        open.Headers.Accept.ParseAdd("text/event-stream");
                        using HttpResponseMessage stream = await fixture.Client.SendAsync(open, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                        RpcResult result = await McpHttpTestRequests.SendAsync(fixture, "tools/call", 1, new { name = "long", arguments = new { } }, busy, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal("finished", result.Result.Get("content")[0].Get("text").String(), "The long request completes.");
                        await Task.Delay(TimeSpan.FromSeconds(7), ct).ConfigureAwait(false);
                        RpcResult after = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 2, null, busy, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, after.StatusCode, "The session is still alive after its long request ended.");
                        RpcResult kept = await McpHttpTestRequests.SendAsync(fixture, "tools/list", 3, null, streaming, "2025-11-25", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, kept.StatusCode, "An open GET stream kept its session alive for 20 seconds.");
                    }),

                    Case(suiteId, "HttpClientRequestsConfiguredVersion", "McpHttpClient requests its configured version on every handshake, after an older negotiation or a stateless connection", async ct =>
                    {
                        await using HttpMcpTestServerFixture older = await HttpMcpTestServerFixture.StartAsync(ct, s => s.MaximumHandshakeProtocolVersion = "2025-03-26").ConfigureAwait(false);
                        using FakeStreamableHttpServer fake = new FakeStreamableHttpServer();
                        using McpHttpClient client = new McpHttpClient { PingIntervalMs = 0 };
                        TestAssert.True(await client.ConnectStreamableAsync(older.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The first connection succeeds.");
                        TestAssert.Equal("2025-03-26", client.ProtocolVersion, "The older version was negotiated.");
                        client.Disconnect();
                        TestAssert.True(await client.ConnectStreamableAsync(fake.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The second connection succeeds.");
                        TestAssert.True(fake.InitializeBodies.Last().Contains("\"2025-11-25\"", StringComparison.Ordinal), $"The configured version is requested: {fake.InitializeBodies.Last()}");

                        client.Disconnect();
                        TestAssert.True(await client.ConnectStatelessAsync(older.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The stateless connection succeeds.");
                        client.Disconnect();
                        TestAssert.True(await client.ConnectStreamableAsync(fake.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The handshake after a stateless connection succeeds.");
                        TestAssert.True(fake.InitializeBodies.Last().Contains("\"2025-11-25\"", StringComparison.Ordinal), $"initialize never requests 2026-07-28: {fake.InitializeBodies.Last()}");
                    }),

                    Case(suiteId, "SchemasResolveAndCompareExactly", "A pointer into an embedded resource resolves there; uniqueItems and huge exponents compare exactly; valid extreme keyword values register and malformed ones do not", async ct =>
                    {
                        const string embedded = "{\"type\":\"object\",\"properties\":{\"v\":{\"$ref\":\"#/$defs/B/properties/x\"}},\"$defs\":{\"B\":{\"$id\":\"http://example.com/b.json\",\"$defs\":{\"s\":{\"type\":\"string\"}},\"properties\":{\"x\":{\"$ref\":\"#/$defs/s\"}}}}}";
                        const string numbers = "{\"type\":\"object\",\"properties\":{\"u\":{\"uniqueItems\":true},\"big\":{\"exclusiveMinimum\":1e9999999999999999999},\"tiny\":{\"multipleOf\":1e-400},\"long\":{\"maxLength\":1e400}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("embedded", "Pointer into a resource", McpStrictSuites.Schema(embedded), args => "ok");
                            s.RegisterTool("numbers", "Exact numbers", McpStrictSuites.Schema(numbers), args => "ok");
                        }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "embedded", "{\"v\":\"text\"}").ConfigureAwait(false), "The pointer target's own reference resolves in its resource.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "embedded", "{\"v\":5}").ConfigureAwait(false), "And is enforced.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "numbers", "{\"u\":[1e-30,2e-30]}").ConfigureAwait(false), "Tiny distinct numbers are unique.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "numbers", "{\"u\":[1,1.0]}").ConfigureAwait(false), "1 and 1.0 are the same number.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "numbers", "{\"big\":1e99999999999999999999}").ConfigureAwait(false), "Huge exponents compare exactly.");

                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        foreach (string malformed in new[] { "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":[]}}}", "{\"type\":\"object\",\"$defs\":{\"a\":{\"$anchor\":\"1bad\"}}}", "{\"type\":\"object\",\"prefixItems\":[{}],\"properties\":{\"a\":{\"$ref\":\"#/prefixItems/00\"}}}" })
                        {
                            TestAssert.Throws<ArgumentException>(() => server.RegisterTool("bad", "Malformed", McpStrictSuites.Schema(malformed), args => "x"), $"Rejected: {malformed}");
                        }
                    }),

                    Case(suiteId, "CompletionsAndTemplatesFollowTheRules", "completion/complete requires argument.value; URI templates honor :n prefixes and empty values", async ct =>
                    {
                        Dictionary<string, string>? seen = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterPrompt("p", "Prompt", new[] { new McpPromptArgument { Name = "topic" } }, args => new McpGetPromptResult());
                            s.RegisterResourceTemplate(new McpResourceTemplate { UriTemplate = "x://y/{id:3}/{rest}", Name = "t", MimeType = "text/plain" }, (uri, variables, token) =>
                            {
                                seen = new Dictionary<string, string>(variables);
                                return Task.FromResult(new McpReadResourceResult { Contents = new List<object> { new McpTextResourceContents { Uri = uri, MimeType = "text/plain", Text = "ok" } } });
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"completion/complete\",\"params\":{\"ref\":{\"type\":\"ref/prompt\",\"name\":\"p\"},\"argument\":{\"name\":\"topic\"}}}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "A missing argument.value is invalid params.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/read\",\"params\":{\"uri\":\"x://y/abcd/r\"}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("error"), "A value longer than the :3 prefix does not match.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/read\",\"params\":{\"uri\":\"x://y/abc/\"}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("result"), "An empty value matches.");
                        TestAssert.True(seen != null && seen["id"] == "abc", "The prefixed value is extracted.");
                    }),

                    Case(suiteId, "RejectedNotificationErrorHasNoId", "The 400 error for a rejected stateless notification carries no id member", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult result = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\"}", new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 } }, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, result.StatusCode, "The message is rejected.");
                        TestAssert.False(TestJson.ParseRoot(result.Body).Has("id"), $"The error has no id: {result.Body}");
                    }),
                });
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "polish", "conformance" });
        }
    }
}
