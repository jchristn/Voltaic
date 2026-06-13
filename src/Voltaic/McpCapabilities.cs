namespace Voltaic
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Capabilities a client can expose to an MCP server.
    /// </summary>
    public class McpClientCapabilities
    {
        /// <summary>
        /// Gets or sets experimental capabilities.
        /// </summary>
        [JsonPropertyName("experimental")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Experimental { get; set; }

        /// <summary>
        /// Gets or sets root capabilities.
        /// </summary>
        [JsonPropertyName("roots")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpListChangedCapability? Roots { get; set; }

        /// <summary>
        /// Gets or sets sampling capabilities.
        /// </summary>
        [JsonPropertyName("sampling")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Sampling { get; set; }

        /// <summary>
        /// Gets or sets elicitation capabilities.
        /// </summary>
        [JsonPropertyName("elicitation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Elicitation { get; set; }
    }

    /// <summary>
    /// Capabilities an MCP server can expose to a client.
    /// </summary>
    public class McpServerCapabilities
    {
        /// <summary>
        /// Gets or sets experimental capabilities.
        /// </summary>
        [JsonPropertyName("experimental")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Experimental { get; set; }

        /// <summary>
        /// Gets or sets logging capabilities.
        /// </summary>
        [JsonPropertyName("logging")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Logging { get; set; }

        /// <summary>
        /// Gets or sets completion capabilities.
        /// </summary>
        [JsonPropertyName("completions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Completions { get; set; }

        /// <summary>
        /// Gets or sets prompt capabilities.
        /// </summary>
        [JsonPropertyName("prompts")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpListChangedCapability? Prompts { get; set; }

        /// <summary>
        /// Gets or sets resource capabilities.
        /// </summary>
        [JsonPropertyName("resources")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpResourceCapability? Resources { get; set; }

        /// <summary>
        /// Gets or sets tool capabilities.
        /// </summary>
        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpListChangedCapability? Tools { get; set; }
    }

    /// <summary>
    /// Common list-changed capability metadata.
    /// </summary>
    public class McpListChangedCapability
    {
        /// <summary>
        /// Gets or sets whether list changed notifications are supported.
        /// </summary>
        [JsonPropertyName("listChanged")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ListChanged { get; set; }
    }

    /// <summary>
    /// Resource capability metadata.
    /// </summary>
    public class McpResourceCapability : McpListChangedCapability
    {
        /// <summary>
        /// Gets or sets whether resource subscriptions are supported.
        /// </summary>
        [JsonPropertyName("subscribe")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Subscribe { get; set; }
    }
}
