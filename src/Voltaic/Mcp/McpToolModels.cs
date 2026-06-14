namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Result returned by tools/call.
    /// </summary>
    public class McpToolCallResult : McpResult
    {
        /// <summary>
        /// Gets or sets content blocks returned by the tool.
        /// </summary>
        [JsonPropertyName("content")]
        public List<object> Content { get; set; } = new List<object>();

        /// <summary>
        /// Gets or sets structured content returned by the tool.
        /// </summary>
        [JsonPropertyName("structuredContent")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? StructuredContent { get; set; }

        /// <summary>
        /// Gets or sets whether the tool result represents an error.
        /// </summary>
        [JsonPropertyName("isError")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsError { get; set; }

        /// <summary>
        /// Creates a text tool result.
        /// </summary>
        /// <param name="text">Text content.</param>
        /// <returns>Tool call result.</returns>
        public static McpToolCallResult FromText(string? text)
        {
            return new McpToolCallResult
            {
                Content = new List<object>
                {
                    new McpTextContent { Text = text ?? string.Empty }
                }
            };
        }

        /// <summary>
        /// Creates a structured tool result.
        /// </summary>
        /// <param name="structuredContent">Structured content.</param>
        /// <returns>Tool call result.</returns>
        public static McpToolCallResult FromStructured(object? structuredContent)
        {
            return new McpToolCallResult
            {
                StructuredContent = structuredContent,
                Content = new List<object>
                {
                    new McpTextContent { Text = JsonSerializer.Serialize(structuredContent) }
                }
            };
        }
    }
}
