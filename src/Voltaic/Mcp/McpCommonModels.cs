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
        /// Gets or sets who the content or resource is intended for: <c>user</c>, <c>assistant</c>, or both. Applies to
        /// content blocks and resources; null omits it.
        /// </summary>
        [JsonPropertyName("audience")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Audience { get; set; }

        /// <summary>
        /// Gets or sets how important the content or resource is, from 0 (least) to 1 (most important). Applies to
        /// content blocks and resources; null omits it.
        /// </summary>
        [JsonPropertyName("priority")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Priority { get; set; }

        /// <summary>
        /// Gets or sets when the content or resource was last modified, as an ISO 8601 timestamp (MCP 2025-06-18 and
        /// later; omitted for older clients). Null omits it.
        /// </summary>
        [JsonPropertyName("lastModified")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastModified { get; set; }

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
        /// The <c>resultType</c> value for a final result (2026-07-28+).
        /// </summary>
        public const string ResultTypeComplete = "complete";

        /// <summary>
        /// The <c>resultType</c> value for a Multi Round-Trip result that needs client input (2026-07-28+).
        /// </summary>
        public const string ResultTypeInputRequired = "input_required";

        /// <summary>
        /// The <c>resultType</c> value for a result that created a task (2026-07-28 tasks extension).
        /// </summary>
        public const string ResultTypeTask = "task";

        /// <summary>
        /// Gets or sets protocol metadata.
        /// </summary>
        [JsonPropertyName("_meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Meta { get; set; }

        /// <summary>
        /// Gets or sets the result discriminator required by stateless-era revisions (2026-07-28+):
        /// <c>complete</c> (<see cref="ResultTypeComplete"/>) for a final result, <c>input_required</c>
        /// (<see cref="ResultTypeInputRequired"/>) for a Multi Round-Trip result, or <c>task</c>
        /// (<see cref="ResultTypeTask"/>) for a created task. Null by default, which omits the field from
        /// the wire, as handshake-era revisions require. Voltaic servers set <c>complete</c> automatically
        /// on built-in results served under a stateless-era revision when this value is null, and never
        /// overwrite a value a handler has already set.
        /// </summary>
        [JsonPropertyName("resultType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResultType { get; set; }
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

        /// <summary>
        /// Gets or sets the time in milliseconds for which the result may be cached (2026-07-28+),
        /// or null when no caching guidance is provided.
        /// </summary>
        [JsonPropertyName("ttlMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? TtlMs { get; set; }

        /// <summary>
        /// Gets or sets the cache scope, for example <c>public</c> or <c>private</c> (2026-07-28+),
        /// or null when no caching guidance is provided.
        /// </summary>
        [JsonPropertyName("cacheScope")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CacheScope { get; set; }
    }
}
