namespace Test.Shared
{
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;
    public static class McpHttpProtocolSuites
    {
        public static TestSuiteDescriptor StreamableHttpCoreProtocol()
        {
            const string suiteId = "McpHttp.Protocol";

            return new TestSuiteDescriptor(
                suiteId,
                "MCP HTTP Core Protocol",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "InitializeAdvertisesRegisteredCapabilities", "initialize negotiates version and advertises registered features", async ct =>
                    {
                        await using HttpServerFixture fixture = await HttpServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult response = await fixture.PostRpcAsync("initialize", new
                        {
                            protocolVersion = McpProtocol.LatestProtocolVersion,
                            clientInfo = new { name = "test-client", version = "1.0.0" }
                        }, 1, null, ct).ConfigureAwait(false);

                        JsonProbe json = JsonProbe.Parse(response.Body);
                        JsonProbe result = json.Get("result");
                        TestAssert.Equal(McpProtocol.LatestProtocolVersion, result.Get("protocolVersion").String());
                        TestAssert.Equal("Voltaic.Test", result.Get("serverInfo").Get("name").String());
                        TestAssert.True(result.Get("capabilities").Has("tools"), "Tools capability should be advertised.");
                        TestAssert.True(result.Get("capabilities").Has("resources"), "Resources capability should be advertised.");
                        TestAssert.True(result.Get("capabilities").Has("prompts"), "Prompts capability should be advertised.");
                        TestAssert.False(String.IsNullOrEmpty(response.SessionId), "Response should include session id.");
                    }),

                    Case(suiteId, "UnsupportedVersionReturnsInvalidParams", "initialize rejects unsupported protocol versions with JSON-RPC invalid params", async ct =>
                    {
                        await using HttpServerFixture fixture = await HttpServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult response = await fixture.PostRpcAsync("initialize", new
                        {
                            protocolVersion = "1900-01-01"
                        }, 2, null, ct).ConfigureAwait(false);

                        JsonProbe json = JsonProbe.Parse(response.Body);
                        JsonProbe error = json.Get("error");
                        TestAssert.Equal(-32602, error.Get("code").Int());
                        TestAssert.True(error.Get("message").String()!.Contains("Unsupported MCP protocol version"), "Error should describe unsupported version.");
                    }),

                    Case(suiteId, "ToolsListAndCall", "tools/list and tools/call expose registered metadata and structured results", async ct =>
                    {
                        await using HttpServerFixture fixture = await HttpServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult listResponse = await fixture.PostRpcAsync("tools/list", new { }, 3, null, ct).ConfigureAwait(false);
                        JsonProbe listJson = JsonProbe.Parse(listResponse.Body);
                        JsonProbe tools = listJson.Get("result").Get("tools");
                        TestAssert.True(tools.EnumerateArray().Any(tool => tool.Get("name").String() == "sum"), "Registered tool should be listed.");

                        RpcResult callResponse = await fixture.PostRpcAsync("tools/call", new
                        {
                            name = "sum",
                            arguments = new { a = 2, b = 3 }
                        }, 4, listResponse.SessionId, ct).ConfigureAwait(false);

                        JsonProbe callJson = JsonProbe.Parse(callResponse.Body);
                        JsonProbe result = callJson.Get("result");
                        TestAssert.Equal(5, result.Get("structuredContent").Get("total").Int());
                        TestAssert.Equal("text", result.Get("content")[0].Get("type").String());
                    }),

                    Case(suiteId, "ResourcesListReadAndTemplates", "resources/list, resources/read, and templates read static and dynamic resources", async ct =>
                    {
                        await using HttpServerFixture fixture = await HttpServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult listResponse = await fixture.PostRpcAsync("resources/list", new { }, 5, null, ct).ConfigureAwait(false);
                        JsonProbe listJson = JsonProbe.Parse(listResponse.Body);
                        JsonProbe resources = listJson.Get("result").Get("resources");
                        TestAssert.True(resources.EnumerateArray().Any(resource => resource.Get("uri").String() == "voltaic://docs/readme"), "Static resource should be listed.");

                        RpcResult staticReadResponse = await fixture.PostRpcAsync("resources/read", new
                        {
                            uri = "voltaic://docs/readme"
                        }, 6, listResponse.SessionId, ct).ConfigureAwait(false);
                        JsonProbe staticReadJson = JsonProbe.Parse(staticReadResponse.Body);
                        TestAssert.Equal("readme contents", staticReadJson.Get("result").Get("contents")[0].Get("text").String());

                        RpcResult templatesResponse = await fixture.PostRpcAsync("resources/templates/list", new { }, 7, listResponse.SessionId, ct).ConfigureAwait(false);
                        JsonProbe templatesJson = JsonProbe.Parse(templatesResponse.Body);
                        TestAssert.Equal("voltaic://docs/{name}", templatesJson.Get("result").Get("resourceTemplates")[0].Get("uriTemplate").String());

                        RpcResult dynamicReadResponse = await fixture.PostRpcAsync("resources/read", new
                        {
                            uri = "voltaic://docs/intro"
                        }, 8, listResponse.SessionId, ct).ConfigureAwait(false);
                        JsonProbe dynamicReadJson = JsonProbe.Parse(dynamicReadResponse.Body);
                        TestAssert.Equal("dynamic:voltaic://docs/intro", dynamicReadJson.Get("result").Get("contents")[0].Get("text").String());
                    }),

                    Case(suiteId, "PromptsListGetAndArgumentValidation", "prompts/list and prompts/get handle required arguments and errors", async ct =>
                    {
                        await using HttpServerFixture fixture = await HttpServerFixture.StartAsync(ct).ConfigureAwait(false);

                        RpcResult listResponse = await fixture.PostRpcAsync("prompts/list", new { }, 9, null, ct).ConfigureAwait(false);
                        JsonProbe listJson = JsonProbe.Parse(listResponse.Body);
                        JsonProbe prompts = listJson.Get("result").Get("prompts");
                        TestAssert.True(prompts.EnumerateArray().Any(prompt => prompt.Get("name").String() == "summarize"), "Prompt should be listed.");

                        RpcResult missingArgResponse = await fixture.PostRpcAsync("prompts/get", new
                        {
                            name = "summarize",
                            arguments = new { }
                        }, 10, listResponse.SessionId, ct).ConfigureAwait(false);
                        JsonProbe missingArgJson = JsonProbe.Parse(missingArgResponse.Body);
                        TestAssert.Equal(-32602, missingArgJson.Get("error").Get("code").Int());

                        RpcResult promptResponse = await fixture.PostRpcAsync("prompts/get", new
                        {
                            name = "summarize",
                            arguments = new { topic = "Voltaic" }
                        }, 11, listResponse.SessionId, ct).ConfigureAwait(false);
                        JsonProbe promptJson = JsonProbe.Parse(promptResponse.Body);
                        TestAssert.Equal("user", promptJson.Get("result").Get("messages")[0].Get("role").String());
                    }),
                });
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "mcp", "http" });
        }

        private sealed class HttpServerFixture : IAsyncDisposable
        {
            private readonly CancellationTokenSource _TokenSource;
            private readonly Task _ServerTask;

            private HttpServerFixture(McpHttpServer server, int port, CancellationTokenSource tokenSource, Task serverTask)
            {
                Server = server;
                Port = port;
                _TokenSource = tokenSource;
                _ServerTask = serverTask;
                Client = new HttpClient();
            }

            public McpHttpServer Server { get; }

            public int Port { get; }

            public HttpClient Client { get; }

            public static async Task<HttpServerFixture> StartAsync(CancellationToken token)
            {
                int port = GetFreePort();
                McpHttpServer server = new McpHttpServer("localhost", port);
                server.ServerName = "Voltaic.Test";
                server.ServerVersion = "0.3.0-test";
                ConfigureServer(server);

                CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task serverTask = Task.Run(() => server.StartAsync(tokenSource.Token), CancellationToken.None);

                HttpServerFixture fixture = new HttpServerFixture(server, port, tokenSource, serverTask);
                await fixture.WaitUntilReadyAsync(token).ConfigureAwait(false);
                return fixture;
            }

            public async Task<RpcResult> PostRpcAsync(string method, object? parameters, object id, string? sessionId, CancellationToken token)
            {
                JsonRpcRequest request = new JsonRpcRequest
                {
                    Method = method,
                    Params = parameters,
                    Id = id
                };

                using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{Port}/mcp/")
                {
                    Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
                };
                message.Headers.Accept.ParseAdd("application/json");
                message.Headers.Accept.ParseAdd("text/event-stream");
                if (!String.IsNullOrEmpty(sessionId))
                {
                    message.Headers.Add(McpProtocol.SessionIdHeader, sessionId);
                }

                using HttpResponseMessage response = await Client.SendAsync(message, token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                string? returnedSessionId = response.Headers.TryGetValues(McpProtocol.SessionIdHeader, out IEnumerable<string>? values)
                    ? values.FirstOrDefault()
                    : null;
                return new RpcResult(body, returnedSessionId);
            }

            public async ValueTask DisposeAsync()
            {
                Server.Stop();
                _TokenSource.Cancel();
                Client.Dispose();

                await Task.WhenAny(_ServerTask, Task.Delay(1000)).ConfigureAwait(false);
                _TokenSource.Dispose();
                Server.Dispose();
            }

            private async Task WaitUntilReadyAsync(CancellationToken token)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        using HttpResponseMessage response = await Client.GetAsync($"http://localhost:{Port}/", token).ConfigureAwait(false);
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            return;
                        }
                    }
                    catch
                    {
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                throw new TimeoutException("HTTP MCP test server did not become ready.");
            }

            private static void ConfigureServer(McpHttpServer server)
            {
                server.RegisterTool(
                    "sum",
                    "Adds two numbers",
                    new
                    {
                        type = "object",
                        properties = new { a = new { type = "number" }, b = new { type = "number" } },
                        required = new[] { "a", "b" }
                    },
                    new { type = "object", properties = new { total = new { type = "number" } } },
                    args =>
                    {
                        int a = (int)(args?.GetInt64("a") ?? 0);
                        int b = (int)(args?.GetInt64("b") ?? 0);
                        return McpToolCallResult.FromStructured(new { total = a + b });
                    });

                server.RegisterResource(
                    "voltaic://docs/readme",
                    "readme",
                    "text/plain",
                    () => new McpReadResourceResult
                    {
                        Contents = new List<object>
                        {
                            new McpTextResourceContents
                            {
                                Uri = "voltaic://docs/readme",
                                MimeType = "text/plain",
                                Text = "readme contents"
                            }
                        }
                    });

                server.RegisterResourceTemplate(
                    "voltaic://docs/{name}",
                    "doc",
                    "text/plain",
                    uri => new McpReadResourceResult
                    {
                        Contents = new List<object>
                        {
                            new McpTextResourceContents
                            {
                                Uri = uri,
                                MimeType = "text/plain",
                                Text = $"dynamic:{uri}"
                            }
                        }
                    });

                server.RegisterPrompt(
                    "summarize",
                    "Summarizes a topic",
                    new[]
                    {
                        new McpPromptArgument { Name = "topic", Required = true }
                    },
                    args => new McpGetPromptResult
                    {
                        Description = "Summary prompt",
                        Messages = new List<McpPromptMessage>
                        {
                            new McpPromptMessage
                            {
                                Role = "user",
                                Content = new McpTextContent
                                {
                                    Text = $"Summarize {args?.GetString("topic")}"
                                }
                            }
                        }
                    });
            }

            private static int GetFreePort()
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        private sealed record RpcResult(string Body, string? SessionId);
    }
}
