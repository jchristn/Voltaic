namespace Voltaic.Core
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    /// <summary>
    /// Ambient, request-scoped context describing the authenticated caller for the JSON-RPC / MCP
    /// request currently being handled on this asynchronous flow.
    /// <para>
    /// The context is populated by the transport immediately after a successful
    /// <see cref="AuthenticationResult"/> and restored to its prior value when the request completes.
    /// Because it is stored in an <see cref="AsyncLocal{T}"/>, it flows automatically through the
    /// awaited call chain into method and tool handlers without any change to their signatures, and
    /// concurrent requests on independent asynchronous flows never observe one another's context.
    /// </para>
    /// <para>
    /// Handlers read <see cref="Current"/> to authorize per-caller (for example, to scope reads or gate
    /// writes by tenant, user, or role). <see cref="Current"/> is <see langword="null"/> when no
    /// authentication handler is configured on the transport, when the transport does not support
    /// authentication, or when the request was allowed to bypass authentication (for example, the MCP
    /// <c>ping</c> handshake).
    /// </para>
    /// </summary>
    /// <remarks>
    /// This type is immutable and therefore thread-safe. The ambient value is isolated per asynchronous
    /// flow by <see cref="AsyncLocal{T}"/>; reads from <see cref="Current"/> on unrelated flows are safe
    /// and independent.
    /// </remarks>
    public sealed class RpcCallContext
    {
        private static readonly AsyncLocal<RpcCallContext?> _Current = new AsyncLocal<RpcCallContext?>();

        private readonly string? _Principal;
        private readonly IReadOnlyDictionary<string, string> _Claims;

        /// <summary>
        /// Gets the context for the request currently being handled on this asynchronous flow, or
        /// <see langword="null"/> when there is no authenticated caller for the current request.
        /// </summary>
        /// <value>
        /// The ambient <see cref="RpcCallContext"/> for the current asynchronous flow, or
        /// <see langword="null"/>. Never throws.
        /// </value>
        public static RpcCallContext? Current => _Current.Value;

        /// <summary>
        /// Gets the authenticated principal supplied by the transport's authentication handler.
        /// This value is host-defined and opaque to Voltaic (for example, a user id or service account).
        /// </summary>
        /// <value>The principal, or <see langword="null"/> when the authentication result carried none.</value>
        public string? Principal => _Principal;

        /// <summary>
        /// Gets the host-defined claims copied from the authentication result (for example,
        /// <c>tenantId</c>, <c>userId</c>, or role flags).
        /// </summary>
        /// <value>
        /// A read-only, never-null dictionary of claims. Empty when the authentication result carried
        /// no claims.
        /// </value>
        public IReadOnlyDictionary<string, string> Claims => _Claims;

        /// <summary>
        /// Initializes a new instance of the <see cref="RpcCallContext"/> class. Intended to be created
        /// by transports; hosts read the ambient value through <see cref="Current"/> rather than
        /// constructing instances directly.
        /// </summary>
        /// <param name="principal">
        /// The authenticated principal, or <see langword="null"/> when none is available.
        /// </param>
        /// <param name="claims">
        /// The host-defined claims associated with the authenticated identity, or <see langword="null"/>.
        /// A <see langword="null"/> value is treated as an empty claim set. The supplied dictionary is
        /// referenced as-is and is expected to be effectively immutable for the lifetime of the context.
        /// </param>
        public RpcCallContext(string? principal, IReadOnlyDictionary<string, string>? claims)
        {
            _Principal = principal;
            _Claims = claims ?? EmptyClaims;
        }

        /// <summary>
        /// Sets the ambient context for the current asynchronous flow and returns a token that restores
        /// the previously ambient value when disposed.
        /// </summary>
        /// <param name="context">
        /// The context to make ambient, or <see langword="null"/> to make no context ambient (a
        /// no-op-then-restore that safely covers unauthenticated code paths).
        /// </param>
        /// <returns>
        /// An <see cref="IDisposable"/> that, when disposed, restores the value that was ambient before
        /// this call. Disposing more than once is safe. Callers should dispose the token (typically via a
        /// <c>using</c> block) so the ambient value never leaks across pooled continuations.
        /// </returns>
        public static IDisposable Push(RpcCallContext? context)
        {
            RpcCallContext? prior = _Current.Value;
            _Current.Value = context;
            return new Scope(prior);
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyClaims =
            new Dictionary<string, string>(0);

        private sealed class Scope : IDisposable
        {
            private readonly RpcCallContext? _Prior;
            private bool _Disposed;

            public Scope(RpcCallContext? prior)
            {
                _Prior = prior;
            }

            public void Dispose()
            {
                if (!_Disposed)
                {
                    _Current.Value = _Prior;
                    _Disposed = true;
                }
            }
        }
    }
}
