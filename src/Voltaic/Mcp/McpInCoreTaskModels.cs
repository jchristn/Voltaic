namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A task record for the experimental in-core tasks feature introduced in <c>2025-11-25</c>.
    /// This is distinct from the 2026-07-28 <see cref="McpTask"/> extension: field names differ
    /// (<c>ttl</c>/<c>pollInterval</c> versus <c>ttlMs</c>/<c>pollIntervalMs</c>) and the two use
    /// different methods and notifications. Do not interchange them.
    /// </summary>
    public class McpInCoreTask : McpResult
    {
        /// <summary>
        /// Gets or sets the unique, receiver-generated task identifier.
        /// </summary>
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the current status. One of the <see cref="McpTaskStatus"/> values.
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = McpTaskStatus.Working;

        /// <summary>
        /// Gets or sets an optional human-readable status message.
        /// </summary>
        [JsonPropertyName("statusMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StatusMessage { get; set; }

        /// <summary>
        /// Gets or sets the ISO 8601 timestamp when the task was created.
        /// </summary>
        [JsonPropertyName("createdAt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the ISO 8601 timestamp when the task status was last updated.
        /// </summary>
        [JsonPropertyName("lastUpdatedAt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastUpdatedAt { get; set; }

        /// <summary>
        /// Gets or sets the time in milliseconds from creation before the task may be deleted,
        /// or null for unlimited.
        /// </summary>
        [JsonPropertyName("ttl")]
        public long? Ttl { get; set; }

        /// <summary>
        /// Gets or sets the suggested polling interval in milliseconds, or null when not provided.
        /// </summary>
        [JsonPropertyName("pollInterval")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PollInterval { get; set; }
    }

    /// <summary>
    /// The result returned when accepting a task-augmented request under the <c>2025-11-25</c>
    /// experimental in-core tasks feature. The underlying operation result is retrieved later via
    /// <c>tasks/result</c>.
    /// </summary>
    public class McpCreateInCoreTaskResult : McpResult
    {
        /// <summary>
        /// Gets or sets the created task record.
        /// </summary>
        [JsonPropertyName("task")]
        public McpInCoreTask Task { get; set; } = new McpInCoreTask();
    }

    /// <summary>
    /// The <c>task</c> augmentation object included in request parameters to request task-based
    /// execution under the <c>2025-11-25</c> experimental in-core tasks feature.
    /// </summary>
    public class McpTaskAugmentation
    {
        /// <summary>
        /// Gets or sets the requested task lifetime in milliseconds, or null to accept the
        /// receiver's default.
        /// </summary>
        [JsonPropertyName("ttl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Ttl { get; set; }
    }

    /// <summary>
    /// The paginated result of a <c>tasks/list</c> request under the <c>2025-11-25</c> experimental
    /// in-core tasks feature.
    /// </summary>
    public class McpListTasksResult : McpPaginatedResult
    {
        /// <summary>
        /// Gets or sets the listed tasks.
        /// </summary>
        [JsonPropertyName("tasks")]
        public List<McpInCoreTask> Tasks { get; set; } = new List<McpInCoreTask>();
    }
}
