namespace Test.Shared
{
    using Touchstone.Core;
    using Voltaic.Core;
    using Voltaic.Mcp;
    public static class ClientConnectionSuites
    {
        public static TestSuiteDescriptor QueueAndLifecycle()
        {
            const string suiteId = "ClientConnection";

            return new TestSuiteDescriptor(
                suiteId,
                "ClientConnection API",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "HttpConstructor", "HTTP constructor sets session and connection type", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session-1");
                        TestAssert.Equal("session-1", connection.SessionId);
                        TestAssert.Equal(ClientConnectionTypeEnum.Http, connection.Type);
                        TestAssert.Equal(0, connection.Count());
                        TestAssert.Null(connection.TcpClient, "HTTP connection should not expose TCP client.");
                        TestAssert.Null(connection.Stream, "HTTP connection should not expose stream.");
                        TestAssert.Null(connection.WebSocket, "HTTP connection should not expose WebSocket.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "TypedConstructor", "Typed constructor preserves requested connection type", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("stdio-1", ClientConnectionTypeEnum.Stdio);
                        TestAssert.Equal(ClientConnectionTypeEnum.Stdio, connection.Type);
                        TestAssert.Equal("stdio-1", connection.SessionId);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ConstructorValidation", "Constructors reject null or empty session IDs", ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection(null!), "Null session should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection(""), "Empty session should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection(null!, ClientConnectionTypeEnum.Http), "Null typed session should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "MaxQueueSizeValidation", "MaxQueueSize enforces minimum value", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        connection.MaxQueueSize = 1;
                        TestAssert.Equal(1, connection.MaxQueueSize);
                        TestAssert.Throws<ArgumentOutOfRangeException>(() => connection.MaxQueueSize = 0, "Zero queue size should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "EnqueueDequeue", "Enqueue and DequeueAsync preserve notifications", async ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        JsonRpcRequest request = new JsonRpcRequest { Method = "notification" };

                        connection.Enqueue(request);
                        TestAssert.Equal(1, connection.Count());

                        JsonRpcRequest? dequeued = await connection.DequeueAsync(ct).ConfigureAwait(false);
                        TestAssert.Equal(request, dequeued);
                        TestAssert.Equal(0, connection.Count());
                    }),

                    Case(suiteId, "QueueLimitDropsOldest", "Queue limit drops oldest notifications", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session") { MaxQueueSize = 2 };
                        connection.Enqueue(new JsonRpcRequest { Method = "one" });
                        connection.Enqueue(new JsonRpcRequest { Method = "two" });
                        connection.Enqueue(new JsonRpcRequest { Method = "three" });

                        List<JsonRpcRequest> all = connection.DequeueAll();
                        TestAssert.Equal(2, all.Count);
                        TestAssert.Equal("two", all[0].Method);
                        TestAssert.Equal("three", all[1].Method);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "DequeueCancellation", "DequeueAsync observes cancellation", async ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        using CancellationTokenSource cts = new CancellationTokenSource(10);

                        await TestAssert.ThrowsAsync<OperationCanceledException>(
                            () => connection.DequeueAsync(cts.Token),
                            "Cancelled dequeue should throw.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "DisposeIdempotency", "Dispose is idempotent and enqueue after dispose fails", ct =>
                    {
                        ClientConnection connection = new ClientConnection("session");
                        connection.Dispose();
                        connection.Dispose();

                        TestAssert.Throws<ObjectDisposedException>(
                            () => connection.Enqueue(new JsonRpcRequest { Method = "x" }),
                            "Enqueue after dispose should fail.");
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
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "connection" });
        }
    }
}
