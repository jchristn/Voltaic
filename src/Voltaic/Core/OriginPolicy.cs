namespace Voltaic.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;

    /// <summary>
    /// Decides which browser origins may call an HTTP or WebSocket server. The MCP Streamable HTTP
    /// specification requires servers to validate the <c>Origin</c> header on every request to prevent DNS
    /// rebinding and cross-site requests, so this policy applies whether or not authentication is configured.
    /// <para>
    /// The default policy allows requests without an <c>Origin</c> header (non-browser clients such as SDKs,
    /// <c>curl</c>, and other servers) and requests from loopback origins (<c>http(s)://localhost</c>,
    /// <c>127.0.0.0/8</c>, and <c>[::1]</c> on any port). Every other origin is rejected with HTTP 403 unless it
    /// is listed in <see cref="AllowedOrigins"/>.
    /// </para>
    /// Configure the policy before the server starts. Reads are thread-safe; changing the policy while the
    /// server is handling requests is not synchronized.
    /// </summary>
    public class OriginPolicy
    {
        /// <summary>
        /// Gets or sets browser origins allowed in addition to loopback origins, for example
        /// <c>https://app.example.com</c> or <c>http://intranet.local:8080</c>. Matching compares scheme, host,
        /// and port case-insensitively, and treats a default port (80 for http, 443 for https) as equal to an
        /// omitted port. The entry <c>*</c> allows every origin, which is not recommended because it re-enables
        /// cross-site and DNS rebinding access. Default is empty. Setting null stores an empty list.
        /// </summary>
        public List<string> AllowedOrigins
        {
            get => _AllowedOrigins;
            set => _AllowedOrigins = value ?? new List<string>();
        }

        /// <summary>
        /// Gets or sets whether loopback origins (<c>http(s)://localhost</c>, <c>127.0.0.0/8</c>, and
        /// <c>[::1]</c> on any port) are always allowed. Default is true, so local browser tools such as the
        /// MCP Inspector keep working.
        /// </summary>
        public bool AllowLoopbackOrigins
        {
            get => _AllowLoopbackOrigins;
            set => _AllowLoopbackOrigins = value;
        }

        /// <summary>
        /// Gets or sets an optional validator that replaces the built-in rules. It receives the raw
        /// <c>Origin</c> header value, or null when the request has none, and returns true to allow the request.
        /// Default is null, which uses <see cref="AllowedOrigins"/> and <see cref="AllowLoopbackOrigins"/>.
        /// Exceptions thrown by the validator are treated as a rejection.
        /// </summary>
        public Func<string?, bool>? OriginValidator
        {
            get => _OriginValidator;
            set => _OriginValidator = value;
        }

        private List<string> _AllowedOrigins = new List<string>();
        private bool _AllowLoopbackOrigins = true;
        private Func<string?, bool>? _OriginValidator;

        /// <summary>
        /// Returns true when a request carrying <paramref name="origin"/> may be served.
        /// A null origin (the header is absent) is allowed unless <see cref="OriginValidator"/> says otherwise.
        /// An empty or malformed origin, including the literal <c>null</c> sent by sandboxed pages and
        /// <c>file:</c> documents, is rejected unless listed or allowed by the validator.
        /// </summary>
        /// <param name="origin">The <c>Origin</c> header value, or null when absent.</param>
        /// <returns>True when the request is allowed; otherwise false.</returns>
        public bool IsAllowed(string? origin)
        {
            Func<string?, bool>? validator = _OriginValidator;
            if (validator != null)
            {
                try
                {
                    return validator(origin);
                }
                catch
                {
                    return false;
                }
            }

            if (origin == null) return true;

            string trimmed = origin.Trim();
            if (trimmed.Length == 0) return false;

            List<string> allowed = _AllowedOrigins;
            if (allowed.Any(entry => entry != null && entry.Trim() == "*")) return true;
            if (_AllowLoopbackOrigins && IsLoopbackOrigin(trimmed)) return true;

            string normalized = NormalizeOrigin(trimmed);
            return allowed.Any(entry => !String.IsNullOrWhiteSpace(entry)
                && StringComparer.OrdinalIgnoreCase.Equals(NormalizeOrigin(entry.Trim()), normalized));
        }

        /// <summary>
        /// Returns true when <paramref name="origin"/> is an <c>http</c> or <c>https</c> origin whose host is
        /// <c>localhost</c>, a <c>127.0.0.0/8</c> address, or <c>[::1]</c>. Lookalikes such as
        /// <c>http://localhost.evil.example</c> or <c>http://127.0.0.1.evil.example</c> are not loopback.
        /// </summary>
        /// <param name="origin">The origin to test. May be null.</param>
        /// <returns>True for loopback origins; otherwise false.</returns>
        public static bool IsLoopbackOrigin(string? origin)
        {
            if (!TryParseOrigin(origin, out Uri? uri) || uri == null) return false;

            if (uri.HostNameType == UriHostNameType.Dns)
            {
                return StringComparer.OrdinalIgnoreCase.Equals(uri.Host, "localhost");
            }

            return IPAddress.TryParse(uri.DnsSafeHost, out IPAddress? address) && LoopbackAddresses.IsLoopback(address);
        }

        private static bool TryParseOrigin(string? origin, out Uri? uri)
        {
            uri = null;
            if (String.IsNullOrWhiteSpace(origin)) return false;
            if (!Uri.TryCreate(origin!.Trim(), UriKind.Absolute, out Uri? parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (!String.IsNullOrEmpty(parsed.UserInfo)) return false;
            if (parsed.AbsolutePath != "/" || !String.IsNullOrEmpty(parsed.Query) || !String.IsNullOrEmpty(parsed.Fragment)) return false;

            uri = parsed;
            return true;
        }

        private static string NormalizeOrigin(string origin)
        {
            return TryParseOrigin(origin, out Uri? uri) && uri != null
                ? uri.GetLeftPart(UriPartial.Authority)
                : origin.TrimEnd('/');
        }
    }
}
