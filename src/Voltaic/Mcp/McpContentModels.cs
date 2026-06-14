namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Text content used by MCP prompts and tool results.
    /// </summary>
    public class McpTextContent
    {
        /// <summary>
        /// Gets the content type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        /// <summary>
        /// Gets or sets the text content.
        /// </summary>
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets optional annotations.
        /// </summary>
        [JsonPropertyName("annotations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpAnnotations? Annotations { get; set; }

        /// <summary>
        /// Gets or sets protocol metadata.
        /// </summary>
        [JsonPropertyName("_meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Meta { get; set; }
    }

    /// <summary>
    /// Image content used by MCP prompts and tool results.
    /// </summary>
    public class McpImageContent
    {
        /// <summary>
        /// Gets the content type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "image";

        /// <summary>
        /// Gets or sets base64 image data.
        /// </summary>
        [JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MIME type.
        /// </summary>
        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets optional annotations.
        /// </summary>
        [JsonPropertyName("annotations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpAnnotations? Annotations { get; set; }
    }

    /// <summary>
    /// Audio content used by MCP prompts and tool results.
    /// </summary>
    public class McpAudioContent
    {
        /// <summary>
        /// Gets the content type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "audio";

        /// <summary>
        /// Gets or sets base64 audio data.
        /// </summary>
        [JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MIME type.
        /// </summary>
        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Embedded resource content used by MCP prompts and tool results.
    /// </summary>
    public class McpEmbeddedResourceContent
    {
        /// <summary>
        /// Gets the content type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "resource";

        /// <summary>
        /// Gets or sets the embedded resource contents.
        /// </summary>
        [JsonPropertyName("resource")]
        public object Resource { get; set; } = new McpTextResourceContents();

        /// <summary>
        /// Gets or sets optional annotations.
        /// </summary>
        [JsonPropertyName("annotations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpAnnotations? Annotations { get; set; }
    }

    /// <summary>
    /// Resource link content used by MCP prompts and tool results.
    /// </summary>
    public class McpResourceLinkContent
    {
        /// <summary>
        /// Gets the content type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "resource_link";

        /// <summary>
        /// Gets or sets the resource URI.
        /// </summary>
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional display name.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the MIME type.
        /// </summary>
        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }
    }
}
