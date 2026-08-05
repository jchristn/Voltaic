namespace Voltaic.Core
{
    /// <summary>
    /// The outcome of attempting to invoke a registered JSON-RPC method: whether a handler was
    /// found and, if so, its result. Replaces a value tuple return. Instances are immutable.
    /// </summary>
    public sealed class RpcMethodInvocation
    {
        /// <summary>
        /// Gets a value indicating whether a registered handler was found and invoked.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Gets the result returned by the handler, or null when no handler was found.
        /// </summary>
        public object? Result { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RpcMethodInvocation"/> class.
        /// </summary>
        /// <param name="success">Whether a handler was found and invoked.</param>
        /// <param name="result">The handler result, or null.</param>
        public RpcMethodInvocation(bool success, object? result)
        {
            Success = success;
            Result = result;
        }
    }
}
