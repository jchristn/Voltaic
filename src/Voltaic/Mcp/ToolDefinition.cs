namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;

    /// <summary>
    /// Represents a tool definition for MCP protocol tool discovery.
    /// </summary>
    public class ToolDefinition
    {
        /// <summary>
        /// Gets or sets the name of the tool.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the human-readable display title of the tool.
        /// </summary>
        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the description of what the tool does.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the JSON schema for the tool's input parameters.
        /// </summary>
        [JsonPropertyName("inputSchema")]
        public object InputSchema { get; set; } = new { };

        /// <summary>
        /// Gets or sets the JSON schema for structured tool output.
        /// </summary>
        [JsonPropertyName("outputSchema")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? OutputSchema { get; set; }

        /// <summary>
        /// Gets or sets optional tool annotations for clients and hosts.
        /// </summary>
        [JsonPropertyName("annotations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpAnnotations? Annotations { get; set; }

        /// <summary>
        /// Gets or sets optional icons that can represent this tool in user interfaces.
        /// </summary>
        [JsonPropertyName("icons")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<McpIcon>? Icons { get; set; }

        /// <summary>
        /// Gets or sets protocol metadata for this tool.
        /// </summary>
        [JsonPropertyName("_meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Meta { get; set; }

        /// <summary>
        /// Represents a tool definition for MCP protocol tool discovery.
        /// </summary>
        public ToolDefinition()
        {

        }
    }
}
