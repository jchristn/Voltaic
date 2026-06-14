namespace Voltaic.Core
{
    /// <summary>
    /// Provides a JSON-RPC error representation for protocol-specific exceptions.
    /// </summary>
    public interface IJsonRpcErrorProvider
    {
        /// <summary>
        /// Converts the exception into a JSON-RPC error object.
        /// </summary>
        /// <returns>JSON-RPC error.</returns>
        JsonRpcError ToJsonRpcError();
    }
}
