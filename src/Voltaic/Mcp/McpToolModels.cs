namespace Voltaic.Mcp
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Voltaic.Core;

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
        /// Gets or sets structured content returned by the tool. Null (the default) omits <c>structuredContent</c>
        /// from the result; to send JSON <c>null</c> (allowed from MCP 2026-07-28), use
        /// <see cref="FromStructured(object?)"/> with null or assign a <see cref="JsonElement"/> of kind
        /// <see cref="JsonValueKind.Null"/>. Before 2026-07-28 a value that is not a JSON object is sent as text
        /// instead, and a tool with an output schema must return an object.
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
        /// Creates a structured tool result whose text block carries the same data as JSON. A null
        /// <paramref name="structuredContent"/> is sent as JSON <c>null</c>, which MCP 2026-07-28 allows; sessions on
        /// earlier revisions, whose schema requires an object, receive the text block only.
        /// </summary>
        /// <param name="structuredContent">Structured content, or null for JSON null.</param>
        /// <returns>Tool call result.</returns>
        public static McpToolCallResult FromStructured(object? structuredContent)
        {
            return new McpToolCallResult
            {
                StructuredContent = structuredContent ?? JsonNullElement(),
                Content = new List<object>
                {
                    new McpTextContent { Text = JsonSerializer.Serialize(structuredContent) }
                }
            };
        }

        private static JsonElement JsonNullElement()
        {
            using (JsonDocument document = JsonDocument.Parse("null"))
            {
                return document.RootElement.Clone();
            }
        }
    }
}
