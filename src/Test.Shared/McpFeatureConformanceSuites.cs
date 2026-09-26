namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Covers server notifications and MCP feature rules on the stream transports (checked on TCP, which shares the
    /// processor and notification fan-out with stdio and WebSocket): list_changed only to initialized sessions,
    /// resources/updated only to subscribers, per-session log levels, progress only for the originating request,
    /// tool name and schema rules, structured output, argument types, completion limits, advertised capabilities,
    /// version downgrades, pagination, URI templates, and the insufficient-scope error.
    /// </summary>
    public static class McpFeatureConformanceSuites
    {
        private static readonly TimeSpan _Wait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _Quiet = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// Server notification cases.
        /// </summary>
        public static TestSuiteDescriptor Notifications()
        {
            const string suiteId = "McpStreams.Notifications";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP server notifications reach only the sessions they concern",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ListChangedGoesOnlyToInitializedSessions", "notifications/*/list_changed reaches sessions that completed initialize, not connections that have not", async ct =>
                    {
                        McpTcpServer? server = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => server = s).ConfigureAwait(false);
                        using RawLineClient ready = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        using RawLineClient pending = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await ready.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await PingAsync(pending, "wait").ConfigureAwait(false);

                        await server!.NotifyToolsChangedAsync(ct).ConfigureAwait(false);
                        await server.NotifyPromptsChangedAsync(ct).ConfigureAwait(false);

                        TestAssert.Equal("notifications/tools/list_changed", Next(ready).Get("method").String(), "The initialized session is told.");
                        TestAssert.Equal("notifications/prompts/list_changed", Next(ready).Get("method").String(), "Every list_changed kind is delivered.");
                        TestAssert.True(pending.Receive(_Quiet) == null, "A connection that has not initialized is not told.");
                    }),

                    Case(suiteId, "ResourceUpdatedGoesOnlyToSubscribers", "notifications/resources/updated reaches sessions subscribed to that URI and stops after unsubscribe", async ct =>
                    {
                        McpTcpServer? server = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            server = s;
                            s.RegisterResource("voltaic://watched", "watched", "text/plain", () => new McpReadResourceResult());
                        }).ConfigureAwait(false);
                        using RawLineClient subscriber = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        using RawLineClient other = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await subscriber.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await other.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await subscriber.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/subscribe\",\"params\":{\"uri\":\"voltaic://watched\"}}").ConfigureAwait(false);
                        TestAssert.True(Next(subscriber).Get("result").IsObject, "The subscription is accepted.");

                        await server!.NotifyResourceUpdatedAsync("voltaic://watched", ct).ConfigureAwait(false);
                        JsonProbe updated = Next(subscriber);
                        TestAssert.Equal("notifications/resources/updated", updated.Get("method").String(), "The subscriber is told.");
                        TestAssert.Equal("voltaic://watched", updated.Get("params").Get("uri").String(), "The notification names the URI.");
                        TestAssert.True(other.Receive(_Quiet) == null, "A session that did not subscribe is not told.");

                        await server.NotifyResourceUpdatedAsync("voltaic://elsewhere", ct).ConfigureAwait(false);
                        TestAssert.True(subscriber.Receive(_Quiet) == null, "Other URIs are not delivered to the subscriber.");

                        await subscriber.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/unsubscribe\",\"params\":{\"uri\":\"voltaic://watched\"}}").ConfigureAwait(false);
                        TestAssert.True(Next(subscriber).Get("result").IsObject, "The unsubscribe is accepted.");
                        await server.NotifyResourceUpdatedAsync("voltaic://watched", ct).ConfigureAwait(false);
                        TestAssert.True(subscriber.Receive(_Quiet) == null, "Nothing arrives after unsubscribing.");
                    }),

                    Case(suiteId, "LogLevelIsAppliedPerSession", "logging/setLevel filters notifications/message for that session only; an unknown level is rejected", async ct =>
                    {
                        McpTcpServer? server = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => server = s).ConfigureAwait(false);
                        using RawLineClient quiet = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        using RawLineClient verbose = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await quiet.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await verbose.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await quiet.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"logging/setLevel\",\"params\":{\"level\":\"warning\"}}").ConfigureAwait(false);
                        TestAssert.True(Next(quiet).Get("result").IsObject, "setLevel is accepted.");
                        await quiet.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"logging/setLevel\",\"params\":{\"level\":\"loud\"}}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, Next(quiet).Get("error").Get("code").Int(), "An unknown level is invalid params.");

                        await server!.NotifyLogMessageAsync("info", "routine", "test", ct).ConfigureAwait(false);
                        TestAssert.True(quiet.Receive(_Quiet) == null, "info is below the warning threshold.");
                        TestAssert.Equal("info", Next(verbose).Get("params").Get("level").String(), "A session without a level gets everything.");

                        await server.NotifyLogMessageAsync("error", "broken", "test", ct).ConfigureAwait(false);
                        JsonProbe error = Next(quiet);
                        TestAssert.Equal("error", error.Get("params").Get("level").String(), "error meets the threshold.");
                        TestAssert.Equal("test", error.Get("params").Get("logger").String(), "The logger name is sent.");

                        await TestAssert.ThrowsAsync<ArgumentException>(() => server.NotifyLogMessageAsync("loud", "x", null, ct), "An unknown level is rejected by the API.");
                    }),

                    Case(suiteId, "ProgressGoesOnlyToTheOriginatingRequest", "Tool progress is sent only when the request carries a progress token, only to its client, and must increase", async ct =>
                    {
                        McpTcpServer? server = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            server = s;
                            s.RegisterTool("work", "Reports progress", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                McpToolCallContext call = McpToolCallContext.Current!;
                                await call.ReportProgressAsync(1, 2, "halfway", token).ConfigureAwait(false);
                                await call.ReportProgressAsync(2, 2, null, token).ConfigureAwait(false);
                                string regression = "accepted";
                                try
                                {
                                    await call.ReportProgressAsync(1, 2, null, token).ConfigureAwait(false);
                                }
                                catch (ArgumentOutOfRangeException)
                                {
                                    regression = "rejected";
                                }

                                return regression;
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient caller = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        using RawLineClient bystander = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await caller.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await bystander.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);

                        await caller.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"work\",\"arguments\":{},\"_meta\":{\"progressToken\":\"p1\"}}}").ConfigureAwait(false);
                        JsonProbe first = Next(caller);
                        JsonProbe second = Next(caller);
                        JsonProbe result = Next(caller);
                        TestAssert.Equal("notifications/progress", first.Get("method").String(), "Progress is sent.");
                        TestAssert.Equal("p1", first.Get("params").Get("progressToken").String(), "Progress carries the request's token.");
                        TestAssert.Equal("halfway", first.Get("params").Get("message").String(), "The message is sent to 2025-03-26+ clients.");
                        TestAssert.Equal(2.0, second.Get("params").Get("progress").Double(), "Progress increases.");
                        TestAssert.Equal("rejected", result.Get("result").Get("content")[0].Get("text").String(), "Decreasing progress is rejected.");
                        TestAssert.True(bystander.Receive(_Quiet) == null, "Other clients get no progress.");

                        await caller.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"work\",\"arguments\":{}}}").ConfigureAwait(false);
                        TestAssert.Equal(2, Next(caller).Get("id").Int(), "Without a progress token only the result is sent.");

                        await server!.NotifyProgressAsync("p1", 5, null, null, ct).ConfigureAwait(false);
                        TestAssert.True(caller.Receive(_Quiet) == null, "Progress for a finished request is not sent.");
                    }),

                    Case(suiteId, "ProgressMessageIsRemovedFor20241105", "A 2024-11-05 session receives progress without the message field that revision lacks", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("work", "Reports progress", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                await McpToolCallContext.Current!.ReportProgressAsync(1, null, "text", token).ConfigureAwait(false);
                                return "ok";
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2024-11-05", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"work\",\"arguments\":{},\"_meta\":{\"progressToken\":7}}}").ConfigureAwait(false);
                        JsonProbe progress = Next(client);
                        TestAssert.Equal(7, progress.Get("params").Get("progressToken").Int(), "Numeric tokens are echoed as numbers.");
                        TestAssert.False(progress.Get("params").Has("message"), "message is removed for 2024-11-05.");
                    }),

                    Case(suiteId, "ToolContextLogHonorsTheSessionLevel", "McpToolCallContext.LogAsync reaches the calling session when the level passes its threshold", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("chatty", "Logs", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
                            {
                                McpToolCallContext call = McpToolCallContext.Current!;
                                await call.LogAsync("debug", "noise", null, token).ConfigureAwait(false);
                                await call.LogAsync("error", "signal", "tool", token).ConfigureAwait(false);
                                return "ok";
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"logging/setLevel\",\"params\":{\"level\":\"notice\"}}").ConfigureAwait(false);
                        Next(client);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"chatty\",\"arguments\":{}}}").ConfigureAwait(false);
                        JsonProbe log = Next(client);
                        TestAssert.Equal("signal", log.Get("params").Get("data").String(), "Only the error entry passes the notice threshold.");
                        TestAssert.Equal(2, Next(client).Get("id").Int(), "The result follows.");
                    }),
                });
        }

        /// <summary>
        /// MCP feature rule cases.
        /// </summary>
        public static TestSuiteDescriptor Features()
        {
            const string suiteId = "McpStreams.Features";
            return new TestSuiteDescriptor(
                suiteId,
                "MCP tools, resources, prompts, completions, and version rules",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ToolNamesAreValidated", "Tool names must be 1-128 characters of letters, digits, underscore, hyphen, and dot", ct =>
                    {
                        using McpTcpServer server = new McpTcpServer(System.Net.IPAddress.Loopback, TestPorts.GetFreePort());
                        server.RegisterTool("files.read_all-v2", "ok", new { type = "object" }, args => "ok");
                        server.RegisterTool(new string('a', 128), "ok", new { type = "object" }, args => "ok");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("has space", "bad", new { type = "object" }, args => "x"), "Spaces are rejected.");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool("slash/name", "bad", new { type = "object" }, args => "x"), "Slashes are rejected.");
                        TestAssert.Throws<ArgumentException>(() => server.RegisterTool(new string('a', 129), "bad", new { type = "object" }, args => "x"), "Names over 128 characters are rejected.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ToolSchemasMustBeObjects", "An input schema without a type gets \"type\": \"object\" and one of another type is rejected; output schemas of any type are accepted (omitted before 2026-07-28); unsupported dialects and unresolvable references are rejected", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("untyped", "No type", new { properties = new { a = new { type = "string" } } }, args => "ok");
                            TestAssert.Throws<ArgumentException>(() => s.RegisterTool("arrayed", "Array", new { type = "array" }, args => "x"), "An array input schema is rejected.");
                            s.RegisterTool("listout", "Array output", new { type = "object" }, new { type = "array", items = new { type = "integer" } }, args => McpToolCallResult.FromStructured(new[] { 1, 2 }));
                            TestAssert.Throws<ArgumentException>(() => s.RegisterTool("dialect", "Old dialect", new Dictionary<string, object> { { "$schema", "http://json-schema.org/draft-04/schema#" }, { "type", "object" } }, args => "x"), "An unsupported dialect is rejected.");
                            TestAssert.Throws<ArgumentException>(() => s.RegisterTool("remote", "Remote ref", new Dictionary<string, object> { { "type", "object" }, { "properties", new { a = new Dictionary<string, object> { { "$ref", "https://example.com/a.json" } } } } }, args => "x"), "An unresolvable reference is rejected.");
                        }).ConfigureAwait(false);
                        using McpTcpClient client = await ConnectAsync(fixture, ct).ConfigureAwait(false);
                        JsonProbe tools = await CallAsync(client, "tools/list", new { }, ct).ConfigureAwait(false);
                        JsonProbe schema = tools.Get("tools").EnumerateArray().First(t => t.Get("name").String() == "untyped").Get("inputSchema");
                        TestAssert.Equal("object", schema.Get("type").String(), "type object is added.");
                        TestAssert.True(schema.Get("properties").Has("a"), "The rest of the schema is kept.");
                        TestAssert.False(tools.Get("tools").EnumerateArray().First(t => t.Get("name").String() == "listout").Has("outputSchema"), "A non-object output schema is omitted for 2025-11-25 sessions.");

                        using RawLineClient stateless = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await stateless.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}}}").ConfigureAwait(false);
                        JsonProbe statelessTools = Next(stateless).Get("result").Get("tools");
                        TestAssert.Equal("array", statelessTools.EnumerateArray().First(t => t.Get("name").String() == "listout").Get("outputSchema").Get("type").String(), "2026-07-28 allows any output schema.");
                    }),

                    Case(suiteId, "OutputSchemaRequiresStructuredContent", "A tool that declares an output schema must return structured content; returning only text is an internal error", async ct =>
                    {
                        object output = new { type = "object", properties = new { n = new { type = "integer" } }, required = new[] { "n" } };
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("typed", "Structured", new { type = "object" }, output, args => McpToolCallResult.FromStructured(new { n = 3 }));
                            s.RegisterTool("untyped", "Text only", new { type = "object" }, output, args => "plain text");
                        }).ConfigureAwait(false);
                        using McpTcpClient client = await ConnectAsync(fixture, ct).ConfigureAwait(false);
                        JsonProbe typed = await CallAsync(client, "tools/call", new { name = "typed", arguments = new { } }, ct).ConfigureAwait(false);
                        TestAssert.Equal(3, typed.Get("structuredContent").Get("n").Int(), "Structured content is returned.");
                        Exception? error = await CaptureAsync(() => CallAsync(client, "tools/call", new { name = "untyped", arguments = new { } }, ct)).ConfigureAwait(false);
                        TestAssert.True(error != null && error.Message.Contains("-32603"), $"Missing structured content is an internal error: {error?.Message}");
                    }),

                    Case(suiteId, "ArgumentTypesAreChecked", "tools/call arguments must be an object and prompt arguments must be strings", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("t", "Tool", new { type = "object" }, args => "ok");
                            s.RegisterPrompt("p", "Prompt", new[] { new McpPromptArgument { Name = "x" } }, args => new McpGetPromptResult());
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"t\",\"arguments\":[1]}}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, Next(client).Get("error").Get("code").Int(), "Array tool arguments are invalid.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"prompts/get\",\"params\":{\"name\":\"p\",\"arguments\":{\"x\":1}}}").ConfigureAwait(false);
                        TestAssert.Equal(-32602, Next(client).Get("error").Get("code").Int(), "A numeric prompt argument is invalid.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"prompts/get\",\"params\":{\"name\":\"p\",\"arguments\":{\"x\":\"1\"}}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("result").IsObject, "String prompt arguments are accepted.");
                    }),

                    Case(suiteId, "CompletionsAreLimitedAndChecked", "completion/complete returns at most 100 values with the full total, and rejects references to unknown prompts", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterPrompt("p", "Prompt", new[] { new McpPromptArgument { Name = "x" } }, args => new McpGetPromptResult());
                            s.RegisterCompletionProvider("ref/prompt", "p", "x", (request, token) =>
                            {
                                McpCompleteResult result = new McpCompleteResult();
                                result.Completion.Values = Enumerable.Range(0, 150).Select(i => "v" + i).ToList();
                                return Task.FromResult(result);
                            });
                        }).ConfigureAwait(false);
                        using McpTcpClient client = await ConnectAsync(fixture, ct).ConfigureAwait(false);
                        JsonProbe completion = (await CallAsync(client, "completion/complete", new { @ref = new { type = "ref/prompt", name = "p" }, argument = new { name = "x", value = "" } }, ct).ConfigureAwait(false)).Get("completion");
                        TestAssert.Equal(100, completion.Get("values").Length, "At most 100 values are returned.");
                        TestAssert.Equal(150, completion.Get("total").Int(), "total counts everything found.");
                        TestAssert.True(completion.Get("hasMore").Bool(), "hasMore is true.");

                        Exception? unknown = await CaptureAsync(() => CallAsync(client, "completion/complete", new { @ref = new { type = "ref/prompt", name = "missing" }, argument = new { name = "x", value = "" } }, ct)).ConfigureAwait(false);
                        TestAssert.True(unknown != null && unknown.Message.Contains("-32602"), $"An unknown prompt reference is invalid params: {unknown?.Message}");
                    }),

                    Case(suiteId, "CapabilitiesAndInstructionsAreAdvertised", "initialize advertises tools, resources, prompts, completions, and logging even with nothing registered, and returns the instructions", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s => s.ServerInstructions = "Use the tools wisely.").ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        JsonProbe result = TestJson.ParseRoot(await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false)).Get("result");
                        JsonProbe capabilities = result.Get("capabilities");
                        foreach (string capability in new[] { "tools", "resources", "prompts", "completions", "logging" })
                        {
                            TestAssert.True(capabilities.Has(capability), $"{capability} is advertised.");
                        }

                        TestAssert.True(capabilities.Get("resources").Get("subscribe").Bool(), "Subscriptions are advertised and work.");
                        TestAssert.Equal("Use the tools wisely.", result.Get("instructions").String(), "Instructions are returned.");
                    }),

                    Case(suiteId, "ResultsAreDowngradedFor20241105", "A 2024-11-05 session gets no completions capability, no structuredContent, and text in place of audio and resource_link content", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("rich", "Rich content", new { type = "object" }, args => new McpToolCallResult
                            {
                                Content = new List<object>
                                {
                                    new McpAudioContent { Data = "AAAA", MimeType = "audio/wav" },
                                    new McpResourceLinkContent { Uri = "voltaic://doc", Name = "doc" }
                                },
                                StructuredContent = new { n = 1 }
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        JsonProbe init = TestJson.ParseRoot(await client.InitializeAsync("2024-11-05", ct).ConfigureAwait(false)).Get("result");
                        TestAssert.False(init.Get("capabilities").Has("completions"), "completions did not exist in 2024-11-05.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"rich\",\"arguments\":{}}}").ConfigureAwait(false);
                        JsonProbe result = Next(client).Get("result");
                        TestAssert.False(result.Has("structuredContent"), "structuredContent is removed.");
                        TestAssert.True(result.Get("content").EnumerateArray().All(block => block.Get("type").String() == "text"), "Audio and resource links become text.");

                        using RawLineClient current = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await current.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await current.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"rich\",\"arguments\":{}}}").ConfigureAwait(false);
                        JsonProbe full = Next(current).Get("result");
                        TestAssert.Equal("audio", full.Get("content")[0].Get("type").String(), "2025-11-25 keeps audio.");
                        TestAssert.Equal("doc", full.Get("content")[1].Get("name").String(), "2025-11-25 keeps resource links.");
                        TestAssert.True(full.Has("structuredContent"), "2025-11-25 keeps structuredContent.");
                    }),

                    Case(suiteId, "ResourceNotFoundUsesMinus32002", "Reading an unknown resource is -32002 in the handshake era; URI templates with query expressions match", async ct =>
                    {
                        List<string> read = new List<string>();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterResourceTemplate("voltaic://items/{id}{?format}", "items", "text/plain", uri =>
                            {
                                lock (read) read.Add(uri);
                                return new McpReadResourceResult();
                            });
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/read\",\"params\":{\"uri\":\"voltaic://items/42?format=json\"}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("result").IsObject, "A URI matching the template with a query is read.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/read\",\"params\":{\"uri\":\"voltaic://items/42\"}}").ConfigureAwait(false);
                        TestAssert.True(Next(client).Get("result").IsObject, "The query is optional.");
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/read\",\"params\":{\"uri\":\"voltaic://other/1\"}}").ConfigureAwait(false);
                        JsonProbe missing = Next(client).Get("error");
                        TestAssert.Equal(-32002, missing.Get("code").Int(), "Resource not found is -32002.");
                        TestAssert.Equal("voltaic://other/1", missing.Get("data").Get("uri").String(), "The error data names the URI.");
                        TestAssert.Equal(2, read.Count, "Only matching URIs reach the handler.");
                    }),

                    Case(suiteId, "CursorsAreOpaqueAndStable", "Pagination follows nextCursor to the end, survives a removal between pages, and rejects a forged cursor", async ct =>
                    {
                        McpTcpServer? server = null;
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            server = s;
                            s.PageSize = 2;
                            foreach (string name in new[] { "a", "b", "c", "d", "e" }) s.RegisterTool(name, name, new { type = "object" }, args => "ok");
                        }).ConfigureAwait(false);
                        using McpTcpClient client = await ConnectAsync(fixture, ct).ConfigureAwait(false);
                        List<string> seen = new List<string>();
                        JsonProbe page = await CallAsync(client, "tools/list", new { }, ct).ConfigureAwait(false);
                        seen.AddRange(page.Get("tools").EnumerateArray().Select(t => t.Get("name").String()!));
                        string cursor = page.Get("nextCursor").String()!;
                        server!.UnregisterTool(seen[0]);
                        while (true)
                        {
                            page = await CallAsync(client, "tools/list", new { cursor }, ct).ConfigureAwait(false);
                            seen.AddRange(page.Get("tools").EnumerateArray().Select(t => t.Get("name").String()!));
                            if (!page.Has("nextCursor")) break;
                            cursor = page.Get("nextCursor").String()!;
                        }

                        TestAssert.Equal(5, seen.Distinct().Count(), $"Every tool was listed once despite the removal: {string.Join(",", seen)}");
                        Exception? forged = await CaptureAsync(() => CallAsync(client, "tools/list", new { cursor = "not-a-cursor" }, ct)).ConfigureAwait(false);
                        TestAssert.True(forged != null && forged.Message.Contains("-32602"), $"A forged cursor is invalid params: {forged?.Message}");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.PageSize = 0, "PageSize must be at least 1.");
                    }),

                    Case(suiteId, "InsufficientScopeIsReportedOnStreams", "A tool that throws McpInsufficientScopeException returns error -32003 naming the scope", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartMcpTcpAsync(ct, s =>
                        {
                            s.RegisterTool("admin", "Needs a scope", new { type = "object" }, args => throw new McpInsufficientScopeException("files:write"));
                        }).ConfigureAwait(false);
                        using RawLineClient client = await RawLineClient.ConnectAsync(fixture.Port, ct).ConfigureAwait(false);
                        await client.InitializeAsync("2025-11-25", ct).ConfigureAwait(false);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"admin\",\"arguments\":{}}}").ConfigureAwait(false);
                        JsonProbe error = Next(client).Get("error");
                        TestAssert.Equal(McpInsufficientScopeException.ErrorCode, error.Get("code").Int(), "The insufficient-scope code is returned.");
                        TestAssert.True(error.Get("message").String()!.Length > 0, "A message is included.");
                    }),

                    Case(suiteId, "ResourceLinkAndAnnotationsSerialize", "resource_link always carries name, and annotations carry audience, priority, and lastModified", ct =>
                    {
                        JsonProbe link = TestJson.SerializeToElement(new McpResourceLinkContent { Uri = "voltaic://x", Name = null! });
                        TestAssert.Equal(string.Empty, link.Get("name").String(), "name is required and never null.");
                        JsonProbe annotations = TestJson.SerializeToElement(new McpAnnotations { Audience = new List<string> { "user" }, Priority = 0.5, LastModified = "2026-01-01T00:00:00Z" });
                        TestAssert.Equal("user", annotations.Get("audience")[0].String(), "audience is serialized.");
                        TestAssert.Equal(0.5, annotations.Get("priority").Double(), "priority is serialized.");
                        TestAssert.Equal("2026-01-01T00:00:00Z", annotations.Get("lastModified").String(), "lastModified is serialized.");
                        return Task.CompletedTask;
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "stream", "conformance" });
        }

        private static async Task<McpTcpClient> ConnectAsync(TcpJsonRpcFixture fixture, CancellationToken token)
        {
            return (McpTcpClient)await fixture.ConnectClientAsync(token).ConfigureAwait(false);
        }

        private static async Task<JsonProbe> CallAsync(McpTcpClient client, string method, object parameters, CancellationToken token)
        {
            return JsonProbe.From(await client.CallAsync<object?>(method, parameters, 10000, token).ConfigureAwait(false));
        }

        private static async Task PingAsync(RawLineClient client, string id)
        {
            await client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"method\":\"ping\"}").ConfigureAwait(false);
            Next(client);
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
    }
}
