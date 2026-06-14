namespace Test.Shared
{
    using System.Text.Json;
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;
    public static class ModelApiSuites
    {
        public static TestSuiteDescriptor JsonRpcModels()
        {
            const string suiteId = "ModelApi.JsonRpc";

            return new TestSuiteDescriptor(
                suiteId,
                "JSON-RPC Model API",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "RequestDefaultsAndSerialization", "JsonRpcRequest defaults and omits nulls", ct =>
                    {
                        JsonRpcRequest request = new JsonRpcRequest { Method = "ping" };
                        string json = JsonSerializer.Serialize(request);

                        TestAssert.Equal("2.0", request.JsonRpc);
                        TestAssert.True(json.Contains("\"jsonrpc\":\"2.0\""), "Serialized request should include jsonrpc.");
                        TestAssert.True(json.Contains("\"method\":\"ping\""), "Serialized request should include method.");
                        TestAssert.False(json.Contains("\"params\""), "Null params should be omitted.");
                        TestAssert.False(json.Contains("\"id\""), "Null id should be omitted.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "RequestRoundTrip", "JsonRpcRequest round-trips params and id", ct =>
                    {
                        JsonRpcRequest request = new JsonRpcRequest
                        {
                            Method = "echo",
                            Params = new { message = "hello" },
                            Id = 7
                        };

                        JsonRpcRequest? roundTrip = JsonSerializer.Deserialize<JsonRpcRequest>(JsonSerializer.Serialize(request));
                        TestAssert.NotNull(roundTrip, "Request should deserialize.");
                        TestAssert.Equal("echo", roundTrip!.Method);
                        TestAssert.Equal(7, ((JsonElement)roundTrip.Id!).GetInt32());
                        TestAssert.Equal("hello", ((JsonElement)roundTrip.Params!).GetProperty("message").GetString());
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResponseSuccessSerialization", "JsonRpcResponse serializes success without error", ct =>
                    {
                        JsonRpcResponse response = new JsonRpcResponse { Result = "pong", Id = "abc" };
                        string json = JsonSerializer.Serialize(response);

                        TestAssert.True(json.Contains("\"result\":\"pong\""), "Serialized response should include result.");
                        TestAssert.True(json.Contains("\"id\":\"abc\""), "Serialized response should include id.");
                        TestAssert.False(json.Contains("\"error\""), "Null error should be omitted.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResponseErrorSerialization", "JsonRpcResponse serializes error without result", ct =>
                    {
                        JsonRpcResponse response = new JsonRpcResponse
                        {
                            Error = JsonRpcError.MethodNotFound(),
                            Id = 1
                        };

                        string json = JsonSerializer.Serialize(response);
                        TestAssert.True(json.Contains("\"error\""), "Serialized response should include error.");
                        TestAssert.False(json.Contains("\"result\""), "Null result should be omitted.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "JsonRpcErrorFactories", "JsonRpcError factories use standard codes", ct =>
                    {
                        TestAssert.Equal(-32700, JsonRpcError.ParseError().Code);
                        TestAssert.Equal(-32600, JsonRpcError.InvalidRequest().Code);
                        TestAssert.Equal(-32601, JsonRpcError.MethodNotFound().Code);
                        TestAssert.Equal(-32602, JsonRpcError.InvalidParams().Code);
                        TestAssert.Equal(-32603, JsonRpcError.InternalError().Code);
                        return Task.CompletedTask;
                    }),
                });
        }

        public static TestSuiteDescriptor EventArgsAndSupportingModels()
        {
            const string suiteId = "ModelApi.Supporting";

            return new TestSuiteDescriptor(
                suiteId,
                "Supporting Model API",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "AuthenticationResultDefaults", "AuthenticationResult exposes expected defaults", ct =>
                    {
                        AuthenticationResult result = new AuthenticationResult();
                        TestAssert.False(result.IsAuthenticated, "Authentication should default to false.");
                        TestAssert.Equal(401, result.StatusCode);
                        TestAssert.Null(result.Principal, "Principal should default to null.");
                        TestAssert.Null(result.Claims, "Claims should default to null.");
                        TestAssert.Null(result.ErrorMessage, "Error message should default to null.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ToolDefinitionDefaults", "ToolDefinition exposes expected defaults", ct =>
                    {
                        ToolDefinition tool = new ToolDefinition();
                        TestAssert.Equal(string.Empty, tool.Name);
                        TestAssert.Equal(string.Empty, tool.Description);
                        TestAssert.NotNull(tool.InputSchema, "Input schema should default to a non-null object.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "RequestSentEventArgs", "RequestSentEventArgs projects request state", ct =>
                    {
                        JsonRpcRequest request = new JsonRpcRequest { Method = "notify" };
                        RequestSentEventArgs args = new RequestSentEventArgs(request);

                        TestAssert.Equal(request, args.Request);
                        TestAssert.Equal("notify", args.Method);
                        TestAssert.True(args.IsNotification, "Request with no id should be a notification.");
                        TestAssert.Null(args.RequestId, "Notification id should be null.");
                        TestAssert.True(args.SentUtc <= DateTime.UtcNow, "Sent timestamp should be set.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ResponseReceivedEventArgs", "ResponseReceivedEventArgs calculates duration and success state", ct =>
                    {
                        DateTime sentUtc = DateTime.UtcNow.AddMilliseconds(-10);
                        JsonRpcRequest request = new JsonRpcRequest { Method = "ping", Id = 1 };
                        JsonRpcResponse response = new JsonRpcResponse { Result = "pong", Id = 1 };
                        ResponseReceivedEventArgs args = new ResponseReceivedEventArgs(request, response, sentUtc);

                        TestAssert.Equal(request, args.Request);
                        TestAssert.Equal(response, args.Response);
                        TestAssert.Equal("ping", args.Method);
                        TestAssert.Equal(1, args.RequestId);
                        TestAssert.True(args.IsSuccess, "Response should be successful.");
                        TestAssert.False(args.IsError, "Response should not be an error.");
                        TestAssert.True(args.Duration >= TimeSpan.Zero, "Duration should be non-negative.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ClientConnectedEventArgs", "ClientConnectedEventArgs validates endpoint and exposes state", ct =>
                    {
                        ClientConnectedEventArgs args = new ClientConnectedEventArgs("localhost:1", ClientConnectionTypeEnum.Tcp);
                        TestAssert.Equal("localhost:1", args.Endpoint);
                        TestAssert.Equal(ClientConnectionTypeEnum.Tcp, args.ConnectionType);
                        TestAssert.True(args.ConnectedUtc <= DateTime.UtcNow, "Connected timestamp should be set.");
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnectedEventArgs(null!, ClientConnectionTypeEnum.Tcp), "Null endpoint should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ClientDisconnectedEventArgs", "ClientDisconnectedEventArgs validates endpoint and reason defaults", ct =>
                    {
                        DateTime connectedUtc = DateTime.UtcNow.AddSeconds(-1);
                        ClientDisconnectedEventArgs args = new ClientDisconnectedEventArgs(connectedUtc, "endpoint", ClientConnectionTypeEnum.Http, null!);

                        TestAssert.Equal("endpoint", args.Endpoint);
                        TestAssert.Equal("Unknown", args.Reason);
                        TestAssert.Equal(ClientConnectionTypeEnum.Http, args.ConnectionType);
                        TestAssert.True(args.ConnectionDuration >= TimeSpan.Zero, "Duration should be non-negative.");
                        TestAssert.Throws<ArgumentNullException>(() => new ClientDisconnectedEventArgs(connectedUtc, null!, ClientConnectionTypeEnum.Http, "x"), "Null endpoint should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "JsonRpcEventArgs", "JSON-RPC event args project request and response state", ct =>
                    {
                        ClientConnection connection = new ClientConnection("session");
                        JsonRpcRequest request = new JsonRpcRequest { Method = "method", Id = 2 };
                        JsonRpcResponse response = new JsonRpcResponse { Error = JsonRpcError.InternalError(), Id = 2 };

                        JsonRpcRequestEventArgs requestArgs = new JsonRpcRequestEventArgs(connection, request);
                        JsonRpcResponseEventArgs responseArgs = new JsonRpcResponseEventArgs(connection, request, response, DateTime.UtcNow.AddMilliseconds(-5));

                        TestAssert.Equal(connection, requestArgs.Client);
                        TestAssert.Equal("method", requestArgs.Method);
                        TestAssert.False(requestArgs.IsNotification, "Request with id should not be notification.");
                        TestAssert.Equal(connection, responseArgs.Client);
                        TestAssert.True(responseArgs.IsError, "Response should be error.");
                        TestAssert.False(responseArgs.IsSuccess, "Error response should not be success.");
                        connection.Dispose();
                        return Task.CompletedTask;
                    }),
                });
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "model" });
        }
    }
}
