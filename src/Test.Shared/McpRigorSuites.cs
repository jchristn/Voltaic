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
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers the requirements closed in v2.1.9: ECMA-262 pattern semantics, keyword-aware schema indexing, boolean
    /// property subschemas and object-only output for handshake sessions, finished request IDs on every rejection path
    /// (servers and clients), ping timeouts that cancel the ping, notification ordering after initialize, client
    /// handling of null IDs, non-object results and batches, the stdio line ending, the <c>/events</c> stream ending
    /// with its session, the stateless client's generic calls, timeouts, re-issue, and mode reset, stateless batches
    /// on HTTP, robust stateless signal parsing, and protected resource metadata at the resource's own URL.
    /// </summary>
    public static class McpRigorSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// JSON Schema cases.
        /// </summary>
        public static TestSuiteDescriptor Schemas()
        {
            const string suiteId = "Mcp.SchemaRigor";
            return new TestSuiteDescriptor(
                suiteId,
                "JSON Schema patterns, indexing, and per-revision shapes",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "PatternsFollowEcma262", "$ matches only at the end, . excludes line terminators, \\s is the ECMA-262 white space set (also in classes), [] and [^] behave as in ECMA-262", async ct =>
                    {
                        const string schema = """
                            {"type":"object","properties":{
                              "end":{"type":"string","pattern":"^a$"},
                              "dot":{"type":"string","pattern":"^.$"},
                              "ws":{"type":"string","pattern":"^\\s$"},
                              "cls":{"type":"string","pattern":"^[\\s]$"},
                              "notnon":{"type":"string","pattern":"^[^\\S]$"},
                              "mixed":{"type":"string","pattern":"^[a\\S]$"},
                              "empty":{"type":"string","pattern":"[]"},
                              "any":{"type":"string","pattern":"^[^]$"},
                              "word":{"type":"string","pattern":"^\\w+$"},
                              "dollar":{"type":"string","pattern":"^\\$$"}}}
                            """;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("p", "Patterns", McpStrictSuites.Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);

                        string[] valid = { "{\"end\":\"a\"}", "{\"dot\":\"x\"}", "{\"ws\":\"\\u00a0\"}", "{\"ws\":\"\\ufeff\"}", "{\"cls\":\"\\u3000\"}", "{\"notnon\":\"\\u00a0\"}", "{\"mixed\":\"b\"}", "{\"mixed\":\"a\"}", "{\"any\":\"\\n\"}", "{\"word\":\"abc_1\"}", "{\"dollar\":\"$\"}" };
                        string[] invalid = { "{\"end\":\"a\\n\"}", "{\"dot\":\"\\r\"}", "{\"dot\":\"\\u2028\"}", "{\"ws\":\"x\"}", "{\"notnon\":\"a\"}", "{\"mixed\":\" \"}", "{\"empty\":\"x\"}", "{\"empty\":\"\"}", "{\"word\":\"\\u00e9\"}", "{\"dollar\":\"$\\n\"}" };
                        foreach (string arguments in valid) TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "p", arguments).ConfigureAwait(false), $"Matches: {arguments}");
                        foreach (string arguments in invalid) TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "p", arguments).ConfigureAwait(false), $"Does not match: {arguments}");
                    }),

                    Case(suiteId, "IndexingFollowsKeywords", "Properties named like data keywords are schemas; unknown keywords are not; a draft-07 $ref hides its siblings", async ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        ArgumentException? unresolved = null;
                        try
                        {
                            server.RegisterTool("a", "Bad ref under a property named enum", McpStrictSuites.Schema("""{"type":"object","properties":{"enum":{"$ref":"#/nowhere"}}}"""), args => "x");
                        }
                        catch (ArgumentException rejected)
                        {
                            unresolved = rejected;
                        }

                        TestAssert.True(unresolved != null && unresolved.Message.Contains("#/nowhere", StringComparison.Ordinal), $"A reference inside a property named enum is checked: {unresolved?.Message}");
                        TestAssert.Throws<ArgumentException>(
                            () => server.RegisterTool("b", "Resource in an unknown keyword", McpStrictSuites.Schema("""{"type":"object","x-meta":{"$id":"https://example.com/fake","$defs":{"x":{"type":"string"}}},"properties":{"b":{"$ref":"https://example.com/fake#/$defs/x"}}}"""), args => "x"),
                            "An $id inside an unknown keyword is not a schema resource.");
                        TestAssert.Throws<ArgumentException>(
                            () => server.RegisterTool("c", "draft-07 sibling $id", McpStrictSuites.Schema("""{"$schema":"http://json-schema.org/draft-07/schema#","type":"object","definitions":{"a":{"$ref":"#/definitions/b","$id":"#hidden"},"b":{"type":"string"}},"properties":{"p":{"$ref":"#hidden"}}}"""), args => "x"),
                            "draft-07 ignores the $id beside a $ref, so its anchor does not exist.");

                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("named", "Properties named like keywords", McpStrictSuites.Schema("""{"type":"object","$defs":{"s":{"type":"string"}},"properties":{"enum":{"$ref":"#/$defs/s"},"default":{"type":"integer"},"const":{"$anchor":"c","type":"string"}},"additionalProperties":{"$ref":"#c"}}"""), args => "ok");
                        }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "named", """{"enum":"x","default":1,"other":"s"}""").ConfigureAwait(false), "Valid values pass.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "named", """{"enum":5}""").ConfigureAwait(false), "The property named enum is validated by its $ref.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "named", """{"default":"x"}""").ConfigureAwait(false), "The property named default is validated.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "named", """{"other":5}""").ConfigureAwait(false), "The anchor inside the property named const resolves.");
                    }),

                    Case(suiteId, "BooleanPropertySchemasPerRevision", "Before 2026-07-28 boolean property subschemas are sent as objects ({} and {\"not\":{}}); 2026-07-28 receives them as written; validation is unchanged", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                            s.RegisterTool("bools", "Boolean subschemas", McpStrictSuites.Schema("""{"type":"object","properties":{"any":true,"never":false}}"""), McpStrictSuites.Schema("""{"type":"object","properties":{"ok":true}}"""), args => McpToolCallResult.FromStructured(new { ok = 1 }))).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);

                        await client.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""").ConfigureAwait(false);
                        JsonProbe tool = McpStrictSuites.Next(client).Get("result").Get("tools")[0];
                        JsonProbe properties = tool.Get("inputSchema").Get("properties");
                        TestAssert.True(properties.Get("any").IsObject && properties.Get("any").Length == 0, "true becomes {}.");
                        TestAssert.True(properties.Get("never").IsObject && properties.Get("never").Get("not").IsObject, "false becomes {\"not\":{}}.");
                        TestAssert.True(tool.Get("outputSchema").Get("properties").Get("ok").IsObject, "Output schemas are converted too.");

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{" + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        JsonProbe modern = McpStrictSuites.Next(client).Get("result").Get("tools")[0].Get("inputSchema").Get("properties");
                        TestAssert.True(modern.Get("any").Bool() && !modern.Get("any").IsObject, "2026-07-28 receives true as written.");

                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "bools", """{"never":1}""").ConfigureAwait(false), "false still rejects the property.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "bools", """{"any":[1]}""").ConfigureAwait(false), "true still accepts anything.");
                    }),

                    Case(suiteId, "ObjectOnlyOutputOnHandshakeSessions", "A typeless object-only output schema is advertised as type object before 2026-07-28, so a non-object result there is -32603; 2026-07-28 accepts it", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("list", "Returns an array", new { type = "object" }, McpStrictSuites.Schema("""{"properties":{"n":{"type":"integer"}}}"""), args => McpToolCallResult.FromStructured(new[] { 1, 2 }));
                            s.RegisterTool("obj", "Returns an object", new { type = "object" }, McpStrictSuites.Schema("""{"properties":{"n":{"type":"integer"}}}"""), args => new { n = 1 });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);

                        await client.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list","arguments":{}}}""").ConfigureAwait(false);
                        TestAssert.Equal(-32603, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "A 2025-11-25 session cannot receive an array under an object schema.");
                        await client.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"obj","arguments":{}}}""").ConfigureAwait(false);
                        TestAssert.Equal(1, McpStrictSuites.Next(client).Get("result").Get("structuredContent").Get("n").Int(), "An object result is sent.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"list\",\"arguments\":{}," + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Get("result").Get("structuredContent").IsArray, "2026-07-28 receives the array.");
                    }),
                });
        }

        /// <summary>
        /// Lifecycle cases on stream transports and clients.
        /// </summary>
        public static TestSuiteDescriptor Lifecycle()
        {
            const string suiteId = "McpStreams.Rigor";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP lifecycle rigor on stream transports and clients",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "RejectedIdsAreFinished", "A cancellation for an ID the server rejected (malformed request, refused batch element) or that preceded the rejection never cancels a later request reusing the ID", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);

                        await client.SendAsync("""{"jsonrpc":"2.0","id":7,"method":5}""").ConfigureAwait(false);
                        TestAssert.Equal(-32600, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "The malformed request is rejected.");
                        await client.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""").ConfigureAwait(false);
                        await client.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Get("result").Has("tools"), "A late cancellation for a malformed request is ignored.");

                        await client.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9}}""").ConfigureAwait(false);
                        await client.SendAsync("""{"jsonrpc":"2.0","id":9,"method":5}""").ConfigureAwait(false);
                        TestAssert.Equal(-32600, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "The malformed request is rejected.");
                        await client.SendAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Get("result").Has("tools"), "A cancellation spent on the rejected request does not cancel the reuse.");

                        await using TcpJsonRpcFixture batching = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient older = await RawLineClient.ConnectAsync(batching.Port, ct).ConfigureAwait(false);
                        await older.InitializeAsync("2025-03-26", ct).ConfigureAwait(false);
                        await older.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/list\",\"params\":{" + McpStrictSuites.Meta("{}") + "}}]").ConfigureAwait(false);
                        TestAssert.Equal(-32600, McpStrictSuites.Next(older)[0].Get("error").Get("code").Int(), "The stateless batch element is refused.");
                        await older.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":4}}""").ConfigureAwait(false);
                        await older.SendAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/list"}""").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(older).Get("result").Has("tools"), "A late cancellation for a refused batch element is ignored.");
                    }),

                    Case(suiteId, "TimedOutPingIsCancelled", "When a ping to the client times out, the server sends notifications/cancelled for it", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => { s.PingIntervalMs = 200; s.PingTimeoutMs = 200; s.PingFailureThreshold = 0; }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        string? pingId = null;
                        bool cancelled = false;
                        DateTime deadline = DateTime.UtcNow + _Wait;
                        while (!cancelled && DateTime.UtcNow < deadline)
                        {
                            string? line = client.Receive(TimeSpan.FromSeconds(2));
                            if (line == null) continue;
                            JsonProbe message = TestJson.ParseRoot(line);
                            string? method = message.Has("method") ? message.Get("method").String() : null;
                            if (method == "ping" && pingId == null) pingId = message.Get("id").String();
                            if (method == "notifications/cancelled" && pingId != null && message.Get("params").Get("requestId").String() == pingId) cancelled = true;
                        }

                        TestAssert.True(pingId != null, "The server pinged.");
                        TestAssert.True(cancelled, "The unanswered ping was cancelled.");
                    }),

                    Case(suiteId, "InitializeResponsePrecedesNotifications", "While tools are registered continuously, the first message every new connection receives is its initialize response", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        McpTcpServer server = (McpTcpServer)fixture.Server;
                        using CancellationTokenSource churnStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        Task churn = Task.Run(async () =>
                        {
                            int round = 0;
                            while (!churnStop.IsCancellationRequested)
                            {
                                server.RegisterTool("churn" + (round % 4), "Churn", new { type = "object" }, args => "x");
                                round++;
                                await Task.Yield();
                            }
                        });

                        try
                        {
                            for (int i = 0; i < 15; i++)
                            {
                                using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                                string first = await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                                TestAssert.Equal("init", TestJson.ParseRoot(first).Get("id").String(), $"Connection {i}: the initialize response comes first: {first}");
                            }
                        }
                        finally
                        {
                            churnStop.Cancel();
                            await churn.ConfigureAwait(false);
                        }
                    }),

                    Case(suiteId, "DuplicateIdInitializeCanBeRetried", "An initialize refused because its ID is in use leaves initialize available", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                            s.RegisterTool("slow", "Waits", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await Task.Delay(1500, token).ConfigureAwait(false);
                                return "done";
                            })).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}," + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        await client.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""").ConfigureAwait(false);
                        JsonProbe refused = McpStrictSuites.Next(client);
                        TestAssert.Equal(-32600, refused.Get("error").Get("code").Int(), "The duplicate ID is refused.");
                        await client.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""").ConfigureAwait(false);

                        JsonProbe? initialized = null;
                        for (int i = 0; i < 2 && initialized == null; i++)
                        {
                            JsonProbe message = McpStrictSuites.Next(client);
                            if (message.Has("id") && message.Get("id").Int() == 2) initialized = message;
                        }

                        TestAssert.True(initialized != null && initialized.Get("result").Has("protocolVersion"), "The retried initialize succeeds.");
                    }),

                    Case(suiteId, "StdioServerWritesLf", "The stdio server ends each message with \\n alone", async ct =>
                    {
                        string[] arguments = McpStdioIntegrationSuites.TestServerArguments();
                        ProcessStartInfo start = new ProcessStartInfo("dotnet")
                        {
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            StandardInputEncoding = new UTF8Encoding(false),
                        };
                        foreach (string argument in arguments) start.ArgumentList.Add(argument);
                        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The test server did not start.");
                        process.ErrorDataReceived += (sender, e) => { };
                        process.BeginErrorReadLine();
                        try
                        {
                            process.StandardInput.NewLine = "\n";
                            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""").ConfigureAwait(false);
                            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

                            Stream output = process.StandardOutput.BaseStream;
                            List<byte> line = new List<byte>();
                            byte[] buffer = new byte[1];
                            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            limit.CancelAfter(TimeSpan.FromSeconds(30));
                            while (await output.ReadAsync(buffer, 0, 1, limit.Token).ConfigureAwait(false) == 1)
                            {
                                line.Add(buffer[0]);
                                if (buffer[0] == (byte)'\n') break;
                            }

                            TestAssert.True(line.Count > 1 && line[line.Count - 1] == (byte)'\n', "A full line arrived.");
                            TestAssert.True(line[line.Count - 2] != (byte)'\r', "The line ends with \\n, not \\r\\n.");
                        }
                        finally
                        {
                            try
                            {
                                process.Kill(true);
                            }
                            catch (InvalidOperationException)
                            {
                            }
                        }
                    }),

                    Case(suiteId, "ClientAnswersNullIdAndNonObjectResults", "A client answers a request with a null ID with -32600 (id null) and a handler result that is not an object with -32603", async ct =>
                    {
                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0 };
                        client.RegisterRequestHandler("custom/text", (_, _) => Task.FromResult<object?>("just text"));
                        client.RegisterRequestHandler("custom/object", (_, _) => Task.FromResult<object?>(new { ok = true }));
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client connects.");
                        await accepting.ConfigureAwait(false);

                        await peer.SendAsync("""{"jsonrpc":"2.0","id":null,"method":"ping"}""", ct).ConfigureAwait(false);
                        JsonProbe nullId = TestJson.ParseRoot((await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                        TestAssert.Equal(-32600, nullId.Get("error").Get("code").Int(), "A null ID is an invalid request.");
                        TestAssert.True(nullId.Has("id") && nullId.Get("id").IsNull, "The error carries a null ID.");

                        await peer.SendAsync("""{"jsonrpc":"2.0","id":3,"method":"custom/text"}""", ct).ConfigureAwait(false);
                        JsonProbe text = TestJson.ParseRoot((await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                        TestAssert.Equal(-32603, text.Get("error").Get("code").Int(), "A non-object result is an internal error.");

                        await peer.SendAsync("""{"jsonrpc":"2.0","id":4,"method":"custom/object"}""", ct).ConfigureAwait(false);
                        JsonProbe obj = TestJson.ParseRoot((await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false))!);
                        TestAssert.True(obj.Get("result").Get("ok").Bool(), "An object result is sent.");
                    }),

                    Case(suiteId, "ClientIgnoresLateCancellations", "A cancellation from the server for a request the client already answered does not cancel a later request that reuses the ID", async ct =>
                    {
                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0 };
                        int handled = 0;
                        client.RegisterRequestHandler("custom/work", (_, _) => { Interlocked.Increment(ref handled); return Task.FromResult<object?>(new { }); });
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client connects.");
                        await accepting.ConfigureAwait(false);

                        await peer.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"custom/work"}""", ct).ConfigureAwait(false);
                        TestAssert.True((await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false)) != null, "The first request is answered.");
                        await peer.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1,"reason":"late"}}""", ct).ConfigureAwait(false);
                        await peer.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"custom/work"}""", ct).ConfigureAwait(false);
                        string? second = await peer.ReceiveAsync(_Wait, ct).ConfigureAwait(false);
                        TestAssert.True(second != null && TestJson.ParseRoot(second).Has("result"), $"The reused ID is answered: {second}");
                        TestAssert.Equal(2, Volatile.Read(ref handled), "Both requests ran.");
                    }),
                });
        }

        /// <summary>
        /// Streamable HTTP and HTTP client cases.
        /// </summary>
        public static TestSuiteDescriptor Http()
        {
            const string suiteId = "McpHttp.Rigor";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP Streamable HTTP rigor",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "EventsStreamEndsWithItsSession", "The legacy /events stream ends when its session is deleted", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.EnableLegacyEndpoints = true).ConfigureAwait(false);
                        string session = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        using HttpRequestMessage open = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/events?session={session}");
                        using HttpResponseMessage stream = await fixture.Client.SendAsync(open, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, stream.StatusCode, "The stream opens.");
                        using Stream body = await stream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                        await McpHttpTestRequests.DeleteSessionAsync(fixture, session, null, ct).ConfigureAwait(false);
                        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        limit.CancelAfter(TimeSpan.FromSeconds(5));
                        byte[] buffer = new byte[4096];
                        bool ended = false;
                        try
                        {
                            while (await body.ReadAsync(buffer, 0, buffer.Length, limit.Token).ConfigureAwait(false) > 0)
                            {
                            }

                            ended = true;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                        }
                        catch (Exception closed) when (closed is IOException || closed is HttpRequestException)
                        {
                            ended = true;
                        }

                        TestAssert.True(ended, "The stream ended with its session.");
                    }),

                    Case(suiteId, "ResumableStreamLimitIsConfigurable", "MaxResumableStreamsPerSession defaults to 8, accepts 1 to 1000, and rejects values outside that range", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("127.0.0.1", TestPorts.GetFreePort());
                        TestAssert.Equal(8, server.MaxResumableStreamsPerSession, "The default is 8.");
                        server.MaxResumableStreamsPerSession = 1;
                        TestAssert.Equal(1, server.MaxResumableStreamsPerSession, "The minimum is accepted.");
                        server.MaxResumableStreamsPerSession = 1000;
                        TestAssert.Equal(1000, server.MaxResumableStreamsPerSession, "The maximum is accepted.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.MaxResumableStreamsPerSession = 0, "0 is rejected.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.MaxResumableStreamsPerSession = 1001, "1001 is rejected.");
                        TestAssert.Equal(1000, server.MaxResumableStreamsPerSession, "A rejected value leaves the setting unchanged.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ChallengeUrlServesTheMetadata", "The resource_metadata URL Voltaic adds to a Bearer challenge serves the document for that resource; the root form does not", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        int port = fixture.Port;
                        string resource = $"http://localhost:{port}/mcp";
                        fixture.Server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = resource, AuthorizationServers = new List<string> { "https://auth.example.com" } };
                        fixture.Server.AuthenticationHandler = _ => Task.FromResult(AuthenticationResult.BearerChallenge(null, "invalid_token"));

                        using HttpRequestMessage post = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp")
                        {
                            Content = new StringContent(McpHttpTestRequests.BuildBody("tools/list", 1, null), Encoding.UTF8, "application/json")
                        };
                        post.Headers.Accept.ParseAdd("application/json");
                        post.Headers.Accept.ParseAdd("text/event-stream");
                        using HttpResponseMessage denied = await fixture.Client.SendAsync(post, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode, "The request is challenged.");
                        string challenge = denied.Headers.WwwAuthenticate.ToString();
                        System.Text.RegularExpressions.Match advertised = System.Text.RegularExpressions.Regex.Match(challenge, "resource_metadata=\"([^\"]+)\"");
                        TestAssert.True(advertised.Success, $"The challenge names the metadata URL: {challenge}");
                        TestAssert.Equal($"http://localhost:{port}{McpProtocol.ProtectedResourceMetadataPath}/mcp", advertised.Groups[1].Value, "The URL is derived from the resource.");

                        using HttpResponseMessage metadata = await fixture.Client.GetAsync(advertised.Groups[1].Value, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, metadata.StatusCode, "The advertised URL serves the document.");
                        TestAssert.Equal(resource, JsonProbe.Parse(await metadata.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Get("resource").String(), "The document names the resource it was derived from.");
                        using HttpResponseMessage root = await fixture.Client.GetAsync($"http://localhost:{port}{McpProtocol.ProtectedResourceMetadataPath}", ct).ConfigureAwait(false);
                        TestAssert.True(root.StatusCode != HttpStatusCode.OK, $"The root form is not served (got {(int)root.StatusCode}).");
                    }),

                    Case(suiteId, "ProtectedResourceMetadataAtTheRootForAPathlessResource", "A resource without a path has its metadata at the root well-known URL only", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s =>
                            s.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "https://mcp.example.com", AuthorizationServers = new List<string> { "https://auth.example.com" } }).ConfigureAwait(false);
                        using HttpResponseMessage root = await fixture.Client.GetAsync($"{fixture.BaseUrl}{McpProtocol.ProtectedResourceMetadataPath}", ct).ConfigureAwait(false);
                        using HttpResponseMessage scoped = await fixture.Client.GetAsync($"{fixture.BaseUrl}{McpProtocol.ProtectedResourceMetadataPath}/mcp", ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, root.StatusCode, "The root form matches the resource.");
                        TestAssert.Equal(HttpStatusCode.NotFound, scoped.StatusCode, "The path form does not.");
                    }),

                    Case(suiteId, "StatelessRequestsCannotBeBatchedOverHttp", "On a 2025-03-26 session, a batched request naming 2026-07-28 gets -32600 while the others are served, and a late cancellation for it is ignored", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string session = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-03-26", null, ct).ConfigureAwait(false)).SessionId!;
                        Dictionary<string, string> headers = new Dictionary<string, string> { { McpProtocol.SessionIdHeader, session }, { McpProtocol.ProtocolVersionHeader, "2025-03-26" } };
                        RpcResult batch = await McpHttpTestRequests.SendAsync(fixture, "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{" + McpStrictSuites.Meta("{}") + "}}]", headers, true, ct).ConfigureAwait(false);
                        List<JsonProbe> answers = TestJson.ParseRoot(batch.Body).EnumerateArray().ToList();
                        TestAssert.True(answers.First(a => a.Get("id").Int() == 1).Has("result"), $"The handshake request is served: {batch.Body}");
                        TestAssert.Equal(-32600, answers.First(a => a.Get("id").Int() == 2).Get("error").Get("code").Int(), "The stateless request is refused.");

                        await McpHttpTestRequests.SendAsync(fixture, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}""", headers, true, ct).ConfigureAwait(false);
                        RpcResult reused = await McpHttpTestRequests.SendAsync(fixture, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", headers, true, ct).ConfigureAwait(false);
                        TestAssert.True(reused.Result.Has("tools"), $"The reused ID is answered: {reused.Body}");
                    }),

                    Case(suiteId, "StatelessSignalsTolerateOtherMembers", "A stateless tools/call whose params carry a member of an unexpected type (uri: 5) is still routed and served", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("t", "Tool", new { type = "object" }, args => "ok")).ConfigureAwait(false);
                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "name", "t" }, { "arguments", new { } }, { "uri", 5 } };
                        RpcResult result = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/call", 1, parameters, "t", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, result.StatusCode, $"The request is served: {result.Body}");
                        TestAssert.Equal("ok", result.Result.Get("content")[0].Get("text").String(), "The tool ran.");
                    }),

                    Case(suiteId, "StatelessClientGenericCallsAndReset", "In stateless mode CallAsync<T> sends stateless requests; Disconnect leaves stateless mode, so a later Streamable connection uses a session", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("t", "Tool", new { type = "object" }, args => "ok")).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient { PingIntervalMs = 0 };
                        TestAssert.True(await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects statelessly.");
                        JsonElement listed = await client.CallAsync<JsonElement>("tools/list", null, 0, ct).ConfigureAwait(false);
                        TestAssert.Equal("t", listed.GetProperty("tools")[0].GetProperty("name").GetString(), "The generic call works in stateless mode.");

                        client.Disconnect();
                        TestAssert.False(client.IsStateless, "Disconnect leaves stateless mode.");
                        TestAssert.True(await client.ConnectStreamableAsync(fixture.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client reconnects with a handshake.");
                        TestAssert.False(client.IsStateless, "The new connection is not stateless.");
                        TestAssert.True(!String.IsNullOrEmpty(client.SessionId), "A session was issued.");
                        JsonRpcResponse again = await client.CallAsync("tools/list", null, 0, ct).ConfigureAwait(false);
                        TestAssert.True(again.Error == null, $"Session requests work: {again.Error?.Message}");
                    }),

                    Case(suiteId, "StatelessClientHonorsTimeoutAndReissues", "A stateless call honors its timeoutMs, and a response body that breaks off is re-issued once with a new ID", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer { IssueSession = false };
                        List<string> flakyIds = new List<string>();
                        server.RequestHandler = async (method, idJson, body, response, token) =>
                        {
                            if (method == "slow")
                            {
                                await Task.Delay(5000, token).ConfigureAwait(false);
                            }
                            else if (method == "flaky")
                            {
                                int attempt;
                                lock (flakyIds)
                                {
                                    flakyIds.Add(idJson);
                                    attempt = flakyIds.Count;
                                }

                                if (attempt == 1)
                                {
                                    byte[] partial = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",");
                                    response.StatusCode = 200;
                                    response.ContentType = "application/json";
                                    response.ContentLength64 = 400;
                                    await response.OutputStream.WriteAsync(partial, 0, partial.Length, token).ConfigureAwait(false);
                                    await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
                                    response.Abort();
                                    return;
                                }
                            }

                            string result = method == "server/discover"
                                ? "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{},\"serverInfo\":{\"name\":\"f\",\"version\":\"1\"},\"resultType\":\"complete\"}"
                                : "{\"resultType\":\"complete\",\"value\":1}";
                            await FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + result + "}", token).ConfigureAwait(false);
                        };

                        using McpHttpClient client = new McpHttpClient { PingIntervalMs = 0 };
                        TestAssert.True(await client.ConnectStatelessAsync(server.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects.");

                        Stopwatch elapsed = Stopwatch.StartNew();
                        bool timedOut = false;
                        try
                        {
                            await client.CallAsync("slow", null, 300, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            timedOut = true;
                        }

                        TestAssert.True(timedOut, "The call timed out.");
                        TestAssert.True(elapsed.ElapsedMilliseconds < 3000, $"The per-call timeout applied ({elapsed.ElapsedMilliseconds} ms).");

                        JsonRpcResponse flaky = await client.CallAsync("flaky", null, 0, ct).ConfigureAwait(false);
                        TestAssert.True(flaky.Error == null, $"The re-issued request succeeds: {flaky.Error?.Message}");
                        lock (flakyIds)
                        {
                            TestAssert.Equal(2, flakyIds.Count, "The request was sent twice.");
                            TestAssert.True(flakyIds[0] != flakyIds[1], "The re-issued request has a new ID.");
                        }
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "rigor", "conformance" });
        }
    }
}
