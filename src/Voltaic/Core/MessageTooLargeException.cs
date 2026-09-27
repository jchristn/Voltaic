namespace Voltaic.Core
{
    using System.IO;

    /// <summary>
    /// Thrown internally when a received message or request body exceeds the configured maximum message size.
    /// </summary>
    internal sealed class MessageTooLargeException : IOException
    {
        /// <summary>
        /// Creates the exception for a limit.
        /// </summary>
        internal MessageTooLargeException(long limit)
            : base($"The message exceeds the maximum message size of {limit} bytes.")
        {
            Limit = limit;
        }

        /// <summary>
        /// Gets the limit that was exceeded, in bytes.
        /// </summary>
        internal long Limit { get; }
    }
}
