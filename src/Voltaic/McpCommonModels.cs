namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Describes an MCP implementation.
    /// </summary>
    public class McpImplementation
    {
        /// <summary>
        /// Gets or sets the implementation name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the optional display title.
        /// </summary>
        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the implementation version.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the optional description.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the optional website URL.
        /// </summary>
        [JsonPropertyName("websiteUrl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WebsiteUrl { get; set; }

        /// <summary>
        /// Gets or sets optional icons.
        /// </summary>
        [JsonPropertyName("icons")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<McpIcon>? Icons { get; set; }
    }

    /// <summary>
    /// Describes an icon for an MCP item.
    /// </summary>
    public class McpIcon
    {
        /// <summary>
        /// Gets or sets the icon URI.
        /// </summary>
        [JsonPropertyName("src")]
        public string Src { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MIME type.
        /// </summary>
        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        /// <summary>
        /// Gets or sets supported sizes.
        /// </summary>
        [JsonPropertyName("sizes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Sizes { get; set; }

        /// <summary>
        /// Gets or sets the intended theme.
        /// </summary>
        [JsonPropertyName("theme")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Theme { get; set; }
    }

    /// <summary>
    /// Optional annotations for MCP items.
    /// </summary>
    public class McpAnnotations
    {
        /// <summary>
        /// Gets or sets a display title.
        /// </summary>
        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets whether the item is read-only.
        /// </summary>
        [JsonPropertyName("readOnlyHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ReadOnlyHint { get; set; }

        /// <summary>
        /// Gets or sets whether the item is destructive.
        /// </summary>
        [JsonPropertyName("destructiveHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? DestructiveHint { get; set; }

        /// <summary>
        /// Gets or sets whether the item is idempotent.
        /// </summary>
        [JsonPropertyName("idempotentHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IdempotentHint { get; set; }

        /// <summary>
        /// Gets or sets whether the item can operate in an open-world context.
        /// </summary>
        [JsonPropertyName("openWorldHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? OpenWorldHint { get; set; }
    }

    /// <summary>
    /// Base result metadata shape used by MCP.
    /// </summary>
    public class McpResult
    {
        /// <summary>
        /// Gets or sets protocol metadata.
        /// </summary>
        [JsonPropertyName("_meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Meta { get; set; }
    }

    /// <summary>
    /// Base paginated result shape used by MCP.
    /// </summary>
    public class McpPaginatedResult : McpResult
    {
        /// <summary>
        /// Gets or sets the cursor for the next page, if one exists.
        /// </summary>
        [JsonPropertyName("nextCursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextCursor { get; set; }
    }
}
