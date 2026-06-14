namespace Test.A2AServer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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

            AgentCard publicCard = CreateAgentCard("Voltaic Test A2A Server", baseUrl, rpcUrl, grpcUrl);
            AgentCard extendedCard = CreateAgentCard("Voltaic Test A2A Server (Extended)", baseUrl, rpcUrl, grpcUrl);
            extendedCard.Description = "Extended Agent Card for manual Voltaic A2A server testing.";
            extendedCard.Skills.Add(new AgentSkill
            {
                Id = "diagnostics",
                Name = "Diagnostics",
                Description = "Returns task and context identifiers for manual client testing.",
                Tags = new List<string> { "diagnostics", "manual" }
            });

            InMemoryA2ATaskStore taskStore = new InMemoryA2ATaskStore();
            ManualAgent agent = new ManualAgent();
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

            PrintBanner(baseUrl, rpcUrl, grpcUrl);

            if (Console.IsInputRedirected)
            {
                Console.WriteLine("Input is redirected; press Ctrl+C to stop.");
                await WaitForShutdownAsync(shutdown.Token).ConfigureAwait(false);
            }
            else
            {
                await RunCommandLoopAsync(server, shutdown).ConfigureAwait(false);
            }

            server.Stop();
            grpcServer.Stop();
        }

        private static AgentCard CreateAgentCard(string name, string baseUrl, string rpcUrl, string grpcUrl)
        {
            return new AgentCard
            {
                Name = name,
                Description = "Manual A2A server for Voltaic client and compatibility testing.",
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
                        Id = "manual-echo",
                        Name = "Manual Echo",
                        Description = "Echoes text, emits artifacts, and supports slow/fail/input-required trigger words.",
                        Tags = new List<string> { "echo", "manual", "test" },
                        Examples = new List<string>
                        {
                            "hello",
                            "slow hello",
                            "fail this task",
                            "input required"
                        }
                    }
                },
                DefaultInputModes = new List<string> { "text/plain" },
                DefaultOutputModes = new List<string> { "text/plain" }
            };
        }

        private static async Task RunCommandLoopAsync(A2AHttpServer server, CancellationTokenSource shutdown)
        {
            Console.WriteLine("Commands: help, tasks, task <id>, configs <taskId>, push <taskId> <configId> <url>, stop");

            while (!shutdown.IsCancellationRequested)
            {
                Console.Write("> ");
                string? input = Console.ReadLine();
                if (String.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string command = parts[0].ToLowerInvariant();

                try
                {
                    switch (command)
                    {
                        case "help":
                            PrintHelp();
                            break;

                        case "tasks":
                            await PrintTasksAsync(server, shutdown.Token).ConfigureAwait(false);
                            break;

                        case "task":
                            await PrintTaskAsync(server, parts, shutdown.Token).ConfigureAwait(false);
                            break;

                        case "configs":
                            await PrintPushConfigsAsync(server, parts, shutdown.Token).ConfigureAwait(false);
                            break;

                        case "push":
                            await CreatePushConfigAsync(server, parts, shutdown.Token).ConfigureAwait(false);
                            break;

                        case "stop":
                        case "exit":
                        case "quit":
                            shutdown.Cancel();
                            break;

                        default:
                            Console.WriteLine("Unknown command. Type 'help' for command list.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }

        private static async Task PrintTasksAsync(A2AHttpServer server, CancellationToken token)
        {
            ListTasksResponse response = await server.ListTasksAsync(new ListTasksRequest { PageSize = 100 }, token).ConfigureAwait(false);
            if (response.Tasks.Count == 0)
            {
                Console.WriteLine("No tasks.");
                return;
            }

            foreach (AgentTask task in response.Tasks)
            {
                Console.WriteLine($"{task.Id}  context={task.ContextId}  state={task.Status.State}  history={task.History?.Count ?? 0}  artifacts={task.Artifacts?.Count ?? 0}");
            }
        }

        private static async Task PrintTaskAsync(A2AHttpServer server, string[] parts, CancellationToken token)
        {
            if (parts.Length < 2)
            {
                Console.WriteLine("Usage: task <id>");
                return;
            }

            AgentTask task = await server.GetTaskAsync(new GetTaskRequest { Id = parts[1] }, token).ConfigureAwait(false);
            Console.WriteLine($"Task: {task.Id}");
            Console.WriteLine($"Context: {task.ContextId}");
            Console.WriteLine($"State: {task.Status.State}");
            Console.WriteLine($"History: {task.History?.Count ?? 0}");
            Console.WriteLine($"Artifacts: {task.Artifacts?.Count ?? 0}");
        }

        private static async Task PrintPushConfigsAsync(A2AHttpServer server, string[] parts, CancellationToken token)
        {
            if (parts.Length < 2)
            {
                Console.WriteLine("Usage: configs <taskId>");
                return;
            }

            ListTaskPushNotificationConfigResponse configs = await server.ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest
            {
                TaskId = parts[1]
            }, token).ConfigureAwait(false);

            if (configs.Configs.Count == 0)
            {
                Console.WriteLine("No push configs.");
                return;
            }

            foreach (TaskPushNotificationConfig config in configs.Configs)
            {
                Console.WriteLine($"{config.Id}  task={config.TaskId}  url={config.PushNotificationConfig.Url}");
            }
        }

        private static async Task CreatePushConfigAsync(A2AHttpServer server, string[] parts, CancellationToken token)
        {
            if (parts.Length < 4)
            {
                Console.WriteLine("Usage: push <taskId> <configId> <url>");
                return;
            }

            TaskPushNotificationConfig config = await server.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest
            {
                TaskId = parts[1],
                ConfigId = parts[2],
                Config = new PushNotificationConfig
                {
                    Id = parts[2],
                    Url = parts[3]
                }
            }, token).ConfigureAwait(false);

            Console.WriteLine($"Created push config {config.Id} for task {config.TaskId}.");
        }

        private static void PrintBanner(string baseUrl, string rpcUrl, string grpcUrl)
        {
            Console.WriteLine("A2A test server running.");
            Console.WriteLine($"  Agent Card: {baseUrl}{A2AProtocol.AgentCardPath}");
            Console.WriteLine($"  JSON-RPC:   {rpcUrl}");
            Console.WriteLine($"  HTTP+JSON:  {baseUrl}");
            Console.WriteLine($"  gRPC:       {grpcUrl}");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Commands:");
            Console.WriteLine("  tasks                         List stored tasks");
            Console.WriteLine("  task <id>                     Show one task");
            Console.WriteLine("  configs <taskId>              List push configs for a task");
            Console.WriteLine("  push <taskId> <configId> <url> Create a push config");
            Console.WriteLine("  stop                          Stop the server");
        }

        private static async Task WaitForShutdownAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private sealed class ManualAgent : IA2AAgentHandler
        {
            public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
            {
                A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(token: token).ConfigureAwait(false);
                await updater.StartAsync(token: token).ConfigureAwait(false);

                string text = context.Message.Parts.FirstOrDefault()?.Text ?? string.Empty;
                if (text.Contains("slow", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    await updater.UpdateStatusAsync(TaskState.Working, CreateAgentMessage(context, "Still working."), token).ConfigureAwait(false);
                }

                await updater.AddArtifactAsync(new Artifact
                {
                    ArtifactId = "manual-echo",
                    Name = "Manual Echo",
                    Description = "Echo artifact emitted by Test.A2AServer.",
                    Parts = new List<Part> { Part.FromText(text) }
                }, token: token).ConfigureAwait(false);

                if (text.Contains("fail", StringComparison.OrdinalIgnoreCase))
                {
                    await updater.FailAsync(CreateAgentMessage(context, "Manual failure requested."), token).ConfigureAwait(false);
                    return;
                }

                if (text.Contains("input", StringComparison.OrdinalIgnoreCase))
                {
                    await updater.UpdateStatusAsync(TaskState.InputRequired, CreateAgentMessage(context, "Additional input required."), token).ConfigureAwait(false);
                    return;
                }

                await updater.CompleteAsync(CreateAgentMessage(context, $"Manual echo: {text}"), token).ConfigureAwait(false);
            }

            private static Message CreateAgentMessage(A2ARequestContext context, string text)
            {
                return new Message
                {
                    Role = Role.Agent,
                    MessageId = Guid.NewGuid().ToString("N"),
                    TaskId = context.TaskId,
                    ContextId = context.ContextId,
                    Parts = new List<Part> { Part.FromText(text) }
                };
            }
        }
    }
}
