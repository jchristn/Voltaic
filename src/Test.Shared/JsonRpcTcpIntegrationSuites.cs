namespace Test.Shared
{
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;
    public static class JsonRpcTcpIntegrationSuites
    {
        public static TestSuiteDescriptor TcpClientServerProtocol()
        {
            const string suiteId = "JsonRpc.Tcp.Integration";

            return new TestSuiteDescriptor(
                suiteId,
                "JSON-RPC TCP Integration Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "BuiltInPingEchoAndAdd", "JsonRpcClient can call built-in server methods", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        string pong = await client.CallAsync<string>("ping", token: ct).ConfigureAwait(false);
                        string echo = await client.CallAsync<string>("echo", new { message = "hello" }, token: ct).ConfigureAwait(false);
                        double sum = await client.CallAsync<double>("add", new { a = 2, b = 3.5 }, token: ct).ConfigureAwait(false);

                        TestAssert.Equal("pong", pong);
                        TestAssert.Equal("hello", echo);
                        TestAssert.Equal(5.5, sum);
                    }),

                    Case(suiteId, "SyncMethodRegistration", "JsonRpcServer invokes synchronous registered methods", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("double", args => args!.Value.GetProperty("value").GetInt32() * 2);
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        int result = await client.CallAsync<int>("double", new { value = 21 }, token: ct).ConfigureAwait(false);

                        TestAssert.Equal(42, result);
                    }),

                    Case(suiteId, "AsyncMethodRegistration", "JsonRpcServer invokes asynchronous registered methods", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("async", async args =>
                            {
                                await Task.Delay(1, ct).ConfigureAwait(false);
                                return (object)new { ok = true };
                            });
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        JsonElement result = await client.CallAsync<JsonElement>("async", token: ct).ConfigureAwait(false);

                        TestAssert.True(result.GetProperty("ok").GetBoolean(), "Async method result should be returned.");
                    }),

                    Case(suiteId, "CancellationAwareMethodRegistration", "JsonRpcServer passes cancellation tokens to token-aware methods", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("token", (args, token) => Task.FromResult<object>(new { canBeCanceled = token.CanBeCanceled }));
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        JsonElement result = await client.CallAsync<JsonElement>("token", token: ct).ConfigureAwait(false);

                        TestAssert.True(result.GetProperty("canBeCanceled").GetBoolean(), "Server token should be cancellable.");
                    }),

                    Case(suiteId, "ObjectOverloadReturnsJsonElement", "JsonRpcClient object overload returns JSON content", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("object", _ => new { answer = 42 });
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        object? result = await client.CallAsync("object", token: ct).ConfigureAwait(false);

                        TestAssert.True(result is JsonElement, "Object overload should deserialize object results as JsonElement.");
                        TestAssert.Equal(42, ((JsonElement)result!).GetProperty("answer").GetInt32());
                    }),

                    Case(suiteId, "NullResultReturnsDefault", "JsonRpcClient returns default when result is null", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("null", (Func<JsonElement?, object>)(_ => null!));
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        string? result = await client.CallAsync<string?>("null", token: ct).ConfigureAwait(false);

                        TestAssert.Null(result, "Null server result should return default for reference types.");
                    }),

                    Case(suiteId, "MethodNotFoundMapsToClientException", "Unknown methods map to JSON-RPC method-not-found errors", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        await TestAssert.ThrowsAsync<Exception>(() => client.CallAsync<string>("missing", token: ct), "Missing method should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "HandlerExceptionMapsToInternalError", "Handler exceptions map to JSON-RPC internal errors", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("explode", _ => throw new InvalidOperationException("boom"));
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        Exception ex = await CaptureExceptionAsync(() => client.CallAsync<string>("explode", token: ct)).ConfigureAwait(false);
                        TestAssert.True(ex.Message.Contains("RPC Error -32603"), "Client exception should include internal error code.");
                    }),

                    Case(suiteId, "McpProtocolExceptionMapsToProtocolError", "McpProtocolException keeps its JSON-RPC error code over TCP", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("bad", _ => throw McpProtocolException.InvalidParams("bad params"));
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        Exception ex = await CaptureExceptionAsync(() => client.CallAsync<string>("bad", token: ct)).ConfigureAwait(false);
                        TestAssert.True(ex.Message.Contains("RPC Error -32602"), "Client exception should include invalid params code.");
                    }),

                    Case(suiteId, "ClientNotificationRaisesServerRequestReceived", "Client notifications are observed by the server and do not require responses", async ct =>
                    {
                        TaskCompletionSource<JsonRpcRequestEventArgs> received = new TaskCompletionSource<JsonRpcRequestEventArgs>();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("notify", _ => "ignored");
                            server.RequestReceived += (_, args) =>
                            {
                                if (args.Method == "notify")
                                {
                                    received.TrySetResult(args);
                                }
                            };
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        await client.NotifyAsync("notify", new { value = 1 }, ct).ConfigureAwait(false);
                        JsonRpcRequestEventArgs args = await WaitForTaskAsync(received.Task, ct).ConfigureAwait(false);

                        TestAssert.True(args.IsNotification, "Request should be a notification.");
                        TestAssert.Null(args.RequestId, "Notification request ID should be null.");
                    }),

                    Case(suiteId, "ClientAndServerEvents", "Client and server lifecycle/request/response events carry expected data", async ct =>
                    {
                        TaskCompletionSource<ClientConnection> serverConnected = new TaskCompletionSource<ClientConnection>();
                        TaskCompletionSource<JsonRpcResponseEventArgs> responseSent = new TaskCompletionSource<JsonRpcResponseEventArgs>();
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("ping", _ => "pong");
                            server.ClientConnected += (_, connection) => serverConnected.TrySetResult(connection);
                            server.ResponseSent += (_, args) =>
                            {
                                if (args.Method == "ping")
                                {
                                    responseSent.TrySetResult(args);
                                }
                            };
                        }, includeDefaultMethods: false).ConfigureAwait(false);

                        using JsonRpcClient client = new JsonRpcClient();
                        TaskCompletionSource<ClientConnectedEventArgs> clientConnected = new TaskCompletionSource<ClientConnectedEventArgs>();
                        TaskCompletionSource<RequestSentEventArgs> requestSent = new TaskCompletionSource<RequestSentEventArgs>();
                        TaskCompletionSource<ResponseReceivedEventArgs> responseReceived = new TaskCompletionSource<ResponseReceivedEventArgs>();
                        client.Connected += (_, args) => clientConnected.TrySetResult(args);
                        client.RequestSent += (_, args) => requestSent.TrySetResult(args);
                        client.ResponseReceived += (_, args) => responseReceived.TrySetResult(args);

                        bool connected = await client.ConnectAsync("127.0.0.1", fixture.Port, ct).ConfigureAwait(false);
                        TestAssert.True(connected, "Client should connect.");
                        string result = await client.CallAsync<string>("ping", token: ct).ConfigureAwait(false);

                        TestAssert.Equal("pong", result);
                        TestAssert.Equal(ClientConnectionTypeEnum.Tcp, (await WaitForTaskAsync(clientConnected.Task, ct).ConfigureAwait(false)).ConnectionType);
                        TestAssert.Equal(ClientConnectionTypeEnum.Tcp, (await WaitForTaskAsync(serverConnected.Task, ct).ConfigureAwait(false)).Type);
                        TestAssert.Equal("ping", (await WaitForTaskAsync(requestSent.Task, ct).ConfigureAwait(false)).Method);
                        TestAssert.Equal("ping", (await WaitForTaskAsync(responseReceived.Task, ct).ConfigureAwait(false)).Method);
                        TestAssert.True((await WaitForTaskAsync(responseSent.Task, ct).ConfigureAwait(false)).IsSuccess, "Server response event should be success.");
                    }),

                    Case(suiteId, "BroadcastNotification", "JsonRpcServer broadcasts notifications to connected clients", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        await WaitUntilAsync(() => fixture.Server.GetConnectedClients().Count == 1, ct).ConfigureAwait(false);

                        TaskCompletionSource<JsonRpcRequest> notification = new TaskCompletionSource<JsonRpcRequest>();
                        client.NotificationReceived += (_, request) =>
                        {
                            if (request.Method == "server/event")
                            {
                                notification.TrySetResult(request);
                            }
                        };

                        await fixture.Server.BroadcastNotificationAsync("server/event", new { value = 9 }, ct).ConfigureAwait(false);
                        JsonRpcRequest received = await WaitForTaskAsync(notification.Task, ct).ConfigureAwait(false);

                        TestAssert.Equal("server/event", received.Method);
                        JsonElement parameters = (JsonElement)received.Params!;
                        TestAssert.Equal(9, parameters.GetProperty("value").GetInt32());
                    }),

                    Case(suiteId, "KickClient", "JsonRpcServer KickClient removes a connected client", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);
                        await WaitUntilAsync(() => fixture.Server.GetConnectedClients().Count == 1, ct).ConfigureAwait(false);

                        string clientId = fixture.Server.GetConnectedClients()[0];
                        bool kicked = fixture.Server.KickClient(clientId);
                        await WaitUntilAsync(() => fixture.Server.GetConnectedClients().Count == 0, ct).ConfigureAwait(false);

                        TestAssert.True(kicked, "Known client should be kicked.");
                        TestAssert.False(fixture.Server.KickClient(clientId), "Kicking an already removed client should return false.");
                    }),

                    Case(suiteId, "ConcurrentCalls", "JsonRpcClient and JsonRpcServer handle multiple concurrent calls", async ct =>
                    {
                        await using TcpJsonRpcFixture fixture = await TcpJsonRpcFixture.StartAsync(ct, server =>
                        {
                            server.RegisterMethod("square", args =>
                            {
                                int value = args!.Value.GetProperty("value").GetInt32();
                                return value * value;
                            });
                        }, includeDefaultMethods: false).ConfigureAwait(false);
                        using JsonRpcClient client = await fixture.ConnectClientAsync(ct).ConfigureAwait(false);

                        int[] results = await Task.WhenAll(Enumerable.Range(1, 10)
                            .Select(i => client.CallAsync<int>("square", new { value = i }, token: ct))).ConfigureAwait(false);

                        TestAssert.Equal(1, results[0]);
                        TestAssert.Equal(100, results[9]);
                    }),
                });
        }

        private static async Task<Exception> CaptureExceptionAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ex;
            }

            throw new InvalidOperationException("Expected an exception.");
        }

        private static async Task<T> WaitForTaskAsync<T>(Task<T> task, CancellationToken token)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(5000, token)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException("Timed out waiting for integration event.");
            }

            return await task.ConfigureAwait(false);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for integration condition.");
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "json-rpc", "tcp", "integration", "matrix" });
        }
    }
}
