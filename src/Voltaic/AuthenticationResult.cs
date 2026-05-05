namespace Voltaic
{
    using System.Collections.Generic;

    /// <summary>
    /// Represents the result of an authentication attempt performed by the <see cref="McpHttpServer.AuthenticationHandler"/>.
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

        private bool _IsAuthenticated;
        private string? _Principal;
        private Dictionary<string, string>? _Claims;
        private int _StatusCode = 401;
        private string? _ErrorMessage;
    }
}
