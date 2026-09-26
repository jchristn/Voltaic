namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// OAuth 2.0 Protected Resource Metadata (RFC 9728) for an MCP server. The MCP authorization
    /// specification requires servers that use OAuth to publish this document so clients can discover the
    /// authorization server. Assign an instance to <see cref="McpHttpServer.ProtectedResourceMetadata"/> and the
    /// server serves it, without authentication, at <see cref="McpProtocol.ProtectedResourceMetadataPath"/>
    /// and at that path followed by the MCP endpoint path (for example
    /// <c>/.well-known/oauth-protected-resource/mcp</c>). Null properties are omitted from the JSON.
    /// Instances are not thread-safe; configure them before the server starts.
    /// </summary>
    public class McpProtectedResourceMetadata
    {
        /// <summary>
        /// Gets or sets the protected resource identifier (<c>resource</c>): the canonical URI of the MCP server,
        /// for example <c>https://mcp.example.com/mcp</c>. Required by RFC 9728; the server rejects metadata
        /// without it. Default is an empty string.
        /// </summary>
        [JsonPropertyName("resource")]
        public string Resource
        {
            get => _Resource;
            set => _Resource = value ?? String.Empty;
        }

        /// <summary>
        /// Gets or sets the issuer URLs of the authorization servers that issue tokens for this resource
        /// (<c>authorization_servers</c>). The MCP authorization specification requires at least one; the server
        /// rejects metadata without one. Never null; setting null stores an empty list.
        /// </summary>
        [JsonPropertyName("authorization_servers")]
        public List<string> AuthorizationServers
        {
            get => _AuthorizationServers;
            set => _AuthorizationServers = value ?? new List<string>();
        }

        /// <summary>
        /// Gets or sets the scopes clients may request for this resource (<c>scopes_supported</c>).
        /// Null (the default) omits the field.
        /// </summary>
        [JsonPropertyName("scopes_supported")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? ScopesSupported { get; set; }

        /// <summary>
        /// Gets or sets how bearer tokens may be presented (<c>bearer_methods_supported</c>), for example
        /// <c>header</c>. MCP clients send tokens only in the <c>Authorization</c> header. Null (the default) omits
        /// the field.
        /// </summary>
        [JsonPropertyName("bearer_methods_supported")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? BearerMethodsSupported { get; set; }

        /// <summary>
        /// Gets or sets a human-readable name for the resource (<c>resource_name</c>). Null (the default) omits
        /// the field.
        /// </summary>
        [JsonPropertyName("resource_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResourceName { get; set; }

        /// <summary>
        /// Gets or sets a URL of human-readable documentation for the resource (<c>resource_documentation</c>).
        /// Null (the default) omits the field.
        /// </summary>
        [JsonPropertyName("resource_documentation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResourceDocumentation { get; set; }

        private string _Resource = String.Empty;
        private List<string> _AuthorizationServers = new List<string>();
    }
}
