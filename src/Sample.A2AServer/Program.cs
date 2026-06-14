namespace Sample.A2AServer
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.A2A;

    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            int port = args.Length > 0 && Int32.TryParse(args[0], out int parsed) ? parsed : 8080;
            int grpcPort = port + 1;
            string baseUrl = $"http://localhost:{port}";
            string rpcUrl = $"{baseUrl}/a2a";
            string grpcUrl = $"http://localhost:{grpcPort}";

            AgentCard publicCard = CreateAgentCard("Voltaic Sample A2A Agent", baseUrl, rpcUrl, grpcUrl);
            AgentCard extendedCard = CreateAgentCard("Voltaic Sample A2A Agent (Extended)", baseUrl, rpcUrl, grpcUrl);
            extendedCard.Description = "Extended Agent Card containing internal/debug skill metadata.";
            extendedCard.Skills.Add(new AgentSkill
            {
                Id = "debug",
                Name = "Debug echo",
                Description = "Returns request context and task identifiers for integration testing.",
                Tags = new List<string> { "debug", "sample" }
            });

            InMemoryA2ATaskStore taskStore = new InMemoryA2ATaskStore();
            SampleAgent agent = new SampleAgent();
            using A2AHttpServer server = new A2AHttpServer("localhost", port, publicCard, agent, taskStore)
            {
                ExtendedAgentCard = extendedCard,
                EnableCors = true
            };
            server.Log += (_, message) => Console.WriteLine(message);

            using A2AGrpcServer grpcServer = new A2AGrpcServer("localhost", grpcPort, publicCard, agent, taskStore)
            {
                ExtendedAgentCard = extendedCard
            };
            grpcServer.Log += (_, message) => Console.WriteLine(message);

            await server.StartAsync().ConfigureAwait(false);
            await grpcServer.StartAsync().ConfigureAwait(false);

            using CancellationTokenSource shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                shutdown.Cancel();
            };

            Console.WriteLine("A2A sample server running.");
            Console.WriteLine($"  Agent Card: {baseUrl}{A2AProtocol.AgentCardPath}");
            Console.WriteLine($"  JSON-RPC:   {rpcUrl}");
            Console.WriteLine($"  HTTP+JSON:  {baseUrl}");
            Console.WriteLine($"  gRPC:       {grpcUrl}");

            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("Press Enter or Ctrl+C to stop.");
                _ = Task.Run(() =>
                {
                    Console.ReadLine();
                    shutdown.Cancel();
                });
            }
            else
            {
                Console.WriteLine("Press Ctrl+C to stop.");
            }

            try
            {
                await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                server.Stop();
                grpcServer.Stop();
            }
        }

        private static AgentCard CreateAgentCard(string name, string baseUrl, string rpcUrl, string grpcUrl)
        {
            return new AgentCard
            {
                Name = name,
                Description = "A dependency-light Voltaic A2A sample agent.",
                Version = "1.0.0",
                SupportedInterfaces = new List<AgentInterface>
                {
                    new AgentInterface
                    {
                        Url = rpcUrl,
                        ProtocolBinding = "JSONRPC",
                        ProtocolVersion = A2AProtocol.ProtocolVersion
                    },
                    new AgentInterface
                    {
                        Url = baseUrl,
                        ProtocolBinding = "HTTP+JSON",
                        ProtocolVersion = A2AProtocol.ProtocolVersion
                    },
                    new AgentInterface
                    {
                        Url = grpcUrl,
                        ProtocolBinding = "GRPC",
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
                Provider = new AgentProvider
                {
                    Organization = "Voltaic"
                },
                Skills = new List<AgentSkill>
                {
                    new AgentSkill
                    {
                        Id = "echo",
                        Name = "Echo",
                        Description = "Echoes text back as an A2A task artifact and final message.",
                        Tags = new List<string> { "echo", "sample" },
                        Examples = new List<string> { "Say hello from A2A" }
                    }
                },
                DefaultInputModes = new List<string> { "text/plain" },
                DefaultOutputModes = new List<string> { "text/plain" }
            };
        }

        private sealed class SampleAgent : IA2AAgentHandler
        {
            public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(token: token).ConfigureAwait(false);
                await updater.StartAsync(token: token).ConfigureAwait(false);

                string text = context.Message.Parts.Count > 0
                    ? context.Message.Parts[0].Text ?? string.Empty
                    : string.Empty;

                await updater.AddArtifactAsync(new Artifact
                {
                    ArtifactId = "echo-artifact",
                    Name = "Echo Artifact",
                    Description = "The text received by the sample agent.",
                    Parts = new List<Part> { Part.FromText(text) }
                }, token: token).ConfigureAwait(false);

                Message response = new Message
                {
                    Role = Role.Agent,
                    MessageId = Guid.NewGuid().ToString("N"),
                    TaskId = context.TaskId,
                    ContextId = context.ContextId,
                    Parts = new List<Part>
                    {
                        Part.FromText($"Echo from Voltaic A2A: {text}")
                    }
                };

                await updater.CompleteAsync(response, token).ConfigureAwait(false);
            }
        }
    }
}
