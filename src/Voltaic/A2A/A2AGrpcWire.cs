#pragma warning disable CS1591
namespace Voltaic.A2A
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Google.Protobuf;
    using Google.Protobuf.WellKnownTypes;
    using GrpcWire = Voltaic.A2A.Grpc;

    internal static class A2AGrpcWire
    {
        public const string ContentType = "application/grpc";
        public const string ServiceName = "lf.a2a.v1.A2AService";
        public const string ServicePrefix = "/" + ServiceName + "/";
        public const string SendMessagePath = ServicePrefix + "SendMessage";
        public const string SendStreamingMessagePath = ServicePrefix + "SendStreamingMessage";
        public const string GetTaskPath = ServicePrefix + "GetTask";
        public const string ListTasksPath = ServicePrefix + "ListTasks";
        public const string CancelTaskPath = ServicePrefix + "CancelTask";
        public const string SubscribeToTaskPath = ServicePrefix + "SubscribeToTask";
        public const string CreateTaskPushNotificationConfigPath = ServicePrefix + "CreateTaskPushNotificationConfig";
        public const string GetTaskPushNotificationConfigPath = ServicePrefix + "GetTaskPushNotificationConfig";
        public const string ListTaskPushNotificationConfigsPath = ServicePrefix + "ListTaskPushNotificationConfigs";
        public const string GetExtendedAgentCardPath = ServicePrefix + "GetExtendedAgentCard";
        public const string DeleteTaskPushNotificationConfigPath = ServicePrefix + "DeleteTaskPushNotificationConfig";

        public static byte[] EncodeMessage(IMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return EncodePayload(message.ToByteArray());
        }

        public static byte[] EncodePayload(byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            byte[] frame = new byte[payload.Length + 5];
            frame[0] = 0;
            frame[1] = (byte)((payload.Length >> 24) & 0xff);
            frame[2] = (byte)((payload.Length >> 16) & 0xff);
            frame[3] = (byte)((payload.Length >> 8) & 0xff);
            frame[4] = (byte)(payload.Length & 0xff);
            Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
            return frame;
        }

        public static byte[] DecodeSinglePayload(byte[] body)
        {
            List<byte[]> frames = DecodePayloads(body);
            if (frames.Count != 1)
            {
                throw A2AProtocolException.InvalidParams($"Expected one gRPC message frame, found {frames.Count}.");
            }

            return frames[0];
        }

        public static List<byte[]> DecodePayloads(byte[] body)
        {
            body ??= Array.Empty<byte>();
            List<byte[]> frames = new List<byte[]>();
            int offset = 0;
            while (offset < body.Length)
            {
                if (body.Length - offset < 5)
                {
                    throw A2AProtocolException.InvalidParams("Incomplete gRPC frame header.");
                }

                if (body[offset] != 0)
                {
                    throw A2AProtocolException.InvalidParams("Compressed gRPC frames are not supported.");
                }

                int length =
                    (body[offset + 1] << 24) |
                    (body[offset + 2] << 16) |
                    (body[offset + 3] << 8) |
                    body[offset + 4];
                offset += 5;
                if (length < 0 || body.Length - offset < length)
                {
                    throw A2AProtocolException.InvalidParams("Incomplete gRPC frame payload.");
                }

                byte[] payload = new byte[length];
                Buffer.BlockCopy(body, offset, payload, 0, length);
                frames.Add(payload);
                offset += length;
            }

            return frames;
        }

        public static async IAsyncEnumerable<byte[]> ReadPayloadsAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            byte[] header = new byte[5];
            while (!token.IsCancellationRequested)
            {
                int headerBytes = await ReadAtLeastOneThenExactAsync(stream, header, token).ConfigureAwait(false);
                if (headerBytes == 0)
                {
                    yield break;
                }

                if (header[0] != 0)
                {
                    throw A2AProtocolException.InvalidParams("Compressed gRPC frames are not supported.");
                }

                int length =
                    (header[1] << 24) |
                    (header[2] << 16) |
                    (header[3] << 8) |
                    header[4];
                byte[] payload = new byte[length];
                await ReadExactAsync(stream, payload, token).ConfigureAwait(false);
                yield return payload;
            }
        }

        public static void ConfigureGrpcRequest(HttpRequestMessage request)
        {
            request.Version = new Version(2, 0);
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.TryAddWithoutValidation("te", "trailers");
            request.Headers.TryAddWithoutValidation(A2AProtocol.VersionHeader, A2AProtocol.ProtocolVersion);
        }

        public static void EnsureGrpcSuccess(HttpResponseMessage response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));

            string? status = GetGrpcHeader(response, "grpc-status");
            if (String.IsNullOrEmpty(status) || status == "0")
            {
                return;
            }

            string message = Uri.UnescapeDataString(GetGrpcHeader(response, "grpc-message") ?? "gRPC request failed.");
            throw new A2AProtocolException(FromGrpcStatus(status), message);
        }

        public static int ToGrpcStatus(Exception ex)
        {
            if (ex is A2AProtocolException protocolException)
            {
                return protocolException.ErrorCode switch
                {
                    A2AErrorCode.InvalidParams => 3,
                    A2AErrorCode.TaskNotFound => 5,
                    A2AErrorCode.MethodNotFound => 12,
                    A2AErrorCode.UnsupportedOperation => 12,
                    A2AErrorCode.TaskNotCancelable => 9,
                    A2AErrorCode.PushNotificationNotSupported => 9,
                    A2AErrorCode.ExtendedAgentCardNotConfigured => 9,
                    _ => 13
                };
            }

            return 13;
        }

        public static A2AErrorCode FromGrpcStatus(string status)
        {
            return status switch
            {
                "3" => A2AErrorCode.InvalidParams,
                "5" => A2AErrorCode.TaskNotFound,
                "9" => A2AErrorCode.UnsupportedOperation,
                "12" => A2AErrorCode.UnsupportedOperation,
                _ => A2AErrorCode.InternalError
            };
        }

        public static GrpcWire.SendMessageRequest ToGrpc(SendMessageRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            GrpcWire.SendMessageRequest grpc = new GrpcWire.SendMessageRequest
            {
                Tenant = request.Tenant ?? string.Empty,
                Message = ToGrpc(request.Message)
            };
            if (request.Configuration != null) grpc.Configuration = ToGrpc(request.Configuration);
            if (request.Metadata != null) grpc.Metadata = ToGrpcStruct(request.Metadata);
            return grpc;
        }

        public static SendMessageRequest FromGrpc(GrpcWire.SendMessageRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new SendMessageRequest
            {
                Tenant = EmptyToNull(request.Tenant),
                Message = FromGrpc(request.Message),
                Configuration = request.Configuration != null ? FromGrpc(request.Configuration) : null,
                Metadata = FromGrpcStruct(request.Metadata)
            };
        }

        public static GrpcWire.SendMessageResponse ToGrpc(SendMessageResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            GrpcWire.SendMessageResponse grpc = new GrpcWire.SendMessageResponse();
            if (response.Task != null) grpc.Task = ToGrpc(response.Task);
            else if (response.Message != null) grpc.Message = ToGrpc(response.Message);
            return grpc;
        }

        public static SendMessageResponse FromGrpc(GrpcWire.SendMessageResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return response.PayloadCase switch
            {
                GrpcWire.SendMessageResponse.PayloadOneofCase.Task => new SendMessageResponse { Task = FromGrpc(response.Task) },
                GrpcWire.SendMessageResponse.PayloadOneofCase.Message => new SendMessageResponse { Message = FromGrpc(response.Message) },
                _ => new SendMessageResponse()
            };
        }

        public static GrpcWire.StreamResponse ToGrpc(StreamResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            GrpcWire.StreamResponse grpc = new GrpcWire.StreamResponse();
            if (response.Task != null) grpc.Task = ToGrpc(response.Task);
            else if (response.Message != null) grpc.Message = ToGrpc(response.Message);
            else if (response.StatusUpdate != null) grpc.StatusUpdate = ToGrpc(response.StatusUpdate);
            else if (response.ArtifactUpdate != null) grpc.ArtifactUpdate = ToGrpc(response.ArtifactUpdate);
            return grpc;
        }

        public static StreamResponse FromGrpc(GrpcWire.StreamResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return response.PayloadCase switch
            {
                GrpcWire.StreamResponse.PayloadOneofCase.Task => new StreamResponse { Task = FromGrpc(response.Task) },
                GrpcWire.StreamResponse.PayloadOneofCase.Message => new StreamResponse { Message = FromGrpc(response.Message) },
                GrpcWire.StreamResponse.PayloadOneofCase.StatusUpdate => new StreamResponse { StatusUpdate = FromGrpc(response.StatusUpdate) },
                GrpcWire.StreamResponse.PayloadOneofCase.ArtifactUpdate => new StreamResponse { ArtifactUpdate = FromGrpc(response.ArtifactUpdate) },
                _ => new StreamResponse()
            };
        }

        public static GrpcWire.GetTaskRequest ToGrpc(GetTaskRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            GrpcWire.GetTaskRequest grpc = new GrpcWire.GetTaskRequest
            {
                Tenant = request.Tenant ?? string.Empty,
                Id = request.Id ?? string.Empty
            };
            if (request.HistoryLength != null) grpc.HistoryLength = request.HistoryLength.Value;
            return grpc;
        }

        public static GetTaskRequest FromGrpc(GrpcWire.GetTaskRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new GetTaskRequest
            {
                Tenant = EmptyToNull(request.Tenant),
                Id = request.Id,
                HistoryLength = request.HasHistoryLength ? request.HistoryLength : null
            };
        }

        public static GrpcWire.ListTasksRequest ToGrpc(ListTasksRequest request)
        {
            request ??= new ListTasksRequest();
            GrpcWire.ListTasksRequest grpc = new GrpcWire.ListTasksRequest
            {
                Tenant = request.Tenant ?? string.Empty,
                ContextId = request.ContextId ?? string.Empty,
                Status = request.Status != null ? ToGrpc(request.Status.Value) : GrpcWire.TaskState.Unspecified,
                PageToken = request.PageToken ?? string.Empty
            };
            if (request.PageSize != null) grpc.PageSize = request.PageSize.Value;
            if (request.HistoryLength != null) grpc.HistoryLength = request.HistoryLength.Value;
            if (request.StatusTimestampAfter != null) grpc.StatusTimestampAfter = Timestamp.FromDateTimeOffset(request.StatusTimestampAfter.Value);
            if (request.IncludeArtifacts != null) grpc.IncludeArtifacts = request.IncludeArtifacts.Value;
            return grpc;
        }

        public static ListTasksRequest FromGrpc(GrpcWire.ListTasksRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new ListTasksRequest
            {
                Tenant = EmptyToNull(request.Tenant),
                ContextId = EmptyToNull(request.ContextId),
                Status = request.Status == GrpcWire.TaskState.Unspecified ? null : FromGrpc(request.Status),
                PageSize = request.HasPageSize ? request.PageSize : null,
                PageToken = EmptyToNull(request.PageToken),
                HistoryLength = request.HasHistoryLength ? request.HistoryLength : null,
                StatusTimestampAfter = request.StatusTimestampAfter?.ToDateTimeOffset(),
                IncludeArtifacts = request.HasIncludeArtifacts ? request.IncludeArtifacts : null
            };
        }

        public static GrpcWire.ListTasksResponse ToGrpc(ListTasksResponse response)
        {
            response ??= new ListTasksResponse();
            GrpcWire.ListTasksResponse grpc = new GrpcWire.ListTasksResponse
            {
                NextPageToken = response.NextPageToken ?? string.Empty,
                PageSize = response.PageSize,
                TotalSize = response.TotalSize
            };
            grpc.Tasks.Add(response.Tasks.Select(ToGrpc));
            return grpc;
        }

        public static ListTasksResponse FromGrpc(GrpcWire.ListTasksResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return new ListTasksResponse
            {
                Tasks = response.Tasks.Select(FromGrpc).ToList(),
                NextPageToken = response.NextPageToken,
                PageSize = response.PageSize,
                TotalSize = response.TotalSize
            };
        }

        public static GrpcWire.CancelTaskRequest ToGrpc(CancelTaskRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            GrpcWire.CancelTaskRequest grpc = new GrpcWire.CancelTaskRequest
            {
                Tenant = request.Tenant ?? string.Empty,
                Id = request.Id ?? string.Empty
            };
            if (request.Metadata != null) grpc.Metadata = ToGrpcStruct(request.Metadata);
            return grpc;
        }

        public static CancelTaskRequest FromGrpc(GrpcWire.CancelTaskRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new CancelTaskRequest
            {
                Tenant = EmptyToNull(request.Tenant),
                Id = request.Id,
                Metadata = FromGrpcStruct(request.Metadata)
            };
        }

        public static GrpcWire.SubscribeToTaskRequest ToGrpc(SubscribeToTaskRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new GrpcWire.SubscribeToTaskRequest
            {
                Tenant = request.Tenant ?? string.Empty,
                Id = request.Id ?? string.Empty
            };
        }

        public static SubscribeToTaskRequest FromGrpc(GrpcWire.SubscribeToTaskRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new SubscribeToTaskRequest
            {
                Tenant = EmptyToNull(request.Tenant),
                Id = request.Id
            };
        }

        public static GrpcWire.GetExtendedAgentCardRequest ToGrpc(GetExtendedAgentCardRequest request)
        {
            request ??= new GetExtendedAgentCardRequest();
            return new GrpcWire.GetExtendedAgentCardRequest { Tenant = request.Tenant ?? string.Empty };
        }

        public static GetExtendedAgentCardRequest FromGrpc(GrpcWire.GetExtendedAgentCardRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new GetExtendedAgentCardRequest { Tenant = EmptyToNull(request.Tenant) };
        }

        public static GrpcWire.GetTaskPushNotificationConfigRequest ToGrpc(GetTaskPushNotificationConfigRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new GrpcWire.GetTaskPushNotificationConfigRequest
            {
                Tenant = request.Tenant ?? string.Empty,
                TaskId = request.TaskId ?? string.Empty,
                Id = request.ConfigId ?? string.Empty
            };
        }

        public static GetTaskPushNotificationConfigRequest FromGrpc(GrpcWire.GetTaskPushNotificationConfigRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new GetTaskPushNotificationConfigRequest
            {
                Tenant = EmptyToNull(request.Tenant),
                TaskId = request.TaskId,
                ConfigId = request.Id
            };
        }

        public static GrpcWire.ListTaskPushNotificationConfigsRequest ToGrpc(ListTaskPushNotificationConfigRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new GrpcWire.ListTaskPushNotificationConfigsRequest
            {
                Tenant = request.Tenant ?? string.Empty,
                TaskId = request.TaskId ?? string.Empty
            };
        }

        public static ListTaskPushNotificationConfigRequest FromGrpc(GrpcWire.ListTaskPushNotificationConfigsRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new ListTaskPushNotificationConfigRequest
            {
                Tenant = EmptyToNull(request.Tenant),
                TaskId = request.TaskId
            };
        }

        public static GrpcWire.ListTaskPushNotificationConfigsResponse ToGrpc(ListTaskPushNotificationConfigResponse response)
        {
            response ??= new ListTaskPushNotificationConfigResponse();
            GrpcWire.ListTaskPushNotificationConfigsResponse grpc = new GrpcWire.ListTaskPushNotificationConfigsResponse();
            grpc.Configs.Add(response.Configs.Select(ToGrpc));
            return grpc;
        }

        public static ListTaskPushNotificationConfigResponse FromGrpc(GrpcWire.ListTaskPushNotificationConfigsResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return new ListTaskPushNotificationConfigResponse
            {
                Configs = response.Configs.Select(FromGrpc).ToList()
            };
        }

        public static GrpcWire.DeleteTaskPushNotificationConfigRequest ToGrpc(DeleteTaskPushNotificationConfigRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new GrpcWire.DeleteTaskPushNotificationConfigRequest
            {
                Tenant = request.Tenant ?? string.Empty,
                TaskId = request.TaskId ?? string.Empty,
                Id = request.ConfigId ?? string.Empty
            };
        }

        public static DeleteTaskPushNotificationConfigRequest FromGrpc(GrpcWire.DeleteTaskPushNotificationConfigRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new DeleteTaskPushNotificationConfigRequest
            {
                Tenant = EmptyToNull(request.Tenant),
                TaskId = request.TaskId,
                ConfigId = request.Id
            };
        }

        public static GrpcWire.TaskPushNotificationConfig ToGrpc(CreateTaskPushNotificationConfigRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new GrpcWire.TaskPushNotificationConfig
            {
                Tenant = request.Tenant ?? string.Empty,
                Id = String.IsNullOrWhiteSpace(request.ConfigId) ? request.Config.Id ?? string.Empty : request.ConfigId,
                TaskId = request.TaskId ?? string.Empty,
                Url = request.Config.Url ?? string.Empty,
                Token = request.Config.Token ?? string.Empty,
                Authentication = request.Config.Authentication != null ? ToGrpc(request.Config.Authentication) : null
            };
        }

        public static CreateTaskPushNotificationConfigRequest ToCreateRequest(GrpcWire.TaskPushNotificationConfig config)
        {
            TaskPushNotificationConfig model = FromGrpc(config);
            return new CreateTaskPushNotificationConfigRequest
            {
                Tenant = model.Tenant,
                TaskId = model.TaskId,
                ConfigId = model.Id,
                Config = model.PushNotificationConfig
            };
        }

        public static GrpcWire.AgentCard ToGrpc(AgentCard card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            GrpcWire.AgentCard grpc = new GrpcWire.AgentCard
            {
                Name = card.Name ?? string.Empty,
                Description = card.Description ?? string.Empty,
                Version = card.Version ?? string.Empty,
                Capabilities = ToGrpc(card.Capabilities)
            };
            if (card.DocumentationUrl != null) grpc.DocumentationUrl = card.DocumentationUrl;
            if (card.IconUrl != null) grpc.IconUrl = card.IconUrl;
            if (card.Provider != null) grpc.Provider = ToGrpc(card.Provider);
            grpc.SupportedInterfaces.Add(card.SupportedInterfaces.Select(ToGrpc));
            grpc.DefaultInputModes.Add(card.DefaultInputModes ?? Enumerable.Empty<string>());
            grpc.DefaultOutputModes.Add(card.DefaultOutputModes ?? Enumerable.Empty<string>());
            grpc.Skills.Add(card.Skills.Select(ToGrpc));
            if (card.SecuritySchemes != null)
            {
                foreach (KeyValuePair<string, SecurityScheme> item in card.SecuritySchemes)
                {
                    grpc.SecuritySchemes[item.Key] = ToGrpc(item.Value);
                }
            }

            if (card.SecurityRequirements != null) grpc.SecurityRequirements.Add(card.SecurityRequirements.Select(ToGrpc));
            if (card.Signatures != null) grpc.Signatures.Add(card.Signatures.Select(ToGrpc));
            return grpc;
        }

        public static AgentCard FromGrpc(GrpcWire.AgentCard card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            return new AgentCard
            {
                Name = card.Name,
                Description = card.Description,
                Version = card.Version,
                DocumentationUrl = card.HasDocumentationUrl ? card.DocumentationUrl : null,
                IconUrl = card.HasIconUrl ? card.IconUrl : null,
                SupportedInterfaces = card.SupportedInterfaces.Select(FromGrpc).ToList(),
                Provider = card.Provider != null ? FromGrpc(card.Provider) : null,
                Capabilities = FromGrpc(card.Capabilities),
                DefaultInputModes = card.DefaultInputModes.ToList(),
                DefaultOutputModes = card.DefaultOutputModes.ToList(),
                Skills = card.Skills.Select(FromGrpc).ToList(),
                SecuritySchemes = card.SecuritySchemes.Count > 0 ? card.SecuritySchemes.ToDictionary(item => item.Key, item => FromGrpc(item.Value), StringComparer.Ordinal) : null,
                SecurityRequirements = card.SecurityRequirements.Count > 0 ? card.SecurityRequirements.Select(FromGrpc).ToList() : null,
                Signatures = card.Signatures.Count > 0 ? card.Signatures.Select(FromGrpc).ToList() : null
            };
        }

        private static async Task<int> ReadAtLeastOneThenExactAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            int read = await stream.ReadAsync(buffer, 0, 1, token).ConfigureAwait(false);
            if (read == 0)
            {
                return 0;
            }

            await ReadExactAsync(stream, buffer, 1, buffer.Length - 1, token).ConfigureAwait(false);
            return buffer.Length;
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            await ReadExactAsync(stream, buffer, 0, buffer.Length, token).ConfigureAwait(false);
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int length, CancellationToken token)
        {
            int read = 0;
            while (read < length)
            {
                int bytes = await stream.ReadAsync(buffer, offset + read, length - read, token).ConfigureAwait(false);
                if (bytes == 0)
                {
                    throw new EndOfStreamException("gRPC stream ended before a full message frame was received.");
                }

                read += bytes;
            }
        }

        private static string? GetGrpcHeader(HttpResponseMessage response, string name)
        {
            if (response.TrailingHeaders.TryGetValues(name, out IEnumerable<string>? trailers))
            {
                return trailers.FirstOrDefault();
            }

            if (response.Headers.TryGetValues(name, out IEnumerable<string>? headers))
            {
                return headers.FirstOrDefault();
            }

            return null;
        }

        private static GrpcWire.SendMessageConfiguration ToGrpc(SendMessageConfiguration configuration)
        {
            GrpcWire.SendMessageConfiguration grpc = new GrpcWire.SendMessageConfiguration
            {
                ReturnImmediately = configuration.ReturnImmediately
            };
            if (configuration.AcceptedOutputModes != null) grpc.AcceptedOutputModes.Add(configuration.AcceptedOutputModes);
            if (configuration.HistoryLength != null) grpc.HistoryLength = configuration.HistoryLength.Value;
            if (configuration.PushNotificationConfig != null)
            {
                grpc.TaskPushNotificationConfig = new GrpcWire.TaskPushNotificationConfig
                {
                    Id = configuration.PushNotificationConfig.Id ?? string.Empty,
                    Url = configuration.PushNotificationConfig.Url ?? string.Empty,
                    Token = configuration.PushNotificationConfig.Token ?? string.Empty,
                    Authentication = configuration.PushNotificationConfig.Authentication != null ? ToGrpc(configuration.PushNotificationConfig.Authentication) : null
                };
            }

            return grpc;
        }

        private static SendMessageConfiguration FromGrpc(GrpcWire.SendMessageConfiguration configuration)
        {
            return new SendMessageConfiguration
            {
                AcceptedOutputModes = configuration.AcceptedOutputModes.Count > 0 ? configuration.AcceptedOutputModes.ToList() : null,
                HistoryLength = configuration.HasHistoryLength ? configuration.HistoryLength : null,
                ReturnImmediately = configuration.ReturnImmediately,
                PushNotificationConfig = configuration.TaskPushNotificationConfig != null
                    ? FromGrpc(configuration.TaskPushNotificationConfig).PushNotificationConfig
                    : null
            };
        }

        public static GrpcWire.Task ToGrpc(AgentTask task)
        {
            GrpcWire.Task grpc = new GrpcWire.Task
            {
                Id = task.Id ?? string.Empty,
                ContextId = task.ContextId ?? string.Empty,
                Status = ToGrpc(task.Status)
            };
            if (task.Artifacts != null) grpc.Artifacts.Add(task.Artifacts.Select(ToGrpc));
            if (task.History != null) grpc.History.Add(task.History.Select(ToGrpc));
            if (task.Metadata != null) grpc.Metadata = ToGrpcStruct(task.Metadata);
            return grpc;
        }

        public static AgentTask FromGrpc(GrpcWire.Task task)
        {
            return new AgentTask
            {
                Id = task.Id,
                ContextId = task.ContextId,
                Status = FromGrpc(task.Status),
                Artifacts = task.Artifacts.Count > 0 ? task.Artifacts.Select(FromGrpc).ToList() : null,
                History = task.History.Count > 0 ? task.History.Select(FromGrpc).ToList() : null,
                Metadata = FromGrpcStruct(task.Metadata)
            };
        }

        private static GrpcWire.TaskStatus ToGrpc(TaskStatus status)
        {
            status ??= new TaskStatus();
            GrpcWire.TaskStatus grpc = new GrpcWire.TaskStatus
            {
                State = ToGrpc(status.State)
            };
            if (status.Message != null) grpc.Message = ToGrpc(status.Message);
            if (status.Timestamp != null) grpc.Timestamp = Timestamp.FromDateTimeOffset(status.Timestamp.Value);
            return grpc;
        }

        private static TaskStatus FromGrpc(GrpcWire.TaskStatus status)
        {
            status ??= new GrpcWire.TaskStatus();
            return new TaskStatus
            {
                State = FromGrpc(status.State),
                Message = status.Message != null ? FromGrpc(status.Message) : null,
                Timestamp = status.Timestamp?.ToDateTimeOffset()
            };
        }

        private static GrpcWire.Part ToGrpc(Part part)
        {
            GrpcWire.Part grpc = new GrpcWire.Part
            {
                Filename = part.Filename ?? string.Empty,
                MediaType = part.MediaType ?? string.Empty
            };

            if (part.Text != null) grpc.Text = part.Text;
            else if (part.Raw != null) grpc.Raw = ByteString.CopyFrom(part.Raw);
            else if (part.Url != null) grpc.Url = part.Url;
            else if (part.Data != null) grpc.Data = ToGrpcValue(part.Data.Value);
            if (part.Metadata != null) grpc.Metadata = ToGrpcStruct(part.Metadata);
            return grpc;
        }

        private static Part FromGrpc(GrpcWire.Part part)
        {
            Part model = new Part
            {
                Filename = EmptyToNull(part.Filename),
                MediaType = EmptyToNull(part.MediaType),
                Metadata = FromGrpcStruct(part.Metadata)
            };

            switch (part.ContentCase)
            {
                case GrpcWire.Part.ContentOneofCase.Text:
                    model.Text = part.Text;
                    break;
                case GrpcWire.Part.ContentOneofCase.Raw:
                    model.Raw = part.Raw.ToByteArray();
                    break;
                case GrpcWire.Part.ContentOneofCase.Url:
                    model.Url = part.Url;
                    break;
                case GrpcWire.Part.ContentOneofCase.Data:
                    model.Data = FromGrpcValue(part.Data);
                    break;
            }

            return model;
        }

        private static GrpcWire.Message ToGrpc(Message message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            GrpcWire.Message grpc = new GrpcWire.Message
            {
                MessageId = message.MessageId ?? string.Empty,
                ContextId = message.ContextId ?? string.Empty,
                TaskId = message.TaskId ?? string.Empty,
                Role = ToGrpc(message.Role)
            };
            grpc.Parts.Add(message.Parts.Select(ToGrpc));
            if (message.Metadata != null) grpc.Metadata = ToGrpcStruct(message.Metadata);
            if (message.Extensions != null) grpc.Extensions.Add(message.Extensions);
            if (message.ReferenceTaskIds != null) grpc.ReferenceTaskIds.Add(message.ReferenceTaskIds);
            return grpc;
        }

        private static Message FromGrpc(GrpcWire.Message message)
        {
            if (message == null) return new Message();
            return new Message
            {
                MessageId = message.MessageId,
                ContextId = EmptyToNull(message.ContextId),
                TaskId = EmptyToNull(message.TaskId),
                Role = FromGrpc(message.Role),
                Parts = message.Parts.Select(FromGrpc).ToList(),
                Metadata = FromGrpcStruct(message.Metadata),
                Extensions = message.Extensions.Count > 0 ? message.Extensions.ToList() : null,
                ReferenceTaskIds = message.ReferenceTaskIds.Count > 0 ? message.ReferenceTaskIds.ToList() : null
            };
        }

        private static GrpcWire.Artifact ToGrpc(Artifact artifact)
        {
            GrpcWire.Artifact grpc = new GrpcWire.Artifact
            {
                ArtifactId = artifact.ArtifactId ?? string.Empty,
                Name = artifact.Name ?? string.Empty,
                Description = artifact.Description ?? string.Empty
            };
            grpc.Parts.Add(artifact.Parts.Select(ToGrpc));
            if (artifact.Metadata != null) grpc.Metadata = ToGrpcStruct(artifact.Metadata);
            if (artifact.Extensions != null) grpc.Extensions.Add(artifact.Extensions);
            return grpc;
        }

        private static Artifact FromGrpc(GrpcWire.Artifact artifact)
        {
            return new Artifact
            {
                ArtifactId = artifact.ArtifactId,
                Name = EmptyToNull(artifact.Name),
                Description = EmptyToNull(artifact.Description),
                Parts = artifact.Parts.Select(FromGrpc).ToList(),
                Metadata = FromGrpcStruct(artifact.Metadata),
                Extensions = artifact.Extensions.Count > 0 ? artifact.Extensions.ToList() : null
            };
        }

        private static GrpcWire.TaskStatusUpdateEvent ToGrpc(TaskStatusUpdateEvent update)
        {
            GrpcWire.TaskStatusUpdateEvent grpc = new GrpcWire.TaskStatusUpdateEvent
            {
                TaskId = update.TaskId ?? string.Empty,
                ContextId = update.ContextId ?? string.Empty,
                Status = ToGrpc(update.Status)
            };
            if (update.Metadata != null) grpc.Metadata = ToGrpcStruct(update.Metadata);
            return grpc;
        }

        private static TaskStatusUpdateEvent FromGrpc(GrpcWire.TaskStatusUpdateEvent update)
        {
            return new TaskStatusUpdateEvent
            {
                TaskId = update.TaskId,
                ContextId = update.ContextId,
                Status = FromGrpc(update.Status),
                Metadata = FromGrpcStruct(update.Metadata)
            };
        }

        private static GrpcWire.TaskArtifactUpdateEvent ToGrpc(TaskArtifactUpdateEvent update)
        {
            GrpcWire.TaskArtifactUpdateEvent grpc = new GrpcWire.TaskArtifactUpdateEvent
            {
                TaskId = update.TaskId ?? string.Empty,
                ContextId = update.ContextId ?? string.Empty,
                Artifact = ToGrpc(update.Artifact),
                Append = update.Append,
                LastChunk = update.LastChunk
            };
            if (update.Metadata != null) grpc.Metadata = ToGrpcStruct(update.Metadata);
            return grpc;
        }

        private static TaskArtifactUpdateEvent FromGrpc(GrpcWire.TaskArtifactUpdateEvent update)
        {
            return new TaskArtifactUpdateEvent
            {
                TaskId = update.TaskId,
                ContextId = update.ContextId,
                Artifact = FromGrpc(update.Artifact),
                Append = update.Append,
                LastChunk = update.LastChunk,
                Metadata = FromGrpcStruct(update.Metadata)
            };
        }

        private static GrpcWire.AuthenticationInfo ToGrpc(AuthenticationInfo authentication)
        {
            return new GrpcWire.AuthenticationInfo
            {
                Scheme = authentication.Scheme ?? string.Empty,
                Credentials = authentication.Credentials ?? string.Empty
            };
        }

        private static AuthenticationInfo FromGrpc(GrpcWire.AuthenticationInfo authentication)
        {
            return new AuthenticationInfo
            {
                Scheme = authentication.Scheme,
                Credentials = EmptyToNull(authentication.Credentials)
            };
        }

        public static GrpcWire.TaskPushNotificationConfig ToGrpc(TaskPushNotificationConfig config)
        {
            GrpcWire.TaskPushNotificationConfig grpc = new GrpcWire.TaskPushNotificationConfig
            {
                Tenant = config.Tenant ?? string.Empty,
                Id = config.Id ?? string.Empty,
                TaskId = config.TaskId ?? string.Empty,
                Url = config.PushNotificationConfig.Url ?? string.Empty,
                Token = config.PushNotificationConfig.Token ?? string.Empty,
                Authentication = config.PushNotificationConfig.Authentication != null ? ToGrpc(config.PushNotificationConfig.Authentication) : null
            };
            return grpc;
        }

        public static TaskPushNotificationConfig FromGrpc(GrpcWire.TaskPushNotificationConfig config)
        {
            return new TaskPushNotificationConfig
            {
                Id = config.Id,
                TaskId = config.TaskId,
                Tenant = EmptyToNull(config.Tenant),
                PushNotificationConfig = new PushNotificationConfig
                {
                    Id = EmptyToNull(config.Id),
                    Url = config.Url,
                    Token = EmptyToNull(config.Token),
                    Authentication = config.Authentication != null ? FromGrpc(config.Authentication) : null
                }
            };
        }

        private static GrpcWire.AgentInterface ToGrpc(AgentInterface item)
        {
            return new GrpcWire.AgentInterface
            {
                Url = item.Url ?? string.Empty,
                ProtocolBinding = item.ProtocolBinding ?? string.Empty,
                Tenant = item.Tenant ?? string.Empty,
                ProtocolVersion = item.ProtocolVersion ?? A2AProtocol.ProtocolVersion
            };
        }

        private static AgentInterface FromGrpc(GrpcWire.AgentInterface item)
        {
            return new AgentInterface
            {
                Url = item.Url,
                ProtocolBinding = item.ProtocolBinding,
                Tenant = EmptyToNull(item.Tenant),
                ProtocolVersion = item.ProtocolVersion
            };
        }

        private static GrpcWire.AgentProvider ToGrpc(AgentProvider provider)
        {
            return new GrpcWire.AgentProvider
            {
                Organization = provider.Organization ?? string.Empty,
                Url = provider.Url ?? string.Empty
            };
        }

        private static AgentProvider FromGrpc(GrpcWire.AgentProvider provider)
        {
            return new AgentProvider
            {
                Organization = provider.Organization,
                Url = EmptyToNull(provider.Url)
            };
        }

        private static GrpcWire.AgentCapabilities ToGrpc(AgentCapabilities capabilities)
        {
            capabilities ??= new AgentCapabilities();
            GrpcWire.AgentCapabilities grpc = new GrpcWire.AgentCapabilities();
            if (capabilities.Streaming != null) grpc.Streaming = capabilities.Streaming.Value;
            if (capabilities.PushNotifications != null) grpc.PushNotifications = capabilities.PushNotifications.Value;
            if (capabilities.ExtendedAgentCard != null) grpc.ExtendedAgentCard = capabilities.ExtendedAgentCard.Value;
            if (capabilities.Extensions != null) grpc.Extensions.Add(capabilities.Extensions.Select(ToGrpc));
            return grpc;
        }

        private static AgentCapabilities FromGrpc(GrpcWire.AgentCapabilities capabilities)
        {
            capabilities ??= new GrpcWire.AgentCapabilities();
            return new AgentCapabilities
            {
                Streaming = capabilities.HasStreaming ? capabilities.Streaming : null,
                PushNotifications = capabilities.HasPushNotifications ? capabilities.PushNotifications : null,
                ExtendedAgentCard = capabilities.HasExtendedAgentCard ? capabilities.ExtendedAgentCard : null,
                Extensions = capabilities.Extensions.Count > 0 ? capabilities.Extensions.Select(FromGrpc).ToList() : null
            };
        }

        private static GrpcWire.AgentExtension ToGrpc(AgentExtension extension)
        {
            GrpcWire.AgentExtension grpc = new GrpcWire.AgentExtension
            {
                Uri = extension.Uri ?? string.Empty,
                Description = extension.Description ?? string.Empty,
                Required = extension.Required ?? false
            };
            if (extension.Params != null) grpc.Params = ToGrpcStruct(extension.Params);
            return grpc;
        }

        private static AgentExtension FromGrpc(GrpcWire.AgentExtension extension)
        {
            return new AgentExtension
            {
                Uri = extension.Uri,
                Description = EmptyToNull(extension.Description),
                Required = extension.Required,
                Params = FromGrpcStruct(extension.Params)
            };
        }

        private static GrpcWire.AgentSkill ToGrpc(AgentSkill skill)
        {
            GrpcWire.AgentSkill grpc = new GrpcWire.AgentSkill
            {
                Id = skill.Id ?? string.Empty,
                Name = skill.Name ?? string.Empty,
                Description = skill.Description ?? string.Empty
            };
            grpc.Tags.Add(skill.Tags ?? Enumerable.Empty<string>());
            if (skill.Examples != null) grpc.Examples.Add(skill.Examples);
            if (skill.InputModes != null) grpc.InputModes.Add(skill.InputModes);
            if (skill.OutputModes != null) grpc.OutputModes.Add(skill.OutputModes);
            if (skill.SecurityRequirements != null) grpc.SecurityRequirements.Add(skill.SecurityRequirements.Select(ToGrpc));
            return grpc;
        }

        private static AgentSkill FromGrpc(GrpcWire.AgentSkill skill)
        {
            return new AgentSkill
            {
                Id = skill.Id,
                Name = skill.Name,
                Description = skill.Description,
                Tags = skill.Tags.ToList(),
                Examples = skill.Examples.Count > 0 ? skill.Examples.ToList() : null,
                InputModes = skill.InputModes.Count > 0 ? skill.InputModes.ToList() : null,
                OutputModes = skill.OutputModes.Count > 0 ? skill.OutputModes.ToList() : null,
                SecurityRequirements = skill.SecurityRequirements.Count > 0 ? skill.SecurityRequirements.Select(FromGrpc).ToList() : null
            };
        }

        private static GrpcWire.AgentCardSignature ToGrpc(AgentCardSignature signature)
        {
            GrpcWire.AgentCardSignature grpc = new GrpcWire.AgentCardSignature
            {
                Protected = signature.Protected ?? string.Empty,
                Signature = signature.Signature ?? string.Empty
            };
            if (!String.IsNullOrWhiteSpace(signature.Header))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(signature.Header);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        grpc.Header = ToGrpcStruct(document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal));
                    }
                }
                catch (JsonException)
                {
                }
            }

            return grpc;
        }

        private static AgentCardSignature FromGrpc(GrpcWire.AgentCardSignature signature)
        {
            return new AgentCardSignature
            {
                Protected = signature.Protected,
                Signature = signature.Signature,
                Header = signature.Header != null ? JsonSerializer.Serialize(FromGrpcStruct(signature.Header), A2AJson.DefaultOptions) : string.Empty
            };
        }

        private static GrpcWire.SecurityRequirement ToGrpc(SecurityRequirement requirement)
        {
            GrpcWire.SecurityRequirement grpc = new GrpcWire.SecurityRequirement();
            if (requirement.Schemes != null)
            {
                foreach (KeyValuePair<string, List<string>> item in requirement.Schemes)
                {
                    GrpcWire.StringList list = new GrpcWire.StringList();
                    if (item.Value != null) list.List.Add(item.Value);
                    grpc.Schemes[item.Key] = list;
                }
            }

            return grpc;
        }

        private static SecurityRequirement FromGrpc(GrpcWire.SecurityRequirement requirement)
        {
            return new SecurityRequirement
            {
                Schemes = requirement.Schemes.Count > 0
                    ? requirement.Schemes.ToDictionary(item => item.Key, item => item.Value.List.ToList(), StringComparer.Ordinal)
                    : null
            };
        }

        private static GrpcWire.SecurityScheme ToGrpc(SecurityScheme scheme)
        {
            GrpcWire.SecurityScheme grpc = new GrpcWire.SecurityScheme();
            switch (scheme.SchemeCase)
            {
                case SecuritySchemeCase.ApiKey:
                    grpc.ApiKeySecurityScheme = new GrpcWire.APIKeySecurityScheme
                    {
                        Name = scheme.ApiKeySecurityScheme!.Name ?? string.Empty,
                        Location = scheme.ApiKeySecurityScheme.In ?? string.Empty,
                        Description = scheme.ApiKeySecurityScheme.Description ?? string.Empty
                    };
                    break;
                case SecuritySchemeCase.HttpAuth:
                    grpc.HttpAuthSecurityScheme = new GrpcWire.HTTPAuthSecurityScheme
                    {
                        Scheme = scheme.HttpAuthSecurityScheme!.Scheme ?? string.Empty,
                        BearerFormat = scheme.HttpAuthSecurityScheme.BearerFormat ?? string.Empty,
                        Description = scheme.HttpAuthSecurityScheme.Description ?? string.Empty
                    };
                    break;
                case SecuritySchemeCase.OAuth2:
                    grpc.Oauth2SecurityScheme = new GrpcWire.OAuth2SecurityScheme
                    {
                        Description = scheme.OAuth2SecurityScheme!.Description ?? string.Empty,
                        Flows = ToGrpc(scheme.OAuth2SecurityScheme.Flows)
                    };
                    break;
                case SecuritySchemeCase.OpenIdConnect:
                    grpc.OpenIdConnectSecurityScheme = new GrpcWire.OpenIdConnectSecurityScheme
                    {
                        OpenIdConnectUrl = scheme.OpenIdConnectSecurityScheme!.OpenIdConnectUrl ?? string.Empty,
                        Description = scheme.OpenIdConnectSecurityScheme.Description ?? string.Empty
                    };
                    break;
                case SecuritySchemeCase.Mtls:
                    grpc.MtlsSecurityScheme = new GrpcWire.MutualTlsSecurityScheme
                    {
                        Description = scheme.MtlsSecurityScheme!.Description ?? string.Empty
                    };
                    break;
            }

            return grpc;
        }

        private static SecurityScheme FromGrpc(GrpcWire.SecurityScheme scheme)
        {
            return scheme.SchemeCase switch
            {
                GrpcWire.SecurityScheme.SchemeOneofCase.ApiKeySecurityScheme => new SecurityScheme
                {
                    ApiKeySecurityScheme = new ApiKeySecurityScheme
                    {
                        Name = scheme.ApiKeySecurityScheme.Name,
                        In = scheme.ApiKeySecurityScheme.Location,
                        Description = EmptyToNull(scheme.ApiKeySecurityScheme.Description)
                    }
                },
                GrpcWire.SecurityScheme.SchemeOneofCase.HttpAuthSecurityScheme => new SecurityScheme
                {
                    HttpAuthSecurityScheme = new HttpAuthSecurityScheme
                    {
                        Scheme = scheme.HttpAuthSecurityScheme.Scheme,
                        BearerFormat = EmptyToNull(scheme.HttpAuthSecurityScheme.BearerFormat),
                        Description = EmptyToNull(scheme.HttpAuthSecurityScheme.Description)
                    }
                },
                GrpcWire.SecurityScheme.SchemeOneofCase.Oauth2SecurityScheme => new SecurityScheme
                {
                    OAuth2SecurityScheme = new OAuth2SecurityScheme
                    {
                        Description = EmptyToNull(scheme.Oauth2SecurityScheme.Description),
                        Flows = FromGrpc(scheme.Oauth2SecurityScheme.Flows)
                    }
                },
                GrpcWire.SecurityScheme.SchemeOneofCase.OpenIdConnectSecurityScheme => new SecurityScheme
                {
                    OpenIdConnectSecurityScheme = new OpenIdConnectSecurityScheme
                    {
                        OpenIdConnectUrl = scheme.OpenIdConnectSecurityScheme.OpenIdConnectUrl,
                        Description = EmptyToNull(scheme.OpenIdConnectSecurityScheme.Description)
                    }
                },
                GrpcWire.SecurityScheme.SchemeOneofCase.MtlsSecurityScheme => new SecurityScheme
                {
                    MtlsSecurityScheme = new MutualTlsSecurityScheme
                    {
                        Description = EmptyToNull(scheme.MtlsSecurityScheme.Description)
                    }
                },
                _ => new SecurityScheme()
            };
        }

        private static GrpcWire.OAuthFlows ToGrpc(OAuthFlows flows)
        {
            flows ??= new OAuthFlows();
            GrpcWire.OAuthFlows grpc = new GrpcWire.OAuthFlows();
            if (flows.AuthorizationCode != null) grpc.AuthorizationCode = ToAuthorizationCodeGrpc(flows.AuthorizationCode);
            else if (flows.ClientCredentials != null) grpc.ClientCredentials = ToClientCredentialsGrpc(flows.ClientCredentials);
            else if (flows.DeviceCode != null) grpc.DeviceCode = ToDeviceCodeGrpc(flows.DeviceCode);
            else if (flows.Implicit != null) grpc.Implicit = ToImplicitGrpc(flows.Implicit);
            else if (flows.Password != null) grpc.Password = ToPasswordGrpc(flows.Password);
            return grpc;
        }

        private static OAuthFlows FromGrpc(GrpcWire.OAuthFlows flows)
        {
            OAuthFlows model = new OAuthFlows();
            if (flows == null) return model;

            switch (flows.FlowCase)
            {
                case GrpcWire.OAuthFlows.FlowOneofCase.AuthorizationCode:
                    model.AuthorizationCode = FromAuthorizationCodeGrpc(flows.AuthorizationCode);
                    break;
                case GrpcWire.OAuthFlows.FlowOneofCase.ClientCredentials:
                    model.ClientCredentials = FromClientCredentialsGrpc(flows.ClientCredentials);
                    break;
                case GrpcWire.OAuthFlows.FlowOneofCase.DeviceCode:
                    model.DeviceCode = FromDeviceCodeGrpc(flows.DeviceCode);
                    break;
                case GrpcWire.OAuthFlows.FlowOneofCase.Implicit:
                    model.Implicit = FromImplicitGrpc(flows.Implicit);
                    break;
                case GrpcWire.OAuthFlows.FlowOneofCase.Password:
                    model.Password = FromPasswordGrpc(flows.Password);
                    break;
            }

            return model;
        }

        private static GrpcWire.AuthorizationCodeOAuthFlow ToAuthorizationCodeGrpc(OAuthFlow flow)
        {
            GrpcWire.AuthorizationCodeOAuthFlow grpc = new GrpcWire.AuthorizationCodeOAuthFlow
            {
                AuthorizationUrl = flow.AuthorizationUrl ?? string.Empty,
                TokenUrl = flow.TokenUrl ?? string.Empty,
                RefreshUrl = flow.RefreshUrl ?? string.Empty
            };
            grpc.Scopes.Add(flow.Scopes);
            return grpc;
        }

        private static OAuthFlow FromAuthorizationCodeGrpc(GrpcWire.AuthorizationCodeOAuthFlow flow)
        {
            return new OAuthFlow
            {
                AuthorizationUrl = EmptyToNull(flow.AuthorizationUrl),
                TokenUrl = EmptyToNull(flow.TokenUrl),
                RefreshUrl = EmptyToNull(flow.RefreshUrl),
                Scopes = flow.Scopes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            };
        }

        private static GrpcWire.ClientCredentialsOAuthFlow ToClientCredentialsGrpc(OAuthFlow flow)
        {
            GrpcWire.ClientCredentialsOAuthFlow grpc = new GrpcWire.ClientCredentialsOAuthFlow
            {
                TokenUrl = flow.TokenUrl ?? string.Empty,
                RefreshUrl = flow.RefreshUrl ?? string.Empty
            };
            grpc.Scopes.Add(flow.Scopes);
            return grpc;
        }

        private static OAuthFlow FromClientCredentialsGrpc(GrpcWire.ClientCredentialsOAuthFlow flow)
        {
            return new OAuthFlow
            {
                TokenUrl = EmptyToNull(flow.TokenUrl),
                RefreshUrl = EmptyToNull(flow.RefreshUrl),
                Scopes = flow.Scopes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            };
        }

        private static GrpcWire.DeviceCodeOAuthFlow ToDeviceCodeGrpc(OAuthFlow flow)
        {
            GrpcWire.DeviceCodeOAuthFlow grpc = new GrpcWire.DeviceCodeOAuthFlow
            {
                DeviceAuthorizationUrl = flow.AuthorizationUrl ?? string.Empty,
                TokenUrl = flow.TokenUrl ?? string.Empty,
                RefreshUrl = flow.RefreshUrl ?? string.Empty
            };
            grpc.Scopes.Add(flow.Scopes);
            return grpc;
        }

        private static OAuthFlow FromDeviceCodeGrpc(GrpcWire.DeviceCodeOAuthFlow flow)
        {
            return new OAuthFlow
            {
                AuthorizationUrl = EmptyToNull(flow.DeviceAuthorizationUrl),
                TokenUrl = EmptyToNull(flow.TokenUrl),
                RefreshUrl = EmptyToNull(flow.RefreshUrl),
                Scopes = flow.Scopes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            };
        }

        private static GrpcWire.ImplicitOAuthFlow ToImplicitGrpc(OAuthFlow flow)
        {
            GrpcWire.ImplicitOAuthFlow grpc = new GrpcWire.ImplicitOAuthFlow
            {
                AuthorizationUrl = flow.AuthorizationUrl ?? string.Empty,
                RefreshUrl = flow.RefreshUrl ?? string.Empty
            };
            grpc.Scopes.Add(flow.Scopes);
            return grpc;
        }

        private static OAuthFlow FromImplicitGrpc(GrpcWire.ImplicitOAuthFlow flow)
        {
            return new OAuthFlow
            {
                AuthorizationUrl = EmptyToNull(flow.AuthorizationUrl),
                RefreshUrl = EmptyToNull(flow.RefreshUrl),
                Scopes = flow.Scopes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            };
        }

        private static GrpcWire.PasswordOAuthFlow ToPasswordGrpc(OAuthFlow flow)
        {
            GrpcWire.PasswordOAuthFlow grpc = new GrpcWire.PasswordOAuthFlow
            {
                TokenUrl = flow.TokenUrl ?? string.Empty,
                RefreshUrl = flow.RefreshUrl ?? string.Empty
            };
            grpc.Scopes.Add(flow.Scopes);
            return grpc;
        }

        private static OAuthFlow FromPasswordGrpc(GrpcWire.PasswordOAuthFlow flow)
        {
            return new OAuthFlow
            {
                TokenUrl = EmptyToNull(flow.TokenUrl),
                RefreshUrl = EmptyToNull(flow.RefreshUrl),
                Scopes = flow.Scopes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            };
        }

        private static GrpcWire.TaskState ToGrpc(TaskState state)
        {
            return (GrpcWire.TaskState)(int)state;
        }

        private static TaskState FromGrpc(GrpcWire.TaskState state)
        {
            return (TaskState)(int)state;
        }

        private static GrpcWire.Role ToGrpc(Role role)
        {
            return (GrpcWire.Role)(int)role;
        }

        private static Role FromGrpc(GrpcWire.Role role)
        {
            return (Role)(int)role;
        }

        private static Struct? ToGrpcStruct(Dictionary<string, JsonElement>? values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            Struct structure = new Struct();
            foreach (KeyValuePair<string, JsonElement> item in values)
            {
                structure.Fields[item.Key] = ToGrpcValue(item.Value);
            }

            return structure;
        }

        private static Dictionary<string, JsonElement>? FromGrpcStruct(Struct? structure)
        {
            if (structure == null || structure.Fields.Count == 0)
            {
                return null;
            }

            Dictionary<string, JsonElement> values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Value> item in structure.Fields)
            {
                values[item.Key] = FromGrpcValue(item.Value);
            }

            return values;
        }

        private static Value ToGrpcValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Struct structure = new Struct();
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        structure.Fields[property.Name] = ToGrpcValue(property.Value);
                    }
                    return new Value { StructValue = structure };
                case JsonValueKind.Array:
                    ListValue list = new ListValue();
                    foreach (JsonElement child in element.EnumerateArray())
                    {
                        list.Values.Add(ToGrpcValue(child));
                    }
                    return new Value { ListValue = list };
                case JsonValueKind.String:
                    return new Value { StringValue = element.GetString() ?? string.Empty };
                case JsonValueKind.Number:
                    return new Value { NumberValue = element.GetDouble() };
                case JsonValueKind.True:
                    return new Value { BoolValue = true };
                case JsonValueKind.False:
                    return new Value { BoolValue = false };
                default:
                    return new Value { NullValue = NullValue.NullValue };
            }
        }

        private static JsonElement FromGrpcValue(Value value)
        {
            return JsonSerializer.SerializeToElement(ToObject(value), A2AJson.DefaultOptions);
        }

        private static object? ToObject(Value value)
        {
            if (value == null)
            {
                return null;
            }

            return value.KindCase switch
            {
                Value.KindOneofCase.BoolValue => value.BoolValue,
                Value.KindOneofCase.NumberValue => value.NumberValue,
                Value.KindOneofCase.StringValue => value.StringValue,
                Value.KindOneofCase.StructValue => value.StructValue.Fields.ToDictionary(item => item.Key, item => ToObject(item.Value), StringComparer.Ordinal),
                Value.KindOneofCase.ListValue => value.ListValue.Values.Select(ToObject).ToList(),
                _ => null
            };
        }

        private static string? EmptyToNull(string value)
        {
            return String.IsNullOrEmpty(value) ? null : value;
        }
    }
}
