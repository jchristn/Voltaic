namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Describes an MCP prompt.
    /// </summary>
    public class McpPrompt
    {
        /// <summary>
        /// Gets or sets the prompt name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display title.
        /// </summary>
        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets prompt arguments.
        /// </summary>
        [JsonPropertyName("arguments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<McpPromptArgument>? Arguments { get; set; }
    }

    /// <summary>
    /// Describes an MCP prompt argument.
    /// </summary>
    public class McpPromptArgument
    {
        /// <summary>
        /// Gets or sets the argument name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display title.
        /// </summary>
        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets whether the argument is required.
        /// </summary>
        [JsonPropertyName("required")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Required { get; set; }
    }

    /// <summary>
    /// Message returned as part of a prompt result.
    /// </summary>
    public class McpPromptMessage
    {
        /// <summary>
        /// Gets or sets the role.
        /// </summary>
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        /// <summary>
        /// Gets or sets the content block.
        /// </summary>
        [JsonPropertyName("content")]
        public object Content { get; set; } = new McpTextContent();
    }

    /// <summary>
    /// Result returned by prompts/get.
    /// </summary>
    public class McpGetPromptResult : McpResult
    {
        /// <summary>
        /// Gets or sets the optional description.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets prompt messages.
        /// </summary>
        [JsonPropertyName("messages")]
        public List<McpPromptMessage> Messages { get; set; } = new List<McpPromptMessage>();
    }
}
