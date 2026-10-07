namespace Voltaic.Core
{
    using System.Text.Json;

    /// <summary>
    /// JSON reader settings for received messages: nesting up to 256 levels, the depth JSON Schema validation allows,
    /// instead of the .NET default of 64, so valid arguments nested deeper than 64 levels are not mistaken for parse
    /// errors. Thread-safe.
    /// </summary>
    internal static class JsonLimits
    {
        /// <summary>
        /// The deepest nesting a received message may have.
        /// </summary>
        internal const int MaxDepth = 256;

        /// <summary>
        /// Options for <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>.
        /// </summary>
        internal static readonly JsonDocumentOptions Document = new JsonDocumentOptions { MaxDepth = MaxDepth };

        /// <summary>
        /// Options for <see cref="JsonSerializer"/> reads and writes of message content.
        /// </summary>
        internal static readonly JsonSerializerOptions Serializer = new JsonSerializerOptions { MaxDepth = MaxDepth, TypeInfoResolver = VoltaicTypeInfoResolver.Instance };

        /// <summary>
        /// The <see cref="JsonSerializer"/> defaults (<see cref="JsonSerializerOptions.Default"/>) with Voltaic's resolver,
        /// for writes that used the default options.
        /// </summary>
        internal static readonly JsonSerializerOptions Plain = new JsonSerializerOptions { TypeInfoResolver = VoltaicTypeInfoResolver.Instance };
    }
}
