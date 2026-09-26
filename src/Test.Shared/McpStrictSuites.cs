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
    /// Covers the requirements closed in v2.1.8: dialect-exact JSON Schema validation with scoped references and
    /// dynamic scope, ECMA-262 patterns, output schemas of any shape and null structured content, UTF-8 stdio, late
    /// cancellations after rejected requests, stateless requests in batches, ping failures as connection failures,
    /// logged cancellation reasons, the <c>Mcp-Name</c> source for <c>resources/read</c>, rejected notifications,
    /// insufficient scope in batches, parse errors on the stateless path, and the stateless client's requests.
    /// </summary>
    public static class McpStrictSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(500);
        private static int _NextId = 1000;

        /// <summary>
        /// JSON Schema dialect cases.
        /// </summary>
        public static TestSuiteDescriptor Schemas()
        {
            const string suiteId = "Mcp.SchemaDialectsExact";
            return new TestSuiteDescriptor(
                suiteId,
                "JSON Schema validation in the declared dialect",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "KeywordsFollowTheDialect", "2020-12 ignores draft-07 dependencies; draft-07 ignores unevaluatedProperties and prefixItems and lets $ref override its siblings", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("modern", "2020-12", Schema("{\"type\":\"object\",\"dependencies\":{\"a\":[\"b\"]}}"), args => "ok");
                            s.RegisterTool("seven", "draft-07", Schema("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"unevaluatedProperties\":false,"
                                + "\"definitions\":{\"s\":{\"type\":\"string\"}},\"properties\":{\"a\":{\"$ref\":\"#/definitions/s\",\"maxLength\":1},\"list\":{\"type\":\"array\",\"prefixItems\":[{\"type\":\"integer\"}]}}}"), args => "ok");
                        }).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.False(await IsErrorAsync(client, "modern", "{\"a\":1}").ConfigureAwait(false), "2020-12 has no dependencies keyword.");
                        TestAssert.False(await IsErrorAsync(client, "seven", "{\"a\":\"abc\",\"extra\":1,\"list\":[\"x\"]}").ConfigureAwait(false), "draft-07 ignores unevaluatedProperties and prefixItems, and $ref siblings.");
                        TestAssert.True(await IsErrorAsync(client, "seven", "{\"a\":1}").ConfigureAwait(false), "The draft-07 $ref target is enforced.");
                    }),

                    Case(suiteId, "InvalidKeywordFormsAreRejected", "The array form of items is rejected in a 2020-12 schema and accepted in draft-07", ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, TestPorts.GetFreePort());
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("arr", "2020-12", Schema("{\"type\":\"object\",\"properties\":{\"t\":{\"type\":\"array\",\"items\":[{\"type\":\"integer\"}]}}}"), args => "x"), "2020-12 rejects array-form items.");
                        server.RegisterTool("arr07", "draft-07", Schema("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"properties\":{\"t\":{\"type\":\"array\",\"items\":[{\"type\":\"integer\"}],\"additionalItems\":false}}}"), args => "x");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ReferencesResolveWithinTheirResource", "A draft-07 fragment $id is an anchor, and a pointer inside an embedded $id resource resolves against that resource", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("frag", "draft-07 anchor", Schema("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"definitions\":{\"n\":{\"$id\":\"#num\",\"type\":\"integer\"}},\"properties\":{\"a\":{\"$ref\":\"#num\"}}}"), args => "ok");
                            s.RegisterTool("nested", "Nested resource", Schema("{\"$id\":\"https://example.com/root\",\"type\":\"object\",\"$defs\":{\"x\":{\"type\":\"integer\"}},"
                                + "\"properties\":{\"a\":{\"$id\":\"https://example.com/inner\",\"$defs\":{\"x\":{\"type\":\"string\"}},\"$ref\":\"#/$defs/x\"}}}"), args => "ok");
                        }).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.True(await IsErrorAsync(client, "frag", "{\"a\":\"x\"}").ConfigureAwait(false), "The fragment $id anchor resolves and is enforced.");
                        TestAssert.False(await IsErrorAsync(client, "nested", "{\"a\":\"text\"}").ConfigureAwait(false), "The pointer resolves within the inner resource (a string).");
                        TestAssert.True(await IsErrorAsync(client, "nested", "{\"a\":5}").ConfigureAwait(false), "The root's $defs/x (an integer) is not used.");
                    }),

                    Case(suiteId, "DynamicRefFollowsTheDynamicScope", "$dynamicRef resolves to the outermost $dynamicAnchor in the dynamic scope", async ct =>
                    {
                        string schema = "{\"$id\":\"https://example.com/root\",\"$dynamicAnchor\":\"node\",\"type\":\"object\",\"required\":[\"kind\"],"
                            + "\"properties\":{\"child\":{\"$ref\":\"list.json\"}},"
                            + "\"$defs\":{\"list\":{\"$id\":\"list.json\",\"$dynamicAnchor\":\"node\",\"type\":\"object\",\"properties\":{\"next\":{\"$dynamicRef\":\"#node\"}}}}}";
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("tree", "Dynamic", Schema(schema), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.True(await IsErrorAsync(client, "tree", "{\"kind\":1,\"child\":{\"next\":{}}}").ConfigureAwait(false), "next is validated by the outermost node (the root, which requires kind).");
                        TestAssert.False(await IsErrorAsync(client, "tree", "{\"kind\":1,\"child\":{\"next\":{\"kind\":2}}}").ConfigureAwait(false), "A next that satisfies the root passes.");
                    }),

                    Case(suiteId, "PatternsAreEcmaScript", "pattern uses ECMA-262 semantics: \\d matches only ASCII digits", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.RegisterTool("digits", "Digits", Schema("{\"type\":\"object\",\"properties\":{\"d\":{\"type\":\"string\",\"pattern\":\"^\\\\d+$\"}}}"), args => "ok")).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        TestAssert.False(await IsErrorAsync(client, "digits", "{\"d\":\"123\"}").ConfigureAwait(false), "ASCII digits match.");
                        TestAssert.True(await IsErrorAsync(client, "digits", "{\"d\":\"\\u0661\\u0662\"}").ConfigureAwait(false), "Arabic-Indic digits do not match \\d.");
                    }),

                    Case(suiteId, "OutputSchemasOfAnyShape", "A typeless output schema is kept as written for 2026-07-28, becomes type object for older sessions when it only describes objects, and is omitted otherwise; null structured content is sent to 2026-07-28 only", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("either", "Array or string", new { type = "object" }, Schema("{\"oneOf\":[{\"type\":\"array\"},{\"type\":\"string\"}]}"), args => McpToolCallResult.FromStructured(new[] { 1 }));
                            s.RegisterTool("props", "Object keywords", new { type = "object" }, Schema("{\"properties\":{\"n\":{\"type\":\"integer\"}}}"), args => McpToolCallResult.FromStructured(new { n = 1 }));
                            s.RegisterTool("nothing", "Null", new { type = "object" }, Schema("{\"type\":\"null\"}"), args => McpToolCallResult.FromStructured(null));
                        }).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}").ConfigureAwait(false);
                        List<JsonProbe> tools = Next(client).Get("result").Get("tools").EnumerateArray().ToList();
                        TestAssert.False(tools.First(t => t.Get("name").String() == "either").Has("outputSchema"), "A non-object output schema is omitted before 2026-07-28.");
                        TestAssert.Equal("object", tools.First(t => t.Get("name").String() == "props").Get("outputSchema").Get("type").String(), "An object-only typeless schema gets type object.");

                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{" + Meta("{}") + "}}").ConfigureAwait(false);
                        JsonProbe either = Next(client).Get("result").Get("tools").EnumerateArray().First(t => t.Get("name").String() == "either");
                        TestAssert.False(either.Get("outputSchema").Has("type"), "2026-07-28 receives the schema as written.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"either\",\"arguments\":{}," + Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("result").Get("structuredContent").IsArray, "An array result validates against the typeless schema.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"nothing\",\"arguments\":{}," + Meta("{}") + "}}").ConfigureAwait(false);
                        string line = client.Receive(_Wait)!;
                        TestAssert.True(line.Contains("\"structuredContent\":null"), $"2026-07-28 receives null structured content: {line}");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"nothing\",\"arguments\":{}}}").ConfigureAwait(false);
                        TestAssert.False(Next(client).Get("result").Has("structuredContent"), "2025-11-25 receives the text block only.");
                    }),
                });
        }

        /// <summary>
        /// Stream transport cases.
        /// </summary>
        public static TestSuiteDescriptor Streams()
        {
            const string suiteId = "McpStreams.Strict";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP stream transport strictness",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "StdioClientDecodesUtf8", "McpClient reads a stdio server's raw UTF-8 output correctly whatever the console code page", async ct =>
                    {
                        using McpClient client = new McpClient { PingIntervalMs = 0 };

                        // A console code page other than UTF-8 (437 is the Windows default) is what used to garble the
                        // server's output; the child process's encoding is chosen when it starts.
                        Encoding? previous = null;
                        try
                        {
                            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                            previous = Console.OutputEncoding;
                            Console.OutputEncoding = Encoding.GetEncoding(437);
                        }
                        catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException || ex is PlatformNotSupportedException)
                        {
                            previous = null;
                        }

                        try
                        {
                            await McpStdioIntegrationSuites.LaunchTestServerAsync(client, ct, "--raw-utf8").ConfigureAwait(false);
                        }
                        finally
                        {
                            if (previous != null) Console.OutputEncoding = previous;
                        }

                        JsonProbe result = JsonProbe.From(await client.CallAsync<object?>("raw/text", new { }, 15000, ct).ConfigureAwait(false));
                        TestAssert.Equal("héllo 世界", result.Get("text").String(), "Non-ASCII text arrives intact.");
                        client.Shutdown();
                    }),

                    Case(suiteId, "LateCancelAfterRejectionDoesNotHitReuse", "A cancellation for a request the server rejected does not cancel a later request that reuses the ID", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, Next(client).Get("error").Get("code").Int(), "The request is rejected (no clientCapabilities).");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":5," + Meta("{}") + "}}").ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/list\",\"params\":{" + Meta("{}") + "}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("result").Has("tools"), "The reused ID is answered.");
                    }),

                    Case(suiteId, "StatelessRequestsCannotBeBatched", "On a 2025-03-26 connection, a batched request naming 2026-07-28 gets -32600 while the other elements are served", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-03-26", ct).ConfigureAwait(false);
                        await client.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{" + Meta("{}") + "}}]").ConfigureAwait(false);
                        List<JsonProbe> answers = Next(client).EnumerateArray().ToList();
                        TestAssert.True(answers.First(a => a.Get("id").Int() == 1).Has("result"), "The handshake request is served.");
                        TestAssert.Equal(-32600, answers.First(a => a.Get("id").Int() == 2).Get("error").Get("code").Int(), "The stateless request is refused in a batch.");
                    }),

                    Case(suiteId, "UnansweredPingsCloseTheConnection", "A server closes a connection whose client does not answer ping; with PingFailureThreshold 0 it only logs", async ct =>
                    {
                        await using TcpJsonRpcFixture strict = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => { s.PingIntervalMs = 200; s.PingTimeoutMs = 200; }).ConfigureAwait(false);
                        using RawLineClient silent = await InitializedAsync(strict, ct).ConfigureAwait(false);
                        TestAssert.True(await WaitUntilAsync(() => { silent.Receive(TimeSpan.FromMilliseconds(50)); return silent.IsClosed; }, ct).ConfigureAwait(false), "The server closed the unresponsive connection.");

                        await using TcpJsonRpcFixture lenient = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => { s.PingIntervalMs = 200; s.PingTimeoutMs = 200; s.PingFailureThreshold = 0; }).ConfigureAwait(false);
                        using RawLineClient kept = await InitializedAsync(lenient, ct).ConfigureAwait(false);
                        await Task.Delay(1200, ct).ConfigureAwait(false);
                        await kept.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"alive\",\"method\":\"tools/list\"}").ConfigureAwait(false);
                        string? line;
                        bool answered = false;
                        while ((line = kept.Receive(_Wait)) != null)
                        {
                            if (TestJson.ParseRoot(line).Has("id") && TestJson.ParseRoot(line).Get("id").String() == "alive")
                            {
                                answered = true;
                                break;
                            }
                        }

                        TestAssert.True(answered, "With threshold 0 the connection stays open.");
                    }),

                    Case(suiteId, "ClientDisconnectsWhenPingsGoUnanswered", "McpTcpClient disconnects when its server does not answer ping", async ct =>
                    {
                        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                        listener.Start();
                        try
                        {
                            using McpTcpClient client = new McpTcpClient { AutoInitialize = false, PingIntervalMs = 200, PingTimeoutMs = 200 };
                            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(ct).AsTask();
                            TestAssert.True(await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, ct).ConfigureAwait(false), "The client connects.");
                            using TcpClient server = await accepting.ConfigureAwait(false);
                            using StreamReader reader = new StreamReader(server.GetStream());
                            Task init = client.InitializeAsync(ct);
                            JsonProbe initialize = JsonProbe.Parse((await ReadLineAsync(reader, ct).ConfigureAwait(false))!);
                            byte[] reply = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + initialize.Get("id").Int() + ",\"result\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"serverInfo\":{\"name\":\"raw\",\"version\":\"1\"}}}\n");
                            await server.GetStream().WriteAsync(reply, 0, reply.Length, ct).ConfigureAwait(false);
                            await init.ConfigureAwait(false);
                            TestAssert.True(await WaitUntilAsync(() => !client.IsConnected, ct).ConfigureAwait(false), "The client disconnected after an unanswered ping.");
                        }
                        finally
                        {
                            listener.Stop();
                        }
                    }),

                    Case(suiteId, "CancellationReasonsAreLogged", "The server logs the reason a client gave for cancelling a request", async ct =>
                    {
                        List<string> log = new List<string>();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.Log += (sender, message) => { lock (log) log.Add(message); };
                            s.RegisterTool("slow", "Waits", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await Task.Delay(5000, token).ConfigureAwait(false);
                                return "done";
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await InitializedAsync(fixture, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"w\",\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}}}").ConfigureAwait(false);
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"w\",\"reason\":\"user gave up\"}}").ConfigureAwait(false);
                        TestAssert.True(await WaitUntilAsync(() => { lock (log) return log.Any(line => line.Contains("user gave up")); }, ct).ConfigureAwait(false), "The reason is logged.");
                    }),
                });
        }

        /// <summary>
        /// Streamable HTTP cases.
        /// </summary>
        public static TestSuiteDescriptor Http()
        {
            const string suiteId = "McpHttp.Strict";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP Streamable HTTP strictness",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "McpNameComesFromTheUriForResourcesRead", "For resources/read, Mcp-Name must match params.uri, never params.name", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterResource("voltaic://doc", "doc", "text/plain", () => new McpReadResourceResult())).ConfigureAwait(false);
                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "uri", "voltaic://doc" }, { "name", "decoy" } };
                        RpcResult byName = await McpHttpTestRequests.SendStatelessAsync(fixture, "resources/read", 1, parameters, "decoy", null, ct).ConfigureAwait(false);
                        RpcResult byUri = await McpHttpTestRequests.SendStatelessAsync(fixture, "resources/read", 2, parameters, "voltaic://doc", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, byName.StatusCode, "A header matching params.name is rejected.");
                        TestAssert.Equal(-32020, byName.Error.Get("code").Int(), "It is a header mismatch.");
                        TestAssert.Equal(HttpStatusCode.OK, byUri.StatusCode, $"A header matching params.uri is accepted: {byUri.Body}");
                    }),

                    Case(suiteId, "RejectedNotificationsGetAnErrorStatus", "A notification the server cannot accept gets 400, alone or in a batch of notifications", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        string session = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-03-26", null, ct).ConfigureAwait(false)).SessionId!;
                        Dictionary<string, string> headers = new Dictionary<string, string> { { McpProtocol.SessionIdHeader, session } };
                        RpcResult single = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":[1]}", headers, true, ct).ConfigureAwait(false);
                        RpcResult batch = await McpHttpTestRequests.SendAsync(fixture, "[{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":[1]}]", headers, true, ct).ConfigureAwait(false);
                        RpcResult accepted = await McpHttpTestRequests.SendAsync(fixture, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/roots/list_changed\"}", headers, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, single.StatusCode, "A malformed notification gets 400.");
                        TestAssert.Equal(HttpStatusCode.BadRequest, batch.StatusCode, "A batch with a malformed notification gets 400.");
                        TestAssert.Equal(HttpStatusCode.Accepted, accepted.StatusCode, "A valid notification gets 202.");
                    }),

                    Case(suiteId, "InsufficientScopeInABatchIs403", "A 2025-03-26 batch whose request fails for a missing scope is answered 403 with the challenge and every response", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct, s => s.RegisterTool("admin", "Needs a scope", new { type = "object" }, args => throw new McpInsufficientScopeException("files:write"))).ConfigureAwait(false);
                        string session = (await McpHttpTestRequests.InitializeAsync(fixture, "2025-03-26", null, ct).ConfigureAwait(false)).SessionId!;
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/mcp/")
                        {
                            Content = new StringContent("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"admin\",\"arguments\":{}}}]", Encoding.UTF8, "application/json")
                        };
                        request.Headers.Accept.ParseAdd("application/json");
                        request.Headers.Accept.ParseAdd("text/event-stream");
                        request.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
                        using HttpResponseMessage response = await fixture.SendRawAsync(request, ct).ConfigureAwait(false);
                        JsonProbe body = JsonProbe.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                        TestAssert.Equal(HttpStatusCode.Forbidden, response.StatusCode, "The batch is answered 403.");
                        TestAssert.True(response.Headers.WwwAuthenticate.ToString().Contains("insufficient_scope"), "The challenge is sent.");
                        TestAssert.Equal(2, body.Length, "Every response is in the body.");
                    }),

                    Case(suiteId, "StatelessParseErrorIsMinus32700", "A stateless POST whose body is not JSON gets 400 -32700", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await HttpMcpTestServerFixture.StartAsync(ct).ConfigureAwait(false);
                        RpcResult result = await McpHttpTestRequests.SendAsync(fixture, "{not json", new Dictionary<string, string> { { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 } }, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, result.StatusCode, "The request is rejected.");
                        TestAssert.Equal(-32700, result.Error.Get("code").Int(), "It is a parse error.");
                    }),

                    Case(suiteId, "StatelessClientCallsCarryVersionAndMeta", "In stateless mode, CallAsync and NotifyAsync send MCP-Protocol-Version and _meta; a non-string resultType is rejected", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer { IssueSession = false };
                        List<string> bodies = new List<string>();
                        server.RequestHandler = (method, idJson, body, response, token) =>
                        {
                            lock (bodies) bodies.Add(body);
                            string result = method == "server/discover"
                                ? "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{},\"serverInfo\":{\"name\":\"f\",\"version\":\"1\"},\"resultType\":\"complete\"}"
                                : method == "odd" ? "{\"resultType\":5}" : "{\"resultType\":\"complete\"}";
                            return FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + result + "}", token);
                        };
                        using McpHttpClient client = new McpHttpClient();
                        TestAssert.True(await client.ConnectStatelessAsync(server.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects.");
                        JsonRpcResponse listed = await client.CallAsync("tools/list", new { }, 0, ct).ConfigureAwait(false);
                        JsonRpcResponse odd = await client.CallAsync("odd", null, 0, ct).ConfigureAwait(false);
                        await client.NotifyAsync("notifications/roots/list_changed", null, 0, ct).ConfigureAwait(false);

                        string? versionHeader = server.RequestHeaders.Select(h => h.Get(McpProtocol.ProtocolVersionHeader)).LastOrDefault();
                        TestAssert.True(listed.Error == null, $"CallAsync works in stateless mode: {listed.Error?.Message}");
                        TestAssert.Equal("2026-07-28", versionHeader, "The version header is sent.");
                        lock (bodies) TestAssert.True(JsonProbe.Parse(bodies.First(b => b.Contains("tools/list"))).Get("params").Get("_meta").Has(McpProtocol.MetaProtocolVersionKey), "The _meta protocol version is sent.");
                        TestAssert.Equal(-32603, odd.Error?.Code, "A numeric resultType is rejected.");
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "strict", "conformance" });
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
