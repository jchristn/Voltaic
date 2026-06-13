namespace Voltaic
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Describes an MCP resource.
    /// </summary>
    public class McpResource
    {
        /// <summary>
        /// Gets or sets the resource URI.
        /// </summary>
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the resource name.
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
        /// Gets or sets the MIME type.
        /// </summary>
        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        /// <summary>
        /// Gets or sets the raw resource size in bytes.
        /// </summary>
        [JsonPropertyName("size")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Size { get; set; }

        /// <summary>
        /// Gets or sets optional annotations.
        /// </summary>
        [JsonPropertyName("annotations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpAnnotations? Annotations { get; set; }

        /// <summary>
        /// Gets or sets optional icons.
        /// </summary>
        [JsonPropertyName("icons")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<McpIcon>? Icons { get; set; }
    }

    /// <summary>
    /// Describes an MCP resource template.
    /// </summary>
    public class McpResourceTemplate
    {
        /// <summary>
        /// Gets or sets the URI template.
        /// </summary>
        [JsonPropertyName("uriTemplate")]
        public string UriTemplate { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the template name.
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
        /// Gets or sets the MIME type.
        /// </summary>
        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }
    }

    /// <summary>
    /// Text contents returned by a resource read.
    /// </summary>
    public class McpTextResourceContents
    {
        /// <summary>
        /// Gets or sets the resource URI.
        /// </summary>
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MIME type.
        /// </summary>
        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        /// <summary>
        /// Gets or sets the text contents.
        /// </summary>
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Binary contents returned by a resource read.
    /// </summary>
    public class McpBlobResourceContents
    {
        /// <summary>
        /// Gets or sets the resource URI.
        /// </summary>
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MIME type.
        /// </summary>
        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        /// <summary>
        /// Gets or sets base64-encoded binary content.
        /// </summary>
        [JsonPropertyName("blob")]
        public string Blob { get; set; } = string.Empty;
    }

    /// <summary>
    /// A resources/read result.
    /// </summary>
    public class McpReadResourceResult : McpResult
    {
        /// <summary>
        /// Gets or sets resource contents.
        /// </summary>
        [JsonPropertyName("contents")]
        public List<object> Contents { get; set; } = new List<object>();
    }
}
