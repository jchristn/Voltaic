namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.A2A;

    /// <summary>
    /// A minimal <see cref="A2AHttpServer"/> on a free loopback port, for tests that exercise the HTTP gate
    /// (origin, CORS, loopback, authentication) rather than agent behavior. No agent handler is registered.
    /// </summary>
    internal sealed class A2AServerFixture : IAsyncDisposable
    {
        private readonly A2AHttpServer _Server;
        private readonly CancellationTokenSource _TokenSource;

        private A2AServerFixture(A2AHttpServer server, int port, CancellationTokenSource tokenSource)
        {
            _Server = server;
            _TokenSource = tokenSource;
            Port = port;
            Client = new HttpClient();
        }

        public int Port { get; }

        public HttpClient Client { get; }

        public string BaseUrl => $"http://localhost:{Port}";

        public static async Task<A2AServerFixture> StartAsync(CancellationToken token, Action<A2AHttpServer>? configure = null)
        {
            int port = TestPorts.GetFreePort();
            AgentCard card = new AgentCard
            {
                Name = "Security Test Agent",
                Description = "Exercises the A2A HTTP gate.",
                Version = "1.0.0",
                SupportedInterfaces = new List<AgentInterface>
                {
                    new AgentInterface { Url = $"http://localhost:{port}/a2a", ProtocolBinding = "JSONRPC", ProtocolVersion = A2AProtocol.ProtocolVersion }
                },
                Capabilities = new AgentCapabilities()
            };

            A2AHttpServer server = new A2AHttpServer("localhost", port, card);
            configure?.Invoke(server);
            CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            await server.StartAsync(tokenSource.Token).ConfigureAwait(false);
            return new A2AServerFixture(server, port, tokenSource);
        }

        public async ValueTask DisposeAsync()
        {
            _Server.Stop();
            _TokenSource.Cancel();
            Client.Dispose();
            await Task.Delay(20).ConfigureAwait(false);
            _TokenSource.Dispose();
            _Server.Dispose();
        }
    }
}
