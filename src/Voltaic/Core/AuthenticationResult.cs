namespace Voltaic.Core
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Represents the result of an HTTP authentication attempt.
    /// When <see cref="IsAuthenticated"/> is false, the server returns the specified <see cref="StatusCode"/> and <see cref="ErrorMessage"/>
    /// to the client without processing the request further.
    /// When <see cref="IsAuthenticated"/> is true, optional identity information such as <see cref="Principal"/> and <see cref="Claims"/>
    /// can be carried forward for use in downstream tool handlers.
    /// </summary>
    public class AuthenticationResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the request was successfully authenticated.
        /// When false, the server will reject the request with the configured <see cref="StatusCode"/> and <see cref="ErrorMessage"/>.
        /// </summary>
        public bool IsAuthenticated
        {
            get => _IsAuthenticated;
            set => _IsAuthenticated = value;
        }

        /// <summary>
        /// Gets or sets the authenticated principal name (e.g., username, service account, API key identifier).
        /// This value is informational and can be used by tool handlers to determine who is calling.
        /// Default is null, indicating no principal information is available.
        /// </summary>
        public string? Principal
        {
            get => _Principal;
            set => _Principal = value;
        }

        /// <summary>
        /// Gets or sets additional claims or metadata associated with the authenticated identity.
        /// Tool handlers can use these claims for fine-grained authorization decisions.
        /// Default is null, indicating no additional claims are available.
        /// </summary>
        public Dictionary<string, string>? Claims
        {
            get => _Claims;
            set => _Claims = value;
        }

        /// <summary>
        /// Gets or sets the HTTP status code to return when authentication fails.
        /// Common values are 401 (Unauthorized) and 403 (Forbidden).
        /// Default is 401. Only used when <see cref="IsAuthenticated"/> is false.
        /// </summary>
        public int StatusCode
        {
            get => _StatusCode;
            set => _StatusCode = value;
        }

        /// <summary>
        /// Gets or sets the error message to include in the response body when authentication fails.
        /// Default is null, which results in no response body being sent.
        /// Only used when <see cref="IsAuthenticated"/> is false.
        /// </summary>
        public string? ErrorMessage
        {
            get => _ErrorMessage;
            set => _ErrorMessage = value;
        }

        /// <summary>
        /// Gets or sets headers to add to the response when authentication fails, for example
        /// <c>WWW-Authenticate</c> (required on a 401 by RFC 6750 and the MCP authorization specification)
        /// or <c>Retry-After</c>. Names are case-insensitive. Ignored when <see cref="IsAuthenticated"/> is true.
        /// Never null; setting null stores an empty dictionary. HTTP and WebSocket servers write these headers
        /// on the rejection; the A2A gRPC server does not.
        /// </summary>
        public Dictionary<string, string> Headers
        {
            get => _Headers;
            set => _Headers = value != null
                ? new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a failed result for bearer-token authentication: status 401 and a
        /// <c>WWW-Authenticate: Bearer</c> challenge in the RFC 6750 format, for example
        /// <c>Bearer resource_metadata="https://api.example/.well-known/oauth-protected-resource", error="invalid_token"</c>.
        /// Parameter values are quoted, and embedded quotes and backslashes are escaped.
        /// </summary>
        /// <param name="resourceMetadataUrl">Optional protected resource metadata URL (RFC 9728), which MCP clients use to discover the authorization server. Null or empty omits it.</param>
        /// <param name="error">Optional RFC 6750 error code, such as <c>invalid_token</c> or <c>insufficient_scope</c>. Null or empty omits it.</param>
        /// <param name="errorDescription">Optional human-readable error description. Null or empty omits it.</param>
        /// <param name="errorMessage">Optional response body text. Null sends no body.</param>
        /// <returns>A failed authentication result carrying the challenge header.</returns>
        public static AuthenticationResult BearerChallenge(string? resourceMetadataUrl = null, string? error = null, string? errorDescription = null, string? errorMessage = null)
        {
            List<string> parameters = new List<string>();
            if (!String.IsNullOrEmpty(resourceMetadataUrl)) parameters.Add($"resource_metadata=\"{EscapeQuoted(resourceMetadataUrl!)}\"");
            if (!String.IsNullOrEmpty(error)) parameters.Add($"error=\"{EscapeQuoted(error!)}\"");
            if (!String.IsNullOrEmpty(errorDescription)) parameters.Add($"error_description=\"{EscapeQuoted(errorDescription!)}\"");

            string challenge = parameters.Count == 0 ? "Bearer" : "Bearer " + String.Join(", ", parameters);

            AuthenticationResult result = new AuthenticationResult
            {
                IsAuthenticated = false,
                StatusCode = 401,
                ErrorMessage = errorMessage
            };
            result.Headers["WWW-Authenticate"] = challenge;
            return result;
        }

        private static string EscapeQuoted(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (character == '"' || character == '\\') builder.Append('\\');
                builder.Append(character);
            }

            return builder.ToString();
        }

        private bool _IsAuthenticated;
        private string? _Principal;
        private Dictionary<string, string>? _Claims;
        private int _StatusCode = 401;
        private string? _ErrorMessage;
        private Dictionary<string, string> _Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
