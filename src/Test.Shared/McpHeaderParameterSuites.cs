namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers <c>x-mcp-header</c> tool parameters (MCP 2026-07-28): invalid annotations are rejected when a tool is
    /// registered, the server requires matching <c>Mcp-Param-{Name}</c> headers on stateless <c>tools/call</c>
    /// (400 <c>-32020</c> otherwise), and <see cref="McpHttpClient"/> mirrors the headers, drops tool definitions with
    /// invalid annotations, retries once after a header mismatch, and encodes header values as the specification
    /// requires.
    /// </summary>
    public static class McpHeaderParameterSuites
    {
        private const string RegionalSchema = "{\"type\":\"object\",\"properties\":{"
            + "\"region\":{\"type\":\"string\",\"x-mcp-header\":\"Region\"},"
            + "\"count\":{\"type\":\"integer\",\"x-mcp-header\":\"Count\"},"
            + "\"flag\":{\"type\":\"boolean\",\"x-mcp-header\":\"Flag\"},"
            + "\"target\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"x-mcp-header\":\"TargetId\"}}},"
            + "\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}";

        /// <summary>
        /// x-mcp-header cases.
        /// </summary>
        public static TestSuiteDescriptor HeaderParameters()
        {
            const string suiteId = "Mcp.HeaderParameters";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP 2026-07-28 x-mcp-header tool parameters",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "InvalidAnnotationsAreRejectedAtRegistration", "RegisterTool rejects x-mcp-header on numbers, outside the properties chain, duplicated, empty, non-token, at the root, or on unsafe integers, and accepts valid nested annotations", ct =>
                    {
                        using McpHttpServer server = new McpHttpServer("localhost", TestPorts.GetFreePort());
                        Dictionary<string, string> invalid = new Dictionary<string, string>
                        {
                            { "number type", "{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"number\",\"x-mcp-header\":\"N\"}}}" },
                            { "under items", "{\"type\":\"object\",\"properties\":{\"list\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"x-mcp-header\":\"Item\"}}}}" },
                            { "under oneOf", "{\"type\":\"object\",\"properties\":{\"v\":{\"oneOf\":[{\"type\":\"string\",\"x-mcp-header\":\"V\"}]}}}" },
                            { "under $defs", "{\"type\":\"object\",\"$defs\":{\"d\":{\"type\":\"string\",\"x-mcp-header\":\"D\"}},\"properties\":{}}" },
                            { "duplicate", "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\",\"x-mcp-header\":\"Same\"},\"b\":{\"type\":\"string\",\"x-mcp-header\":\"SAME\"}}}" },
                            { "empty", "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\",\"x-mcp-header\":\"\"}}}" },
                            { "not a token", "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\",\"x-mcp-header\":\"Bad Name\"}}}" },
                            { "not a string", "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\",\"x-mcp-header\":5}}}" },
                            { "root", "{\"type\":\"object\",\"x-mcp-header\":\"Root\",\"properties\":{}}" },
                            { "unsafe integer", "{\"type\":\"object\",\"properties\":{\"i\":{\"type\":\"integer\",\"maximum\":1e17,\"x-mcp-header\":\"I\"}}}" },
                        };

                        foreach (KeyValuePair<string, string> schema in invalid)
                        {
                            TestAssert.Throws<ArgumentException>(() => server.RegisterTool("t", "d", Schema(schema.Value), _ => "x"), $"An x-mcp-header {schema.Key} is rejected.");
                        }

                        server.RegisterTool("regional", "Regional query", Schema(RegionalSchema), _ => "ok");
                        server.RegisterTool("nullable", "Nullable header parameter", Schema("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":[\"string\",\"null\"],\"x-mcp-header\":\"A\"}}}"), _ => "ok");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("t", "d", Schema("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":[\"string\",\"integer\"],\"x-mcp-header\":\"A\"}}}"), _ => "x"), "A union of two primitive types is rejected.");
                        server.RegisterTool("defaults", "Default values are data", Schema("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\",\"default\":{\"x-mcp-header\":\"NotAnAnnotation\"}}}}"), _ => "ok");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "MatchingHeadersAreAccepted", "A stateless tools/call whose Mcp-Param headers match the arguments (plain, Base64 sentinel, numeric 42.0 for 42, nested properties) is served; absent values need no header", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);

                        RpcResult plain = await CallAsync(fixture, new { region = "us-west1", count = 42, flag = true, target = new { id = "t-1" }, query = "q" },
                            new Dictionary<string, string> { { "Mcp-Param-Region", "us-west1" }, { "Mcp-Param-Count", "42.0" }, { "Mcp-Param-Flag", "true" }, { "Mcp-Param-TargetId", "t-1" } }, ct).ConfigureAwait(false);
                        RpcResult encoded = await CallAsync(fixture, new { region = "Zürich", query = "q" },
                            new Dictionary<string, string> { { "mcp-param-region", "=?base64?WsO8cmljaA==?=" } }, ct).ConfigureAwait(false);
                        RpcResult absent = await CallAsync(fixture, new { query = "q" }, new Dictionary<string, string>(), ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, plain.StatusCode, $"Body: {plain.Body}");
                        TestAssert.Equal("ok:us-west1", plain.Result.Get("content")[0].Get("text").String());
                        TestAssert.Equal(HttpStatusCode.OK, encoded.StatusCode, $"A Base64 value matches after decoding (header names are case-insensitive). Body: {encoded.Body}");
                        TestAssert.Equal(HttpStatusCode.OK, absent.StatusCode, $"Absent values need no header. Body: {absent.Body}");
                    }),

                    Case(suiteId, "MissingOrMismatchedHeadersAreRejected", "A missing header for a present value, a different value, a header without a body value, invalid Base64, or a wrong boolean or integer gets 400 -32020 and the tool does not run", async ct =>
                    {
                        int calls = 0;
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct, () => Interlocked.Increment(ref calls)).ConfigureAwait(false);
                        List<RpcResult> rejected = new List<RpcResult>
                        {
                            await CallAsync(fixture, new { region = "us", query = "q" }, new Dictionary<string, string>(), ct).ConfigureAwait(false),
                            await CallAsync(fixture, new { region = "us", query = "q" }, new Dictionary<string, string> { { "Mcp-Param-Region", "eu" } }, ct).ConfigureAwait(false),
                            await CallAsync(fixture, new { query = "q" }, new Dictionary<string, string> { { "Mcp-Param-Region", "us" } }, ct).ConfigureAwait(false),
                            await CallAsync(fixture, new { region = "us", query = "q" }, new Dictionary<string, string> { { "Mcp-Param-Region", "=?base64?***?=" } }, ct).ConfigureAwait(false),
                            await CallAsync(fixture, new { flag = true, query = "q" }, new Dictionary<string, string> { { "Mcp-Param-Flag", "True" } }, ct).ConfigureAwait(false),
                            await CallAsync(fixture, new { count = 42, query = "q" }, new Dictionary<string, string> { { "Mcp-Param-Count", "43" } }, ct).ConfigureAwait(false),
                            await CallAsync(fixture, new { target = new { id = "a" }, query = "q" }, new Dictionary<string, string> { { "Mcp-Param-TargetId", "b" } }, ct).ConfigureAwait(false),
                        };

                        foreach (RpcResult result in rejected)
                        {
                            TestAssert.Equal(HttpStatusCode.BadRequest, result.StatusCode, $"Body: {result.Body}");
                            TestAssert.Equal(-32020, result.Error.Get("code").Int(), "The error is HeaderMismatch.");
                        }

                        TestAssert.Equal(0, Volatile.Read(ref calls), "The tool never ran.");

                        RpcResult unsafeInteger = await CallAsync(fixture, new { count = 9007199254740993, query = "q" }, new Dictionary<string, string> { { "Mcp-Param-Count", "9007199254740993" } }, ct).ConfigureAwait(false);
                        TestAssert.Equal(-32020, unsafeInteger.Error.Get("code").Int(), "An integer outside the JavaScript safe range cannot be mirrored.");

                        Dictionary<string, object?> parameters = new Dictionary<string, object?> { { "name", "regional" }, { "arguments", new { query = "q" } }, { "_meta", McpHttpTestRequests.StatelessMeta() } };
                        Dictionary<string, string> badName = new Dictionary<string, string>
                        {
                            { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                            { McpProtocol.MethodHeader, "tools/call" },
                            { McpProtocol.NameHeader, "=?base64?%%%?=" }
                        };
                        RpcResult malformedName = await McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/call", 9, parameters), badName, true, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, malformedName.StatusCode, "A malformed Base64 Mcp-Name is rejected.");
                        TestAssert.Equal(-32020, malformedName.Error.Get("code").Int(), "The error is HeaderMismatch.");
                    }),

                    Case(suiteId, "WrongTypeArgumentIsToolError", "An annotated argument of the wrong type, mirrored in its header as a conforming client must, is reported by input validation as an isError result", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        RpcResult wrongType = await CallAsync(fixture, new { count = "forty-two", query = "q" }, new Dictionary<string, string> { { "Mcp-Param-Count", "forty-two" } }, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, wrongType.StatusCode, $"Body: {wrongType.Body}");
                        TestAssert.True(wrongType.Result.Get("isError").Bool(), "The schema violation is a tool execution error.");
                    }),

                    Case(suiteId, "HandshakeEraIgnoresParamHeaders", "Handshake-era tools/call does not require Mcp-Param headers", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        RpcResult result = await fixture.PostMcpAsync("tools/call", new { name = "regional", arguments = new { region = "us", query = "q" } }, 1, null, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, result.StatusCode, $"Body: {result.Body}");
                        TestAssert.Equal("ok:us", result.Result.Get("content")[0].Get("text").String());
                    }),

                    Case(suiteId, "VoltaicClientMirrorsHeaders", "McpHttpClient mirrors annotated arguments into Mcp-Param headers (Base64 for non-ASCII and edge whitespace), learning definitions by retrying once after HeaderMismatch", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await StartAsync(ct).ConfigureAwait(false);
                        using McpHttpClient client = new McpHttpClient();
                        ConcurrentQueue<string> log = new ConcurrentQueue<string>();
                        client.Log += (sender, message) => log.Enqueue(message);
                        TestAssert.True(await client.ConnectStatelessAsync(fixture.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects.");

                        object arguments = new { region = " Zürich ", count = 7, flag = false, target = new { id = "t1" }, query = "q" };
                        JsonRpcResponse first = await client.CallToolStatelessAsync("regional", arguments, null, 3, ct).ConfigureAwait(false);
                        int retriesAfterFirst = log.Count(message => message.Contains("HeaderMismatch"));
                        JsonRpcResponse second = await client.CallToolStatelessAsync("regional", arguments, null, 3, ct).ConfigureAwait(false);

                        TestAssert.True(first.Error == null, $"The call succeeds after the client learns the definitions: {first.Error?.Message}");
                        TestAssert.Equal("ok: Zürich ", TestJson.SerializeToElement(first.Result!).Get("content")[0].Get("text").String());
                        TestAssert.Equal(1, retriesAfterFirst, "The first call was retried once.");
                        TestAssert.True(second.Error == null, "The second call succeeds.");
                        TestAssert.Equal(1, log.Count(message => message.Contains("HeaderMismatch")), "The second call needed no retry.");
                    }),

                    Case(suiteId, "ClientDropsInvalidToolDefinitions", "McpHttpClient removes tool definitions with invalid x-mcp-header annotations from tools/list and logs why, sends Mcp-Param headers for valid ones, and derives and encodes Mcp-Name from params", async ct =>
                    {
                        using FakeStreamableHttpServer server = new FakeStreamableHttpServer();
                        server.RequestHandler = async (method, idJson, body, response, token) =>
                        {
                            string result = method switch
                            {
                                "server/discover" => "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"},\"resultType\":\"complete\"}",
                                "tools/list" => "{\"tools\":[{\"name\":\"good\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"r\":{\"type\":\"string\",\"x-mcp-header\":\"R\"}}}},"
                                    + "{\"name\":\"bad\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"number\",\"x-mcp-header\":\"N\"}}}}],\"resultType\":\"complete\"}",
                                _ => "{\"resultType\":\"complete\"}"
                            };
                            await FakeStreamableHttpServer.WriteJsonAsync(response, "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + result + "}", token).ConfigureAwait(false);
                        };

                        using McpHttpClient client = new McpHttpClient();
                        ConcurrentQueue<string> log = new ConcurrentQueue<string>();
                        client.Log += (sender, message) => log.Enqueue(message);
                        TestAssert.True(await client.ConnectStatelessAsync(server.BaseUrl, "/mcp", null, true, ct).ConfigureAwait(false), "The client connects.");

                        JsonElement tools = await client.CallStatelessAsync<JsonElement>("tools/list", null, null, ct).ConfigureAwait(false);
                        List<string> names = tools.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToList();
                        TestAssert.True(names.SequenceEqual(new[] { "good" }), $"Only the valid tool is listed: {String.Join(",", names)}");
                        TestAssert.True(log.Any(message => message.Contains("'bad'") && message.Contains("x-mcp-header")), "A warning names the rejected tool.");

                        await client.SendStatelessAsync("tools/call", new Dictionary<string, object?> { { "name", "good" }, { "arguments", new { r = "west" } } }, null, ct).ConfigureAwait(false);
                        await client.SendStatelessAsync("resources/read", new Dictionary<string, object?> { { "uri", " padded " } }, null, ct).ConfigureAwait(false);
                        List<WebHeaderCollectionSnapshot> requests = server.RequestHeaders.ToList();
                        WebHeaderCollectionSnapshot call = requests[requests.Count - 2];
                        WebHeaderCollectionSnapshot read = requests[requests.Count - 1];
                        TestAssert.Equal("good", call.Get("Mcp-Name"), "Mcp-Name comes from params.name.");
                        TestAssert.Equal("west", call.Get("Mcp-Param-R"), "The annotated argument is mirrored.");
                        TestAssert.Equal("=?base64?IHBhZGRlZCA=?=", read.Get("Mcp-Name"), "A value with edge whitespace is Base64-encoded, taken from params.uri.");
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "stateless", "header-parameters" });
        }

        private static JsonElement Schema(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static Task<HttpMcpTestServerFixture> StartAsync(CancellationToken token, Action? onCall = null)
        {
            return HttpMcpTestServerFixture.StartAsync(token, server =>
            {
                server.RegisterTool("regional", "Regional query", Schema(RegionalSchema), args =>
                {
                    onCall?.Invoke();
                    return "ok:" + (args?.GetString("region") ?? "");
                });
            });
        }

        private static Task<RpcResult> CallAsync(HttpMcpTestServerFixture fixture, object arguments, Dictionary<string, string> paramHeaders, CancellationToken token)
        {
            Dictionary<string, object?> parameters = new Dictionary<string, object?>
            {
                { "name", "regional" },
                { "arguments", arguments },
                { "_meta", McpHttpTestRequests.StatelessMeta() }
            };

            Dictionary<string, string> headers = new Dictionary<string, string>(paramHeaders)
            {
                { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                { McpProtocol.MethodHeader, "tools/call" },
                { McpProtocol.NameHeader, "regional" }
            };

            return McpHttpTestRequests.SendAsync(fixture, McpHttpTestRequests.BuildBody("tools/call", 1, parameters), headers, true, token);
        }
    }
}
