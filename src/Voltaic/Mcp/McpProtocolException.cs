namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Represents an MCP protocol error that should be surfaced as a JSON-RPC error response.
    /// </summary>
    public class McpProtocolException : Exception, IJsonRpcErrorProvider
    {
        /// <summary>
        /// Gets the JSON-RPC error code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets optional JSON-RPC error data.
        /// </summary>
        public object? ErrorData { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpProtocolException"/> class.
        /// </summary>
        /// <param name="code">JSON-RPC error code.</param>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        public McpProtocolException(int code, string message, object? data = null)
            : base(message)
        {
            Code = code;
            ErrorData = data;
        }

        /// <summary>
        /// Creates an invalid params error.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException InvalidParams(string message, object? data = null)
        {
            return new McpProtocolException(-32602, message, data);
        }

        /// <summary>
        /// Creates a validation error.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException ValidationError(string message, object? data = null)
        {
            return InvalidParams(message, data);
        }

        /// <summary>
        /// Creates an unsupported protocol version error.
        /// </summary>
        /// <param name="version">Unsupported version.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException UnsupportedVersion(string version)
        {
            return InvalidParams($"Unsupported MCP protocol version '{version}'.", new { protocolVersion = version });
        }

        /// <summary>
        /// Creates an invalid cursor error.
        /// </summary>
        /// <param name="cursor">Invalid cursor value.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException InvalidCursor(string cursor)
        {
            return InvalidParams($"Invalid cursor '{cursor}'.", new { cursor });
        }

        /// <summary>
        /// Creates an invalid session error.
        /// </summary>
        /// <param name="sessionId">Invalid session identifier.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException InvalidSession(string sessionId)
        {
            return InvalidParams($"Invalid MCP session '{sessionId}'.", new { sessionId });
        }

        /// <summary>
        /// Creates a cancelled request error.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException CancelledRequest(string message = "Request cancelled.", object? data = null)
        {
            return new McpProtocolException(-32800, message, data);
        }

        /// <summary>
        /// Creates a method not found error.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException MethodNotFound(string message, object? data = null)
        {
            return new McpProtocolException(-32601, message, data);
        }

        /// <summary>
        /// Creates a header-mismatch error (JSON-RPC code <c>-32020</c>) used by the stateless
        /// 2026-07-28 transport when an HTTP routing header does not match the request body,
        /// or when a required routing header is missing or malformed. The HTTP status is <c>400</c>.
        /// </summary>
        /// <param name="message">Error message describing the mismatch. May not be null.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException HeaderMismatch(string message)
        {
            return new McpProtocolException(-32020, message);
        }

        /// <summary>
        /// Creates a missing-required-client-capability error (JSON-RPC code <c>-32021</c>) used by
        /// the stateless 2026-07-28 transport when a request requires a capability the client did
        /// not declare. The HTTP status is <c>400</c>.
        /// </summary>
        /// <param name="message">Error message. May not be null.</param>
        /// <param name="requiredCapabilities">The capabilities required by the server. May be null.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException MissingRequiredClientCapability(string message, object? requiredCapabilities = null)
        {
            return new McpProtocolException(-32021, message, requiredCapabilities == null ? null : new { requiredCapabilities });
        }

        /// <summary>
        /// Creates an unsupported-protocol-version error (JSON-RPC code <c>-32022</c>) used by the
        /// stateless 2026-07-28 transport when the requested version is unknown or unsupported.
        /// The <c>data</c> payload carries the supported versions and the requested value.
        /// The HTTP status is <c>400</c>.
        /// </summary>
        /// <param name="requested">The requested (unsupported) version. May not be null.</param>
        /// <param name="supported">The versions the server supports. May not be null.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException UnsupportedProtocolVersion(string requested, IEnumerable<string> supported)
        {
            string[] supportedArray = supported == null ? Array.Empty<string>() : supported.ToArray();
            return new McpProtocolException(
                -32022,
                $"Unsupported MCP protocol version '{requested}'.",
                new { supported = supportedArray, requested });
        }

        /// <summary>
        /// Converts this exception into a JSON-RPC error object.
        /// </summary>
        /// <returns>JSON-RPC error.</returns>
        public JsonRpcError ToJsonRpcError()
        {
            return new JsonRpcError
            {
                Code = Code,
                Message = Message,
                Data = ErrorData
            };
        }
    }
}
