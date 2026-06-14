namespace Test.A2AClient
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Voltaic.A2A;

    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            string baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "http://localhost:8080";
            string text = args.Length > 1 ? String.Join(" ", args.Skip(1)) : "hello from Voltaic";

            using HttpClient http = new HttpClient();
            using A2ACardResolver resolver = new A2ACardResolver(http);
            AgentCard card = await resolver.GetAgentCardAsync(baseUrl).ConfigureAwait(false);

            Console.WriteLine($"Agent: {card.Name} ({card.Version})");
            Console.WriteLine($"Skills: {String.Join(", ", card.Skills.Select(skill => skill.Name))}");

            AgentInterface jsonRpcInterface = card.SupportedInterfaces.FirstOrDefault(item => item.ProtocolBinding == "JSONRPC")
                ?? throw new InvalidOperationException("Agent Card does not advertise a JSONRPC interface.");
            AgentInterface? restInterface = card.SupportedInterfaces.FirstOrDefault(item => item.ProtocolBinding == "HTTP+JSON");
            AgentInterface? grpcInterface = card.SupportedInterfaces.FirstOrDefault(item => item.ProtocolBinding == "GRPC");

            using A2AClient rpcClient = new A2AClient(jsonRpcInterface.Url, http);
            SendMessageRequest request = CreateRequest(text);

            SendMessageResponse response = await rpcClient.SendMessageAsync(request).ConfigureAwait(false);
            Console.WriteLine($"JSON-RPC task: {response.Task?.Id} {response.Task?.Status.State}");

            Console.WriteLine("JSON-RPC stream:");
            await foreach (StreamResponse item in rpcClient.SendStreamingMessageAsync(CreateRequest(text + " stream")))
            {
                Console.WriteLine($"  {Describe(item)}");
            }

            if (restInterface != null)
            {
                using A2AHttpJsonClient restClient = new A2AHttpJsonClient(restInterface.Url, http);
                SendMessageResponse restResponse = await restClient.SendMessageAsync(CreateRequest(text + " rest")).ConfigureAwait(false);
                Console.WriteLine($"HTTP+JSON task: {restResponse.Task?.Id} {restResponse.Task?.Status.State}");

                TaskPushNotificationConfig config = await restClient.CreateTaskPushNotificationConfigAsync(new CreateTaskPushNotificationConfigRequest
                {
                    TaskId = restResponse.Task?.Id ?? "manual-task",
                    Config = new PushNotificationConfig
                    {
                        Id = "manual",
                        Url = "https://example.com/a2a/push"
                    }
                }).ConfigureAwait(false);
                Console.WriteLine($"Push config: {config.Id} -> {config.PushNotificationConfig.Url}");
            }

            if (grpcInterface != null)
            {
                AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
                using HttpClient grpcHttp = new HttpClient(new SocketsHttpHandler { EnableMultipleHttp2Connections = true });
                using A2AGrpcClient grpcClient = new A2AGrpcClient(grpcInterface.Url, grpcHttp);
                SendMessageResponse grpcResponse = await grpcClient.SendMessageAsync(CreateRequest(text + " grpc")).ConfigureAwait(false);
                Console.WriteLine($"gRPC task: {grpcResponse.Task?.Id} {grpcResponse.Task?.Status.State}");

                Console.WriteLine("gRPC stream:");
                await foreach (StreamResponse item in grpcClient.SendStreamingMessageAsync(CreateRequest(text + " grpc stream")))
                {
                    Console.WriteLine($"  {Describe(item)}");
                }
            }

            if (card.Capabilities.ExtendedAgentCard == true)
            {
                AgentCard extended = await rpcClient.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest()).ConfigureAwait(false);
                Console.WriteLine($"Extended card: {extended.Name}, skills={extended.Skills.Count}");
            }
        }

        private static SendMessageRequest CreateRequest(string text)
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

        private static string Describe(StreamResponse response)
        {
            if (response.StatusUpdate != null)
            {
                return $"status {response.StatusUpdate.Status.State}";
            }

            if (response.ArtifactUpdate != null)
            {
                return $"artifact {response.ArtifactUpdate.Artifact.ArtifactId}";
            }

            if (response.Task != null)
            {
                return $"task {response.Task.Id} {response.Task.Status.State}";
            }

            if (response.Message != null)
            {
                return $"message {response.Message.MessageId}";
            }

            return "empty";
        }
    }
}
