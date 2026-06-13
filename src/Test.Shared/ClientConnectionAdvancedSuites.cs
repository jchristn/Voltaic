namespace Test.Shared
{
    using System.Net;
    using System.Net.Sockets;
    using Touchstone.Core;
    using Voltaic;

    public static class ClientConnectionAdvancedSuites
    {
        public static TestSuiteDescriptor ComprehensiveQueueAndLifecycle()
        {
            const string suiteId = "ClientConnection.Matrix";

            return new TestSuiteDescriptor(
                suiteId,
                "ClientConnection Comprehensive Matrix",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "DefaultQueueSize", "ClientConnection defaults to a queue size of 100", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        TestAssert.Equal(100, connection.MaxQueueSize);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "TypedConstructorAllEnumValues", "Typed constructor accepts every connection type enum value", ct =>
                    {
                        foreach (ClientConnectionTypeEnum type in Enum.GetValues<ClientConnectionTypeEnum>())
                        {
                            using ClientConnection connection = new ClientConnection($"session-{type}", type);
                            TestAssert.Equal(type, connection.Type);
                            TestAssert.Equal($"session-{type}", connection.SessionId);
                        }

                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "TcpConstructor", "TCP constructor exposes TcpClient and NetworkStream", async ct =>
                    {
                        int port = TestPorts.GetFreePort();
                        TcpListener listener = new TcpListener(IPAddress.Loopback, port);
                        listener.Start();
                        try
                        {
                            using TcpClient client = new TcpClient();
                            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
                            await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                            TcpClient accepted = await acceptTask.ConfigureAwait(false);

                            using ClientConnection connection = new ClientConnection("tcp-session", accepted);
                            TestAssert.Equal(ClientConnectionTypeEnum.Tcp, connection.Type);
                            TestAssert.Equal("tcp-session", connection.SessionId);
                            TestAssert.NotNull(connection.TcpClient, "TCP client should be exposed.");
                            TestAssert.NotNull(connection.Stream, "Network stream should be exposed.");
                            TestAssert.Null(connection.WebSocket, "TCP connection should not expose WebSocket.");
                        }
                        finally
                        {
                            listener.Stop();
                        }
                    }),

                    Case(suiteId, "TcpConstructorValidation", "TCP constructor validates required inputs", ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection(null!, new TcpClient()), "Null session should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection("", new TcpClient()), "Empty session should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection("session", (TcpClient)null!), "Null TCP client should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "WebSocketConstructor", "WebSocket constructor exposes WebSocket and token source", ct =>
                    {
                        TestWebSocket webSocket = new TestWebSocket();
                        using ClientConnection connection = new ClientConnection("ws-session", webSocket);

                        TestAssert.Equal(ClientConnectionTypeEnum.Websockets, connection.Type);
                        TestAssert.Equal("ws-session", connection.SessionId);
                        TestAssert.Equal(webSocket, connection.WebSocket);
                        TestAssert.NotNull(connection.TokenSource, "WebSocket connection should create a token source.");
                        TestAssert.Null(connection.TcpClient, "WebSocket connection should not expose TCP client.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "WebSocketConstructorValidation", "WebSocket constructor validates required inputs", ct =>
                    {
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection(null!, new TestWebSocket()), "Null session should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection("", new TestWebSocket()), "Empty session should fail.");
                        TestAssert.Throws<ArgumentNullException>(() => new ClientConnection("session", (System.Net.WebSockets.WebSocket)null!), "Null WebSocket should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "EnqueueNullValidation", "Enqueue rejects null notifications", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        TestAssert.Throws<ArgumentNullException>(() => connection.Enqueue(null!), "Null notification should fail.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "DequeueAllPreservesOrder", "DequeueAll returns FIFO order and clears the queue", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        connection.Enqueue(new JsonRpcRequest { Method = "one" });
                        connection.Enqueue(new JsonRpcRequest { Method = "two" });
                        connection.Enqueue(new JsonRpcRequest { Method = "three" });

                        List<JsonRpcRequest> all = connection.DequeueAll();
                        TestAssert.Equal(3, all.Count);
                        TestAssert.Equal("one", all[0].Method);
                        TestAssert.Equal("two", all[1].Method);
                        TestAssert.Equal("three", all[2].Method);
                        TestAssert.Equal(0, connection.Count());
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "LastActivityUpdatesOnEnqueue", "LastActivity updates when a notification is enqueued", async ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        DateTime initial = connection.LastActivity;
                        await Task.Delay(10, ct).ConfigureAwait(false);

                        connection.Enqueue(new JsonRpcRequest { Method = "event" });

                        TestAssert.True(connection.LastActivity > initial, "Enqueue should update LastActivity.");
                    }),

                    Case(suiteId, "LastActivityUpdatesOnDequeueAsync", "LastActivity updates when a notification is dequeued", async ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        connection.Enqueue(new JsonRpcRequest { Method = "event" });
                        DateTime afterEnqueue = connection.LastActivity;
                        await Task.Delay(10, ct).ConfigureAwait(false);

                        JsonRpcRequest? request = await connection.DequeueAsync(ct).ConfigureAwait(false);

                        TestAssert.NotNull(request, "Request should be dequeued.");
                        TestAssert.True(connection.LastActivity > afterEnqueue, "Dequeue should update LastActivity.");
                    }),

                    Case(suiteId, "LastActivityUpdatesOnDequeueAll", "LastActivity updates when DequeueAll is called", async ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        DateTime initial = connection.LastActivity;
                        await Task.Delay(10, ct).ConfigureAwait(false);

                        List<JsonRpcRequest> all = connection.DequeueAll();

                        TestAssert.Equal(0, all.Count);
                        TestAssert.True(connection.LastActivity > initial, "DequeueAll should update LastActivity even when the queue is empty.");
                    }),

                    Case(suiteId, "QueueLimitOneKeepsNewest", "Queue limit of one keeps only the newest notification", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session") { MaxQueueSize = 1 };
                        connection.Enqueue(new JsonRpcRequest { Method = "old" });
                        connection.Enqueue(new JsonRpcRequest { Method = "new" });

                        List<JsonRpcRequest> all = connection.DequeueAll();
                        TestAssert.Equal(1, all.Count);
                        TestAssert.Equal("new", all[0].Method);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ChangingQueueLimitDoesNotDropUntilNextEnqueue", "Changing MaxQueueSize trims on the next enqueue", ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session");
                        connection.Enqueue(new JsonRpcRequest { Method = "one" });
                        connection.Enqueue(new JsonRpcRequest { Method = "two" });
                        connection.MaxQueueSize = 2;
                        TestAssert.Equal(2, connection.Count());

                        connection.MaxQueueSize = 1;
                        TestAssert.Equal(2, connection.Count());

                        connection.Enqueue(new JsonRpcRequest { Method = "three" });
                        List<JsonRpcRequest> all = connection.DequeueAll();
                        TestAssert.Equal(1, all.Count);
                        TestAssert.Equal("three", all[0].Method);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "DequeueAsyncAfterDisposeReturnsNull", "DequeueAsync returns null after disposal", async ct =>
                    {
                        ClientConnection connection = new ClientConnection("session");
                        connection.Dispose();

                        JsonRpcRequest? request = await connection.DequeueAsync(ct).ConfigureAwait(false);

                        TestAssert.Null(request, "Disposed connection should not dequeue.");
                    }),

                    Case(suiteId, "CountAfterDisposeRemainsReadable", "Count remains readable after disposal", ct =>
                    {
                        ClientConnection connection = new ClientConnection("session");
                        connection.Enqueue(new JsonRpcRequest { Method = "queued" });
                        connection.Dispose();

                        TestAssert.Equal(1, connection.Count());
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "WebSocketDisposeDisposesSocketAndToken", "Disposing a WebSocket connection disposes owned resources", ct =>
                    {
                        TestWebSocket webSocket = new TestWebSocket();
                        ClientConnection connection = new ClientConnection("session", webSocket);
                        CancellationTokenSource? tokenSource = connection.TokenSource;

                        connection.Dispose();

                        TestAssert.True(webSocket.Disposed, "WebSocket should be disposed.");
                        TestAssert.Throws<ObjectDisposedException>(() => tokenSource!.Token.Register(() => { }).Dispose(), "Token source should be disposed.");
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "ConcurrentEnqueue", "Concurrent Enqueue calls keep all notifications up to the queue limit", async ct =>
                    {
                        using ClientConnection connection = new ClientConnection("session") { MaxQueueSize = 200 };

                        await Task.WhenAll(Enumerable.Range(0, 100)
                            .Select(i => Task.Run(() => connection.Enqueue(new JsonRpcRequest { Method = $"m{i}" }), ct))).ConfigureAwait(false);

                        TestAssert.Equal(100, connection.Count());
                        TestAssert.Equal(100, connection.DequeueAll().Count);
                    }),
                });
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "connection", "matrix" });
        }
    }
}
