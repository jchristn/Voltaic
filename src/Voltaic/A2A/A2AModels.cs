#pragma warning disable CS1591
namespace Voltaic.A2A
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class AgentCard
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? DocumentationUrl { get; set; }
        public string? IconUrl { get; set; }
        public List<AgentInterface> SupportedInterfaces { get; set; } = new List<AgentInterface>();
        public AgentCapabilities Capabilities { get; set; } = new AgentCapabilities();
        public AgentProvider? Provider { get; set; }
        public List<AgentSkill> Skills { get; set; } = new List<AgentSkill>();
        public List<string> DefaultInputModes { get; set; } = new List<string>();
        public List<string> DefaultOutputModes { get; set; } = new List<string>();
        public Dictionary<string, SecurityScheme>? SecuritySchemes { get; set; }
        public List<SecurityRequirement>? SecurityRequirements { get; set; }
        public List<AgentCardSignature>? Signatures { get; set; }
    }

    public class AgentCapabilities
    {
        public bool? Streaming { get; set; }
        public bool? PushNotifications { get; set; }
        public bool? StateTransitionHistory { get; set; }
        public bool? ExtendedAgentCard { get; set; }
        public List<AgentExtension>? Extensions { get; set; }
    }

    public class AgentInterface
    {
        public string Url { get; set; } = string.Empty;
        public string ProtocolBinding { get; set; } = "JSONRPC";
        public string? Tenant { get; set; }
        public string ProtocolVersion { get; set; } = A2AProtocol.ProtocolVersion;
    }

    public class AgentProvider
    {
        public string Organization { get; set; } = string.Empty;
        public string? Url { get; set; }
    }

    public class AgentSkill
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new List<string>();
        public List<string>? Examples { get; set; }
        public List<string>? InputModes { get; set; }
        public List<string>? OutputModes { get; set; }
        public List<SecurityRequirement>? SecurityRequirements { get; set; }
    }

    public class AgentExtension
    {
        public string Uri { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool? Required { get; set; }
        public Dictionary<string, JsonElement>? Params { get; set; }
    }

    public class AgentCardSignature
    {
        public string Header { get; set; } = string.Empty;
        public string Protected { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }

    public enum SecuritySchemeCase
    {
        None,
        ApiKey,
        HttpAuth,
        OAuth2,
        OpenIdConnect,
        Mtls
    }

    public class SecurityScheme
    {
        public ApiKeySecurityScheme? ApiKeySecurityScheme { get; set; }
        public HttpAuthSecurityScheme? HttpAuthSecurityScheme { get; set; }
        [JsonPropertyName("oauth2SecurityScheme")]
        public OAuth2SecurityScheme? OAuth2SecurityScheme { get; set; }
        public OpenIdConnectSecurityScheme? OpenIdConnectSecurityScheme { get; set; }
        public MutualTlsSecurityScheme? MtlsSecurityScheme { get; set; }

        [JsonIgnore]
        public SecuritySchemeCase SchemeCase =>
            ApiKeySecurityScheme != null ? SecuritySchemeCase.ApiKey :
            HttpAuthSecurityScheme != null ? SecuritySchemeCase.HttpAuth :
            OAuth2SecurityScheme != null ? SecuritySchemeCase.OAuth2 :
            OpenIdConnectSecurityScheme != null ? SecuritySchemeCase.OpenIdConnect :
            MtlsSecurityScheme != null ? SecuritySchemeCase.Mtls :
            SecuritySchemeCase.None;
    }

    public class ApiKeySecurityScheme
    {
        public string Name { get; set; } = string.Empty;
        public string In { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public class HttpAuthSecurityScheme
    {
        public string Scheme { get; set; } = string.Empty;
        public string? BearerFormat { get; set; }
        public string? Description { get; set; }
    }

    public class OAuth2SecurityScheme
    {
        public OAuthFlows Flows { get; set; } = new OAuthFlows();
        public string? Description { get; set; }
    }

    public class OpenIdConnectSecurityScheme
    {
        public string OpenIdConnectUrl { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public class MutualTlsSecurityScheme
    {
        public string? Description { get; set; }
    }

    public class OAuthFlows
    {
        public OAuthFlow? AuthorizationCode { get; set; }
        public OAuthFlow? ClientCredentials { get; set; }
        public OAuthFlow? DeviceCode { get; set; }
        public OAuthFlow? Implicit { get; set; }
        public OAuthFlow? Password { get; set; }
    }

    public class OAuthFlow
    {
        public string? AuthorizationUrl { get; set; }
        public string? TokenUrl { get; set; }
        public string? RefreshUrl { get; set; }
        public Dictionary<string, string> Scopes { get; set; } = new Dictionary<string, string>();
    }

    public class SecurityRequirement
    {
        public Dictionary<string, List<string>>? Schemes { get; set; }
    }

    [JsonConverter(typeof(RoleJsonConverter))]
    public enum Role
    {
        Unspecified = 0,
        User = 1,
        Agent = 2
    }

    public enum PartContentCase
    {
        None,
        Text,
        Raw,
        Url,
        Data
    }

    public class Part
    {
        public string? Text { get; set; }
        public byte[]? Raw { get; set; }
        public string? Url { get; set; }
        public JsonElement? Data { get; set; }
        public Dictionary<string, JsonElement>? Metadata { get; set; }
        public string? Filename { get; set; }
        public string? MediaType { get; set; }

        [JsonIgnore]
        public PartContentCase ContentCase =>
            Text != null ? PartContentCase.Text :
            Raw != null ? PartContentCase.Raw :
            Url != null ? PartContentCase.Url :
            Data != null ? PartContentCase.Data :
            PartContentCase.None;

        public static Part FromText(string text)
        {
            return new Part { Text = text };
        }

        public static Part FromRaw(byte[] raw, string? mediaType = null, string? filename = null)
        {
            return new Part { Raw = raw, MediaType = mediaType, Filename = filename };
        }

        public static Part FromUrl(string url, string? mediaType = null, string? filename = null)
        {
            return new Part { Url = url, MediaType = mediaType, Filename = filename };
        }

        public static Part FromData(JsonElement data)
        {
            return new Part { Data = data };
        }
    }

    public class Message
    {
        public Role Role { get; set; }
        public List<Part> Parts { get; set; } = new List<Part>();
        public string MessageId { get; set; } = string.Empty;
        public string? ContextId { get; set; }
        public string? TaskId { get; set; }
        public List<string>? ReferenceTaskIds { get; set; }
        public List<string>? Extensions { get; set; }
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    public class Artifact
    {
        public string ArtifactId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<Part> Parts { get; set; } = new List<Part>();
        public List<string>? Extensions { get; set; }
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    [JsonConverter(typeof(TaskStateJsonConverter))]
    public enum TaskState
    {
        Unspecified = 0,
        Submitted = 1,
        Working = 2,
        Completed = 3,
        Failed = 4,
        Canceled = 5,
        InputRequired = 6,
        Rejected = 7,
        AuthRequired = 8
    }

    public class TaskStatus
    {
        public TaskState State { get; set; }
        public Message? Message { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }

    public class AgentTask
    {
        public string Id { get; set; } = string.Empty;
        public string ContextId { get; set; } = string.Empty;
        public TaskStatus Status { get; set; } = new TaskStatus();
        public List<Message>? History { get; set; }
        public List<Artifact>? Artifacts { get; set; }
        public Dictionary<string, JsonElement>? Metadata { get; set; }

        public AgentTask WithHistoryTrimmedTo(int? historyLength)
        {
            if (historyLength == null || History == null)
            {
                return this;
            }

            AgentTask clone = Clone();
            clone.History = historyLength.Value <= 0
                ? new List<Message>()
                : clone.History?.Skip(Math.Max(0, clone.History.Count - historyLength.Value)).ToList();
            return clone;
        }

        public AgentTask Clone()
        {
            string json = JsonSerializer.Serialize(this, A2AJson.DefaultOptions);
            return JsonSerializer.Deserialize<AgentTask>(json, A2AJson.DefaultOptions) ?? new AgentTask();
        }
    }

    public class TaskStatusUpdateEvent
    {
        public string TaskId { get; set; } = string.Empty;
        public string ContextId { get; set; } = string.Empty;
        public TaskStatus Status { get; set; } = new TaskStatus();
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    public class TaskArtifactUpdateEvent
    {
        public string TaskId { get; set; } = string.Empty;
        public string ContextId { get; set; } = string.Empty;
        public Artifact Artifact { get; set; } = new Artifact();
        public bool Append { get; set; }
        public bool LastChunk { get; set; }
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    public enum SendMessageResponseCase
    {
        None,
        Task,
        Message
    }

    public class SendMessageResponse
    {
        public AgentTask? Task { get; set; }
        public Message? Message { get; set; }

        [JsonIgnore]
        public SendMessageResponseCase PayloadCase =>
            Task != null ? SendMessageResponseCase.Task :
            Message != null ? SendMessageResponseCase.Message :
            SendMessageResponseCase.None;
    }

    public enum StreamResponseCase
    {
        None,
        Task,
        Message,
        StatusUpdate,
        ArtifactUpdate
    }

    public class StreamResponse
    {
        public AgentTask? Task { get; set; }
        public Message? Message { get; set; }
        public TaskStatusUpdateEvent? StatusUpdate { get; set; }
        public TaskArtifactUpdateEvent? ArtifactUpdate { get; set; }

        [JsonIgnore]
        public StreamResponseCase PayloadCase =>
            Task != null ? StreamResponseCase.Task :
            Message != null ? StreamResponseCase.Message :
            StatusUpdate != null ? StreamResponseCase.StatusUpdate :
            ArtifactUpdate != null ? StreamResponseCase.ArtifactUpdate :
            StreamResponseCase.None;
    }

    public class SendMessageRequest
    {
        public string? Tenant { get; set; }
        public Message Message { get; set; } = new Message();
        public SendMessageConfiguration? Configuration { get; set; }
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    public class SendMessageConfiguration
    {
        public List<string>? AcceptedOutputModes { get; set; }
        public PushNotificationConfig? PushNotificationConfig { get; set; }
        public int? HistoryLength { get; set; }
        public bool ReturnImmediately { get; set; }
    }

    public class GetTaskRequest
    {
        public string? Tenant { get; set; }
        public string Id { get; set; } = string.Empty;
        public int? HistoryLength { get; set; }
    }

    public class ListTasksRequest
    {
        public string? Tenant { get; set; }
        public string? ContextId { get; set; }
        public TaskState? Status { get; set; }
        public int? PageSize { get; set; }
        public string? PageToken { get; set; }
        public int? HistoryLength { get; set; }
        public DateTimeOffset? StatusTimestampAfter { get; set; }
        public bool? IncludeArtifacts { get; set; }
    }

    public class ListTasksResponse
    {
        public List<AgentTask> Tasks { get; set; } = new List<AgentTask>();
        public string NextPageToken { get; set; } = string.Empty;
        public int PageSize { get; set; }
        public int TotalSize { get; set; }
    }

    public class CancelTaskRequest
    {
        public string? Tenant { get; set; }
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    public class SubscribeToTaskRequest
    {
        public string? Tenant { get; set; }
        public string Id { get; set; } = string.Empty;
    }

    public class GetExtendedAgentCardRequest
    {
        public string? Tenant { get; set; }
    }

    public class AuthenticationInfo
    {
        public string Scheme { get; set; } = string.Empty;
        public string? Credentials { get; set; }
    }

    public class PushNotificationConfig
    {
        public string? Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public AuthenticationInfo? Authentication { get; set; }
        public string? Token { get; set; }
    }

    public class TaskPushNotificationConfig
    {
        public string Id { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public PushNotificationConfig PushNotificationConfig { get; set; } = new PushNotificationConfig();
        public string? Tenant { get; set; }
    }

    public class CreateTaskPushNotificationConfigRequest
    {
        public string? Tenant { get; set; }
        public string TaskId { get; set; } = string.Empty;
        public string ConfigId { get; set; } = string.Empty;
        public PushNotificationConfig Config { get; set; } = new PushNotificationConfig();
    }

    public class GetTaskPushNotificationConfigRequest
    {
        public string? Tenant { get; set; }
        public string TaskId { get; set; } = string.Empty;
        public string ConfigId { get; set; } = string.Empty;
    }

    public class ListTaskPushNotificationConfigRequest
    {
        public string? Tenant { get; set; }
        public string TaskId { get; set; } = string.Empty;
    }

    public class ListTaskPushNotificationConfigResponse
    {
        public List<TaskPushNotificationConfig> Configs { get; set; } = new List<TaskPushNotificationConfig>();
    }

    public class DeleteTaskPushNotificationConfigRequest
    {
        public string? Tenant { get; set; }
        public string TaskId { get; set; } = string.Empty;
        public string ConfigId { get; set; } = string.Empty;
    }
}
