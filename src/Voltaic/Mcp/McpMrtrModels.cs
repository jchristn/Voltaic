namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A single server-initiated request surfaced to the client through the Multi Round-Trip
    /// Requests (MRTR) pattern (2026-07-28+). The <see cref="Method"/> is one of
    /// <c>elicitation/create</c>, <c>sampling/createMessage</c>, or <c>roots/list</c>.
    /// </summary>
    public class McpInputRequest
    {
        /// <summary>
        /// Gets or sets the server-initiated method name.
        /// </summary>
        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the parameters for the server-initiated request, or null.
        /// </summary>
        [JsonPropertyName("params")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Params { get; set; }
    }

    /// <summary>
    /// A result indicating that additional input is required before a request can be completed
    /// (2026-07-28+). Valid only on responses to <c>tools/call</c>, <c>resources/read</c>, and
    /// <c>prompts/get</c>. A conforming response includes at least one of <see cref="InputRequests"/>
    /// or <see cref="RequestState"/>.
    /// </summary>
    public class McpInputRequiredResult : McpResult
    {
        /// <summary>
        /// Gets or sets the result discriminator. Always <c>input_required</c>.
        /// </summary>
        [JsonPropertyName("resultType")]
        public string ResultType { get; set; } = "input_required";

        /// <summary>
        /// Gets or sets the map of server-initiated requests the client must fulfill. Keys are
        /// server-assigned identifiers; the client echoes the same keys in its input responses.
        /// Null when the server only needs the original request retried with echoed state.
        /// </summary>
        [JsonPropertyName("inputRequests")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, McpInputRequest>? InputRequests { get; set; }

        /// <summary>
        /// Gets or sets an opaque server-owned state string. Clients must echo it verbatim on retry
        /// and must not inspect, parse, or modify it. Null when the server needs no echoed state.
        /// </summary>
        [JsonPropertyName("requestState")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RequestState { get; set; }
    }
}
