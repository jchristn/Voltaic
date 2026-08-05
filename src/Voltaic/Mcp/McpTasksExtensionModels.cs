namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Task status values for the 2026-07-28 <c>io.modelcontextprotocol/tasks</c> extension.
    /// Modeled as string constants (rather than an enum) so the wire values <c>input_required</c>
    /// and friends serialize identically across .NET 8.0 and .NET 10.0.
    /// </summary>
    public static class McpTaskStatus
    {
        /// <summary>The operation is in progress.</summary>
        public const string Working = "working";

        /// <summary>The server needs client input before continuing; see input requests.</summary>
        public const string InputRequired = "input_required";

        /// <summary>The operation finished; the result is available.</summary>
        public const string Completed = "completed";

        /// <summary>A JSON-RPC error occurred during execution.</summary>
        public const string Failed = "failed";

        /// <summary>The operation was cancelled.</summary>
        public const string Cancelled = "cancelled";
    }

    /// <summary>
    /// A durable task handle for the 2026-07-28 tasks extension. Status-specific fields
    /// (<see cref="InputRequests"/>, <see cref="Result"/>, <see cref="Error"/>) are populated only
    /// for the corresponding status, matching the schema's <c>DetailedTask</c> variants.
    /// </summary>
    public class McpTask : McpResult
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
        /// Gets or sets the time in milliseconds before the task may be discarded, or null for
        /// unlimited. This field is always serialized (including as JSON <c>null</c>) because the
        /// schema requires it.
        /// </summary>
        [JsonPropertyName("ttlMs")]
        public long? TtlMs { get; set; }

        /// <summary>
        /// Gets or sets the suggested polling interval in milliseconds, or null when not provided.
        /// </summary>
        [JsonPropertyName("pollIntervalMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PollIntervalMs { get; set; }

        /// <summary>
        /// Gets or sets input requests surfaced while the task is in the
        /// <see cref="McpTaskStatus.InputRequired"/> status; otherwise null.
        /// </summary>
        [JsonPropertyName("inputRequests")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, McpInputRequest>? InputRequests { get; set; }

        /// <summary>
        /// Gets or sets the final result when the task is <see cref="McpTaskStatus.Completed"/>;
        /// otherwise null. The structure matches the original request's result type.
        /// </summary>
        [JsonPropertyName("result")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Result { get; set; }

        /// <summary>
        /// Gets or sets the JSON-RPC error when the task is <see cref="McpTaskStatus.Failed"/>;
        /// otherwise null.
        /// </summary>
        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Error { get; set; }
    }

    /// <summary>
    /// The result returned when a server elects asynchronous execution of a supported request
    /// (2026-07-28 tasks extension). Carries the task handle with <c>resultType</c> set to
    /// <c>task</c>.
    /// </summary>
    public class McpCreateTaskResult : McpTask
    {
        /// <summary>
        /// Gets or sets the result discriminator. Always <c>task</c>.
        /// </summary>
        [JsonPropertyName("resultType")]
        public string ResultType { get; set; } = "task";
    }

    /// <summary>
    /// Parameters for a <c>tasks/update</c> request (2026-07-28 tasks extension). Carries responses
    /// keyed to the input requests previously surfaced by the server.
    /// </summary>
    public class McpUpdateTaskParams
    {
        /// <summary>
        /// Gets or sets the target task identifier.
        /// </summary>
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the client responses keyed to outstanding input-request keys.
        /// </summary>
        [JsonPropertyName("inputResponses")]
        public Dictionary<string, object?> InputResponses { get; set; } = new Dictionary<string, object?>();
    }

    /// <summary>
    /// An empty acknowledgement returned by <c>tasks/update</c> and <c>tasks/cancel</c>
    /// (2026-07-28 tasks extension), with <c>resultType</c> set to <c>complete</c>.
    /// </summary>
    public class McpTaskAck : McpResult
    {
        /// <summary>
        /// Gets or sets the result discriminator. Always <c>complete</c>.
        /// </summary>
        [JsonPropertyName("resultType")]
        public string ResultType { get; set; } = "complete";
    }
}
