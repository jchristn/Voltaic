namespace Test.Shared
{
    using System.Diagnostics;
    using System.Net;
    using System.Net.Http;
    using System.Net.NetworkInformation;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic.A2A;
    using Voltaic.Core;

    public static class A2AProtocolSuites
    {
        public static TestSuiteDescriptor ProtocolAndTransports()
        {
            const string suiteId = "A2A.Protocol";

            return new TestSuiteDescriptor(
                suiteId,
                "A2A Protocol and Transport Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "AgentCardSerializesV1Shape", "AgentCard serializes with v1 interface and capability shape", ct =>
                    {
                        AgentCard card = CreateCard("http://localhost/a2a");
                        string json = JsonSerializer.Serialize(card, A2AJson.DefaultOptions);
                        JsonProbe root = JsonProbe.Parse(json);

                        TestAssert.Equal("Voltaic Test Agent", root.Get("name").String());
                        TestAssert.Equal("1.0", root.Get("supportedInterfaces")[0].Get("protocolVersion").String());
                        TestAssert.Equal("JSONRPC", root.Get("supportedInterfaces")[0].Get("protocolBinding").String());
                        TestAssert.True(root.Get("capabilities").Get("streaming").Bool(), "Streaming should serialize.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "TaskStateAndRoleUseA2AWireNames", "A2A enums use official wire names", ct =>
                    {
                        Message message = new Message
                        {
                            Role = Role.Agent,
                            MessageId = "m1",
                            Parts = new List<Part> { Part.FromText("done") }
                        };
                        TaskStatus status = new TaskStatus { State = TaskState.Completed, Message = message };
                        string json = JsonSerializer.Serialize(status, A2AJson.DefaultOptions);
                        JsonProbe document = JsonProbe.Parse(json);
                        TestAssert.Equal("TASK_STATE_COMPLETED", document.Get("state").String());
                        TestAssert.Equal("ROLE_AGENT", document.Get("message").Get("role").String());
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "AgentCardResolverFetchesWellKnownCard", "A2ACardResolver fetches public Agent Card", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        A2ACardResolver resolver = new A2ACardResolver(fixture.Client);
                        AgentCard card = await resolver.GetAgentCardAsync(fixture.BaseUrl, ct).ConfigureAwait(false);

                        TestAssert.Equal("Voltaic Test Agent", card.Name);
                        TestAssert.Equal(fixture.EndpointUrl, card.SupportedInterfaces[0].Url);
                    }),

                    Case(suiteId, "JsonRpcSendMessageAndGetTask", "A2A JSON-RPC client sends a message and reads the projected task", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);

                        SendMessageResponse response = await client.SendMessageAsync(CreateMessageRequest("hello"), ct).ConfigureAwait(false);
                        TestAssert.NotNull(response.Task, "SendMessage should return a task.");
                        TestAssert.Equal(TaskState.Completed, response.Task!.Status.State);

                        AgentTask task = await client.GetTaskAsync(new GetTaskRequest { Id = response.Task.Id }, ct).ConfigureAwait(false);
                        TestAssert.Equal(TaskState.Completed, task.Status.State);
                        TestAssert.True(task.History?.Count >= 2, "Task history should include user and agent messages.");
                    }),

                    Case(suiteId, "JsonRpcStreamingMessageUsesSse", "A2A streaming JSON-RPC returns SSE task updates", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);

                        List<StreamResponse> events = new List<StreamResponse>();
                        await foreach (StreamResponse item in client.SendStreamingMessageAsync(CreateMessageRequest("stream"), ct))
                        {
                            events.Add(item);
                        }

                        TestAssert.True(events.Any(item => item.StatusUpdate?.Status.State == TaskState.Working), "Stream should include working status.");
                        TestAssert.True(events.Any(item => item.StatusUpdate?.Status.State == TaskState.Completed), "Stream should include completed status.");
                    }),

                    Case(suiteId, "RestSendMessageAndGetTask", "A2A HTTP+JSON REST routes use the same task projection", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        string requestJson = JsonSerializer.Serialize(CreateMessageRequest("rest"), A2AJson.DefaultOptions);
                        using HttpResponseMessage sendResponse = await fixture.Client.PostAsync(
                            $"{fixture.BaseUrl}/message:send",
                            new StringContent(requestJson, Encoding.UTF8, "application/json"),
                            ct).ConfigureAwait(false);

                        string sendBody = await sendResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, sendResponse.StatusCode);
                        SendMessageResponse? response = JsonSerializer.Deserialize<SendMessageResponse>(sendBody, A2AJson.DefaultOptions);
                        TestAssert.NotNull(response?.Task, "REST send should return a task.");

                        using HttpResponseMessage getResponse = await fixture.Client.GetAsync($"{fixture.BaseUrl}/tasks/{response!.Task!.Id}", ct).ConfigureAwait(false);
                        string getBody = await getResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        AgentTask? task = JsonSerializer.Deserialize<AgentTask>(getBody, A2AJson.DefaultOptions);
                        TestAssert.Equal(TaskState.Completed, task!.Status.State);
                    }),

                    Case(suiteId, "PushNotificationConfigCrud", "A2A push notification configuration APIs create, get, list, and delete configs", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);
                        string taskPush = (await client.SendMessageAsync(CreateMessageRequest("push-crud"), ct).ConfigureAwait(false)).Task!.Id;

                        TaskPushNotificationConfig created = await client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest
                        {
                            TaskId = taskPush,
                            ConfigId = "webhook",
                            Config = new PushNotificationConfig
                            {
                                Url = "https://example.com/webhook",
                                Token = "token"
                            }
                        }, ct).ConfigureAwait(false);

                        TestAssert.Equal("webhook", created.Id);
                        TaskPushNotificationConfig fetched = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest
                        {
                            TaskId = taskPush,
                            ConfigId = "webhook"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("https://example.com/webhook", fetched.PushNotificationConfig.Url);

                        ListTaskPushNotificationConfigResponse list = await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
                        {
                            TaskId = taskPush
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, list.Configs.Count);

                        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
                        {
                            TaskId = taskPush,
                            ConfigId = "webhook"
                        }, ct).ConfigureAwait(false);
                        ListTaskPushNotificationConfigResponse afterDelete = await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
                        {
                            TaskId = taskPush
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal(0, afterDelete.Configs.Count);
                    }),

                    Case(suiteId, "ExtendedAgentCardJsonRpcAndRest", "A2A extended Agent Card is available through JSON-RPC and REST", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);

                        AgentCard rpcCard = await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), ct).ConfigureAwait(false);
                        TestAssert.Equal("Voltaic Extended Agent", rpcCard.Name);

                        using HttpResponseMessage response = await fixture.Client.GetAsync($"{fixture.BaseUrl}/extendedAgentCard", ct).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        AgentCard? restCard = JsonSerializer.Deserialize<AgentCard>(body, A2AJson.DefaultOptions);
                        TestAssert.Equal("Voltaic Extended Agent", restCard!.Name);
                    }),

                    Case(suiteId, "HttpJsonClientCoversRestBinding", "A2A HTTP+JSON client covers REST send, stream, task, push config, and extended card routes", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AHttpJsonClient client = new A2AHttpJsonClient(fixture.BaseUrl, fixture.Client);

                        SendMessageResponse response = await client.SendMessageAsync(CreateMessageRequest("http-json"), ct).ConfigureAwait(false);
                        TestAssert.NotNull(response.Task, "HTTP+JSON send should return a task.");
                        TestAssert.Equal(TaskState.Completed, response.Task!.Status.State);

                        AgentTask task = await client.GetTaskAsync(new GetTaskRequest { Id = response.Task.Id }, ct).ConfigureAwait(false);
                        TestAssert.Equal(TaskState.Completed, task.Status.State);

                        List<StreamResponse> stream = new List<StreamResponse>();
                        await foreach (StreamResponse item in client.SendStreamingMessageAsync(CreateMessageRequest("http-json-stream"), ct))
                        {
                            stream.Add(item);
                        }

                        TestAssert.True(stream.Any(item => item.StatusUpdate?.Status.State == TaskState.Completed), "HTTP+JSON stream should include completed status.");

                        TaskPushNotificationConfig created = await client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest
                        {
                            TaskId = response.Task.Id,
                            Config = new PushNotificationConfig
                            {
                                Id = "rest-client",
                                Url = "https://example.com/a2a"
                            }
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("rest-client", created.Id);

                        TaskPushNotificationConfig fetched = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest
                        {
                            TaskId = response.Task.Id,
                            ConfigId = "rest-client"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("https://example.com/a2a", fetched.PushNotificationConfig.Url);

                        ListTaskPushNotificationConfigResponse configs = await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
                        {
                            TaskId = response.Task.Id
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, configs.Configs.Count);

                        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
                        {
                            TaskId = response.Task.Id,
                            ConfigId = "rest-client"
                        }, ct).ConfigureAwait(false);

                        AgentCard extended = await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), ct).ConfigureAwait(false);
                        TestAssert.Equal("Voltaic Extended Agent", extended.Name);
                    }),

                    Case(suiteId, "GrpcClientServerCoversA2AService", "A2A gRPC client and Watson server cover message, task, push config, and extended card RPCs", async ct =>
                    {
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AGrpcClient client = new A2AGrpcClient(fixture.BaseUrl, fixture.Client);

                        SendMessageResponse response = await client.SendMessageAsync(CreateMessageRequest("grpc"), ct).ConfigureAwait(false);
                        TestAssert.NotNull(response.Task, "gRPC SendMessage should return a task.");
                        TestAssert.Equal(TaskState.Completed, response.Task!.Status.State);

                        AgentTask task = await client.GetTaskAsync(new GetTaskRequest { Id = response.Task.Id }, ct).ConfigureAwait(false);
                        TestAssert.Equal(TaskState.Completed, task.Status.State);
                        TestAssert.True(task.Artifacts?.Any(artifact => artifact.ArtifactId == "echo-result") == true, "gRPC task projection should include artifacts.");

                        ListTasksResponse list = await client.ListTasksAsync(new ListTasksRequest { Status = TaskState.Completed }, ct).ConfigureAwait(false);
                        TestAssert.True(list.Tasks.Any(item => item.Id == response.Task.Id), "gRPC ListTasks should include the completed task.");

                        List<StreamResponse> stream = new List<StreamResponse>();
                        await foreach (StreamResponse item in client.SendStreamingMessageAsync(CreateMessageRequest("grpc-stream"), ct))
                        {
                            stream.Add(item);
                        }

                        TestAssert.True(stream.Any(item => item.StatusUpdate?.Status.State == TaskState.Working), "gRPC stream should include working status.");
                        TestAssert.True(stream.Any(item => item.StatusUpdate?.Status.State == TaskState.Completed), "gRPC stream should include completed status.");

                        TaskPushNotificationConfig created = await client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest
                        {
                            TaskId = response.Task.Id,
                            ConfigId = "grpc-webhook",
                            Config = new PushNotificationConfig
                            {
                                Url = "https://example.com/grpc",
                                Token = "grpc-token"
                            }
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("grpc-webhook", created.Id);

                        TaskPushNotificationConfig fetched = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest
                        {
                            TaskId = response.Task.Id,
                            ConfigId = "grpc-webhook"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("https://example.com/grpc", fetched.PushNotificationConfig.Url);

                        ListTaskPushNotificationConfigResponse configs = await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
                        {
                            TaskId = response.Task.Id
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, configs.Configs.Count);

                        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
                        {
                            TaskId = response.Task.Id,
                            ConfigId = "grpc-webhook"
                        }, ct).ConfigureAwait(false);

                        AgentCard extended = await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), ct).ConfigureAwait(false);
                        TestAssert.Equal("Voltaic Extended Agent", extended.Name);
                        TestAssert.True(extended.SupportedInterfaces.Any(item => item.ProtocolBinding == "GRPC"), "Extended Agent Card should advertise gRPC.");
                    }),

                    Case(suiteId, "GrpcErrorMapsToA2AProtocolException", "A2A gRPC status trailers map protocol errors back to A2AProtocolException", async ct =>
                    {
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AGrpcClient client = new A2AGrpcClient(fixture.BaseUrl, fixture.Client);

                        A2AProtocolException? ex = null;
                        try
                        {
                            await client.GetTaskAsync(new GetTaskRequest { Id = "missing-grpc-task" }, ct).ConfigureAwait(false);
                        }
                        catch (A2AProtocolException caught)
                        {
                            ex = caught;
                        }

                        TestAssert.NotNull(ex, "Missing gRPC task should map to an A2A protocol exception.");
                        TestAssert.Equal(A2AErrorCode.TaskNotFound, ex!.ErrorCode);
                    }),

                    Case(suiteId, "GrpcPreservesRichMessagePartsAndMetadata", "A2A gRPC preserves text, raw, URL, data parts, and metadata through task history", async ct =>
                    {
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AGrpcClient client = new A2AGrpcClient(fixture.BaseUrl, fixture.Client);

                        SendMessageRequest request = CreateMessageRequest("rich");
                        request.Message.Parts.Add(Part.FromRaw(new byte[] { 1, 2, 3, 4 }, "application/octet-stream", "payload.bin"));
                        request.Message.Parts.Add(Part.FromUrl("https://example.com/payload.txt", "text/plain", "payload.txt"));
                        request.Message.Parts.Add(Part.FromData(ParseJsonValue("""{"kind":"example","count":2}""")));
                        request.Message.Metadata = new Dictionary<string, object?>
                        {
                            { "source", ParseJsonValue("\"qa\"") },
                            { "attempt", ParseJsonValue("7") }
                        };
                        request.Metadata = new Dictionary<string, object?>
                        {
                            { "requestId", ParseJsonValue("\"req-123\"") }
                        };

                        SendMessageResponse response = await client.SendMessageAsync(request, ct).ConfigureAwait(false);
                        TestAssert.NotNull(response.Task, "gRPC SendMessage should return a task.");

                        AgentTask task = await client.GetTaskAsync(new GetTaskRequest { Id = response.Task!.Id }, ct).ConfigureAwait(false);
                        Message userMessage = task.History!.Single(message => message.Role == Role.User);
                        TestAssert.Equal(4, userMessage.Parts.Count);
                        TestAssert.Equal("rich", userMessage.Parts[0].Text);
                        TestAssert.Equal("application/octet-stream", userMessage.Parts[1].MediaType);
                        TestAssert.Equal("payload.bin", userMessage.Parts[1].Filename);
                        TestAssert.Equal(4, userMessage.Parts[1].Raw!.Length);
                        TestAssert.Equal("https://example.com/payload.txt", userMessage.Parts[2].Url);
                        TestAssert.Equal("example", new RpcParameters(JsonSerializer.Serialize(userMessage.Parts[3].Data, A2AJson.DefaultOptions)).GetString("kind"));
                        TestAssert.Equal("qa", MetaValue<string>(userMessage.Metadata!["source"]));
                        TestAssert.Equal(7, MetaValue<int>(userMessage.Metadata["attempt"]));
                    }),

                    Case(suiteId, "GrpcSubscribeAndCancelTask", "A2A gRPC SubscribeToTask receives live cancellation updates", async ct =>
                    {
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct, new WorkingOnlyAgent()).ConfigureAwait(false);
                        using A2AGrpcClient client = new A2AGrpcClient(fixture.BaseUrl, fixture.Client);

                        SendMessageResponse response = await client.SendMessageAsync(CreateMessageRequest("subscribe"), ct).ConfigureAwait(false);
                        TestAssert.NotNull(response.Task, "Working agent should return a task.");
                        TestAssert.Equal(TaskState.Working, response.Task!.Status.State);

                        using CancellationTokenSource subscriptionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        subscriptionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        IAsyncEnumerator<StreamResponse> enumerator = client.SubscribeToTaskAsync(
                            new SubscribeToTaskRequest { Id = response.Task.Id },
                            subscriptionTimeout.Token).GetAsyncEnumerator(subscriptionTimeout.Token);

                        try
                        {
                            TestAssert.True(await enumerator.MoveNextAsync().ConfigureAwait(false), "Subscription should yield the current task.");
                            TestAssert.Equal(response.Task.Id, enumerator.Current.Task!.Id);

                            AgentTask canceled = await client.CancelTaskAsync(new CancelTaskRequest { Id = response.Task.Id }, ct).ConfigureAwait(false);
                            TestAssert.Equal(TaskState.Canceled, canceled.Status.State);

                            TestAssert.True(await enumerator.MoveNextAsync().ConfigureAwait(false), "Subscription should yield cancellation update.");
                            TestAssert.Equal(TaskState.Canceled, enumerator.Current.StatusUpdate!.Status.State);
                        }
                        finally
                        {
                            await enumerator.DisposeAsync().ConfigureAwait(false);
                        }
                    }),

                    Case(suiteId, "GrpcAuthenticationBlocksRpcButAllowsAgentCard", "A2A gRPC authentication blocks RPCs while Agent Card discovery stays public", async ct =>
                    {
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct, blockRpcWithAuthentication: true).ConfigureAwait(false);
                        using HttpResponseMessage cardResponse = await fixture.Client.GetAsync($"{fixture.BaseUrl}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, cardResponse.StatusCode);

                        using A2AGrpcClient client = new A2AGrpcClient(fixture.BaseUrl, fixture.Client);
                        HttpRequestException? ex = null;
                        try
                        {
                            await client.SendMessageAsync(CreateMessageRequest("blocked"), ct).ConfigureAwait(false);
                        }
                        catch (HttpRequestException caught)
                        {
                            ex = caught;
                        }

                        TestAssert.NotNull(ex, "Blocked gRPC RPC should surface an HTTP request failure.");
                        TestAssert.Equal(HttpStatusCode.Forbidden, ex!.StatusCode);
                    }),

                    Case(suiteId, "AuthenticatedCallerReachesAgentContext", "The authenticated caller (Principal + Claims) reaches the A2A agent handler context over HTTP", async ct =>
                    {
                        CapturingAuthAgent agent = new CapturingAuthAgent();
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct, agent, server =>
                            server.AuthenticationHandler = _ => Task.FromResult(new AuthenticationResult
                            {
                                IsAuthenticated = true,
                                Principal = "a2a-user",
                                Claims = new Dictionary<string, string> { { "tenantId", "tenant-7" } }
                            })).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);

                        SendMessageResponse response = await client.SendMessageAsync(CreateMessageRequest("hi"), ct).ConfigureAwait(false);

                        TestAssert.NotNull(response.Task, "SendMessage should return a task.");
                        TestAssert.True(agent.Invoked, "Agent handler should have run.");
                        TestAssert.False(agent.PrincipalWasNull, "Principal should be non-null for an authenticated request.");
                        TestAssert.Equal("a2a-user", agent.Principal, "Principal should match the AuthenticationResult.");
                        TestAssert.Equal("tenant-7", agent.TenantId, "tenantId claim should reach the agent context.");
                    }),

                    Case(suiteId, "UnauthenticatedRequestHasNullCallerInContext", "Without an AuthenticationHandler the A2A agent context carries no caller identity", async ct =>
                    {
                        CapturingAuthAgent agent = new CapturingAuthAgent();
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct, agent).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);

                        await client.SendMessageAsync(CreateMessageRequest("hi"), ct).ConfigureAwait(false);

                        TestAssert.True(agent.Invoked, "Agent handler should have run.");
                        TestAssert.True(agent.PrincipalWasNull, "Principal should be null when no AuthenticationHandler is configured.");
                        TestAssert.Null(agent.TenantId, "Claims should be absent when no AuthenticationHandler is configured.");
                    }),

                    Case(suiteId, "GrpcAuthenticatedCallerReachesAgentContext", "The authenticated caller reaches the agent context over the A2A gRPC transport", async ct =>
                    {
                        CapturingAuthAgent agent = new CapturingAuthAgent();
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct, agent, configure: server =>
                            server.AuthenticationHandler = _ => Task.FromResult(new AuthenticationResult
                            {
                                IsAuthenticated = true,
                                Principal = "grpc-user",
                                Claims = new Dictionary<string, string> { { "tenantId", "tenant-grpc" } }
                            })).ConfigureAwait(false);
                        using A2AGrpcClient client = new A2AGrpcClient(fixture.BaseUrl, fixture.Client);

                        SendMessageResponse response = await client.SendMessageAsync(CreateMessageRequest("hi"), ct).ConfigureAwait(false);

                        TestAssert.NotNull(response.Task, "gRPC SendMessage should return a task.");
                        TestAssert.True(agent.Invoked, "Agent handler should have run.");
                        TestAssert.Equal("grpc-user", agent.Principal, "Principal should reach the agent over gRPC.");
                        TestAssert.Equal("tenant-grpc", agent.TenantId, "tenantId claim should reach the agent over gRPC.");
                    }),

                    Case(suiteId, "ReturnImmediatelyPersistsSubmittedTask", "A2A returnImmediately returns a submitted task and lets the handler finish in the background", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct, new SlowAgent()).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);

                        SendMessageRequest request = CreateMessageRequest("return-immediately");
                        request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
                        SendMessageResponse response = await client.SendMessageAsync(request, ct).ConfigureAwait(false);

                        TestAssert.NotNull(response.Task, "returnImmediately should return a task.");
                        TestAssert.Equal(TaskState.Submitted, response.Task!.Status.State);

                        AgentTask completed = await WaitForTaskStateAsync(client, response.Task.Id, TaskState.Completed, ct).ConfigureAwait(false);
                        TestAssert.True(completed.History?.Any(message => message.Role == Role.Agent) == true, "Background handler should append the agent response.");
                    }),

                    Case(suiteId, "OfficialJsonRpcRequestShapeAccepted", "Voltaic server accepts official SDK style A2A JSON-RPC request payloads", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        string body = """{"jsonrpc":"2.0","method":"SendMessage","id":1,"params":{"message":{"messageId":"m-official","role":"ROLE_USER","parts":[{"text":"official"}]}}}""";
                        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, fixture.EndpointUrl);
                        request.Headers.TryAddWithoutValidation(A2AProtocol.VersionHeader, A2AProtocol.ProtocolVersion);
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                        using HttpResponseMessage response = await fixture.Client.SendAsync(request, ct).ConfigureAwait(false);
                        string responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);

                        JsonRpcResponse? rpc = JsonSerializer.Deserialize<JsonRpcResponse>(responseBody, A2AJson.DefaultOptions);
                        TestAssert.NotNull(rpc?.Result, "JSON-RPC response should contain result.");
                        string resultJson = JsonSerializer.Serialize(rpc!.Result, A2AJson.DefaultOptions);
                        SendMessageResponse? sendResponse = JsonSerializer.Deserialize<SendMessageResponse>(resultJson, A2AJson.DefaultOptions);
                        TestAssert.Equal(TaskState.Completed, sendResponse!.Task!.Status.State);
                    }),

                    Case(suiteId, "OfficialRestPushConfigBodyAccepted", "Voltaic server accepts official SDK HTTP+JSON push config request bodies", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AClient rpcClient = new A2AClient(fixture.EndpointUrl, fixture.Client);
                        string taskId = (await rpcClient.SendMessageAsync(CreateMessageRequest("official-rest"), ct).ConfigureAwait(false)).Task!.Id;
                        PushNotificationConfig config = new PushNotificationConfig
                        {
                            Id = "official-rest",
                            Url = "https://example.com/official"
                        };
                        string json = JsonSerializer.Serialize(config, A2AJson.DefaultOptions);

                        using HttpResponseMessage response = await fixture.Client.PostAsync(
                            $"{fixture.BaseUrl}/tasks/{taskId}/pushNotificationConfigs",
                            new StringContent(json, Encoding.UTF8, "application/json"),
                            ct).ConfigureAwait(false);

                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TaskPushNotificationConfig? created = JsonSerializer.Deserialize<TaskPushNotificationConfig>(body, A2AJson.DefaultOptions);
                        TestAssert.Equal("official-rest", created!.Id);
                        TestAssert.Equal("https://example.com/official", created.PushNotificationConfig.Url);
                    }),
                });
        }

        /// <summary>
        /// Push notification delivery, push configuration errors, gRPC server hardening, restartable servers, and
        /// task store null handling (v2.1.2).
        /// </summary>
        public static TestSuiteDescriptor Hardening()
        {
            const string suiteId = "A2A.Hardening";

            return new TestSuiteDescriptor(
                suiteId,
                "A2A Push Delivery and Server Hardening",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "PushDeliveryCarriesStreamResponsesAndCredentials", "A webhook registered through SendMessage receives every task event as a StreamResponse with Authorization, the notification token, and application/a2a+json", async ct =>
                    {
                        using WebhookReceiver receiver = new WebhookReceiver();
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct, null, server => server.PushNotificationUrlValidator = uri => uri.IsLoopback).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);

                        SendMessageRequest request = CreateMessageRequest("push me");
                        request.Configuration = new SendMessageConfiguration
                        {
                            PushNotificationConfig = new PushNotificationConfig
                            {
                                Url = receiver.Url,
                                Token = "notify-token",
                                Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "webhook-secret" }
                            }
                        };
                        SendMessageResponse response = await client.SendMessageAsync(request, ct).ConfigureAwait(false);

                        TestAssert.True(await receiver.WaitForAsync(3, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false), $"The webhook receives the task's events (got {receiver.Requests.Count}).");
                        await Task.Delay(300, ct).ConfigureAwait(false);
                        ReceivedWebhook[] received = receiver.Requests.ToArray();
                        foreach (ReceivedWebhook item in received)
                        {
                            TestAssert.Equal("POST", item.Method);
                            TestAssert.Equal("/hook", item.Path);
                            TestAssert.Equal("Bearer webhook-secret", item.Headers["Authorization"], "Authorization is {scheme} {credentials}.");
                            TestAssert.Equal("notify-token", item.Headers[A2AProtocol.NotificationTokenHeader], "The token header is sent.");
                            TestAssert.True(item.Headers["Content-Type"].StartsWith("application/a2a+json", StringComparison.Ordinal), "The payload is application/a2a+json.");
                        }

                        List<StreamResponse> events = received.Select(item => JsonSerializer.Deserialize<StreamResponse>(item.Body, A2AJson.DefaultOptions)!).ToList();
                        TestAssert.True(events.All(item => (item.StatusUpdate?.TaskId ?? item.ArtifactUpdate?.TaskId ?? item.Task?.Id) == response.Task!.Id), "Every payload is for the task.");
                        TestAssert.True(events.Any(item => item.StatusUpdate?.Status.State == TaskState.Completed), "The completion is delivered.");
                        TestAssert.True(events.Any(item => item.ArtifactUpdate != null), "Artifact updates are delivered.");
                        int completedIndex = events.FindIndex(item => item.StatusUpdate?.Status.State == TaskState.Completed);
                        int workingIndex = events.FindIndex(item => item.StatusUpdate?.Status.State == TaskState.Working);
                        TestAssert.True(workingIndex >= 0 && workingIndex < completedIndex, "Events are delivered in order.");
                    }),

                    Case(suiteId, "PushConfigUsesSpecFieldName", "SendMessageConfiguration writes taskPushNotificationConfig, reads both names, and a spec-named config sent over JSON-RPC is delivered to", async ct =>
                    {
                        using WebhookReceiver receiver = new WebhookReceiver();
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct, null, server => server.PushNotificationUrlValidator = uri => uri.IsLoopback).ConfigureAwait(false);

                        string written = JsonSerializer.Serialize(new SendMessageConfiguration { PushNotificationConfig = new PushNotificationConfig { Url = "https://example.com/hook" } }, A2AJson.DefaultOptions);
                        SendMessageConfiguration? legacy = JsonSerializer.Deserialize<SendMessageConfiguration>("{\"pushNotificationConfig\":{\"url\":\"https://example.com/old\"}}", A2AJson.DefaultOptions);
                        TestAssert.True(written.Contains("\"taskPushNotificationConfig\""), $"The spec name is written: {written}");
                        TestAssert.False(written.Contains("\"pushNotificationConfig\""), "The older name is not written.");
                        TestAssert.Equal("https://example.com/old", legacy!.PushNotificationConfig!.Url, "The older name is still read.");

                        string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + A2AProtocol.SendMessage + "\",\"params\":{\"message\":{\"messageId\":\"m-spec\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"spec\"}]},"
                            + "\"configuration\":{\"taskPushNotificationConfig\":{\"url\":\"" + receiver.Url + "\"}}}}";
                        using HttpResponseMessage response = await fixture.Client.PostAsync(fixture.EndpointUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestAssert.True(await receiver.WaitForAsync(1, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false), "A spec-named config is registered and delivered to.");
                    }),

                    Case(suiteId, "PushDeliveryRetriesAndStopsAfterDelete", "A failed delivery is retried until it succeeds, and a deleted configuration receives nothing further", async ct =>
                    {
                        using WebhookReceiver receiver = new WebhookReceiver();
                        receiver.EnqueueStatus(500);
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct, new StepAgent(), server =>
                        {
                            server.PushNotificationUrlValidator = uri => uri.IsLoopback;
                            server.PushNotificationMaxAttempts = 3;
                        }).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);

                        string taskId = (await client.SendMessageAsync(CreateMessageRequest("first"), ct).ConfigureAwait(false)).Task!.Id;
                        await client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest { TaskId = taskId, ConfigId = "retry", Config = new PushNotificationConfig { Url = receiver.Url } }, ct).ConfigureAwait(false);

                        await client.SendMessageAsync(ContinueTask(taskId, "second"), ct).ConfigureAwait(false);
                        TestAssert.True(await receiver.WaitForAsync(2, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false), "The failed delivery is retried.");
                        ReceivedWebhook[] attempts = receiver.Requests.ToArray();
                        TestAssert.Equal(500, attempts[0].AnsweredStatus, "The first attempt failed.");
                        TestAssert.Equal(attempts[0].Body, attempts[1].Body, "The retry carries the same event.");

                        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = taskId, ConfigId = "retry" }, ct).ConfigureAwait(false);
                        int before = receiver.Requests.Count;
                        await client.SendMessageAsync(ContinueTask(taskId, "third"), ct).ConfigureAwait(false);
                        await Task.Delay(1500, ct).ConfigureAwait(false);
                        TestAssert.Equal(before, receiver.Requests.Count, "Nothing is delivered after the configuration is deleted.");
                    }),

                    Case(suiteId, "DefaultWebhookPolicyRejectsLocalTargets", "Without a validator, loopback, private, link-local, non-http, and credential-bearing webhook URLs are rejected with InvalidParams", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);
                        string taskId = (await client.SendMessageAsync(CreateMessageRequest("policy"), ct).ConfigureAwait(false)).Task!.Id;

                        foreach (string url in new[] { "http://127.0.0.1:9/hook", "http://localhost/hook", "http://api.localhost/hook", "http://10.1.2.3/hook", "http://172.16.0.1/hook", "http://192.168.1.1/hook", "http://169.254.169.254/latest", "http://[::1]/hook", "http://[fd00::1]/hook", "http://0.0.0.0/hook", "ftp://example.com/hook", "https://user:pw@example.com/hook", "not a url" })
                        {
                            A2AProtocolException? error = await CaptureAsync(() => client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest { TaskId = taskId, Config = new PushNotificationConfig { Url = url } }, ct)).ConfigureAwait(false);
                            TestAssert.Equal(A2AErrorCode.InvalidParams, error?.ErrorCode, $"'{url}' is rejected.");
                        }

                        TaskPushNotificationConfig allowed = await client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest { TaskId = taskId, Config = new PushNotificationConfig { Url = "https://example.com/hook" } }, ct).ConfigureAwait(false);
                        TestAssert.Equal("https://example.com/hook", allowed.PushNotificationConfig.Url, "A public URL is accepted.");

                        SendMessageRequest local = CreateMessageRequest("local");
                        local.Configuration = new SendMessageConfiguration { PushNotificationConfig = new PushNotificationConfig { Url = "http://127.0.0.1/hook" } };
                        A2AProtocolException? sendError = await CaptureAsync(() => client.SendMessageAsync(local, ct)).ConfigureAwait(false);
                        TestAssert.Equal(A2AErrorCode.InvalidParams, sendError?.ErrorCode, "SendMessage rejects a local webhook too.");
                    }),

                    Case(suiteId, "PushConfigErrorsFollowSpec", "Push config operations on a missing task return TaskNotFound, a missing config names the config, and delete is idempotent", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);
                        string taskId = (await client.SendMessageAsync(CreateMessageRequest("errors"), ct).ConfigureAwait(false)).Task!.Id;

                        A2AProtocolException? create = await CaptureAsync(() => client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest { TaskId = "no-such-task", Config = new PushNotificationConfig { Url = "https://example.com/hook" } }, ct)).ConfigureAwait(false);
                        A2AProtocolException? list = await CaptureAsync(() => client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest { TaskId = "no-such-task" }, ct)).ConfigureAwait(false);
                        A2AProtocolException? delete = await CaptureAsync(async () => { await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "no-such-task", ConfigId = "x" }, ct).ConfigureAwait(false); return true; }).ConfigureAwait(false);
                        A2AProtocolException? get = await CaptureAsync(() => client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = taskId, ConfigId = "missing-config" }, ct)).ConfigureAwait(false);

                        TestAssert.Equal(A2AErrorCode.TaskNotFound, create?.ErrorCode, "Create on a missing task is TaskNotFound.");
                        TestAssert.Equal(A2AErrorCode.TaskNotFound, list?.ErrorCode, "List on a missing task is TaskNotFound.");
                        TestAssert.Equal(A2AErrorCode.TaskNotFound, delete?.ErrorCode, "Delete on a missing task is TaskNotFound.");
                        TestAssert.Equal(A2AErrorCode.TaskNotFound, get?.ErrorCode, "A missing config is the spec's not-found error.");
                        TestAssert.True(get!.Message.Contains("missing-config"), $"The error names the missing configuration: {get.Message}");

                        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = taskId, ConfigId = "never-created" }, ct).ConfigureAwait(false);
                    }),

                    Case(suiteId, "PushConfigRequiresCapability", "A push config in SendMessage is rejected with PushNotificationNotSupported when the Agent Card does not advertise push notifications", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct, null, server => server.AgentCard.Capabilities.PushNotifications = false).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);
                        SendMessageRequest request = CreateMessageRequest("no push");
                        request.Configuration = new SendMessageConfiguration { PushNotificationConfig = new PushNotificationConfig { Url = "https://example.com/hook" } };

                        A2AProtocolException? error = await CaptureAsync(() => client.SendMessageAsync(request, ct)).ConfigureAwait(false);
                        TestAssert.Equal(A2AErrorCode.PushNotificationNotSupported, error?.ErrorCode);
                    }),

                    Case(suiteId, "PushSettingsValidateRanges", "PushNotificationTimeoutMs and PushNotificationMaxAttempts reject out-of-range values", ct =>
                    {
                        using A2AHttpServer server = new A2AHttpServer("localhost", 1, CreateCard("http://localhost/a2a"));
                        TestAssert.Equal(10000, server.PushNotificationTimeoutMs, "The default timeout is 10 seconds.");
                        TestAssert.Equal(3, server.PushNotificationMaxAttempts, "The default is three attempts.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.PushNotificationTimeoutMs = 999, "Below the minimum timeout.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.PushNotificationTimeoutMs = 300001, "Above the maximum timeout.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.PushNotificationMaxAttempts = 0, "Below the minimum attempts.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => server.PushNotificationMaxAttempts = 11, "Above the maximum attempts.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "HttpServerRestartsAfterStop", "A2AHttpServer can be started again after Stop, and a disposed server cannot be started", async ct =>
                    {
                        int port = TestPorts.GetFreePort();
                        A2AHttpServer server = new A2AHttpServer("localhost", port, CreateCard($"http://localhost:{port}/a2a"), new EchoAgent());
                        using HttpClient http = new HttpClient();

                        await server.StartAsync(ct).ConfigureAwait(false);
                        using (HttpResponseMessage first = await http.GetAsync($"http://localhost:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false))
                            TestAssert.Equal(HttpStatusCode.OK, first.StatusCode, "The first start serves requests.");
                        server.Stop();
                        await server.StartAsync(ct).ConfigureAwait(false);
                        using (HttpResponseMessage second = await http.GetAsync($"http://localhost:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false))
                            TestAssert.Equal(HttpStatusCode.OK, second.StatusCode, "The restarted server serves requests.");
                        await TestAssert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(ct), "Starting a running server fails.").ConfigureAwait(false);
                        server.Dispose();
                        await TestAssert.ThrowsAsync<ObjectDisposedException>(() => server.StartAsync(ct), "A disposed server cannot start.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "GrpcServerRestartsAfterStop", "A2AGrpcServer can be started again after Stop", async ct =>
                    {
                        int port = TestPorts.GetFreePort();
                        A2AGrpcServer server = new A2AGrpcServer("localhost", port, CreateCard($"http://localhost:{port}"), new EchoAgent());
                        using HttpClient http = new HttpClient();

                        await server.StartAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, await GetStatusWithRetryAsync(http, $"http://localhost:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false), "The first start serves requests.");
                        server.Stop();
                        await server.StartAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, await GetStatusWithRetryAsync(http, $"http://localhost:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false), "The restarted server serves requests.");
                        server.Dispose();
                        await TestAssert.ThrowsAsync<ObjectDisposedException>(() => server.StartAsync(ct), "A disposed server cannot start.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "GrpcLocalhostListensOnBothLoopbackAddresses", "A2AGrpcServer bound to localhost listens on 127.0.0.1 and ::1, so clients that try ::1 first connect without a fallback delay; an explicit 127.0.0.1 binds IPv4 only", async ct =>
                    {
                        using HttpClient http = new HttpClient();
                        int port = TestPorts.GetFreePort();
                        using A2AGrpcServer server = new A2AGrpcServer("localhost", port, CreateCard($"http://localhost:{port}"), new EchoAgent());
                        await server.StartAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(HttpStatusCode.OK, await GetStatusWithRetryAsync(http, $"http://127.0.0.1:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false), "IPv4 loopback is served.");

                        if (Socket.OSSupportsIPv6)
                        {
                            TestAssert.True(ListenerAddresses(port).Contains(IPAddress.IPv6Loopback), "An IPv6 loopback listener is started.");
                            Stopwatch watch = Stopwatch.StartNew();
                            using HttpResponseMessage viaIpv6 = await http.GetAsync($"http://[::1]:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false);
                            using HttpClient fresh = new HttpClient();
                            using HttpResponseMessage viaName = await fresh.GetAsync($"http://localhost:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false);
                            TestAssert.Equal(HttpStatusCode.OK, viaIpv6.StatusCode, "IPv6 loopback is served.");
                            TestAssert.Equal(HttpStatusCode.OK, viaName.StatusCode, "localhost is served.");
                            TestAssert.True(watch.ElapsedMilliseconds < 1500, $"No refused-IPv6 fallback delay ({watch.ElapsedMilliseconds} ms).");
                        }

                        int ipv4Port = TestPorts.GetFreePort();
                        using A2AGrpcServer ipv4Only = new A2AGrpcServer("127.0.0.1", ipv4Port, CreateCard($"http://127.0.0.1:{ipv4Port}"), new EchoAgent());
                        await ipv4Only.StartAsync(ct).ConfigureAwait(false);
                        List<IPAddress> ipv4Addresses = ListenerAddresses(ipv4Port);
                        TestAssert.True(ipv4Addresses.Contains(IPAddress.Loopback) && !ipv4Addresses.Contains(IPAddress.IPv6Loopback), "An explicit IPv4 address gets no IPv6 listener.");
                    }),

                    Case(suiteId, "GrpcStopReleasesEveryListener", "A2AGrpcServer.Stop, including a stop right after start and a cancelled start token, closes every listening socket so the port can be reused", async ct =>
                    {
                        int port = TestPorts.GetFreePort();
                        using A2AGrpcServer server = new A2AGrpcServer("localhost", port, CreateCard($"http://localhost:{port}"), new EchoAgent());
                        for (int i = 0; i < 3; i++)
                        {
                            await server.StartAsync(ct).ConfigureAwait(false);
                            server.Stop();
                            TestAssert.Equal(0, ListenerAddresses(port).Count, $"Stop immediately after start releases the port (iteration {i}).");
                        }

                        using (CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            await server.StartAsync(cancel.Token).ConfigureAwait(false);
                            TestAssert.True(ListenerAddresses(port).Count > 0, "The server listens.");
                            cancel.Cancel();
                            TestAssert.Equal(0, ListenerAddresses(port).Count, "Cancelling the start token stops the server and releases the port.");
                        }

                        using (CancellationTokenSource cancelled = new CancellationTokenSource())
                        {
                            cancelled.Cancel();
                            await TestAssert.ThrowsAsync<OperationCanceledException>(() => server.StartAsync(cancelled.Token), "An already-cancelled token does not start the server.").ConfigureAwait(false);
                        }

                        await server.StartAsync(ct).ConfigureAwait(false);
                        using HttpClient http = new HttpClient();
                        TestAssert.Equal(HttpStatusCode.OK, await GetStatusWithRetryAsync(http, $"http://localhost:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false), "The server restarts on the same port.");
                        server.Stop();
                    }),

                    Case(suiteId, "GrpcLocalhostStartsWhenIpv6PortIsTaken", "When ::1 is unavailable on the port, A2AGrpcServer still starts on 127.0.0.1 and logs that the IPv6 listener was skipped", async ct =>
                    {
                        if (!Socket.OSSupportsIPv6) return;

                        int port = TestPorts.GetFreePort();
                        TcpListener blocker = new TcpListener(IPAddress.IPv6Loopback, port);
                        blocker.Start();
                        try
                        {
                            List<string> logs = new List<string>();
                            using A2AGrpcServer server = new A2AGrpcServer("localhost", port, CreateCard($"http://localhost:{port}"), new EchoAgent());
                            server.Log += (sender, message) =>
                            {
                                lock (logs) logs.Add(message);
                            };
                            await server.StartAsync(ct).ConfigureAwait(false);

                            using HttpClient http = new HttpClient();
                            TestAssert.Equal(HttpStatusCode.OK, await GetStatusWithRetryAsync(http, $"http://127.0.0.1:{port}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false), "IPv4 still serves.");
                            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                            while (DateTime.UtcNow < deadline && !Snapshot(logs).Any(message => message.Contains("IPv6 listener"))) await Task.Delay(20, ct).ConfigureAwait(false);
                            TestAssert.True(Snapshot(logs).Any(message => message.Contains("IPv6 listener")), "The skipped IPv6 listener is logged.");
                            server.Stop();
                            TestAssert.True(ListenerAddresses(port).All(address => address.Equals(IPAddress.IPv6Loopback)), "Only the unrelated IPv6 listener remains after Stop.");
                        }
                        finally
                        {
                            blocker.Stop();
                        }
                    }),

                    Case(suiteId, "GrpcExtendedCardRequiresAuthentication", "On A2AGrpcServer only the public card skips authentication; the extended card GET needs credentials and a failure carries WWW-Authenticate", async ct =>
                    {
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct, null, false, server =>
                        {
                            server.AuthenticationHandler = context => Task.FromResult(context.Request.RetrieveHeaderValue("Authorization") == "Bearer ok"
                                ? new AuthenticationResult { IsAuthenticated = true, Principal = "ok" }
                                : AuthenticationResult.BearerChallenge(null, "invalid_token", null, "denied"));
                        }).ConfigureAwait(false);
                        using HttpClient http = new HttpClient();

                        using HttpResponseMessage publicCard = await http.GetAsync($"{fixture.BaseUrl}{A2AProtocol.AgentCardPath}", ct).ConfigureAwait(false);
                        using HttpResponseMessage anonymous = await http.GetAsync($"{fixture.BaseUrl}{A2AProtocol.ExtendedAgentCardPath}", ct).ConfigureAwait(false);
                        using HttpRequestMessage authorized = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}{A2AProtocol.ExtendedAgentCardPath}");
                        authorized.Headers.TryAddWithoutValidation("Authorization", "Bearer ok");
                        using HttpResponseMessage withCredentials = await http.SendAsync(authorized, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.OK, publicCard.StatusCode, "The public card needs no credentials.");
                        TestAssert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode, "The extended card needs credentials.");
                        TestAssert.Equal("Bearer error=\"invalid_token\"", anonymous.Headers.WwwAuthenticate.ToString(), "The failure carries the result's headers.");
                        TestAssert.Equal(HttpStatusCode.OK, withCredentials.StatusCode, "Authenticated callers get the extended card.");
                    }),

                    Case(suiteId, "GrpcServerValidatesOrigin", "A2AGrpcServer rejects a disallowed Origin with 403, allows loopback origins, and defaults to loopback-only clients for localhost", async ct =>
                    {
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct).ConfigureAwait(false);
                        using HttpClient http = new HttpClient();

                        using HttpRequestMessage evil = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}{A2AProtocol.AgentCardPath}");
                        evil.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                        using HttpResponseMessage evilResponse = await http.SendAsync(evil, ct).ConfigureAwait(false);
                        using HttpRequestMessage local = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}{A2AProtocol.AgentCardPath}");
                        local.Headers.TryAddWithoutValidation("Origin", "http://localhost:3000");
                        using HttpResponseMessage localResponse = await http.SendAsync(local, ct).ConfigureAwait(false);

                        TestAssert.Equal(HttpStatusCode.Forbidden, evilResponse.StatusCode);
                        TestAssert.Equal(HttpStatusCode.OK, localResponse.StatusCode);
                        using A2AGrpcServer loopbackDefault = new A2AGrpcServer("localhost", 1, CreateCard("http://localhost"));
                        using A2AGrpcServer allInterfaces = new A2AGrpcServer("*", 1, CreateCard("http://localhost"));
                        TestAssert.True(loopbackDefault.RestrictToLoopbackClients && !allInterfaces.RestrictToLoopbackClients, "The loopback default follows the host name.");
                    }),

                    Case(suiteId, "GrpcHidesInternalErrorDetails", "A handler exception reaches a gRPC client as a generic error without its message", async ct =>
                    {
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct, new ThrowingAgent()).ConfigureAwait(false);
                        using A2AGrpcClient client = new A2AGrpcClient(fixture.BaseUrl, fixture.Client);

                        A2AProtocolException? error = await CaptureAsync(() => client.SendMessageAsync(CreateMessageRequest("boom"), ct)).ConfigureAwait(false);
                        TestAssert.NotNull(error, "The failure surfaces as a protocol exception.");
                        TestAssert.False(error!.Message.Contains("db-password"), $"Internal details are not sent: {error.Message}");
                        TestAssert.True(error.Message.Contains("Internal error"), $"The client sees a generic message: {error.Message}");

                        A2AProtocolException? notFound = await CaptureAsync(() => client.GetTaskAsync(new GetTaskRequest { Id = "missing-task" }, ct)).ConfigureAwait(false);
                        TestAssert.True(notFound!.Message.Contains("missing-task"), "Protocol errors keep their own message.");
                    }),

                    Case(suiteId, "JsonRpcAndRestHideInternalErrorDetails", "A handler exception reaches JSON-RPC (HTTP 200) and HTTP+JSON (HTTP 500) callers as a generic internal error without its message", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct, new ThrowingAgent()).ConfigureAwait(false);
                        using A2AClient rpcClient = new A2AClient(fixture.EndpointUrl, fixture.Client);
                        using A2AHttpJsonClient restClient = new A2AHttpJsonClient(fixture.BaseUrl, fixture.Client);

                        A2AProtocolException? rpcError = await CaptureAsync(() => rpcClient.SendMessageAsync(CreateMessageRequest("boom"), ct)).ConfigureAwait(false);
                        A2AProtocolException? restError = await CaptureAsync(() => restClient.SendMessageAsync(CreateMessageRequest("boom"), ct)).ConfigureAwait(false);

                        string rpcBody = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"" + A2AProtocol.SendMessage + "\",\"params\":{\"message\":{\"messageId\":\"m\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"boom\"}]}}}";
                        using HttpResponseMessage rawRpc = await fixture.Client.PostAsync(fixture.EndpointUrl, new StringContent(rpcBody, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                        string rawRpcBody = await rawRpc.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using HttpResponseMessage rawRest = await fixture.Client.PostAsync($"{fixture.BaseUrl}/message:send", new StringContent("{\"message\":{\"messageId\":\"m2\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"boom\"}]}}", Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                        string rawRestBody = await rawRest.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                        TestAssert.Equal(A2AErrorCode.InternalError, rpcError?.ErrorCode, "JSON-RPC callers get InternalError.");
                        TestAssert.Equal(A2AErrorCode.InternalError, restError?.ErrorCode, "HTTP+JSON callers get InternalError.");
                        TestAssert.Equal(System.Net.HttpStatusCode.OK, rawRpc.StatusCode, "JSON-RPC errors use HTTP 200.");
                        TestAssert.Equal(System.Net.HttpStatusCode.InternalServerError, rawRest.StatusCode, "HTTP+JSON internal errors use HTTP 500.");
                        foreach (string body in new[] { rawRpcBody, rawRestBody, rpcError!.Message, restError!.Message })
                        {
                            TestAssert.False(body.Contains("db-password") || body.Contains("hunter2"), $"Internal details are not sent: {body}");
                        }
                    }),

                    Case(suiteId, "PushConfigJsonMatchesV1AndReadsLegacy", "Push config JSON is the flat A2A v1.0 shape on output, and both flat and legacy nested shapes are read", ct =>
                    {
                        TaskPushNotificationConfig config = new TaskPushNotificationConfig
                        {
                            Id = "c1", TaskId = "t1",
                            PushNotificationConfig = new PushNotificationConfig { Url = "https://example.com/hook", Token = "tok", Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "x" } }
                        };
                        string written = JsonSerializer.Serialize(config, A2AJson.DefaultOptions);
                        JsonProbe flat = JsonProbe.Parse(written);
                        TestAssert.Equal("https://example.com/hook", flat.Get("url").String(), $"url is top-level: {written}");
                        TestAssert.Equal("c1", flat.Get("id").String());
                        TestAssert.Equal("t1", flat.Get("taskId").String());
                        TestAssert.Equal("Bearer", flat.Get("authentication").Get("scheme").String());
                        TestAssert.False(flat.Has("pushNotificationConfig"), "No nested object is written.");

                        TaskPushNotificationConfig? legacy = JsonSerializer.Deserialize<TaskPushNotificationConfig>("{\"id\":\"c2\",\"taskId\":\"t2\",\"pushNotificationConfig\":{\"url\":\"https://old.example/hook\",\"token\":\"t\"}}", A2AJson.DefaultOptions);
                        TestAssert.Equal("https://old.example/hook", legacy!.PushNotificationConfig.Url, "The legacy nested shape is read.");
                        TaskPushNotificationConfig? roundTrip = JsonSerializer.Deserialize<TaskPushNotificationConfig>(written, A2AJson.DefaultOptions);
                        TestAssert.Equal("x", roundTrip!.PushNotificationConfig.Authentication!.Credentials, "The flat shape round-trips.");

                        CreateTaskPushNotificationConfigRequest? createFlat = JsonSerializer.Deserialize<CreateTaskPushNotificationConfigRequest>("{\"taskId\":\"t\",\"id\":\"c\",\"url\":\"https://a/h\"}", A2AJson.DefaultOptions);
                        CreateTaskPushNotificationConfigRequest? createLegacy = JsonSerializer.Deserialize<CreateTaskPushNotificationConfigRequest>("{\"taskId\":\"t\",\"configId\":\"c\",\"config\":{\"url\":\"https://a/h\"}}", A2AJson.DefaultOptions);
                        TestAssert.True(createFlat!.ConfigId == "c" && createFlat.Config.Url == "https://a/h" && createLegacy!.ConfigId == "c" && createLegacy.Config.Url == "https://a/h", "Create requests read both shapes.");
                        TestAssert.True(JsonSerializer.Serialize(new CreateTaskPushNotificationConfigRequest { TaskId = "t", ConfigId = "c", Config = new PushNotificationConfig { Url = "https://a/h" } }, A2AJson.DefaultOptions).Contains("\"url\":\"https://a/h\""), "Create requests are written flat.");

                        GetTaskPushNotificationConfigRequest? getNew = JsonSerializer.Deserialize<GetTaskPushNotificationConfigRequest>("{\"taskId\":\"t\",\"id\":\"c\"}", A2AJson.DefaultOptions);
                        DeleteTaskPushNotificationConfigRequest? deleteOld = JsonSerializer.Deserialize<DeleteTaskPushNotificationConfigRequest>("{\"taskId\":\"t\",\"configId\":\"c\"}", A2AJson.DefaultOptions);
                        string getWritten = JsonSerializer.Serialize(new GetTaskPushNotificationConfigRequest { TaskId = "t", ConfigId = "c" }, A2AJson.DefaultOptions);
                        TestAssert.True(getNew!.ConfigId == "c" && deleteOld!.ConfigId == "c", "id and the legacy configId are both read.");
                        TestAssert.True(getWritten.Contains("\"id\":\"c\"") && !getWritten.Contains("configId"), $"id is written: {getWritten}");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ListPushConfigsAcceptsBothMethodNames", "JSON-RPC accepts ListTaskPushNotificationConfigs (v1.0) and the legacy singular name; A2AClient sends the v1.0 name", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        using A2AClient client = new A2AClient(fixture.EndpointUrl, fixture.Client);
                        string taskId = (await client.SendMessageAsync(CreateMessageRequest("names"), ct).ConfigureAwait(false)).Task!.Id;
                        await client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest { TaskId = taskId, ConfigId = "c", Config = new PushNotificationConfig { Url = "https://example.com/hook" } }, ct).ConfigureAwait(false);

                        foreach (string method in new[] { "ListTaskPushNotificationConfigs", "ListTaskPushNotificationConfig" })
                        {
                            string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\",\"params\":{\"taskId\":\"" + taskId + "\"}}";
                            using HttpResponseMessage response = await fixture.Client.PostAsync(fixture.EndpointUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                            JsonProbe root = JsonProbe.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                            TestAssert.Equal(1, root.Get("result").Get("configs").Length, $"{method} lists the config.");
                        }

                        TestAssert.Equal("ListTaskPushNotificationConfigs", A2AProtocol.ListTaskPushNotificationConfig, "The client constant is the v1.0 name.");
                        TestAssert.Equal(1, (await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest { TaskId = taskId }, ct).ConfigureAwait(false)).Configs.Count, "A2AClient lists through the v1.0 name.");
                    }),

                    Case(suiteId, "GrpcServerDeliversPushWithItsSettings", "A2AGrpcServer exposes the push settings and delivers push notifications for gRPC-created tasks", async ct =>
                    {
                        using WebhookReceiver receiver = new WebhookReceiver();
                        await using A2AGrpcTestFixture fixture = await A2AGrpcTestFixture.StartAsync(ct, null, false, server =>
                        {
                            server.PushNotificationUrlValidator = uri => uri.IsLoopback;
                            server.PushNotificationTimeoutMs = 5000;
                            server.PushNotificationMaxAttempts = 2;
                        }).ConfigureAwait(false);
                        using A2AGrpcClient client = new A2AGrpcClient(fixture.BaseUrl, fixture.Client);

                        SendMessageRequest request = CreateMessageRequest("grpc push");
                        request.Configuration = new SendMessageConfiguration { PushNotificationConfig = new PushNotificationConfig { Url = receiver.Url, Token = "g" } };
                        await client.SendMessageAsync(request, ct).ConfigureAwait(false);

                        TestAssert.True(await receiver.WaitForAsync(1, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false), "The gRPC-created task's events reach the webhook.");
                        TestAssert.Equal("g", receiver.Requests.First().Headers[A2AProtocol.NotificationTokenHeader]);
                        using A2AGrpcServer settings = new A2AGrpcServer("localhost", 1, CreateCard("http://localhost"));
                        TestAssert.Equal(10000, settings.PushNotificationTimeoutMs, "The default timeout passes through.");
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => settings.PushNotificationMaxAttempts = 0, "Ranges are validated.");
                    }),

                    Case(suiteId, "TaskStoreHandlesNulls", "InMemoryA2ATaskStore rejects a null task with ArgumentNullException and treats a null list request as empty", async ct =>
                    {
                        InMemoryA2ATaskStore store = new InMemoryA2ATaskStore();
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => store.SaveTaskAsync("t", null!, ct), "A null task is rejected.").ConfigureAwait(false);
                        await TestAssert.ThrowsAsync<ArgumentNullException>(() => store.SaveTaskAsync(null!, new AgentTask(), ct), "A null ID is rejected.").ConfigureAwait(false);
                        await store.SaveTaskAsync("t", new AgentTask { Id = "t" }, ct).ConfigureAwait(false);
                        ListTasksResponse all = await store.ListTasksAsync(null!, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, all.Tasks.Count, "A null request lists everything.");
                    }),
                });
        }

        private static SendMessageRequest ContinueTask(string taskId, string text)
        {
            SendMessageRequest request = CreateMessageRequest(text);
            request.Message.TaskId = taskId;
            return request;
        }

        private static async Task<A2AProtocolException?> CaptureAsync<T>(Func<Task<T>> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (A2AProtocolException ex)
            {
                return ex;
            }
        }

        private static async Task<HttpStatusCode> GetStatusWithRetryAsync(HttpClient http, string url, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try
                {
                    using HttpResponseMessage response = await http.GetAsync(url, token).ConfigureAwait(false);
                    return response.StatusCode;
                }
                catch (HttpRequestException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }

        private sealed class StepAgent : IA2AAgentHandler
        {
            public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
                if (!context.IsContinuation)
                {
                    await updater.SubmitAsync(token: token).ConfigureAwait(false);
                }

                await updater.StartAsync(token: token).ConfigureAwait(false);
            }
        }

        private sealed class ThrowingAgent : IA2AAgentHandler
        {
            public Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                throw new InvalidOperationException("connection string db-password=hunter2 is wrong");
            }
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "a2a", "protocol", "matrix" });
        }

        private static AgentCard CreateCard(string endpointUrl, string name = "Voltaic Test Agent")
        {
            return new AgentCard
            {
                Name = name,
                Description = "A test A2A agent.",
                Version = "1.0.0",
                SupportedInterfaces = new List<AgentInterface>
                {
                    new AgentInterface
                    {
                        Url = endpointUrl,
                        ProtocolBinding = "JSONRPC",
                        ProtocolVersion = A2AProtocol.ProtocolVersion
                    }
                },
                Capabilities = new AgentCapabilities
                {
                    Streaming = true,
                    PushNotifications = true,
                    StateTransitionHistory = true,
                    ExtendedAgentCard = true
                },
                Skills = new List<AgentSkill>
                {
                    new AgentSkill
                    {
                        Id = "echo",
                        Name = "Echo",
                        Description = "Echoes text.",
                        Tags = new List<string> { "test" }
                    }
                },
                DefaultInputModes = new List<string> { "text/plain" },
                DefaultOutputModes = new List<string> { "text/plain" }
            };
        }

        private static SendMessageRequest CreateMessageRequest(string text)
        {
            return new SendMessageRequest
            {
                Message = new Message
                {
                    Role = Role.User,
                    MessageId = Guid.NewGuid().ToString("N"),
                    Parts = new List<Part> { Part.FromText(text) }
                }
            };
        }

        private static object? ParseJsonValue(string json)
        {
            return JsonSerializer.Deserialize<object?>(json, A2AJson.DefaultOptions);
        }

        private static T? MetaValue<T>(object? value)
        {
            return value == null ? default : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, A2AJson.DefaultOptions), A2AJson.DefaultOptions);
        }

        private sealed class EchoAgent : IA2AAgentHandler
        {
            public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(token: token).ConfigureAwait(false);
                await updater.StartAsync(token: token).ConfigureAwait(false);

                string text = context.Message.Parts.FirstOrDefault()?.Text ?? string.Empty;
                Message response = new Message
                {
                    Role = Role.Agent,
                    MessageId = Guid.NewGuid().ToString("N"),
                    TaskId = context.TaskId,
                    ContextId = context.ContextId,
                    Parts = new List<Part> { Part.FromText("echo:" + text) }
                };

                await updater.AddArtifactAsync(new Artifact
                {
                    ArtifactId = "echo-result",
                    Name = "Echo Result",
                    Parts = new List<Part> { Part.FromText(text) }
                }, token: token).ConfigureAwait(false);
                await updater.CompleteAsync(response, token).ConfigureAwait(false);
            }
        }

        private sealed class SlowAgent : IA2AAgentHandler
        {
            public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
                await Task.Delay(250, token).ConfigureAwait(false);
                Message response = new Message
                {
                    Role = Role.Agent,
                    MessageId = Guid.NewGuid().ToString("N"),
                    TaskId = context.TaskId,
                    ContextId = context.ContextId,
                    Parts = new List<Part> { Part.FromText("done") }
                };
                await updater.CompleteAsync(response, token).ConfigureAwait(false);
            }
        }

        private sealed class WorkingOnlyAgent : IA2AAgentHandler
        {
            public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(token: token).ConfigureAwait(false);
                await updater.StartAsync(token: token).ConfigureAwait(false);
            }
        }

        private sealed class CapturingAuthAgent : IA2AAgentHandler
        {
            public volatile bool Invoked;
            public volatile bool PrincipalWasNull;
            public string? Principal;
            public string? TenantId;

            public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                Invoked = true;
                Principal = context.Principal;
                PrincipalWasNull = context.Principal == null;
                TenantId = context.Claims != null && context.Claims.TryGetValue("tenantId", out string? tenant) ? tenant : null;

                A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(token: token).ConfigureAwait(false);
                await updater.StartAsync(token: token).ConfigureAwait(false);
                Message response = new Message
                {
                    Role = Role.Agent,
                    MessageId = Guid.NewGuid().ToString("N"),
                    TaskId = context.TaskId,
                    ContextId = context.ContextId,
                    Parts = new List<Part> { Part.FromText("done") }
                };
                await updater.CompleteAsync(response, token).ConfigureAwait(false);
            }
        }

        private sealed class A2ATestFixture : IAsyncDisposable
        {
            private readonly A2AHttpServer _Server;
            private readonly CancellationTokenSource _TokenSource;

            private A2ATestFixture(A2AHttpServer server, int port, CancellationTokenSource tokenSource)
            {
                _Server = server;
                _TokenSource = tokenSource;
                Port = port;
                Client = new HttpClient();
            }

            public int Port { get; }

            public HttpClient Client { get; }

            public string BaseUrl => $"http://localhost:{Port}";

            public string EndpointUrl => $"{BaseUrl}/a2a";

            public static async Task<A2ATestFixture> StartAsync(CancellationToken token, IA2AAgentHandler? handler = null, Action<A2AHttpServer>? configure = null)
            {
                int port = TestPorts.GetFreePort();
                string endpointUrl = $"http://localhost:{port}/a2a";
                AgentCard card = CreateCard(endpointUrl);
                A2AHttpServer server = new A2AHttpServer("localhost", port, card, handler ?? new EchoAgent())
                {
                    ExtendedAgentCard = CreateCard(endpointUrl, "Voltaic Extended Agent")
                };
                configure?.Invoke(server);
                CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                await server.StartAsync(tokenSource.Token).ConfigureAwait(false);

                A2ATestFixture fixture = new A2ATestFixture(server, port, tokenSource);
                await fixture.WaitUntilReadyAsync(token).ConfigureAwait(false);
                return fixture;
            }

            public async ValueTask DisposeAsync()
            {
                _Server.Stop();
                _TokenSource.Cancel();
                Client.Dispose();
                await Task.Delay(50).ConfigureAwait(false);
                _TokenSource.Dispose();
                _Server.Dispose();
            }

            private async Task WaitUntilReadyAsync(CancellationToken token)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        using HttpResponseMessage response = await Client.GetAsync($"{BaseUrl}{A2AProtocol.AgentCardPath}", token).ConfigureAwait(false);
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

                throw new TimeoutException("A2A test server did not become ready.");
            }
        }

        private static List<IPAddress> ListenerAddresses(int port)
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port == port)
                .Select(endpoint => endpoint.Address)
                .ToList();
        }

        private static List<string> Snapshot(List<string> items)
        {
            lock (items)
            {
                return new List<string>(items);
            }
        }

        private sealed class A2AGrpcTestFixture : IAsyncDisposable
        {
            private readonly A2AGrpcServer _Server;
            private readonly CancellationTokenSource _TokenSource;

            private A2AGrpcTestFixture(A2AGrpcServer server, int port, CancellationTokenSource tokenSource)
            {
                _Server = server;
                _TokenSource = tokenSource;
                Port = port;
                Client = new HttpClient(new SocketsHttpHandler { EnableMultipleHttp2Connections = true });
            }

            public int Port { get; }

            public HttpClient Client { get; }

            public string BaseUrl => $"http://localhost:{Port}";

            public static async Task<A2AGrpcTestFixture> StartAsync(
                CancellationToken token,
                IA2AAgentHandler? handler = null,
                bool blockRpcWithAuthentication = false,
                Action<A2AGrpcServer>? configure = null)
            {
                AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

                int port = TestPorts.GetFreePort();
                string baseUrl = $"http://localhost:{port}";
                AgentCard card = CreateCard(baseUrl);
                AddGrpcInterface(card, baseUrl);
                AgentCard extendedCard = CreateCard(baseUrl, "Voltaic Extended Agent");
                AddGrpcInterface(extendedCard, baseUrl);

                A2AGrpcServer server = new A2AGrpcServer("localhost", port, card, handler ?? new EchoAgent())
                {
                    ExtendedAgentCard = extendedCard
                };
                if (blockRpcWithAuthentication)
                {
                    server.AuthenticationHandler = _ => Task.FromResult(new AuthenticationResult
                    {
                        IsAuthenticated = false,
                        StatusCode = 403,
                        ErrorMessage = "blocked"
                    });
                }

                configure?.Invoke(server);

                CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                await server.StartAsync(tokenSource.Token).ConfigureAwait(false);

                A2AGrpcTestFixture fixture = new A2AGrpcTestFixture(server, port, tokenSource);
                await fixture.WaitUntilReadyAsync(token).ConfigureAwait(false);
                return fixture;
            }

            public async ValueTask DisposeAsync()
            {
                _Server.Stop();
                _TokenSource.Cancel();
                Client.Dispose();
                await Task.Delay(50).ConfigureAwait(false);
                _TokenSource.Dispose();
                _Server.Dispose();
            }

            private async Task WaitUntilReadyAsync(CancellationToken token)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        using HttpResponseMessage response = await Client.GetAsync($"{BaseUrl}{A2AProtocol.AgentCardPath}", token).ConfigureAwait(false);
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

                throw new TimeoutException("A2A gRPC test server did not become ready.");
            }
        }

        private static void AddGrpcInterface(AgentCard card, string endpointUrl)
        {
            card.SupportedInterfaces.Add(new AgentInterface
            {
                Url = endpointUrl,
                ProtocolBinding = "GRPC",
                ProtocolVersion = A2AProtocol.ProtocolVersion
            });
        }

        private static async Task<AgentTask> WaitForTaskStateAsync(A2AClient client, string taskId, TaskState state, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                AgentTask task = await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, token).ConfigureAwait(false);
                if (task.Status.State == state)
                {
                    return task;
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }

            throw new TimeoutException($"Task '{taskId}' did not reach state '{state}'.");
        }
    }
}
