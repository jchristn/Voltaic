namespace Test.Shared
{
    using System.Net;
    using System.Net.Http;
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
                        using JsonDocument document = JsonDocument.Parse(json);
                        JsonElement root = document.RootElement;

                        TestAssert.Equal("Voltaic Test Agent", root.GetProperty("name").GetString());
                        TestAssert.Equal("1.0", root.GetProperty("supportedInterfaces")[0].GetProperty("protocolVersion").GetString());
                        TestAssert.Equal("JSONRPC", root.GetProperty("supportedInterfaces")[0].GetProperty("protocolBinding").GetString());
                        TestAssert.True(root.GetProperty("capabilities").GetProperty("streaming").GetBoolean(), "Streaming should serialize.");
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
                        using JsonDocument document = JsonDocument.Parse(json);
                        TestAssert.Equal("TASK_STATE_COMPLETED", document.RootElement.GetProperty("state").GetString());
                        TestAssert.Equal("ROLE_AGENT", document.RootElement.GetProperty("message").GetProperty("role").GetString());
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

                        TaskPushNotificationConfig created = await client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-push",
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
                            TaskId = "task-push",
                            ConfigId = "webhook"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("https://example.com/webhook", fetched.PushNotificationConfig.Url);

                        ListTaskPushNotificationConfigResponse list = await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-push"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, list.Configs.Count);

                        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-push",
                            ConfigId = "webhook"
                        }, ct).ConfigureAwait(false);
                        ListTaskPushNotificationConfigResponse afterDelete = await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-push"
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
                            TaskId = "task-rest-client",
                            Config = new PushNotificationConfig
                            {
                                Id = "rest-client",
                                Url = "https://example.com/a2a"
                            }
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("rest-client", created.Id);

                        TaskPushNotificationConfig fetched = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-rest-client",
                            ConfigId = "rest-client"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("https://example.com/a2a", fetched.PushNotificationConfig.Url);

                        ListTaskPushNotificationConfigResponse configs = await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-rest-client"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, configs.Configs.Count);

                        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-rest-client",
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
                            TaskId = "task-grpc-push",
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
                            TaskId = "task-grpc-push",
                            ConfigId = "grpc-webhook"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal("https://example.com/grpc", fetched.PushNotificationConfig.Url);

                        ListTaskPushNotificationConfigResponse configs = await client.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-grpc-push"
                        }, ct).ConfigureAwait(false);
                        TestAssert.Equal(1, configs.Configs.Count);

                        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-grpc-push",
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
                        request.Message.Parts.Add(Part.FromData(ParseJsonElement("""{"kind":"example","count":2}""")));
                        request.Message.Metadata = new Dictionary<string, JsonElement>
                        {
                            { "source", ParseJsonElement("\"qa\"") },
                            { "attempt", ParseJsonElement("7") }
                        };
                        request.Metadata = new Dictionary<string, JsonElement>
                        {
                            { "requestId", ParseJsonElement("\"req-123\"") }
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
                        TestAssert.Equal("example", userMessage.Parts[3].Data!.Value.GetProperty("kind").GetString());
                        TestAssert.Equal("qa", userMessage.Metadata!["source"].GetString());
                        TestAssert.Equal(7, userMessage.Metadata["attempt"].GetInt32());
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
                        string resultJson = ((JsonElement)rpc!.Result!).GetRawText();
                        SendMessageResponse? sendResponse = JsonSerializer.Deserialize<SendMessageResponse>(resultJson, A2AJson.DefaultOptions);
                        TestAssert.Equal(TaskState.Completed, sendResponse!.Task!.Status.State);
                    }),

                    Case(suiteId, "OfficialRestPushConfigBodyAccepted", "Voltaic server accepts official SDK HTTP+JSON push config request bodies", async ct =>
                    {
                        await using A2ATestFixture fixture = await A2ATestFixture.StartAsync(ct).ConfigureAwait(false);
                        PushNotificationConfig config = new PushNotificationConfig
                        {
                            Id = "official-rest",
                            Url = "https://example.com/official"
                        };
                        string json = JsonSerializer.Serialize(config, A2AJson.DefaultOptions);

                        using HttpResponseMessage response = await fixture.Client.PostAsync(
                            $"{fixture.BaseUrl}/tasks/task-official-rest/pushNotificationConfigs",
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

        private static JsonElement ParseJsonElement(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
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

            public static async Task<A2ATestFixture> StartAsync(CancellationToken token, IA2AAgentHandler? handler = null)
            {
                int port = TestPorts.GetFreePort();
                string endpointUrl = $"http://localhost:{port}/a2a";
                AgentCard card = CreateCard(endpointUrl);
                A2AHttpServer server = new A2AHttpServer("localhost", port, card, handler ?? new EchoAgent())
                {
                    ExtendedAgentCard = CreateCard(endpointUrl, "Voltaic Extended Agent")
                };
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
                bool blockRpcWithAuthentication = false)
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
