namespace Voltaic.A2A
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// An A2A Agent Card: the self-describing document an agent publishes (at <c>/.well-known/agent-card.json</c>)
    /// to advertise its identity, endpoints, capabilities, skills, and security requirements.
    /// Instances are plain data objects and are not thread-safe.
    /// </summary>
    public class AgentCard
    {
        /// <summary>
        /// Gets or sets the human-readable agent name. Default is an empty string.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a human-readable description of what the agent does. Default is an empty string.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the agent's own version string (not the A2A protocol version). Default is an empty string.
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional URL of documentation for the agent. Null when not provided.
        /// </summary>
        public string? DocumentationUrl { get; set; }

        /// <summary>
        /// Gets or sets an optional URL of an icon for the agent. Null when not provided.
        /// </summary>
        public string? IconUrl { get; set; }

        /// <summary>
        /// Gets or sets the endpoints the agent serves, each with its URL, protocol binding, and protocol version.
        /// The first entry is the preferred interface. Default is an empty list.
        /// </summary>
        public List<AgentInterface> SupportedInterfaces { get; set; } = new List<AgentInterface>();

        /// <summary>
        /// Gets or sets the optional protocol features the agent supports. Default is an instance with every capability unset.
        /// </summary>
        public AgentCapabilities Capabilities { get; set; } = new AgentCapabilities();

        /// <summary>
        /// Gets or sets the organization that provides the agent. Null when not provided.
        /// </summary>
        public AgentProvider? Provider { get; set; }

        /// <summary>
        /// Gets or sets the skills (distinct capabilities) the agent offers. Default is an empty list.
        /// </summary>
        public List<AgentSkill> Skills { get; set; } = new List<AgentSkill>();

        /// <summary>
        /// Gets or sets the media types the agent accepts as input across all skills, for example <c>text/plain</c>.
        /// Default is an empty list.
        /// </summary>
        public List<string> DefaultInputModes { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the media types the agent produces as output across all skills. Default is an empty list.
        /// </summary>
        public List<string> DefaultOutputModes { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the security schemes available to authenticate to the agent, keyed by scheme name.
        /// Null when the agent declares none.
        /// </summary>
        public Dictionary<string, SecurityScheme>? SecuritySchemes { get; set; }

        /// <summary>
        /// Gets or sets the security requirements that apply to all agent interactions; each entry is one acceptable
        /// combination of schemes. Null when none are declared.
        /// </summary>
        public List<SecurityRequirement>? SecurityRequirements { get; set; }

        /// <summary>
        /// Gets or sets JSON Web Signatures computed over the Agent Card. Null when the card is unsigned.
        /// </summary>
        public List<AgentCardSignature>? Signatures { get; set; }
    }

    /// <summary>
    /// Optional A2A protocol features an agent supports. A null value means the capability is not declared
    /// (treated as unsupported). Instances are not thread-safe.
    /// </summary>
    public class AgentCapabilities
    {
        /// <summary>
        /// Gets or sets whether the agent supports streaming responses (<c>SendStreamingMessage</c> and
        /// <c>SubscribeToTask</c>). Null when not declared.
        /// </summary>
        public bool? Streaming { get; set; }

        /// <summary>
        /// Gets or sets whether the agent supports push notification configuration for tasks. Null when not declared.
        /// </summary>
        public bool? PushNotifications { get; set; }

        /// <summary>
        /// Gets or sets whether the agent records task state transition history. Null when not declared.
        /// </summary>
        public bool? StateTransitionHistory { get; set; }

        /// <summary>
        /// Gets or sets whether the agent serves an authenticated extended Agent Card. Null when not declared.
        /// </summary>
        public bool? ExtendedAgentCard { get; set; }

        /// <summary>
        /// Gets or sets the protocol extensions the agent supports. Null when none are declared.
        /// </summary>
        public List<AgentExtension>? Extensions { get; set; }
    }

    /// <summary>
    /// One endpoint an agent serves: its URL, protocol binding, and A2A protocol version. Instances are not thread-safe.
    /// </summary>
    public class AgentInterface
    {
        /// <summary>
        /// Gets or sets the absolute URL of the endpoint. Default is an empty string.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the protocol binding served at <see cref="Url"/>: <c>JSONRPC</c>, <c>HTTP+JSON</c>, or
        /// <c>GRPC</c>. Default is <c>JSONRPC</c>.
        /// </summary>
        public string ProtocolBinding { get; set; } = "JSONRPC";

        /// <summary>
        /// Gets or sets an optional tenant identifier that requests to this interface must carry. Null when the
        /// interface is not multi-tenant.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the A2A protocol version served at this interface. Default is <see cref="A2AProtocol.ProtocolVersion"/>.
        /// </summary>
        public string ProtocolVersion { get; set; } = A2AProtocol.ProtocolVersion;
    }

    /// <summary>
    /// The organization that provides an agent. Instances are not thread-safe.
    /// </summary>
    public class AgentProvider
    {
        /// <summary>
        /// Gets or sets the provider's organization name. Default is an empty string.
        /// </summary>
        public string Organization { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the provider's website URL. Null when not provided.
        /// </summary>
        public string? Url { get; set; }
    }

    /// <summary>
    /// A distinct capability an agent offers, described for clients and routing. Instances are not thread-safe.
    /// </summary>
    public class AgentSkill
    {
        /// <summary>
        /// Gets or sets the skill identifier, unique within the agent. Default is an empty string.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the human-readable skill name. Default is an empty string.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a description of what the skill does. Default is an empty string.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets keywords describing the skill. Default is an empty list.
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets example prompts or scenarios the skill handles. Null when none are provided.
        /// </summary>
        public List<string>? Examples { get; set; }

        /// <summary>
        /// Gets or sets the input media types this skill accepts, overriding <see cref="AgentCard.DefaultInputModes"/>.
        /// Null to use the card defaults.
        /// </summary>
        public List<string>? InputModes { get; set; }

        /// <summary>
        /// Gets or sets the output media types this skill produces, overriding <see cref="AgentCard.DefaultOutputModes"/>.
        /// Null to use the card defaults.
        /// </summary>
        public List<string>? OutputModes { get; set; }

        /// <summary>
        /// Gets or sets security requirements specific to this skill. Null when the card-level requirements apply.
        /// </summary>
        public List<SecurityRequirement>? SecurityRequirements { get; set; }
    }

    /// <summary>
    /// A protocol extension an agent supports. Instances are not thread-safe.
    /// </summary>
    public class AgentExtension
    {
        /// <summary>
        /// Gets or sets the URI that identifies the extension. Default is an empty string.
        /// </summary>
        public string Uri { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a description of how the agent uses the extension. Null when not provided.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets whether clients must support the extension to interact with the agent. Null when not declared (not required).
        /// </summary>
        public bool? Required { get; set; }

        /// <summary>
        /// Gets or sets extension-specific configuration parameters. Null when none are provided.
        /// </summary>
        public Dictionary<string, object?>? Params { get; set; }
    }

    /// <summary>
    /// A JSON Web Signature (RFC 7515) over an Agent Card, in flattened form. Instances are not thread-safe.
    /// </summary>
    public class AgentCardSignature
    {
        /// <summary>
        /// Gets or sets the unprotected JWS header. Default is an empty string.
        /// </summary>
        public string Header { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the base64url-encoded protected JWS header. Default is an empty string.
        /// </summary>
        public string Protected { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the base64url-encoded signature. Default is an empty string.
        /// </summary>
        public string Signature { get; set; } = string.Empty;
    }

    /// <summary>
    /// Identifies which variant a <see cref="SecurityScheme"/> carries.
    /// </summary>
    public enum SecuritySchemeCase
    {
        /// <summary>
        /// No variant is set.
        /// </summary>
        None,

        /// <summary>
        /// <see cref="SecurityScheme.ApiKeySecurityScheme"/> is set.
        /// </summary>
        ApiKey,

        /// <summary>
        /// <see cref="SecurityScheme.HttpAuthSecurityScheme"/> is set.
        /// </summary>
        HttpAuth,

        /// <summary>
        /// <see cref="SecurityScheme.OAuth2SecurityScheme"/> is set.
        /// </summary>
        OAuth2,

        /// <summary>
        /// <see cref="SecurityScheme.OpenIdConnectSecurityScheme"/> is set.
        /// </summary>
        OpenIdConnect,

        /// <summary>
        /// <see cref="SecurityScheme.MtlsSecurityScheme"/> is set.
        /// </summary>
        Mtls
    }

    /// <summary>
    /// A security scheme an agent accepts. Exactly one variant property should be set; <see cref="SchemeCase"/>
    /// reports which. Instances are not thread-safe.
    /// </summary>
    public class SecurityScheme
    {
        /// <summary>
        /// Gets or sets the API key scheme. Null when another variant is used.
        /// </summary>
        public ApiKeySecurityScheme? ApiKeySecurityScheme { get; set; }

        /// <summary>
        /// Gets or sets the HTTP authentication scheme (for example Bearer). Null when another variant is used.
        /// </summary>
        public HttpAuthSecurityScheme? HttpAuthSecurityScheme { get; set; }

        /// <summary>
        /// Gets or sets the OAuth 2.0 scheme, serialized as <c>oauth2SecurityScheme</c>. Null when another variant is used.
        /// </summary>
        [JsonPropertyName("oauth2SecurityScheme")]
        public OAuth2SecurityScheme? OAuth2SecurityScheme { get; set; }

        /// <summary>
        /// Gets or sets the OpenID Connect scheme. Null when another variant is used.
        /// </summary>
        public OpenIdConnectSecurityScheme? OpenIdConnectSecurityScheme { get; set; }

        /// <summary>
        /// Gets or sets the mutual TLS scheme. Null when another variant is used.
        /// </summary>
        public MutualTlsSecurityScheme? MtlsSecurityScheme { get; set; }

        /// <summary>
        /// Gets which variant is set, checked in declaration order; <see cref="SecuritySchemeCase.None"/> when none is.
        /// Not serialized.
        /// </summary>
        [JsonIgnore]
        public SecuritySchemeCase SchemeCase =>
            ApiKeySecurityScheme != null ? SecuritySchemeCase.ApiKey :
            HttpAuthSecurityScheme != null ? SecuritySchemeCase.HttpAuth :
            OAuth2SecurityScheme != null ? SecuritySchemeCase.OAuth2 :
            OpenIdConnectSecurityScheme != null ? SecuritySchemeCase.OpenIdConnect :
            MtlsSecurityScheme != null ? SecuritySchemeCase.Mtls :
            SecuritySchemeCase.None;
    }

    /// <summary>
    /// An API key security scheme. Instances are not thread-safe.
    /// </summary>
    public class ApiKeySecurityScheme
    {
        /// <summary>
        /// Gets or sets the name of the header, query parameter, or cookie carrying the key. Default is an empty string.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets where the key is sent: <c>header</c>, <c>query</c>, or <c>cookie</c>. Default is an empty string.
        /// </summary>
        public string In { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a description of the scheme. Null when not provided.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// An HTTP authentication security scheme (RFC 7235), such as Bearer or Basic. Instances are not thread-safe.
    /// </summary>
    public class HttpAuthSecurityScheme
    {
        /// <summary>
        /// Gets or sets the HTTP authentication scheme name, for example <c>Bearer</c>. Default is an empty string.
        /// </summary>
        public string Scheme { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a hint about the bearer token format, for example <c>JWT</c>. Null when not provided.
        /// </summary>
        public string? BearerFormat { get; set; }

        /// <summary>
        /// Gets or sets a description of the scheme. Null when not provided.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// An OAuth 2.0 security scheme. Instances are not thread-safe.
    /// </summary>
    public class OAuth2SecurityScheme
    {
        /// <summary>
        /// Gets or sets the OAuth flows the agent supports. Default is an instance with no flows set.
        /// </summary>
        public OAuthFlows Flows { get; set; } = new OAuthFlows();

        /// <summary>
        /// Gets or sets a description of the scheme. Null when not provided.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// An OpenID Connect security scheme. Instances are not thread-safe.
    /// </summary>
    public class OpenIdConnectSecurityScheme
    {
        /// <summary>
        /// Gets or sets the OpenID Connect discovery URL. Default is an empty string.
        /// </summary>
        public string OpenIdConnectUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a description of the scheme. Null when not provided.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// A mutual TLS security scheme: clients authenticate with a TLS client certificate. Instances are not thread-safe.
    /// </summary>
    public class MutualTlsSecurityScheme
    {
        /// <summary>
        /// Gets or sets a description of the scheme. Null when not provided.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// The OAuth 2.0 flows an <see cref="OAuth2SecurityScheme"/> supports. Unset flows are null.
    /// Instances are not thread-safe.
    /// </summary>
    public class OAuthFlows
    {
        /// <summary>
        /// Gets or sets the authorization code flow. Null when not supported.
        /// </summary>
        public OAuthFlow? AuthorizationCode { get; set; }

        /// <summary>
        /// Gets or sets the client credentials flow. Null when not supported.
        /// </summary>
        public OAuthFlow? ClientCredentials { get; set; }

        /// <summary>
        /// Gets or sets the device authorization flow (RFC 8628). Null when not supported.
        /// </summary>
        public OAuthFlow? DeviceCode { get; set; }

        /// <summary>
        /// Gets or sets the implicit flow (deprecated in OAuth 2.1). Null when not supported.
        /// </summary>
        public OAuthFlow? Implicit { get; set; }

        /// <summary>
        /// Gets or sets the resource owner password flow (deprecated in OAuth 2.1). Null when not supported.
        /// </summary>
        public OAuthFlow? Password { get; set; }
    }

    /// <summary>
    /// The endpoints and scopes of one OAuth 2.0 flow. Which URLs apply depends on the flow. Instances are not thread-safe.
    /// </summary>
    public class OAuthFlow
    {
        /// <summary>
        /// Gets or sets the authorization endpoint URL (authorization code and implicit flows). Null when not applicable.
        /// </summary>
        public string? AuthorizationUrl { get; set; }

        /// <summary>
        /// Gets or sets the token endpoint URL. Null when not applicable (implicit flow).
        /// </summary>
        public string? TokenUrl { get; set; }

        /// <summary>
        /// Gets or sets the refresh token endpoint URL. Null when not provided.
        /// </summary>
        public string? RefreshUrl { get; set; }

        /// <summary>
        /// Gets or sets the available scopes, mapping each scope name to its description. Default is an empty dictionary.
        /// </summary>
        public Dictionary<string, string> Scopes { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// One acceptable combination of security schemes: a request satisfies it by meeting every listed scheme.
    /// Instances are not thread-safe.
    /// </summary>
    public class SecurityRequirement
    {
        /// <summary>
        /// Gets or sets the required schemes, mapping each scheme name (a key of <see cref="AgentCard.SecuritySchemes"/>)
        /// to the scopes it requires (empty when the scheme has no scopes). Null when no schemes are listed.
        /// </summary>
        public Dictionary<string, List<string>>? Schemes { get; set; }
    }

    /// <summary>
    /// The sender of a <see cref="Message"/>. Serialized as <c>ROLE_USER</c>, <c>ROLE_AGENT</c>, or
    /// <c>ROLE_UNSPECIFIED</c>; the lowercase forms <c>user</c> and <c>agent</c> are also accepted when reading.
    /// </summary>
    [JsonConverter(typeof(RoleJsonConverter))]
    public enum Role
    {
        /// <summary>
        /// The role is not specified.
        /// </summary>
        Unspecified = 0,

        /// <summary>
        /// The message was sent by the client (user side).
        /// </summary>
        User = 1,

        /// <summary>
        /// The message was sent by the agent.
        /// </summary>
        Agent = 2
    }

    /// <summary>
    /// Identifies which content variant a <see cref="Part"/> carries.
    /// </summary>
    public enum PartContentCase
    {
        /// <summary>
        /// No content is set.
        /// </summary>
        None,

        /// <summary>
        /// <see cref="Part.Text"/> is set.
        /// </summary>
        Text,

        /// <summary>
        /// <see cref="Part.Raw"/> is set.
        /// </summary>
        Raw,

        /// <summary>
        /// <see cref="Part.Url"/> is set.
        /// </summary>
        Url,

        /// <summary>
        /// <see cref="Part.Data"/> is set.
        /// </summary>
        Data
    }

    /// <summary>
    /// One piece of content in a <see cref="Message"/> or <see cref="Artifact"/>: text, raw bytes, a URL reference,
    /// or structured data. Exactly one content property should be set; <see cref="ContentCase"/> reports which.
    /// Instances are not thread-safe.
    /// </summary>
    public class Part
    {
        /// <summary>
        /// Gets or sets text content. Null when another content variant is used.
        /// </summary>
        public string? Text { get; set; }

        /// <summary>
        /// Gets or sets raw binary content (base64-encoded in JSON). Null when another content variant is used.
        /// </summary>
        public byte[]? Raw { get; set; }

        /// <summary>
        /// Gets or sets a URL referencing the content. Null when another content variant is used.
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets structured JSON content. Null when another content variant is used.
        /// </summary>
        public object? Data { get; set; }

        /// <summary>
        /// Gets or sets optional metadata associated with the part. Null when none is provided.
        /// </summary>
        public Dictionary<string, object?>? Metadata { get; set; }

        /// <summary>
        /// Gets or sets an optional file name for file content (<see cref="Raw"/> or <see cref="Url"/>). Null when not provided.
        /// </summary>
        public string? Filename { get; set; }

        /// <summary>
        /// Gets or sets the media type of the content, for example <c>image/png</c>. Null when not provided.
        /// </summary>
        public string? MediaType { get; set; }

        /// <summary>
        /// Gets which content variant is set, checked in the order text, raw, URL, data;
        /// <see cref="PartContentCase.None"/> when none is. Not serialized.
        /// </summary>
        [JsonIgnore]
        public PartContentCase ContentCase =>
            Text != null ? PartContentCase.Text :
            Raw != null ? PartContentCase.Raw :
            Url != null ? PartContentCase.Url :
            Data != null ? PartContentCase.Data :
            PartContentCase.None;

        /// <summary>
        /// Creates a text part.
        /// </summary>
        /// <param name="text">The text content. Should not be null; a null value produces a part with no content.</param>
        /// <returns>A new part carrying <paramref name="text"/>.</returns>
        public static Part FromText(string text)
        {
            return new Part { Text = text };
        }

        /// <summary>
        /// Creates a part carrying raw bytes.
        /// </summary>
        /// <param name="raw">The binary content. Should not be null; a null value produces a part with no content.</param>
        /// <param name="mediaType">Optional media type of the content. Default is null.</param>
        /// <param name="filename">Optional file name. Default is null.</param>
        /// <returns>A new part carrying <paramref name="raw"/>.</returns>
        public static Part FromRaw(byte[] raw, string? mediaType = null, string? filename = null)
        {
            return new Part { Raw = raw, MediaType = mediaType, Filename = filename };
        }

        /// <summary>
        /// Creates a part that references content by URL.
        /// </summary>
        /// <param name="url">The content URL. Should not be null; a null value produces a part with no content.</param>
        /// <param name="mediaType">Optional media type of the content. Default is null.</param>
        /// <param name="filename">Optional file name. Default is null.</param>
        /// <returns>A new part referencing <paramref name="url"/>.</returns>
        public static Part FromUrl(string url, string? mediaType = null, string? filename = null)
        {
            return new Part { Url = url, MediaType = mediaType, Filename = filename };
        }

        /// <summary>
        /// Creates a part carrying structured data.
        /// </summary>
        /// <param name="data">A JSON-serializable value. Null produces a part with no content.</param>
        /// <returns>A new part carrying <paramref name="data"/>.</returns>
        public static Part FromData(object? data)
        {
            return new Part { Data = data };
        }
    }

    /// <summary>
    /// One communication turn between a client and an agent. Instances are not thread-safe.
    /// </summary>
    public class Message
    {
        /// <summary>
        /// Gets or sets who sent the message. Default is <see cref="Role.Unspecified"/>.
        /// </summary>
        public Role Role { get; set; }

        /// <summary>
        /// Gets or sets the message content. Default is an empty list.
        /// </summary>
        public List<Part> Parts { get; set; } = new List<Part>();

        /// <summary>
        /// Gets or sets the sender-generated message identifier. Default is an empty string.
        /// </summary>
        public string MessageId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the conversation context the message belongs to. Null lets the agent assign or infer one.
        /// </summary>
        public string? ContextId { get; set; }

        /// <summary>
        /// Gets or sets the task the message continues. Null starts a new task (or needs none).
        /// </summary>
        public string? TaskId { get; set; }

        /// <summary>
        /// Gets or sets identifiers of other tasks the message refers to for context. Null when none.
        /// </summary>
        public List<string>? ReferenceTaskIds { get; set; }

        /// <summary>
        /// Gets or sets the URIs of extensions that apply to this message. Null when none.
        /// </summary>
        public List<string>? Extensions { get; set; }

        /// <summary>
        /// Gets or sets optional message metadata. Null when none.
        /// </summary>
        public Dictionary<string, object?>? Metadata { get; set; }
    }

    /// <summary>
    /// An output an agent produces for a task, such as a document or data set. Instances are not thread-safe.
    /// </summary>
    public class Artifact
    {
        /// <summary>
        /// Gets or sets the artifact identifier, unique within its task. Default is an empty string.
        /// </summary>
        public string ArtifactId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a human-readable artifact name. Null when not provided.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets a description of the artifact. Null when not provided.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the artifact content. Default is an empty list.
        /// </summary>
        public List<Part> Parts { get; set; } = new List<Part>();

        /// <summary>
        /// Gets or sets the URIs of extensions that apply to this artifact. Null when none.
        /// </summary>
        public List<string>? Extensions { get; set; }

        /// <summary>
        /// Gets or sets optional artifact metadata. Null when none.
        /// </summary>
        public Dictionary<string, object?>? Metadata { get; set; }
    }

    /// <summary>
    /// The lifecycle state of an <see cref="AgentTask"/>. Serialized as the A2A wire names
    /// (<c>TASK_STATE_SUBMITTED</c>, <c>TASK_STATE_WORKING</c>, and so on). <see cref="Completed"/>,
    /// <see cref="Failed"/>, <see cref="Canceled"/>, and <see cref="Rejected"/> are terminal;
    /// <see cref="InputRequired"/> and <see cref="AuthRequired"/> are interrupted states awaiting the client.
    /// </summary>
    [JsonConverter(typeof(TaskStateJsonConverter))]
    public enum TaskState
    {
        /// <summary>
        /// The state is not specified.
        /// </summary>
        Unspecified = 0,

        /// <summary>
        /// The task was received and acknowledged but has not started.
        /// </summary>
        Submitted = 1,

        /// <summary>
        /// The agent is processing the task.
        /// </summary>
        Working = 2,

        /// <summary>
        /// The task finished successfully. Terminal.
        /// </summary>
        Completed = 3,

        /// <summary>
        /// The task finished with an error. Terminal.
        /// </summary>
        Failed = 4,

        /// <summary>
        /// The task was canceled. Terminal.
        /// </summary>
        Canceled = 5,

        /// <summary>
        /// The agent needs more input from the client to continue.
        /// </summary>
        InputRequired = 6,

        /// <summary>
        /// The agent declined to perform the task. Terminal.
        /// </summary>
        Rejected = 7,

        /// <summary>
        /// The client must authenticate (or provide additional credentials) to continue.
        /// </summary>
        AuthRequired = 8
    }

    /// <summary>
    /// The current status of a task: its state, an optional message, and when the state was set.
    /// Instances are not thread-safe.
    /// </summary>
    public class TaskStatus
    {
        /// <summary>
        /// Gets or sets the task state. Default is <see cref="TaskState.Unspecified"/>.
        /// </summary>
        public TaskState State { get; set; }

        /// <summary>
        /// Gets or sets an optional message describing the status, for example the question for
        /// <see cref="TaskState.InputRequired"/>. Null when none.
        /// </summary>
        public Message? Message { get; set; }

        /// <summary>
        /// Gets or sets when the status was set. Null when not recorded.
        /// </summary>
        public DateTimeOffset? Timestamp { get; set; }
    }

    /// <summary>
    /// A unit of work an agent performs, with its status, message history, and artifacts. Named <c>AgentTask</c> to
    /// avoid a clash with <see cref="System.Threading.Tasks.Task"/>. Instances are not thread-safe.
    /// </summary>
    public class AgentTask
    {
        /// <summary>
        /// Gets or sets the server-generated task identifier. Default is an empty string.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the conversation context the task belongs to. Default is an empty string.
        /// </summary>
        public string ContextId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the current task status. Default is a status with <see cref="TaskState.Unspecified"/>.
        /// </summary>
        public TaskStatus Status { get; set; } = new TaskStatus();

        /// <summary>
        /// Gets or sets the messages exchanged for the task, oldest first. Null when history is not included.
        /// </summary>
        public List<Message>? History { get; set; }

        /// <summary>
        /// Gets or sets the artifacts the task has produced. Null when none are included.
        /// </summary>
        public List<Artifact>? Artifacts { get; set; }

        /// <summary>
        /// Gets or sets optional task metadata. Null when none.
        /// </summary>
        public Dictionary<string, object?>? Metadata { get; set; }

        /// <summary>
        /// Returns the task with its history limited to the most recent <paramref name="historyLength"/> messages,
        /// as the A2A <c>historyLength</c> request parameter requires. The original instance is not modified.
        /// </summary>
        /// <param name="historyLength">
        /// The maximum number of history messages to keep. Null (or a task without history) returns this same instance
        /// unchanged. Zero or a negative value returns a copy with an empty history.
        /// </param>
        /// <returns>This instance when no trimming applies; otherwise a trimmed deep copy.</returns>
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

        /// <summary>
        /// Creates a deep copy of the task by round-tripping it through JSON with <see cref="A2AJson.DefaultOptions"/>.
        /// Values in <see cref="Metadata"/> and <see cref="Part.Data"/> come back as <see cref="JsonElement"/> instances.
        /// </summary>
        /// <returns>A new, independent task instance.</returns>
        public AgentTask Clone()
        {
            string json = JsonSerializer.Serialize(this, A2AJson.DefaultOptions);
            return JsonSerializer.Deserialize<AgentTask>(json, A2AJson.DefaultOptions) ?? new AgentTask();
        }
    }

    /// <summary>
    /// A streaming event reporting a task status change. Instances are not thread-safe.
    /// </summary>
    public class TaskStatusUpdateEvent
    {
        /// <summary>
        /// Gets or sets the task the event belongs to. Default is an empty string.
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the task's conversation context. Default is an empty string.
        /// </summary>
        public string ContextId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the new task status. Default is a status with <see cref="TaskState.Unspecified"/>.
        /// </summary>
        public TaskStatus Status { get; set; } = new TaskStatus();

        /// <summary>
        /// Gets or sets optional event metadata. Null when none.
        /// </summary>
        public Dictionary<string, object?>? Metadata { get; set; }
    }

    /// <summary>
    /// A streaming event delivering a new or updated task artifact, possibly in chunks. Instances are not thread-safe.
    /// </summary>
    public class TaskArtifactUpdateEvent
    {
        /// <summary>
        /// Gets or sets the task the event belongs to. Default is an empty string.
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the task's conversation context. Default is an empty string.
        /// </summary>
        public string ContextId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the artifact, or the chunk of it this event carries. Default is an empty artifact.
        /// </summary>
        public Artifact Artifact { get; set; } = new Artifact();

        /// <summary>
        /// Gets or sets whether the parts are appended to a previously sent artifact with the same identifier
        /// (true) or replace it (false). Default is false.
        /// </summary>
        public bool Append { get; set; }

        /// <summary>
        /// Gets or sets whether this is the final chunk of the artifact. Default is false.
        /// </summary>
        public bool LastChunk { get; set; }

        /// <summary>
        /// Gets or sets optional event metadata. Null when none.
        /// </summary>
        public Dictionary<string, object?>? Metadata { get; set; }
    }

    /// <summary>
    /// Identifies which payload a <see cref="SendMessageResponse"/> carries.
    /// </summary>
    public enum SendMessageResponseCase
    {
        /// <summary>
        /// No payload is set.
        /// </summary>
        None,

        /// <summary>
        /// <see cref="SendMessageResponse.Task"/> is set.
        /// </summary>
        Task,

        /// <summary>
        /// <see cref="SendMessageResponse.Message"/> is set.
        /// </summary>
        Message
    }

    /// <summary>
    /// The result of <c>SendMessage</c>: either a task (for work that is tracked) or a direct message reply.
    /// Exactly one payload should be set. Instances are not thread-safe.
    /// </summary>
    public class SendMessageResponse
    {
        /// <summary>
        /// Gets or sets the task the message created or continued. Null when the agent replied with a message.
        /// </summary>
        public AgentTask? Task { get; set; }

        /// <summary>
        /// Gets or sets the agent's direct reply. Null when the agent returned a task.
        /// </summary>
        public Message? Message { get; set; }

        /// <summary>
        /// Gets which payload is set, preferring <see cref="Task"/>; <see cref="SendMessageResponseCase.None"/> when
        /// neither is. Not serialized.
        /// </summary>
        [JsonIgnore]
        public SendMessageResponseCase PayloadCase =>
            Task != null ? SendMessageResponseCase.Task :
            Message != null ? SendMessageResponseCase.Message :
            SendMessageResponseCase.None;
    }

    /// <summary>
    /// Identifies which payload a <see cref="StreamResponse"/> carries.
    /// </summary>
    public enum StreamResponseCase
    {
        /// <summary>
        /// No payload is set.
        /// </summary>
        None,

        /// <summary>
        /// <see cref="StreamResponse.Task"/> is set.
        /// </summary>
        Task,

        /// <summary>
        /// <see cref="StreamResponse.Message"/> is set.
        /// </summary>
        Message,

        /// <summary>
        /// <see cref="StreamResponse.StatusUpdate"/> is set.
        /// </summary>
        StatusUpdate,

        /// <summary>
        /// <see cref="StreamResponse.ArtifactUpdate"/> is set.
        /// </summary>
        ArtifactUpdate
    }

    /// <summary>
    /// One event on an A2A stream (<c>SendStreamingMessage</c> or <c>SubscribeToTask</c>). Exactly one payload
    /// should be set. Instances are not thread-safe.
    /// </summary>
    public class StreamResponse
    {
        /// <summary>
        /// Gets or sets a task snapshot. Null when another payload is used.
        /// </summary>
        public AgentTask? Task { get; set; }

        /// <summary>
        /// Gets or sets a message from the agent. Null when another payload is used.
        /// </summary>
        public Message? Message { get; set; }

        /// <summary>
        /// Gets or sets a task status change. Null when another payload is used.
        /// </summary>
        public TaskStatusUpdateEvent? StatusUpdate { get; set; }

        /// <summary>
        /// Gets or sets an artifact update. Null when another payload is used.
        /// </summary>
        public TaskArtifactUpdateEvent? ArtifactUpdate { get; set; }

        /// <summary>
        /// Gets which payload is set, checked in the order task, message, status update, artifact update;
        /// <see cref="StreamResponseCase.None"/> when none is. Not serialized.
        /// </summary>
        [JsonIgnore]
        public StreamResponseCase PayloadCase =>
            Task != null ? StreamResponseCase.Task :
            Message != null ? StreamResponseCase.Message :
            StatusUpdate != null ? StreamResponseCase.StatusUpdate :
            ArtifactUpdate != null ? StreamResponseCase.ArtifactUpdate :
            StreamResponseCase.None;
    }

    /// <summary>
    /// Parameters of <c>SendMessage</c> and <c>SendStreamingMessage</c>. Instances are not thread-safe.
    /// </summary>
    public class SendMessageRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the message to send. Default is an empty message.
        /// </summary>
        public Message Message { get; set; } = new Message();

        /// <summary>
        /// Gets or sets optional send behavior (output modes, push notifications, history length, blocking).
        /// Null uses the defaults.
        /// </summary>
        public SendMessageConfiguration? Configuration { get; set; }

        /// <summary>
        /// Gets or sets optional request metadata. Null when none.
        /// </summary>
        public Dictionary<string, object?>? Metadata { get; set; }
    }

    /// <summary>
    /// Options that control how <c>SendMessage</c> is processed. Instances are not thread-safe.
    /// </summary>
    public class SendMessageConfiguration
    {
        /// <summary>
        /// Gets or sets the output media types the client accepts. Null accepts any.
        /// </summary>
        public List<string>? AcceptedOutputModes { get; set; }

        /// <summary>
        /// Gets or sets a push notification configuration to register for the resulting task. Null registers none.
        /// Serialized as <c>taskPushNotificationConfig</c>, the A2A v1.0 name; the older name
        /// <c>pushNotificationConfig</c> is also accepted when reading.
        /// </summary>
        [JsonPropertyName("taskPushNotificationConfig")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PushNotificationConfig? PushNotificationConfig { get; set; }

        [JsonInclude]
        [JsonPropertyName("pushNotificationConfig")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        private PushNotificationConfig? LegacyPushNotificationConfig
        {
            get => null;
            set
            {
                if (value != null && PushNotificationConfig == null) PushNotificationConfig = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of history messages to include in the returned task. Null includes the
        /// full history; zero includes none.
        /// </summary>
        public int? HistoryLength { get; set; }

        /// <summary>
        /// Gets or sets whether the server returns as soon as the task is created (true) instead of waiting for it to
        /// reach a terminal or interrupted state (false). Default is false.
        /// </summary>
        public bool ReturnImmediately { get; set; }
    }

    /// <summary>
    /// Parameters of <c>GetTask</c>. Instances are not thread-safe.
    /// </summary>
    public class GetTaskRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the task to retrieve. Default is an empty string.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the maximum number of history messages to include. Null includes the full history.
        /// </summary>
        public int? HistoryLength { get; set; }
    }

    /// <summary>
    /// Parameters of <c>ListTasks</c>. Every filter is optional. Instances are not thread-safe.
    /// </summary>
    public class ListTasksRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets a context to list tasks for. Null lists tasks from all contexts.
        /// </summary>
        public string? ContextId { get; set; }

        /// <summary>
        /// Gets or sets a task state to filter by. Null includes all states.
        /// </summary>
        public TaskState? Status { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of tasks per page. Null uses the server default.
        /// </summary>
        public int? PageSize { get; set; }

        /// <summary>
        /// Gets or sets the <see cref="ListTasksResponse.NextPageToken"/> of a previous page. Null starts at the first page.
        /// </summary>
        public string? PageToken { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of history messages to include per task. Null uses the server default.
        /// </summary>
        public int? HistoryLength { get; set; }

        /// <summary>
        /// Gets or sets a lower bound on the tasks' status timestamp; only tasks updated after it are returned.
        /// Null applies no bound.
        /// </summary>
        public DateTimeOffset? StatusTimestampAfter { get; set; }

        /// <summary>
        /// Gets or sets whether artifacts are included in the returned tasks. Null uses the server default.
        /// </summary>
        public bool? IncludeArtifacts { get; set; }
    }

    /// <summary>
    /// The result of <c>ListTasks</c>: one page of tasks. Instances are not thread-safe.
    /// </summary>
    public class ListTasksResponse
    {
        /// <summary>
        /// Gets or sets the tasks on this page. Default is an empty list.
        /// </summary>
        public List<AgentTask> Tasks { get; set; } = new List<AgentTask>();

        /// <summary>
        /// Gets or sets the token for the next page, or an empty string when this is the last page.
        /// </summary>
        public string NextPageToken { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of tasks on this page. Default is 0.
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// Gets or sets the total number of tasks matching the filters across all pages. Default is 0.
        /// </summary>
        public int TotalSize { get; set; }
    }

    /// <summary>
    /// Parameters of <c>CancelTask</c>. Instances are not thread-safe.
    /// </summary>
    public class CancelTaskRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the task to cancel. Default is an empty string.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets optional request metadata. Null when none.
        /// </summary>
        public Dictionary<string, object?>? Metadata { get; set; }
    }

    /// <summary>
    /// Parameters of <c>SubscribeToTask</c>, which streams updates for an existing task. Instances are not thread-safe.
    /// </summary>
    public class SubscribeToTaskRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the task to subscribe to. Default is an empty string.
        /// </summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>
    /// Parameters of <c>GetExtendedAgentCard</c>, which returns the authenticated extended Agent Card.
    /// Instances are not thread-safe.
    /// </summary>
    public class GetExtendedAgentCardRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }
    }

    /// <summary>
    /// Credentials the agent presents when it calls a push notification endpoint. Instances are not thread-safe.
    /// </summary>
    public class AuthenticationInfo
    {
        /// <summary>
        /// Gets or sets the authentication scheme, for example <c>Bearer</c>. Default is an empty string.
        /// </summary>
        public string Scheme { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the credentials for <see cref="Scheme"/>. Null when the scheme needs none or they are
        /// obtained out of band. Treat as a secret.
        /// </summary>
        public string? Credentials { get; set; }
    }

    /// <summary>
    /// Where and how an agent delivers push notifications for a task. Instances are not thread-safe.
    /// </summary>
    public class PushNotificationConfig
    {
        /// <summary>
        /// Gets or sets the configuration identifier. Null lets the server assign one.
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Gets or sets the client endpoint URL the agent posts notifications to. Default is an empty string.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the credentials the agent uses when calling <see cref="Url"/>. Null when none are needed.
        /// </summary>
        public AuthenticationInfo? Authentication { get; set; }

        /// <summary>
        /// Gets or sets an opaque token the agent includes with each notification so the receiver can validate it.
        /// Null when not used.
        /// </summary>
        public string? Token { get; set; }
    }

    /// <summary>
    /// A push notification configuration registered for a specific task. Instances are not thread-safe.
    /// </summary>
    [JsonConverter(typeof(TaskPushNotificationConfigJsonConverter))]
    public class TaskPushNotificationConfig
    {
        /// <summary>
        /// Gets or sets the configuration identifier. Default is an empty string.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the task the configuration belongs to. Default is an empty string.
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the notification endpoint and credentials. Default is an empty configuration.
        /// </summary>
        public PushNotificationConfig PushNotificationConfig { get; set; } = new PushNotificationConfig();

        /// <summary>
        /// Gets or sets the tenant the configuration belongs to. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }
    }

    /// <summary>
    /// Parameters of <c>CreateTaskPushNotificationConfig</c>. Instances are not thread-safe.
    /// </summary>
    [JsonConverter(typeof(CreateTaskPushNotificationConfigRequestJsonConverter))]
    public class CreateTaskPushNotificationConfigRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the task to register the configuration for. Default is an empty string.
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the identifier to give the configuration. Default is an empty string.
        /// </summary>
        public string ConfigId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the configuration to register. Default is an empty configuration.
        /// </summary>
        public PushNotificationConfig Config { get; set; } = new PushNotificationConfig();
    }

    /// <summary>
    /// Parameters of <c>GetTaskPushNotificationConfig</c>. Instances are not thread-safe.
    /// </summary>
    public class GetTaskPushNotificationConfigRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the task the configuration belongs to. Default is an empty string.
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the identifier of the configuration to retrieve. Default is an empty string.
        /// Serialized as <c>id</c>, the A2A v1.0 name; the older <c>configId</c> is also accepted when reading.
        /// </summary>
        [JsonPropertyName("id")]
        public string ConfigId { get; set; } = string.Empty;

        [JsonInclude]
        [JsonPropertyName("configId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        private string? LegacyConfigId
        {
            get => null;
            set
            {
                if (!String.IsNullOrEmpty(value) && String.IsNullOrEmpty(ConfigId)) ConfigId = value;
            }
        }
    }

    /// <summary>
    /// Parameters of <c>ListTaskPushNotificationConfig</c>. Instances are not thread-safe.
    /// </summary>
    public class ListTaskPushNotificationConfigRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the task whose configurations to list. Default is an empty string.
        /// </summary>
        public string TaskId { get; set; } = string.Empty;
    }

    /// <summary>
    /// The result of <c>ListTaskPushNotificationConfig</c>. Instances are not thread-safe.
    /// </summary>
    public class ListTaskPushNotificationConfigResponse
    {
        /// <summary>
        /// Gets or sets the task's push notification configurations. Default is an empty list.
        /// </summary>
        public List<TaskPushNotificationConfig> Configs { get; set; } = new List<TaskPushNotificationConfig>();
    }

    /// <summary>
    /// Parameters of <c>DeleteTaskPushNotificationConfig</c>. Instances are not thread-safe.
    /// </summary>
    public class DeleteTaskPushNotificationConfigRequest
    {
        /// <summary>
        /// Gets or sets the tenant the request targets. Null for single-tenant agents.
        /// </summary>
        public string? Tenant { get; set; }

        /// <summary>
        /// Gets or sets the task the configuration belongs to. Default is an empty string.
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the identifier of the configuration to delete. Default is an empty string.
        /// Serialized as <c>id</c>, the A2A v1.0 name; the older <c>configId</c> is also accepted when reading.
        /// </summary>
        [JsonPropertyName("id")]
        public string ConfigId { get; set; } = string.Empty;

        [JsonInclude]
        [JsonPropertyName("configId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        private string? LegacyConfigId
        {
            get => null;
            set
            {
                if (!String.IsNullOrEmpty(value) && String.IsNullOrEmpty(ConfigId)) ConfigId = value;
            }
        }
    }
}
