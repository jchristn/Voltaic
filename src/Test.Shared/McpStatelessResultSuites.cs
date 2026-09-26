namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Positive and negative coverage for results served under the stateless 2026-07-28 revision. Every
    /// result carries <c>resultType</c>; cacheable results (list results, <c>resources/read</c>, and
    /// <c>server/discover</c>) carry <c>ttlMs</c> and <c>cacheScope</c>; handler-supplied values are
    /// never overwritten; handshake-era output is unchanged; and the exact request sequence Claude Code
    /// 2.1.x sends succeeds with and without an <c>AuthenticationHandler</c>.
    /// </summary>
    public static class McpStatelessResultSuites
    {
        /// <summary>
        /// Builds the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public static TestSuiteDescriptor StatelessResults()
        {
            const string suiteId = "McpVersion.StatelessResults";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP Stateless Result Shape (2026-07-28)",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ClaudeCodeSequenceWithoutAuth", "The Claude Code 2.1.x discover, list, and call sequence succeeds without authentication", async ct =>
                    {
                        await AssertClaudeCodeSequenceAsync(false, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "ClaudeCodeSequenceWithAuth", "The Claude Code 2.1.x discover, list, and call sequence succeeds with an AuthenticationHandler", async ct =>
                    {
                        await AssertClaudeCodeSequenceAsync(true, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "ClaudeCodeSequenceOnRpcPathWithoutAuth", "The Claude Code 2.1.x sequence sent to the JSON-RPC path gets stateless results without authentication", async ct =>
                    {
                        await AssertClaudeCodeSequenceAsync(false, ct, "/rpc/").ConfigureAwait(false);
                    }),

                    Case(suiteId, "ClaudeCodeSequenceOnRpcPathWithAuth", "The Claude Code 2.1.x sequence sent to the JSON-RPC path gets stateless results with an AuthenticationHandler", async ct =>
                    {
                        await AssertClaudeCodeSequenceAsync(true, ct, "/rpc/").ConfigureAwait(false);
                    }),

                    Case(suiteId, "RpcPathStatelessRequiresRoutingHeaders", "A stateless request to the JSON-RPC path without Mcp-Method is rejected like one to the MCP path", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, RegisterSurface, ct).ConfigureAwait(false);

                        Dictionary<string, string> headers = new Dictionary<string, string>
                        {
                            { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 }
                        };
                        RpcResult list = await McpHttpTestRequests.SendAsync(
                            fixture, McpHttpTestRequests.BuildBody("tools/list", 1, null), headers, false, ct, "/rpc/").ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.BadRequest, list.StatusCode, $"A stateless request without Mcp-Method is rejected. Body: {list.Body}");
                        TestAssert.Null(list.SessionId, "A rejected stateless request carries no session id.");
                    }),

                    Case(suiteId, "RpcPathWithoutVersionHeaderUnchanged", "A plain JSON-RPC request to the JSON-RPC path gets handshake results, no stateless fields, and no session", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, RegisterSurface, ct).ConfigureAwait(false);

                        RpcResult list = await McpHttpTestRequests.SendAsync(
                            fixture, McpHttpTestRequests.BuildBody("tools/list", 1, null), new Dictionary<string, string>(), false, ct, "/rpc/").ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, list.StatusCode, $"tools/list should succeed. Body: {list.Body}");
                        TestAssert.True(list.SessionId == null, "A sessionless JSON-RPC request is not issued a session.");
                        TestAssert.False(list.Result.Has("resultType"), "Handshake-era results carry no resultType.");
                        TestAssert.True(list.Result.Get("tools").EnumerateArray().Any(tool => tool.Get("name").String() == "echo-tool"), "tools/list returns the registered tools.");
                    }),

                    Case(suiteId, "DiscoverOmitsUndeliverableChangeNotifications", "server/discover does not advertise listChanged or subscribe, which need subscriptions/listen", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, RegisterSurface, ct).ConfigureAwait(false);

                        RpcResult discover = await McpHttpTestRequests.SendStatelessAsync(fixture, "server/discover", "d", null, null, null, ct).ConfigureAwait(false);
                        JsonProbe capabilities = discover.Result.Get("capabilities");
                        TestAssert.True(capabilities.Has("tools") && capabilities.Has("resources") && capabilities.Has("prompts"), "Registered features are still advertised.");
                        TestAssert.False(capabilities.Get("tools").Has("listChanged"), "tools.listChanged is not advertised on the stateless surface.");
                        TestAssert.False(capabilities.Get("prompts").Has("listChanged"), "prompts.listChanged is not advertised on the stateless surface.");
                        TestAssert.False(capabilities.Get("resources").Has("listChanged"), "resources.listChanged is not advertised on the stateless surface.");
                        TestAssert.False(capabilities.Get("resources").Has("subscribe"), "resources.subscribe is not advertised on the stateless surface.");

                        RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20251125, null, ct).ConfigureAwait(false);
                        TestAssert.True(initialize.Result.Get("capabilities").Get("tools").Get("listChanged").Bool(), "The handshake surface still advertises listChanged.");
                        TestAssert.True(initialize.Result.Get("capabilities").Get("resources").Get("subscribe").Bool(), "The handshake surface still advertises resource subscriptions.");
                    }),

                    Case(suiteId, "EveryBuiltInResultCarriesResultType", "Every built-in method's stateless result carries resultType complete", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, RegisterSurface, ct).ConfigureAwait(false);

                        List<StatelessCall> calls = new List<StatelessCall>
                        {
                            new StatelessCall("server/discover", null, null),
                            new StatelessCall("tools/list", null, null),
                            new StatelessCall("tools/call", "echo-tool", new Dictionary<string, object?> { { "name", "echo-tool" }, { "arguments", new { } } }),
                            new StatelessCall("resources/list", null, null),
                            new StatelessCall("resources/templates/list", null, null),
                            new StatelessCall("resources/read", "voltaic://static", new Dictionary<string, object?> { { "uri", "voltaic://static" } }),
                            new StatelessCall("prompts/list", null, null),
                            new StatelessCall("prompts/get", "greeting", new Dictionary<string, object?> { { "name", "greeting" } }),
                            new StatelessCall("completion/complete", null, new Dictionary<string, object?>
                            {
                                { "ref", new { type = "ref/prompt", name = "greeting" } },
                                { "argument", new { name = "topic", value = "Vo" } }
                            }),
                        };

                        int id = 0;
                        foreach (StatelessCall call in calls)
                        {
                            RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, call.Method, ++id, call.Parameters, call.Name, null, ct).ConfigureAwait(false);
                            TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, $"{call.Method} should return 200. Body: {response.Body}");
                            TestAssert.False(response.Root.Has("error"), $"{call.Method} should not fail. Body: {response.Body}");
                            TestAssert.Equal("complete", response.Result.Get("resultType").String(), $"{call.Method} should carry resultType complete.");
                            TestAssert.Equal("Voltaic.Test", response.Result.Get("_meta").Get("io.modelcontextprotocol/serverInfo").Get("name").String(), $"{call.Method} identifies the server in _meta.");
                        }

                        // Methods 2026-07-28 removed are not served under it.
                        foreach (string removed in new[] { "ping", "logging/setLevel", "resources/subscribe", "resources/unsubscribe" })
                        {
                            RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, removed, ++id, new Dictionary<string, object?> { { "uri", "voltaic://static" }, { "level", "debug" } }, null, null, ct).ConfigureAwait(false);
                            TestAssert.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{removed} is not part of 2026-07-28. Body: {response.Body}");
                            TestAssert.Equal(-32601, response.Error.Get("code").Int(), $"{removed} is method not found.");
                        }
                    }),

                    Case(suiteId, "CacheableResultsCarryDefaultCacheGuidance", "Cacheable stateless results default to ttlMs 0 and cacheScope private", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, RegisterSurface, ct).ConfigureAwait(false);

                        List<StatelessCall> cacheable = new List<StatelessCall>
                        {
                            new StatelessCall("server/discover", null, null),
                            new StatelessCall("tools/list", null, null),
                            new StatelessCall("resources/list", null, null),
                            new StatelessCall("resources/templates/list", null, null),
                            new StatelessCall("prompts/list", null, null),
                            new StatelessCall("resources/read", "voltaic://static", new Dictionary<string, object?> { { "uri", "voltaic://static" } })
                        };

                        foreach (StatelessCall call in cacheable)
                        {
                            RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, call.Method, 1, call.Parameters, call.Name, null, ct).ConfigureAwait(false);
                            TestAssert.Equal(0L, response.Result.Get("ttlMs").Long(), $"{call.Method} ttlMs should default to 0.");
                            TestAssert.Equal("private", response.Result.Get("cacheScope").String(), $"{call.Method} cacheScope should default to private.");
                        }

                        RpcResult toolCall = await McpHttpTestRequests.SendStatelessAsync(
                            fixture, "tools/call", 2, new Dictionary<string, object?> { { "name", "echo-tool" }, { "arguments", new { } } }, "echo-tool", null, ct).ConfigureAwait(false);
                        TestAssert.False(toolCall.Result.Has("ttlMs"), "A tools/call result is not cacheable and carries no ttlMs.");
                        TestAssert.False(toolCall.Result.Has("cacheScope"), "A tools/call result is not cacheable and carries no cacheScope.");
                    }),

                    Case(suiteId, "ConfiguredCacheGuidanceNotOverwritten", "ListCacheTtlMs and ListCacheScope take precedence over the stateless defaults", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, server =>
                        {
                            RegisterSurface(server);
                            server.ListCacheTtlMs = 60000;
                            server.ListCacheScope = "public";
                        }, ct).ConfigureAwait(false);

                        foreach (string method in new[] { "tools/list", "resources/list", "resources/templates/list", "prompts/list" })
                        {
                            RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, method, 1, null, null, null, ct).ConfigureAwait(false);
                            TestAssert.Equal(60000L, response.Result.Get("ttlMs").Long(), $"{method} keeps the configured ttlMs.");
                            TestAssert.Equal("public", response.Result.Get("cacheScope").String(), $"{method} keeps the configured cacheScope.");
                        }
                    }),

                    Case(suiteId, "InputRequiredNotOverwritten", "An input_required result keeps its resultType", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, server =>
                            server.RegisterTool("confirm", "Needs confirmation", new { type = "object" }, _ => new McpInputRequiredResult
                            {
                                InputRequests = new Dictionary<string, McpInputRequest>
                                {
                                    { "confirm", new McpInputRequest { Method = "elicitation/create", Params = new { message = "Proceed?" } } }
                                },
                                RequestState = "state-1"
                            }), ct).ConfigureAwait(false);

                        RpcResult response = await McpHttpTestRequests.SendStatelessAsync(
                            fixture, "tools/call", 1, new Dictionary<string, object?> { { "name", "confirm" }, { "arguments", new { } } }, "confirm", null, ct).ConfigureAwait(false);
                        TestAssert.Equal("input_required", response.Result.Get("resultType").String(), "input_required must not be replaced by complete.");
                    }),

                    Case(suiteId, "TaskResultNotOverwritten", "A task result from a custom method keeps resultType task", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, server =>
                            server.RegisterMethod("tasks/start", _ => new McpCreateTaskResult { TaskId = "task-1", Status = McpTaskStatus.Working }), ct).ConfigureAwait(false);

                        RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, "tasks/start", 1, null, null, null, ct).ConfigureAwait(false);
                        TestAssert.Equal("task", response.Result.Get("resultType").String(), "task must not be replaced by complete.");
                        TestAssert.Equal("task-1", response.Result.Get("taskId").String(), "The task payload is intact.");
                    }),

                    Case(suiteId, "CustomMcpResultIsStamped", "A custom method returning an McpResult subclass is stamped", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, server =>
                            server.RegisterMethod("custom/typed", _ => new McpEmptyResult()), ct).ConfigureAwait(false);

                        RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, "custom/typed", 1, null, null, null, ct).ConfigureAwait(false);
                        TestAssert.True(response.Result.IsObject && response.Result.Length == 2, $"An empty McpResult has resultType and _meta. Body: {response.Body}");
                        TestAssert.Equal("complete", response.Result.Get("resultType").String(), "resultType is complete.");
                        TestAssert.True(response.Result.Get("_meta").Has("io.modelcontextprotocol/serverInfo"), "_meta identifies the server.");
                    }),

                    Case(suiteId, "CustomPlainObjectReturnedUnmodified", "A custom method's plain object result gets resultType under 2026-07-28, and a non-object result becomes -32603 (results must be objects)", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, server =>
                        {
                            server.RegisterMethod("custom/plain", _ => new { value = 7 });
                            server.RegisterMethod("custom/text", _ => "just text");
                        }, ct).ConfigureAwait(false);

                        RpcResult response = await McpHttpTestRequests.SendStatelessAsync(fixture, "custom/plain", 1, null, null, null, ct).ConfigureAwait(false);
                        RpcResult text = await McpHttpTestRequests.SendStatelessAsync(fixture, "custom/text", 2, null, null, null, ct).ConfigureAwait(false);
                        TestAssert.Equal(7, response.Result.Get("value").Int(), "The plain payload is intact.");
                        TestAssert.Equal("complete", response.Result.Get("resultType").String(), "Every 2026-07-28 result carries resultType.");
                        TestAssert.Equal(-32603, text.Error.Get("code").Int(), "A non-object result cannot carry resultType.");
                    }),

                    Case(suiteId, "ErrorsAreNotStamped", "Stateless error responses carry no result and no resultType", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, RegisterSurface, ct).ConfigureAwait(false);

                        RpcResult unknownTool = await McpHttpTestRequests.SendStatelessAsync(
                            fixture, "tools/call", 1, new Dictionary<string, object?> { { "name", "missing" }, { "arguments", new { } } }, "missing", null, ct).ConfigureAwait(false);
                        TestAssert.Equal(-32602, unknownTool.Error.Get("code").Int(), "An unknown tool is invalid params.");
                        TestAssert.False(unknownTool.Root.Has("result"), "An error response has no result.");

                        RpcResult unknownMethod = await McpHttpTestRequests.SendStatelessAsync(fixture, "does/notexist", 2, null, null, null, ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.NotFound, unknownMethod.StatusCode, "An unknown stateless method is 404.");
                        TestAssert.False(unknownMethod.Root.Has("result"), "A method-not-found response has no result.");
                    }),

                    Case(suiteId, "ReusedResultInstanceDoesNotLeak", "A result instance reused across requests carries no stateless fields into a handshake response", async ct =>
                    {
                        McpToolCallResult shared = McpToolCallResult.FromText("shared");
                        McpListToolsResult sharedList = new McpListToolsResult { Tools = new List<ToolDefinition>() };

                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, server =>
                        {
                            server.RegisterTool("shared-tool", "Returns a shared instance", new { type = "object" }, _ => shared);
                            server.RegisterMethod("custom/list", _ => sharedList);
                        }, ct).ConfigureAwait(false);

                        RpcResult stateless = await McpHttpTestRequests.SendStatelessAsync(
                            fixture, "tools/call", 1, new Dictionary<string, object?> { { "name", "shared-tool" }, { "arguments", new { } } }, "shared-tool", null, ct).ConfigureAwait(false);
                        TestAssert.Equal("complete", stateless.Result.Get("resultType").String(), "The stateless response is stamped.");
                        TestAssert.Null(shared.ResultType, "The shared instance is restored after serialization.");

                        RpcResult statelessList = await McpHttpTestRequests.SendStatelessAsync(fixture, "custom/list", 2, null, null, null, ct).ConfigureAwait(false);
                        TestAssert.Equal(0L, statelessList.Result.Get("ttlMs").Long(), "The stateless list response carries ttlMs.");
                        TestAssert.Null(sharedList.TtlMs, "The shared list instance's ttlMs is restored.");
                        TestAssert.Null(sharedList.CacheScope, "The shared list instance's cacheScope is restored.");

                        RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, McpProtocol.ProtocolVersion20251125, null, ct).ConfigureAwait(false);
                        RpcResult handshake = await McpHttpTestRequests.SendAsync(
                            fixture, "tools/call", 3, new { name = "shared-tool", arguments = new { } }, initialize.SessionId, McpProtocol.ProtocolVersion20251125, null, ct).ConfigureAwait(false);
                        TestAssert.False(handshake.Result.Has("resultType"), "The handshake response must not inherit resultType from the earlier stateless request.");
                    }),

                    Case(suiteId, "HandshakeResultsOmitStatelessFields", "Handshake-era results carry no resultType, ttlMs, or cacheScope, and empty results stay {}", async ct =>
                    {
                        await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(false, RegisterSurface, ct).ConfigureAwait(false);

                        foreach (McpProtocolVersionInfo info in McpProtocol.SupportedVersions.Where(entry => entry.Era == McpProtocolEra.Handshake))
                        {
                            string version = info.Version;
                            RpcResult initialize = await McpHttpTestRequests.InitializeAsync(fixture, version, null, ct).ConfigureAwait(false);
                            TestAssert.Equal(version, initialize.Result.Get("protocolVersion").String(), $"initialize negotiates {version}.");
                            TestAssert.False(initialize.Result.Has("resultType"), $"initialize under {version} has no resultType.");

                            string? session = initialize.SessionId;
                            foreach (string method in new[] { "tools/list", "resources/list", "prompts/list" })
                            {
                                RpcResult list = await McpHttpTestRequests.SendAsync(fixture, method, 2, new { }, session, version, null, ct).ConfigureAwait(false);
                                TestAssert.False(list.Result.Has("resultType"), $"{method} under {version} has no resultType.");
                                TestAssert.False(list.Result.Has("ttlMs"), $"{method} under {version} has no ttlMs.");
                                TestAssert.False(list.Result.Has("cacheScope"), $"{method} under {version} has no cacheScope.");
                            }

                            RpcResult read = await McpHttpTestRequests.SendAsync(fixture, "resources/read", 3, new { uri = "voltaic://static" }, session, version, null, ct).ConfigureAwait(false);
                            TestAssert.False(read.Result.Has("resultType"), $"resources/read under {version} has no resultType.");

                            RpcResult setLevel = await McpHttpTestRequests.SendAsync(fixture, "logging/setLevel", 4, new { level = "info" }, session, version, null, ct).ConfigureAwait(false);
                            TestAssert.True(setLevel.Result.IsObject && setLevel.Result.Length == 0, $"An empty result under {version} serializes to {{}}. Body: {setLevel.Body}");
                        }
                    }),
                });
        }

        private static async Task AssertClaudeCodeSequenceAsync(bool withAuth, CancellationToken token, string path = "/mcp/")
        {
            string? observedPrincipal = null;
            await using HttpMcpTestServerFixture fixture = await McpHttpTestRequests.StartAsync(withAuth, server =>
            {
                RegisterSurface(server);
                server.RegisterTool("whoami", "Reports the caller", new { type = "object" }, _ =>
                {
                    observedPrincipal = RpcCallContext.Current?.Principal;
                    return "caller:" + (observedPrincipal ?? "none");
                });
            }, token).ConfigureAwait(false);
            string? authorization = withAuth ? McpHttpTestRequests.ValidToken : null;
            int sessionsBefore = fixture.Server.GetActiveSessions().Count;

            // 1. server/discover: the first request Claude Code 2.1.x sends; it never calls initialize.
            RpcResult discover = await McpHttpTestRequests.SendStatelessAsync(fixture, "server/discover", "server-discover-probe-1", null, null, authorization, token, path).ConfigureAwait(false);
            TestAssert.Equal(HttpStatusCode.OK, discover.StatusCode, $"server/discover should succeed. Body: {discover.Body}");
            TestAssert.Equal("complete", discover.Result.Get("resultType").String(), "server/discover carries resultType complete.");
            TestAssert.True(discover.Result.Get("supportedVersions").EnumerateArray().Any(version => version.String() == McpProtocol.ProtocolVersion20260728), "The stateless revision is offered.");
            TestAssert.Null(discover.SessionId, "Stateless responses carry no session id.");

            // 2. tools/list: this failed before v1.1.0 (missing resultType, then missing ttlMs/cacheScope).
            RpcResult tools = await McpHttpTestRequests.SendStatelessAsync(fixture, "tools/list", 0, null, null, authorization, token, path).ConfigureAwait(false);
            TestAssert.Equal(HttpStatusCode.OK, tools.StatusCode, $"tools/list should succeed. Body: {tools.Body}");
            TestAssert.Equal("complete", tools.Result.Get("resultType").String(), "tools/list carries resultType complete.");
            TestAssert.Equal(0L, tools.Result.Get("ttlMs").Long(), "tools/list carries a numeric ttlMs.");
            TestAssert.Equal("private", tools.Result.Get("cacheScope").String(), "tools/list carries a valid cacheScope.");
            TestAssert.True(tools.Result.Get("tools").EnumerateArray().Any(tool => tool.Get("name").String() == "whoami"), "tools/list returns the registered tools.");
            TestAssert.Equal(
                "echo-tool,whoami",
                String.Join(",", tools.Result.Get("tools").EnumerateArray().Select(tool => tool.Get("name").String()).OrderBy(name => name, StringComparer.Ordinal)),
                "tools/list returns exactly the tools the application registered, with no Voltaic diagnostic tools.");

            // 3. tools/call
            RpcResult call = await McpHttpTestRequests.SendStatelessAsync(
                fixture, "tools/call", 1, new Dictionary<string, object?> { { "name", "whoami" }, { "arguments", new { } } }, "whoami", authorization, token, path).ConfigureAwait(false);
            TestAssert.Equal(HttpStatusCode.OK, call.StatusCode, $"tools/call should succeed. Body: {call.Body}");
            TestAssert.Equal("complete", call.Result.Get("resultType").String(), "tools/call carries resultType complete.");
            TestAssert.Equal(withAuth ? "caller:test-user" : "caller:none", call.Result.Get("content")[0].Get("text").String(), "The caller context reaches the stateless tool handler.");

            TestAssert.Equal(sessionsBefore, fixture.Server.GetActiveSessions().Count, "Stateless requests must not create server sessions.");
        }

        private static void RegisterSurface(McpHttpServer server)
        {
            server.RegisterTool("echo-tool", "Echo tool", new { type = "object" }, _ => "echoed");
            server.RegisterResource("voltaic://static", "static", "text/plain", () => new McpReadResourceResult
            {
                Contents = new List<object> { new McpTextResourceContents { Uri = "voltaic://static", MimeType = "text/plain", Text = "static" } }
            });
            server.RegisterResourceTemplate("voltaic://docs/{name}", "doc", "text/plain", uri => new McpReadResourceResult
            {
                Contents = new List<object> { new McpTextResourceContents { Uri = uri, MimeType = "text/plain", Text = uri } }
            });
            server.RegisterPrompt(
                "greeting",
                "Greeting prompt",
                new[] { new McpPromptArgument { Name = "topic", Required = false } },
                _ => new McpGetPromptResult
                {
                    Messages = new List<McpPromptMessage> { new McpPromptMessage { Role = "user", Content = new McpTextContent { Text = "hello" } } }
                });
            server.RegisterCompletionProvider("ref/prompt", "greeting", "topic", (request, token) => Task.FromResult(new McpCompleteResult
            {
                Completion = new McpCompletion { Values = new List<string> { "Voltaic" } }
            }));
        }

        private sealed class StatelessCall
        {
            public StatelessCall(string method, string? name, Dictionary<string, object?>? parameters)
            {
                Method = method;
                Name = name;
                Parameters = parameters;
            }

            public string Method { get; }

            public string? Name { get; }

            public Dictionary<string, object?>? Parameters { get; }
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "mcp", "http", "version", "stateless" });
        }
    }
}
