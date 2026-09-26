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
    /// Covers the requirements closed in v2.1.10: ECMA-262 pattern syntax (class re-wrapping, <c>\u{...}</c>, Unicode
    /// property names, rejected .NET-only syntax, Annex B hyphens), empty property names, draft-07 structure rules,
    /// tool names, object output only where the schema is advertised, URI template query expressions, request methods
    /// sent without an ID, caching hints on every cacheable result, elicitation modes, stateless input answered by
    /// registered handlers, stateless <c>_meta</c> without a version on stream transports, <c>x-mcp-header</c> rules for
    /// other servers' tools, resumption without a session, rejected notifications and responses, an MCP endpoint at the
    /// root, and resource identifiers.
    /// </summary>
    public static class McpClosureSuites
    {
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// JSON Schema and feature cases.
        /// </summary>
        public static TestSuiteDescriptor Schemas()
        {
            const string suiteId = "Mcp.SchemaClosure";
            return new TestSuiteDescriptor(
                suiteId,
                "JSON Schema syntax, structure, and feature rules",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "PatternSyntaxFollowsEcma262", "Classes next to negated shorthands keep their meaning, \\u{...} and ECMA-262 property names work, Annex B hyphens are literal, and .NET-only syntax is rejected", async ct =>
                    {
                        string schema = "{\"type\":\"object\",\"properties\":{"
                            + "\"caret\":{\"type\":\"string\",\"pattern\":\"^[\\\\S^]$\"},"
                            + "\"notcaret\":{\"type\":\"string\",\"pattern\":\"^[^^\\\\S]$\"},"
                            + "\"code\":{\"type\":\"string\",\"pattern\":\"^\\\\u{41}$\"},"
                            + "\"letter\":{\"type\":\"string\",\"pattern\":\"^\\\\p{Letter}+$\"},"
                            + "\"upper\":{\"type\":\"string\",\"pattern\":\"^\\\\p{gc=Lu}$\"},"
                            + "\"hyphen\":{\"type\":\"string\",\"pattern\":\"^[\\\\w-.]+$\"}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("p", "Patterns", McpStrictSuites.Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);

                        string[] valid = { "{\"caret\":\"^\"}", "{\"caret\":\"a\"}", "{\"notcaret\":\" \"}", "{\"code\":\"A\"}", "{\"letter\":\"h\\u00e9\"}", "{\"upper\":\"Q\"}", "{\"hyphen\":\"a-b.c\"}" };
                        string[] invalid = { "{\"caret\":\" \"}", "{\"notcaret\":\"^\"}", "{\"notcaret\":\"a\"}", "{\"code\":\"B\"}", "{\"letter\":\"a1\"}", "{\"upper\":\"q\"}", "{\"hyphen\":\"a b\"}" };
                        foreach (string arguments in valid) TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "p", arguments).ConfigureAwait(false), $"Matches: {arguments}");
                        foreach (string arguments in invalid) TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "p", arguments).ConfigureAwait(false), $"Does not match: {arguments}");

                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        foreach (string pattern in new[] { "^a\\\\Z", "(?i)a", "(?#note)a", "(?>a)", "\\\\p{Script=Greek}", "a\\\\z" })
                        {
                            TestAssert.Throws<ArgumentException>(
                                () => server.RegisterTool("bad", "Bad pattern", McpStrictSuites.Schema("{\"type\":\"object\",\"properties\":{\"s\":{\"type\":\"string\",\"pattern\":\"" + pattern + "\"}}}"), args => "x"),
                                $"Syntax ECMA-262 does not define is rejected: {pattern}");
                        }
                    }),

                    Case(suiteId, "EmptyPropertyNamesAreRequired", "An empty property name in required and dependentRequired is enforced like any other", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("req", "Requires the empty name", McpStrictSuites.Schema("{\"type\":\"object\",\"required\":[\"\"]}"), args => "ok");
                            s.RegisterTool("dep", "a requires the empty name", McpStrictSuites.Schema("{\"type\":\"object\",\"dependentRequired\":{\"a\":[\"\"]}}"), args => "ok");
                        }).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "req", "{}").ConfigureAwait(false), "A missing empty-named property fails required.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "req", "{\"\":1}").ConfigureAwait(false), "Present, it passes.");
                        TestAssert.True(await McpStrictSuites.IsErrorAsync(client, "dep", "{\"a\":1}").ConfigureAwait(false), "dependentRequired enforces the empty name.");
                        TestAssert.False(await McpStrictSuites.IsErrorAsync(client, "dep", "{\"a\":1,\"\":2}").ConfigureAwait(false), "Satisfied, it passes.");
                    }),

                    Case(suiteId, "Draft07StructureIsDialectExact", "draft-07 checks references beside $ref and ignores $ref siblings; allows items []; each dialect checks only its own definitions keyword", ct =>
                    {
                        const string draft07 = "\"$schema\":\"http://json-schema.org/draft-07/schema#\"";
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        TestAssert.Throws<ArgumentException>(
                            () => server.RegisterTool("missing", "Unresolvable nested reference", McpStrictSuites.Schema("{" + draft07 + ",\"$ref\":\"#/definitions/a\",\"definitions\":{\"a\":{\"type\":\"object\",\"properties\":{\"b\":{\"$ref\":\"#/definitions/missing\"}}}}}"), args => "x"),
                            "A reference under the siblings of a draft-07 $ref is still checked.");
                        server.RegisterTool("siblings", "Ignored siblings", McpStrictSuites.Schema("{" + draft07 + ",\"type\":\"object\",\"properties\":{\"a\":{\"$ref\":\"#/definitions/s\",\"type\":\"strin\"}},\"definitions\":{\"s\":{\"type\":\"string\"}}}"), args => "x");
                        server.RegisterTool("emptyitems", "draft-07 items []", McpStrictSuites.Schema("{" + draft07 + ",\"type\":\"object\",\"properties\":{\"t\":{\"type\":\"array\",\"items\":[]}}}"), args => "x");
                        server.RegisterTool("olddefs", "2020-12 ignores definitions", McpStrictSuites.Schema("{\"type\":\"object\",\"definitions\":\"x\"}"), args => "x");
                        server.RegisterTool("newdefs", "draft-07 ignores $defs", McpStrictSuites.Schema("{" + draft07 + ",\"type\":\"object\",\"$defs\":\"x\"}"), args => "x");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("baddefs", "2020-12 checks $defs", McpStrictSuites.Schema("{\"type\":\"object\",\"$defs\":\"x\"}"), args => "x"), "2020-12 checks $defs.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ToolNamesRejectTrailingNewline", "A tool name ending with a newline is rejected", ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("abc\n", "Newline", new { type = "object" }, args => "x"), "The newline is not a name character.");
                        server.RegisterTool("abc", "Plain", new { type = "object" }, args => "x");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ObjectOutputOnlyWhereAdvertised", "A typeless object-only output schema requires object output on 2025-06-18 and later handshake sessions, not on 2025-03-26, which never sees the schema", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                            s.RegisterTool("text", "Returns a string", new { type = "object" }, McpStrictSuites.Schema("{\"properties\":{\"a\":true}}"), args => McpToolCallResult.FromStructured("just a string"))).ConfigureAwait(false);
                        using RawLineClient older = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await older.InitializeAsync("2025-03-26", ct).ConfigureAwait(false);
                        await older.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"text\",\"arguments\":{}}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(older).Has("result"), "2025-03-26 receives the text.");

                        using RawLineClient newer = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await newer.InitializeAsync("2025-06-18", ct).ConfigureAwait(false);
                        await newer.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"text\",\"arguments\":{}}}").ConfigureAwait(false);
                        TestAssert.Equal(-32603, McpStrictSuites.Next(newer).Get("error").Get("code").Int(), "2025-06-18 sees an object schema, so a string is a server fault.");
                    }),

                    Case(suiteId, "UriTemplateQueryContinuations", "{&var} variables after a {?var} expression are extracted", async ct =>
                    {
                        Dictionary<string, string>? seen = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                            s.RegisterResourceTemplate(new McpResourceTemplate { UriTemplate = "x://y/{id}{?page}{&limit}", Name = "paged", MimeType = "text/plain" }, (uri, variables, token) =>
                            {
                                seen = new Dictionary<string, string>(variables);
                                return Task.FromResult(new McpReadResourceResult { Contents = new List<object> { new McpTextResourceContents { Uri = uri, MimeType = "text/plain", Text = "ok" } } });
                            })).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/read\",\"params\":{\"uri\":\"x://y/7?page=2&limit=5\"}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Has("result"), "The URI matches.");
                        TestAssert.True(seen != null && seen["id"] == "7" && seen["page"] == "2" && seen.TryGetValue("limit", out string? limit) && limit == "5", "id, page, and limit are all extracted.");
                    }),
                });
        }

        /// <summary>
        /// Stream transport and stateless cases.
        /// </summary>
        public static TestSuiteDescriptor Streams()
        {
            const string suiteId = "McpStreams.Closure";
            return new TestSuiteDescriptor(
                suiteId,
                "Request IDs, caching hints, and stateless rules",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "RequestMethodsWithoutIdNeverRun", "A request method sent without an id is not run, before or after initialize, handshake-era or stateless", async ct =>
                    {
                        int runs = 0;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("danger", "Counts calls", new { type = "object" }, args => { Interlocked.Increment(ref runs); return "ran"; })).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"name\":\"danger\",\"arguments\":{}," + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"name\":\"danger\",\"arguments\":{}}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"danger\",\"arguments\":{}}}").ConfigureAwait(false);
                        JsonProbe answered = McpStrictSuites.Next(client);
                        TestAssert.Equal(9, answered.Get("id").Int(), "Only the request with an id is answered.");
                        TestAssert.Equal(1, Volatile.Read(ref runs), "Only the request with an id ran.");
                    }),

                    Case(suiteId, "CancellationFollowingItsRequestAlwaysApplies", "A cancellation sent right after its request always suppresses it, including a legitimately reused 2026-07-28 ID", async ct =>
                    {
                        int runs = 0;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("slow", "Waits", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                        {
                            Interlocked.Increment(ref runs);
                            await Task.Delay(300, token).ConfigureAwait(false);
                            return "done";
                        })).ConfigureAwait(false);
                        using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                        string meta = McpStrictSuites.Meta("{}");

                        for (int i = 0; i < 40; i++)
                        {
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"c" + i + "\",\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}" + (i % 2 == 0 ? "," + meta : "") + "}}").ConfigureAwait(false);
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"c" + i + "\"" + (i % 2 == 0 ? "," + meta : "") + "}}").ConfigureAwait(false);
                        }

                        // A stateless ID answered once may be used again; a cancellation for the reuse applies to it.
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"reuse\",\"method\":\"tools/list\",\"params\":{" + meta + "}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Get("result").Has("tools"), "The first use is answered.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"reuse\",\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}," + meta + "}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"reuse\"," + meta + "}}").ConfigureAwait(false);

                        await Task.Delay(800, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"after\",\"method\":\"ping\"}").ConfigureAwait(false);
                        TestAssert.Equal("after", McpStrictSuites.Next(client).Get("id").String(), "No cancelled request was answered.");
                        TestAssert.True(client.Receive(_Quiet) == null, "Nothing else arrives.");
                    }),

                    Case(suiteId, "StdioServerExitsWhenPingsGoUnanswered", "The stdio server stops, and its process exits, when the client leaves a ping unanswered", async ct =>
                    {
                        System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo("dotnet")
                        {
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            StandardInputEncoding = new UTF8Encoding(false),
                        };
                        foreach (string argument in McpStdioIntegrationSuites.TestServerArguments()) start.ArgumentList.Add(argument);
                        start.Environment["VOLTAIC_PING_MS"] = "300";
                        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The test server did not start.");
                        process.OutputDataReceived += (sender, e) => { };
                        process.ErrorDataReceived += (sender, e) => { };
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        try
                        {
                            process.StandardInput.NewLine = "\n";
                            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw\",\"version\":\"1\"}}}").ConfigureAwait(false);
                            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}").ConfigureAwait(false);
                            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

                            // Stdin stays open and no ping is answered: the connection has failed.
                            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            limit.CancelAfter(TimeSpan.FromSeconds(20));
                            bool exited;
                            try
                            {
                                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                                exited = true;
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                exited = false;
                            }

                            TestAssert.True(exited, "The server process exited after the unanswered ping.");
                        }
                        finally
                        {
                            try
                            {
                                if (!process.HasExited) process.Kill(true);
                            }
                            catch (InvalidOperationException)
                            {
                            }
                        }
                    }),

                    Case(suiteId, "InitializeTimeoutIsConfigurable", "InitializeTimeoutMs and InitializeAsync(timeoutMs) bound the handshake; values outside 100 to 600000 are rejected", async ct =>
                    {
                        using RawTcpPeer peer = RawTcpPeer.Listen();
                        using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 0 };
                        TestAssert.Equal(30000, client.InitializeTimeoutMs, "The default is 30 seconds.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => client.InitializeTimeoutMs = 99, "Below the minimum is rejected.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => client.InitializeTimeoutMs = 600001, "Above the maximum is rejected.");
                        Task accepting = peer.AcceptAsync(ct);
                        TestAssert.True(await client.ConnectAsync("127.0.0.1", peer.Port, ct).ConfigureAwait(false), "The client connects.");
                        await accepting.ConfigureAwait(false);

                        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                        bool failed = false;
                        try
                        {
                            await client.InitializeAsync(400, ct).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is OperationCanceledException || error is TimeoutException || error is InvalidOperationException)
                        {
                            failed = true;
                        }

                        TestAssert.True(failed, "An unanswered initialize fails.");
                        TestAssert.True(elapsed.ElapsedMilliseconds < 5000, $"It fails after the per-call timeout ({elapsed.ElapsedMilliseconds} ms).");
                    }),

                    Case(suiteId, "ProgressIsRateLimited", "A server coalesces progress updates closer than ProgressIntervalMs and always sends the final one; 0 sends every update", async ct =>
                    {
                        async Task<List<double>> RunAsync(int interval)
                        {
                            await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                            {
                                s.ProgressIntervalMs = interval;
                                s.RegisterTool("busy", "Reports progress", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                                {
                                    McpToolCallContext call = McpToolCallContext.Current!;
                                    for (int step = 1; step <= 10; step++) await call.ReportProgressAsync(step, 10, null, token).ConfigureAwait(false);
                                    return "done";
                                });
                            }).ConfigureAwait(false);
                            using RawLineClient client = await McpStrictSuites.InitializedAsync(fixture, ct).ConfigureAwait(false);
                            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"busy\",\"arguments\":{},\"_meta\":{\"progressToken\":\"p\"}}}").ConfigureAwait(false);
                            List<double> seen = new List<double>();
                            while (true)
                            {
                                JsonProbe message = McpStrictSuites.Next(client);
                                if (message.Has("id")) break;
                                if (message.Get("method").String() == "notifications/progress") seen.Add(message.Get("params").Get("progress").Double());
                            }

                            return seen;
                        }

                        List<double> limited = await RunAsync(60000).ConfigureAwait(false);
                        List<double> unlimited = await RunAsync(0).ConfigureAwait(false);
                        TestAssert.True(limited.Count == 2 && limited[0] == 1 && limited[1] == 10, $"Only the first and final updates are sent within the interval: {String.Join(",", limited)}");
                        TestAssert.Equal(10, unlimited.Count, "With 0 every update is sent.");
                    }),

                    Case(suiteId, "CachingHintsOnEveryCacheableResult", "A shared result instance served concurrently always carries ttlMs and cacheScope on 2026-07-28 and never on handshake sessions; so does a result from RegisterMethod", async ct =>
                    {
                        McpReadResourceResult shared = new McpReadResourceResult { Contents = new List<object> { new McpTextResourceContents { Uri = "voltaic://shared", MimeType = "text/plain", Text = "same" } } };
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s =>
                        {
                            s.RegisterResource("voltaic://shared", "shared", "text/plain", () => shared);
                            s.RegisterMethod("prompts/list", args => new { prompts = new object[0] });
                        }).ConfigureAwait(false);

                        Task<RpcResult>[] reads = Enumerable.Range(1, 40)
                            .Select(i => McpHttpTestRequests.SendStatelessAsync(fixture, "resources/read", i, new Dictionary<string, object?> { { "uri", "voltaic://shared" } }, "voltaic://shared", null, ct))
                            .ToArray();
                        string session = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-11-25", null, ct).ConfigureAwait(false)).SessionId!;
                        RpcResult handshake = await McpHttpTestRequests.SendAsync(fixture, "resources/read", 100, new { uri = "voltaic://shared" }, session, "2025-11-25", null, ct).ConfigureAwait(false);
                        RpcResult[] results = await Task.WhenAll(reads).ConfigureAwait(false);

                        foreach (RpcResult result in results)
                        {
                            TestAssert.True(result.Result.Has("ttlMs") && result.Result.Has("cacheScope"), $"Every stateless read carries the hints: {result.Body}");
                        }

                        TestAssert.False(handshake.Result.Has("ttlMs") || handshake.Result.Has("resultType"), $"The handshake read carries none: {handshake.Body}");
                        RpcResult custom = await McpHttpTestRequests.SendStatelessAsync(fixture, "prompts/list", 200, null, null, null, ct).ConfigureAwait(false);
                        TestAssert.True(custom.Result.Has("ttlMs") && custom.Result.Get("cacheScope").String() == "private", $"A RegisterMethod result gets the hints: {custom.Body}");
                    }),

                    Case(suiteId, "UnknownElicitationModeIsAServerFault", "An input request with an elicitation mode other than form or url is -32603, never -32021", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("bogus", "Bad mode", new { type = "object" }, args => new McpInputRequiredResult
                        {
                            InputRequests = new Dictionary<string, McpInputRequest> { { "x", new McpInputRequest { Method = "elicitation/create", Params = new { mode = "bogus", message = "?" } } } }
                        })).ConfigureAwait(false);
                        RpcResult result = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/call", 1, new Dictionary<string, object?> { { "name", "bogus" }, { "arguments", new { } } }, "bogus", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(-32603, result.Error.Get("code").Int(), $"The mode is a server fault: {result.Body}");
                    }),

                    Case(suiteId, "StatelessInputAnsweredByRegisteredHandlers", "Without a callback, CallToolStatelessAsync answers input requests with the handlers registered through RegisterRequestHandler", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("ask", "Asks for confirmation", new { type = "object" }, args =>
                        {
                            McpToolCallContext call = McpToolCallContext.Current!;
                            if (call.RequestState == "ask" && call.InputResponses.TryGetValue("c", out JsonElement answer))
                            {
                                return McpToolCallResult.FromText("got " + answer.GetProperty("action").GetString());
                            }

                            return new McpInputRequiredResult
                            {
                                InputRequests = new Dictionary<string, McpInputRequest> { { "c", new McpInputRequest { Method = "elicitation/create", Params = new { mode = "form", message = "Sure?", requestedSchema = new { type = "object" } } } } },
                                RequestState = "ask"
                            };
                        })).ConfigureAwait(false);

                        using McpHttpClient client = new McpHttpClient { PingIntervalMs = 0 };
                        client.RegisterRequestHandler("elicitation/create", (parameters, token) => Task.FromResult<object?>(new { action = "accept", content = new { } }));
                        TestAssert.True(await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects.");
                        JsonRpcResponse response = await client.CallToolStatelessAsync("ask", new { }, null, 3, ct).ConfigureAwait(false);
                        JsonProbe result = JsonProbe.From(response.Result);
                        TestAssert.Equal("got accept", result.Get("content")[0].Get("text").String(), $"The registered handler answered: {JsonSerializer.Serialize(response.Result)}");
                    }),

                    Case(suiteId, "StatelessMetaWithoutVersionIsInvalid", "On a stream transport, a request whose _meta carries the stateless client fields without a string protocol version gets -32602", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/clientCapabilities\":{},\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "A missing version is a missing required field.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":20260728,\"io.modelcontextprotocol/clientCapabilities\":{}}}}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, McpStrictSuites.Next(client).Get("error").Get("code").Int(), "A numeric version is invalid.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\",\"params\":{" + McpStrictSuites.Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.True(McpStrictSuites.Next(client).Get("result").Has("tools"), "A complete _meta is served.");
                    }),

                    Case(suiteId, "ClientKeepsToolsTheSpecificationAllows", "McpHttpClient keeps another server's tools whose x-mcp-header parameters are typed by enum or have wide integer bounds, and still drops invalid ones", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer { IssueSession = false };
                        server.RequestHandler = (method, idJson, body, response, token) =>
                        {
                            string result = method == "server/discover"
                                ? "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"f\",\"version\":\"1\"},\"resultType\":\"complete\"}"
                                : "{\"resultType\":\"complete\",\"tools\":["
                                    + "{\"name\":\"enumtyped\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"region\":{\"enum\":[\"eu\",\"us\"],\"x-mcp-header\":\"Region\"}}}},"
                                    + "{\"name\":\"widebounds\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"integer\",\"maximum\":1e16,\"x-mcp-header\":\"N\"}}}},"
                                    + "{\"name\":\"numbertyped\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"number\",\"x-mcp-header\":\"N\"}}}}]}";
                            return FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + result + "}", token);
                        };

                        using McpHttpClient client = new McpHttpClient { PingIntervalMs = 0 };
                        TestAssert.True(await client.ConnectStatelessAsync(server.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects.");
                        JsonRpcResponse listed = await client.CallAsync("tools/list", null, 0, ct).ConfigureAwait(false);
                        List<string?> names = JsonProbe.From(listed.Result).Get("tools").EnumerateArray().Select(tool => tool.Get("name").String()).ToList();
                        TestAssert.True(names.Contains("enumtyped") && names.Contains("widebounds"), $"Tools the specification allows are kept: {String.Join(",", names)}");
                        TestAssert.False(names.Contains("numbertyped"), "A number-typed header parameter is still invalid.");
                    }),
                });
        }

        /// <summary>
        /// Streamable HTTP cases.
        /// </summary>
        public static TestSuiteDescriptor Http()
        {
            const string suiteId = "McpHttp.Closure";
            return new TestSuiteDescriptor(
                suiteId,
                "Streamable HTTP message acceptance, resumption, and configuration",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "UnacceptableMessagesGetAnErrorStatus", "Notifications and responses the server cannot accept, request methods without an id, and batches of only such messages get 400; valid ones get 202", async ct =>
                    {
                        int runs = 0;
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("danger", "Counts calls", new { type = "object" }, args => { Interlocked.Increment(ref runs); return "ran"; })).ConfigureAwait(false);
                        string session = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-03-26", null, ct).ConfigureAwait(false)).SessionId!;
                        Dictionary<string, string> headers = new Dictionary<string, string> { { McpProtocol.SessionIdHeader, session } };
                        RpcResult badVersion = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"1.0\",\"method\":\"notifications/initialized\"}", headers, true, ct).ConfigureAwait(false);
                        RpcResult badResponse = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"1.0\",\"id\":1,\"result\":{}}", headers, true, ct).ConfigureAwait(false);
                        RpcResult noId = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"name\":\"danger\",\"arguments\":{}}}", headers, true, ct).ConfigureAwait(false);
                        RpcResult statelessBatch = await McpHttpTestRequests.SendAsync(fixture, "[{\"jsonrpc\":\"2.0\",\"method\":\"notifications/roots/list_changed\",\"params\":{" + McpStrictSuites.Meta("{}") + "}}]", headers, true, ct).ConfigureAwait(false);
                        RpcResult accepted = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/roots/list_changed\"}", headers, true, ct).ConfigureAwait(false);
                        RpcResult statelessNoId = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"name\":\"danger\",\"arguments\":{}," + McpStrictSuites.Meta("{}") + "}}", new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 } }, true, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.BadRequest, badVersion.StatusCode, "A notification with the wrong jsonrpc gets 400.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode, "A response with the wrong jsonrpc gets 400.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, noId.StatusCode, "A request method without an id gets 400.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, statelessBatch.StatusCode, "A batch of only a refused notification gets 400.");
                        TestAssert.Equal(HttpStatusCode.Accepted, accepted.StatusCode, "A valid notification gets 202.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, statelessNoId.StatusCode, "A stateless request method without an id gets 400.");
                        TestAssert.Equal(0, Volatile.Read(ref runs), "The tool never ran.");
                    }),

                    Case(suiteId, "ClientResumesWithoutASession", "On a server without sessions, McpHttpClient resumes a POST response stream the server closed after its priming event", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer { IssueSession = false };
                        string? pendingId = null;
                        server.RequestHandler = async (method, idJson, body, response, token) =>
                        {
                            if (method != "slow")
                            {
                                await FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":{}}", token).ConfigureAwait(false);
                                return;
                            }

                            pendingId = idJson;
                            FakeStreamableHttpServer.BeginSse(response);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "id: p1\nretry: 50\ndata:\n\n", token).ConfigureAwait(false);
                            response.Close();
                        };
                        server.GetHandler = async (index, response, token) =>
                        {
                            FakeStreamableHttpServer.BeginSse(response);
                            await FakeStreamableHttpServer.WriteSseAsync(response, "id: p2\ndata: {\"jsonrpc\":\"2.0\",\"id\":" + pendingId + ",\"result\":{\"resumed\":true}}\n\n", token).ConfigureAwait(false);
                        };

                        using McpHttpClient client = new McpHttpClient { PingIntervalMs = 0 };
                        TestAssert.True(await client.ConnectStreamableAsync(server.BaseUrl, "/mcp", ct).ConfigureAwait(false), "The client connects.");
                        TestAssert.True(String.IsNullOrEmpty(client.SessionId), "The server issued no session.");
                        JsonRpcResponse response = await client.CallAsync("slow", null, 10000, ct).ConfigureAwait(false);
                        TestAssert.True(response.Error == null && JsonProbe.From(response.Result).Get("resumed").Bool(), "The response arrived on the resumed stream.");
                        TestAssert.True(server.GetLastEventIds.Contains("p1"), "The client resumed after the priming event.");
                    }),

                    Case(suiteId, "McpEndpointAtTheRoot", "An MCP endpoint mounted at / is served (the health check yields to it)", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, mcpPath: "/").ConfigureAwait(false);
                        string body = McpHttpTestRequests.BuildBody("initialize", 1, new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "t", version = "1" } });
                        RpcResult initialized = await McpHttpTestRequests.SendAsync(fixture, body, new Dictionary<string, string>(), true, ct, "/").ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, initialized.StatusCode, $"initialize at / is served: {initialized.Body}");
                        TestAssert.Equal("2025-11-25", initialized.Result.Get("protocolVersion").String(), "It is an MCP response.");
                        TestAssert.True(!String.IsNullOrEmpty(initialized.SessionId), "A session is issued.");
                    }),

                    Case(suiteId, "ResourceIdentifierMustBeAnHttpUrl", "ProtectedResourceMetadata rejects a resource identifier that is not an absolute http(s) URL or has a fragment", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("127.0.0.1", TestPorts.GetFreePort());
                        List<string> servers = new List<string> { "https://auth.example.com" };
                        TestAssert.Throws<ArgumentException>(() => server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "urn:example:mcp", AuthorizationServers = servers }, "A URN is rejected.");
                        TestAssert.Throws<ArgumentException>(() => server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "https://mcp.example.com/mcp#x", AuthorizationServers = servers }, "A fragment is rejected.");
                        TestAssert.Throws<ArgumentException>(() => server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "/mcp", AuthorizationServers = servers }, "A relative URL is rejected.");
                        server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "https://mcp.example.com/mcp", AuthorizationServers = servers };
                        server.ProtectedResourceMetadata = new McpProtectedResourceMetadata { Resource = "http://localhost:8080/mcp", AuthorizationServers = servers };
                        return Task.CompletedTask;
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "closure", "conformance" });
        }
    }
}
