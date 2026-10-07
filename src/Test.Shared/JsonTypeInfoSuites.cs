namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Text.Json;
    using System.Text.Json.Serialization.Metadata;
    using System.Threading;
    using System.Threading.Tasks;
    using Touchstone.Core;
    using Voltaic.A2A;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Proves the trimming and Native AOT JSON contract: <see cref="VoltaicJson"/> validates its input and consults
    /// application resolvers in order, and every type Voltaic serializes comes from its source-generated metadata.
    /// A recording resolver added after Voltaic's metadata sees only the types that metadata lacks; no Voltaic type
    /// may reach it. The Native AOT round trips themselves run in <c>src/Test.Aot</c>.
    /// </summary>
    public static class JsonTypeInfoSuites
    {
        private static readonly RecordingResolver _Recorder = new RecordingResolver();

        public static TestSuiteDescriptor Cases()
        {
            const string suiteId = "Json.TypeInfo";

            return new TestSuiteDescriptor(
                suiteId,
                "Source-generated JSON metadata for trimming and Native AOT",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "AddTypeInfoResolverValidates", "AddTypeInfoResolver rejects null, and TypeInfoResolver is never null", ct =>
                    {
                        AssertThrows<ArgumentNullException>(() => VoltaicJson.AddTypeInfoResolver(null!));
                        Assert(VoltaicJson.TypeInfoResolver != null, "TypeInfoResolver is null.");
                        return Task.CompletedTask;
                    }),
                    Case(suiteId, "ApplicationResolversAreConsultedOnce", "Application resolvers supply types Voltaic does not know; adding one twice has no effect", ct =>
                    {
                        CountingResolver counting = new CountingResolver(typeof(ApplicationPoint));
                        VoltaicJson.AddTypeInfoResolver(counting);
                        VoltaicJson.AddTypeInfoResolver(counting);

                        JsonSerializerOptions options = new JsonSerializerOptions { TypeInfoResolver = VoltaicJson.TypeInfoResolver };
                        string json = JsonSerializer.Serialize(new ApplicationPoint { X = 3 }, options.GetTypeInfo(typeof(ApplicationPoint)));
                        Assert(json == "{\"X\":3}", "Unexpected JSON " + json);
                        Assert(counting.Calls == 1, "The resolver was consulted " + counting.Calls + " times; expected once.");

                        options.GetTypeInfo(typeof(McpToolCallResult));
                        Assert(counting.Calls == 1, "A Voltaic type reached the application resolver.");
                        return Task.CompletedTask;
                    }),
                    Case(suiteId, "EveryVoltaicModelIsSourceGenerated", "Every exported Voltaic model, and the values Voltaic builds itself, serialize from source-generated metadata", ct =>
                    {
                        VoltaicJson.AddTypeInfoResolver(_Recorder);
                        JsonSerializerOptions options = new JsonSerializerOptions { TypeInfoResolver = VoltaicJson.TypeInfoResolver };

                        Type[] models = typeof(JsonRpcClient).Assembly.GetExportedTypes()
                            .Where(type => type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition
                                && !typeof(Exception).IsAssignableFrom(type) && !typeof(IDisposable).IsAssignableFrom(type)
                                && !typeof(EventArgs).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null
                                && IsModelNamespace(type))
                            .ToArray();
                        Assert(models.Length > 80, "Only " + models.Length + " model types were found.");

                        foreach (Type model in models)
                        {
                            object instance = Activator.CreateInstance(model)!;
                            JsonSerializer.Serialize(instance, options.GetTypeInfo(typeof(object)));
                        }

                        object[] built =
                        {
                            new ToolDefinition { Name = "t" },
                            McpProtocolException.InvalidCursor("c").ErrorData!,
                            McpProtocolException.UnsupportedProtocolVersion("v", new[] { "1" }).ErrorData!,
                            McpProtocolException.UnsupportedVersion("v").ErrorData!,
                            McpProtocolException.InvalidSession("s").ErrorData!,
                            McpProtocolException.MissingRequiredClientCapability("m").ErrorData!,
                            new McpProtocolException(-32021, "m").ErrorData!,
                            new McpInsufficientScopeException("s").ErrorData!,
                            A2AProtocolException.TaskNotFound("t").ErrorData!
                        };

                        foreach (object value in built)
                        {
                            JsonSerializer.Serialize(value, options.GetTypeInfo(typeof(object)));
                        }

                        AssertNoVoltaicTypes();
                        return Task.CompletedTask;
                    }),
                    Case(suiteId, "McpRoundTripIsSourceGenerated", "A TCP MCP session (initialize, tools, resources, prompts, errors) serializes only source-generated Voltaic types", async ct =>
                    {
                        VoltaicJson.AddTypeInfoResolver(_Recorder);
                        int port = FreePort();
                        using McpTcpServer server = new McpTcpServer(IPAddress.Loopback, port, includeDiagnosticTools: true);
                        server.RegisterTool("sum", "Adds", new Dictionary<string, object?> { { "type", "object" } },
                            args => McpToolCallResult.FromStructured(new Dictionary<string, object?> { { "total", 3 } }));
                        server.RegisterResource("test://r", "r", "text/plain", () => new McpReadResourceResult
                        {
                            Contents = new List<object> { new McpTextResourceContents { Uri = "test://r", Text = "x" } }
                        });
                        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        Task serverTask = server.StartAsync(stop.Token);

                        using McpTcpClient client = new McpTcpClient();
                        Assert(await client.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false), "Connect failed.");
                        await client.CallAsync<McpListToolsResult>("tools/list", null, 10000, ct).ConfigureAwait(false);
                        await client.CallAsync<McpToolCallResult>("tools/call", new Dictionary<string, object?> { { "name", "sum" }, { "arguments", new Dictionary<string, object?>() } }, 10000, ct).ConfigureAwait(false);
                        await client.CallAsync<McpToolCallResult>("tools/call", new Dictionary<string, object?> { { "name", "echo" }, { "arguments", new Dictionary<string, object?> { { "message", "m" } } } }, 10000, ct).ConfigureAwait(false);
                        await client.CallAsync<McpReadResourceResult>("resources/read", new Dictionary<string, object?> { { "uri", "test://r" } }, 10000, ct).ConfigureAwait(false);
                        try
                        {
                            await client.CallAsync<JsonElement>("resources/read", new Dictionary<string, object?> { { "uri", "test://missing" } }, 10000, ct).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                        }

                        client.Disconnect();
                        server.Stop();
                        stop.Cancel();
                        await Task.WhenAny(serverTask, Task.Delay(5000, CancellationToken.None)).ConfigureAwait(false);
                        AssertNoVoltaicTypes();
                    }),
                });
        }

        private static readonly Type[] _NotModels =
        {
            typeof(A2AAgentEventQueue),
            typeof(A2ARequestContext),
            typeof(InMemoryA2ATaskStore),
            typeof(McpRateLimits)
        };

        private static bool IsModelNamespace(Type type)
        {
            if (_NotModels.Contains(type)) return false;
            return type.Namespace == "Voltaic.Mcp" || type.Namespace == "Voltaic.A2A"
                || type == typeof(JsonRpcRequest) || type == typeof(JsonRpcResponse) || type == typeof(JsonRpcError);
        }

        private static void AssertNoVoltaicTypes()
        {
            string[] missed = _Recorder.Types
                .Where(type => !type.IsInterface && !type.IsAbstract && (type.Assembly == typeof(JsonRpcClient).Assembly
                    || (type.IsGenericType && type.GetGenericArguments().Any(argument => argument.Assembly == typeof(JsonRpcClient).Assembly))))
                .Select(type => type.FullName ?? type.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert(missed.Length == 0, "Voltaic types without source-generated metadata: " + string.Join(", ", missed));
        }

        private static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertThrows<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
        }

        private static TestCaseDescriptor Case(string suiteId, string caseId, string displayName, Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "json", "aot" });
        }

        /// <summary>
        /// A type only an application resolver knows.
        /// </summary>
        public sealed class ApplicationPoint
        {
            public int X { get; set; }
        }

        /// <summary>
        /// Records every type it is asked for and supplies none.
        /// </summary>
        private sealed class RecordingResolver : IJsonTypeInfoResolver
        {
            private readonly ConcurrentDictionary<Type, bool> _Types = new ConcurrentDictionary<Type, bool>();

            internal IEnumerable<Type> Types => _Types.Keys;

            public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
            {
                _Types[type] = true;
                return null;
            }
        }

        /// <summary>
        /// Supplies reflection metadata for one type and counts how often it is asked for anything.
        /// </summary>
        private sealed class CountingResolver : IJsonTypeInfoResolver
        {
            private readonly Type _Type;
            private readonly DefaultJsonTypeInfoResolver _Inner = new DefaultJsonTypeInfoResolver();
            private int _Calls = 0;

            internal CountingResolver(Type type)
            {
                _Type = type;
            }

            internal int Calls => Volatile.Read(ref _Calls);

            public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
            {
                Interlocked.Increment(ref _Calls);
                return type == _Type ? _Inner.GetTypeInfo(type, options) : null;
            }
        }
    }
}
