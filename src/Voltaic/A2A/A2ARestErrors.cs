namespace Voltaic.A2A
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// Maps A2A errors to and from the HTTP+JSON binding's error format: a <c>google.rpc.Status</c> JSON object with a
    /// <c>google.rpc.ErrorInfo</c> detail whose <c>reason</c> names the A2A error (A2A v1.0 section 11.6).
    /// </summary>
    internal static class A2ARestErrors
    {
        internal const string ErrorInfoType = "type.googleapis.com/google.rpc.ErrorInfo";

        internal const string Domain = "a2a-protocol.org";

        /// <summary>
        /// Returns the HTTP status for an A2A error code: 404 for task not found, 500 for internal and
        /// invalid-agent-response errors, 400 otherwise.
        /// </summary>
        internal static int HttpStatus(A2AErrorCode code)
        {
            return code switch
            {
                A2AErrorCode.TaskNotFound => 404,
                A2AErrorCode.InvalidAgentResponse => 500,
                A2AErrorCode.InternalError => 500,
                _ => 400
            };
        }

        /// <summary>
        /// Builds the <c>google.rpc.Status</c> body. A2A-specific errors carry an <c>ErrorInfo</c> detail with the reason.
        /// </summary>
        internal static Dictionary<string, object?> Body(A2AErrorCode code, string message)
        {
            int status = HttpStatus(code);
            Dictionary<string, object?> error = new Dictionary<string, object?>
            {
                { "code", status },
                { "status", CanonicalStatus(code) },
                { "message", message }
            };

            string? reason = Reason(code);
            error["details"] = reason == null
                ? new List<object>()
                : new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        { "@type", ErrorInfoType },
                        { "reason", reason },
                        { "domain", Domain },
                        { "metadata", new Dictionary<string, string>() }
                    }
                };

            return new Dictionary<string, object?> { { "error", error } };
        }

        /// <summary>
        /// Parses an HTTP+JSON error body into an <see cref="A2AProtocolException"/>. Reads the <c>google.rpc.Status</c>
        /// shape (preferring the <c>ErrorInfo</c> reason) and the JSON-RPC-style body older Voltaic servers sent.
        /// Returns null when the body is neither.
        /// </summary>
        internal static A2AProtocolException? Parse(string body, int httpStatus)
        {
            if (String.IsNullOrWhiteSpace(body)) return null;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(body))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                    if (!document.RootElement.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object) return null;

                    string message = error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";

                    if (error.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement detail in details.EnumerateArray())
                        {
                            if (detail.ValueKind == JsonValueKind.Object
                                && detail.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String
                                && FromReason(reason.GetString()) is A2AErrorCode fromReason)
                            {
                                return new A2AProtocolException(fromReason, message, JsonSerializer.Deserialize<object>(details.GetRawText()));
                            }
                        }
                    }

                    // Older Voltaic servers sent a JSON-RPC error object: its code is the A2A code.
                    if (error.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int value) && Enum.IsDefined(typeof(A2AErrorCode), value))
                    {
                        return new A2AProtocolException((A2AErrorCode)value, message);
                    }

                    string? status = error.TryGetProperty("status", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    return new A2AProtocolException(FromStatus(status, httpStatus), message);
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Reason(A2AErrorCode code)
        {
            return code switch
            {
                A2AErrorCode.TaskNotFound => "TASK_NOT_FOUND",
                A2AErrorCode.TaskNotCancelable => "TASK_NOT_CANCELABLE",
                A2AErrorCode.PushNotificationNotSupported => "PUSH_NOTIFICATION_NOT_SUPPORTED",
                A2AErrorCode.UnsupportedOperation => "UNSUPPORTED_OPERATION",
                A2AErrorCode.ContentTypeNotSupported => "CONTENT_TYPE_NOT_SUPPORTED",
                A2AErrorCode.InvalidAgentResponse => "INVALID_AGENT_RESPONSE",
                A2AErrorCode.ExtendedAgentCardNotConfigured => "EXTENDED_AGENT_CARD_NOT_CONFIGURED",
                A2AErrorCode.ExtensionSupportRequired => "EXTENSION_SUPPORT_REQUIRED",
                A2AErrorCode.VersionNotSupported => "VERSION_NOT_SUPPORTED",
                _ => null
            };
        }

        private static A2AErrorCode? FromReason(string? reason)
        {
            foreach (A2AErrorCode code in Enum.GetValues(typeof(A2AErrorCode)))
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(Reason(code), reason)) return code;
            }

            return null;
        }

        private static string CanonicalStatus(A2AErrorCode code)
        {
            return code switch
            {
                A2AErrorCode.TaskNotFound => "NOT_FOUND",
                A2AErrorCode.MethodNotFound => "UNIMPLEMENTED",
                A2AErrorCode.UnsupportedOperation => "FAILED_PRECONDITION",
                A2AErrorCode.TaskNotCancelable => "FAILED_PRECONDITION",
                A2AErrorCode.PushNotificationNotSupported => "FAILED_PRECONDITION",
                A2AErrorCode.ExtendedAgentCardNotConfigured => "FAILED_PRECONDITION",
                A2AErrorCode.ExtensionSupportRequired => "FAILED_PRECONDITION",
                A2AErrorCode.VersionNotSupported => "FAILED_PRECONDITION",
                A2AErrorCode.InternalError => "INTERNAL",
                A2AErrorCode.InvalidAgentResponse => "INTERNAL",
                _ => "INVALID_ARGUMENT"
            };
        }

        private static A2AErrorCode FromStatus(string? status, int httpStatus)
        {
            return status switch
            {
                "INVALID_ARGUMENT" => A2AErrorCode.InvalidParams,
                "UNIMPLEMENTED" => A2AErrorCode.UnsupportedOperation,
                "FAILED_PRECONDITION" => A2AErrorCode.UnsupportedOperation,
                _ => httpStatus >= 400 && httpStatus < 500 ? A2AErrorCode.InvalidRequest : A2AErrorCode.InternalError
            };
        }
    }
}
