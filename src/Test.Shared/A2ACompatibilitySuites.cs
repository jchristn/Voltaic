namespace Test.Shared
{
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic.A2A;
    using Voltaic.Core;

    public static class A2ACompatibilitySuites
    {
        public static TestSuiteDescriptor OfficialSdkOracle()
        {
            const string suiteId = "A2A.Compatibility";

            return new TestSuiteDescriptor(
                suiteId,
                "A2A Official SDK Compatibility Oracle",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "JsonRpcClientEnvelopeMatchesOfficialSdk", "Voltaic JSON-RPC client emits official SDK method, header, and envelope shape", async ct =>
                    {
                        RecordingHandler handler = new RecordingHandler((request, body) =>
                        {
                            JsonRpcResponse response = new JsonRpcResponse
                            {
                                Id = "oracle",
                                Result = new SendMessageResponse
                                {
                                    Message = new Message
                                    {
                                        Role = Role.Agent,
                                        MessageId = "agent-1",
                                        Parts = new List<Part> { Part.FromText("ok") }
                                    }
                                }
                            };
                            return JsonResponse(response);
                        });

                        using HttpClient http = new HttpClient(handler);
                        using A2AClient client = new A2AClient("http://localhost/a2a", http);

                        await client.SendMessageAsync(new SendMessageRequest
                        {
                            Message = new Message
                            {
                                Role = Role.User,
                                MessageId = "user-1",
                                Parts = new List<Part> { Part.FromText("hello") }
                            }
                        }, ct).ConfigureAwait(false);

                        RecordedRequest recorded = handler.Requests.Single();
                        TestAssert.Equal("POST", recorded.Method);
                        TestAssert.Equal("http://localhost/a2a", recorded.Url);
                        TestAssert.Equal(A2AProtocol.ProtocolVersion, recorded.Headers[A2AProtocol.VersionHeader]);

                        using JsonDocument document = JsonDocument.Parse(recorded.Body);
                        JsonElement root = document.RootElement;
                        TestAssert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
                        TestAssert.Equal(A2AProtocol.SendMessage, root.GetProperty("method").GetString());
                        TestAssert.Equal("ROLE_USER", root.GetProperty("params").GetProperty("message").GetProperty("role").GetString());
                        TestAssert.Equal("hello", root.GetProperty("params").GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString());
                    }),

                    Case(suiteId, "HttpJsonClientRoutesMatchOfficialSdk", "Voltaic HTTP+JSON client emits official SDK REST route and body shape", async ct =>
                    {
                        RecordingHandler handler = new RecordingHandler((request, body) =>
                        {
                            TaskPushNotificationConfig response = new TaskPushNotificationConfig
                            {
                                Id = "cfg-1",
                                TaskId = "task-1",
                                PushNotificationConfig = new PushNotificationConfig
                                {
                                    Id = "cfg-1",
                                    Url = "https://example.com/webhook"
                                }
                            };
                            return JsonResponse(response);
                        });

                        using HttpClient http = new HttpClient(handler);
                        using A2AHttpJsonClient client = new A2AHttpJsonClient("http://localhost", http);

                        await client.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest
                        {
                            TaskId = "task-1",
                            Config = new PushNotificationConfig
                            {
                                Id = "cfg-1",
                                Url = "https://example.com/webhook"
                            }
                        }, ct).ConfigureAwait(false);

                        RecordedRequest recorded = handler.Requests.Single();
                        TestAssert.Equal("POST", recorded.Method);
                        TestAssert.Equal("http://localhost/tasks/task-1/pushNotificationConfigs", recorded.Url);
                        TestAssert.Equal(A2AProtocol.ProtocolVersion, recorded.Headers[A2AProtocol.VersionHeader]);

                        using JsonDocument document = JsonDocument.Parse(recorded.Body);
                        JsonElement root = document.RootElement;
                        TestAssert.Equal("cfg-1", root.GetProperty("id").GetString());
                        TestAssert.Equal("https://example.com/webhook", root.GetProperty("url").GetString());
                        TestAssert.False(root.TryGetProperty("config", out _), "HTTP+JSON request body should be the push config, not the JSON-RPC wrapper.");
                    }),

                    Case(suiteId, "HttpJsonListTasksQueryMatchesOfficialSdk", "Voltaic HTTP+JSON client emits A2A wire names in ListTasks query strings", async ct =>
                    {
                        RecordingHandler handler = new RecordingHandler((request, body) => JsonResponse(new ListTasksResponse()));
                        using HttpClient http = new HttpClient(handler);
                        using A2AHttpJsonClient client = new A2AHttpJsonClient("http://localhost", http);

                        await client.ListTasksAsync(new ListTasksRequest
                        {
                            ContextId = "ctx-1",
                            Status = TaskState.Completed,
                            PageSize = 25,
                            PageToken = "50",
                            HistoryLength = 2,
                            IncludeArtifacts = false
                        }, ct).ConfigureAwait(false);

                        RecordedRequest recorded = handler.Requests.Single();
                        TestAssert.Equal("GET", recorded.Method);
                        TestAssert.True(recorded.Url.Contains("/tasks?", StringComparison.Ordinal), "ListTasks should call /tasks with a query string.");
                        TestAssert.True(recorded.Url.Contains("contextId=ctx-1", StringComparison.Ordinal), "contextId should be present.");
                        TestAssert.True(recorded.Url.Contains("status=TASK_STATE_COMPLETED", StringComparison.Ordinal), "TaskState should use A2A wire names.");
                        TestAssert.True(recorded.Url.Contains("includeArtifacts=false", StringComparison.Ordinal), "includeArtifacts should be present.");
                    }),

                    Case(suiteId, "HttpJsonClientParsesOfficialRestSse", "Voltaic HTTP+JSON client parses official SDK style REST SSE data events", async ct =>
                    {
                        RecordingHandler handler = new RecordingHandler((request, body) =>
                        {
                            StreamResponse item = new StreamResponse
                            {
                                StatusUpdate = new TaskStatusUpdateEvent
                                {
                                    TaskId = "task-stream",
                                    ContextId = "ctx-stream",
                                    Status = new TaskStatus { State = TaskState.Working }
                                }
                            };
                            string eventBody = ": connected\n\ndata: " + JsonSerializer.Serialize(item, A2AJson.DefaultOptions) + "\n\n";
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(eventBody, Encoding.UTF8, "text/event-stream")
                            };
                        });

                        using HttpClient http = new HttpClient(handler);
                        using A2AHttpJsonClient client = new A2AHttpJsonClient("http://localhost", http);

                        List<StreamResponse> events = new List<StreamResponse>();
                        await foreach (StreamResponse item in client.SendStreamingMessageAsync(new SendMessageRequest
                        {
                            Message = new Message
                            {
                                Role = Role.User,
                                MessageId = "user-stream",
                                Parts = new List<Part> { Part.FromText("stream") }
                            }
                        }, ct))
                        {
                            events.Add(item);
                        }

                        RecordedRequest recorded = handler.Requests.Single();
                        TestAssert.Equal("http://localhost/message:stream", recorded.Url);
                        TestAssert.Equal(TaskState.Working, events.Single().StatusUpdate!.Status.State);
                    }),
                });
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "a2a", "compatibility", "oracle", "matrix" });
        }

        private static HttpResponseMessage JsonResponse(object value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, A2AJson.DefaultOptions), Encoding.UTF8, "application/json")
            };
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _Respond;

            public RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
            {
                _Respond = respond;
            }

            public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                Dictionary<string, string> headers = request.Headers
                    .ToDictionary(header => header.Key, header => String.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);

                Requests.Add(new RecordedRequest(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? string.Empty,
                    headers,
                    body));

                return _Respond(request, body);
            }
        }

        private sealed record RecordedRequest(
            string Method,
            string Url,
            Dictionary<string, string> Headers,
            string Body);
    }
}
