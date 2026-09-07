namespace Voltaic.A2A
{
    using System;
    using System.Threading;
    using Voltaic.Core;

    /// <summary>
    /// Internal, request-scoped bridge that carries the authenticated caller's
    /// <see cref="AuthenticationResult"/> from a server's authentication site to the point where the
    /// <see cref="A2ARequestContext"/> handed to the agent handler is constructed.
    /// <para>
    /// The A2A gRPC server delegates to the A2A HTTP endpoint's public methods, so the authenticated
    /// identity cannot be threaded through as a parameter without changing that public surface. This
    /// <see cref="AsyncLocal{T}"/> flows the caller along the shared asynchronous request flow instead;
    /// the constructed <see cref="A2ARequestContext"/> copies the identity onto itself so it survives
    /// into the background handler task and the streaming loop, where the ambient scope no longer applies.
    /// </para>
    /// </summary>
    internal static class A2ACallerContext
    {
        private static readonly AsyncLocal<AuthenticationResult?> _Current = new AsyncLocal<AuthenticationResult?>();

        /// <summary>Gets the authenticated caller for the current request flow, or null when unauthenticated.</summary>
        public static AuthenticationResult? Current => _Current.Value;

        /// <summary>
        /// Makes the supplied authenticated caller ambient for the current asynchronous flow and returns a
        /// token that restores the prior value when disposed. Pushing null is a safe no-op-then-restore for
        /// unauthenticated request paths.
        /// </summary>
        /// <param name="caller">The authenticated caller, or null.</param>
        /// <returns>An <see cref="IDisposable"/> that restores the previously ambient value when disposed.</returns>
        public static IDisposable Push(AuthenticationResult? caller)
        {
            AuthenticationResult? prior = _Current.Value;
            _Current.Value = caller;
            return new Scope(prior);
        }

        private sealed class Scope : IDisposable
        {
            private readonly AuthenticationResult? _Prior;
            private bool _Disposed;

            public Scope(AuthenticationResult? prior)
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
