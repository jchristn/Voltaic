namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Result of the <c>server/discover</c> RPC (2026-07-28+). Reports the server's supported
    /// protocol versions, capabilities, and identity in a single response. The result is cacheable
    /// via <see cref="TtlMs"/> and <see cref="CacheScope"/>.
    /// </summary>
    public class McpDiscoverResult : McpResult
    {
        /// <summary>
        /// Gets or sets the result discriminator. Always <c>complete</c> for a discovery result.
        /// </summary>
        [JsonPropertyName("resultType")]
        public string ResultType { get; set; } = "complete";

        /// <summary>
        /// Gets or sets the protocol versions the server supports. The client should choose one of
        /// these for subsequent requests.
        /// </summary>
        [JsonPropertyName("supportedVersions")]
        public List<string> SupportedVersions { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the capabilities the server supports.
        /// </summary>
        [JsonPropertyName("capabilities")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpServerCapabilities? Capabilities { get; set; }

        /// <summary>
        /// Gets or sets optional natural-language guidance for LLMs on how to use this server.
        /// </summary>
        [JsonPropertyName("instructions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Instructions { get; set; }

        /// <summary>
        /// Gets or sets the time in milliseconds for which the result may be cached, or null when
        /// caching guidance is not provided.
        /// </summary>
        [JsonPropertyName("ttlMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? TtlMs { get; set; }

        /// <summary>
        /// Gets or sets the cache scope, for example <c>public</c> or <c>private</c>, or null when
        /// caching guidance is not provided.
        /// </summary>
        [JsonPropertyName("cacheScope")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CacheScope { get; set; }
    }
}
