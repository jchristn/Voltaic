namespace Voltaic.Mcp
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The notification types a <c>subscriptions/listen</c> stream asks for (2026-07-28). Omitted or false fields are
    /// not subscribed. Not thread-safe.
    /// </summary>
    public class McpSubscriptionFilter
    {
        /// <summary>
        /// Gets or sets whether to receive <c>notifications/tools/list_changed</c>. Default is false.
        /// </summary>
        [JsonPropertyName("toolsListChanged")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ToolsListChanged { get; set; }

        /// <summary>
        /// Gets or sets whether to receive <c>notifications/prompts/list_changed</c>. Default is false.
        /// </summary>
        [JsonPropertyName("promptsListChanged")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool PromptsListChanged { get; set; }

        /// <summary>
        /// Gets or sets whether to receive <c>notifications/resources/list_changed</c>. Default is false.
        /// </summary>
        [JsonPropertyName("resourcesListChanged")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ResourcesListChanged { get; set; }

        /// <summary>
        /// Gets or sets the resource URIs to receive <c>notifications/resources/updated</c> for. Null or empty subscribes
        /// to none. Default is null.
        /// </summary>
        [JsonPropertyName("resourceSubscriptions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? ResourceSubscriptions { get; set; }
    }
}
