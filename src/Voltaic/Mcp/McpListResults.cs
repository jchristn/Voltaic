namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Result returned by tools/list.
    /// </summary>
    public class McpListToolsResult : McpPaginatedResult
    {
        /// <summary>
        /// Gets or sets the listed tools.
        /// </summary>
        [JsonPropertyName("tools")]
        public List<ToolDefinition> Tools { get; set; } = new List<ToolDefinition>();
    }

    /// <summary>
    /// Result returned by resources/list.
    /// </summary>
    public class McpListResourcesResult : McpPaginatedResult
    {
        /// <summary>
        /// Gets or sets the listed resources.
        /// </summary>
        [JsonPropertyName("resources")]
        public List<McpResource> Resources { get; set; } = new List<McpResource>();
    }

    /// <summary>
    /// Result returned by resources/templates/list.
    /// </summary>
    public class McpListResourceTemplatesResult : McpPaginatedResult
    {
        /// <summary>
        /// Gets or sets the listed resource templates.
        /// </summary>
        [JsonPropertyName("resourceTemplates")]
        public List<McpResourceTemplate> ResourceTemplates { get; set; } = new List<McpResourceTemplate>();
    }

    /// <summary>
    /// Result returned by prompts/list.
    /// </summary>
    public class McpListPromptsResult : McpPaginatedResult
    {
        /// <summary>
        /// Gets or sets the listed prompts.
        /// </summary>
        [JsonPropertyName("prompts")]
        public List<McpPrompt> Prompts { get; set; } = new List<McpPrompt>();
    }
}
