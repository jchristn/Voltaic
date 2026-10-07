namespace Voltaic.Core
{
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.Json.Serialization.Metadata;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// <see cref="JsonSerializer"/> calls that take their metadata from the options' resolver
    /// (<see cref="VoltaicTypeInfoResolver"/> for every Voltaic option set) rather than from reflection, so they are
    /// safe under trimming and Native AOT. A null options argument means <see cref="JsonLimits.Plain"/>. Thread-safe.
    /// </summary>
    internal static class VoltaicJsonSerializer
    {
        /// <summary>
        /// Serializes a value to a JSON string.
        /// </summary>
        internal static string Serialize<T>(T value, JsonSerializerOptions? options = null)
        {
            return JsonSerializer.Serialize(value, Info<T>(options));
        }

        /// <summary>
        /// Writes a value to a writer.
        /// </summary>
        internal static void Serialize<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions? options = null)
        {
            JsonSerializer.Serialize(writer, value, Info<T>(options));
        }

        /// <summary>
        /// Serializes a value to a <see cref="JsonElement"/>.
        /// </summary>
        internal static JsonElement SerializeToElement<T>(T value, JsonSerializerOptions? options = null)
        {
            return JsonSerializer.SerializeToElement(value, Info<T>(options));
        }

        /// <summary>
        /// Serializes a value to a <see cref="JsonNode"/>.
        /// </summary>
        internal static JsonNode? SerializeToNode<T>(T value, JsonSerializerOptions? options = null)
        {
            return JsonSerializer.SerializeToNode(value, Info<T>(options));
        }

        /// <summary>
        /// Reads a value from a JSON string.
        /// </summary>
        internal static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
        {
            return JsonSerializer.Deserialize(json, Info<T>(options));
        }

        /// <summary>
        /// Reads a value from a <see cref="JsonElement"/>.
        /// </summary>
        internal static T? Deserialize<T>(JsonElement element, JsonSerializerOptions? options = null)
        {
            return element.Deserialize(Info<T>(options));
        }

        /// <summary>
        /// Reads a value from a reader.
        /// </summary>
        internal static T? Deserialize<T>(ref Utf8JsonReader reader, JsonSerializerOptions? options = null)
        {
            return JsonSerializer.Deserialize(ref reader, Info<T>(options));
        }

        /// <summary>
        /// Reads a value from a stream.
        /// </summary>
        internal static ValueTask<T?> DeserializeAsync<T>(Stream stream, JsonSerializerOptions? options = null, CancellationToken token = default)
        {
            return JsonSerializer.DeserializeAsync(stream, Info<T>(options), token);
        }

        private static JsonTypeInfo<T> Info<T>(JsonSerializerOptions? options)
        {
            return (JsonTypeInfo<T>)(options ?? JsonLimits.Plain).GetTypeInfo(typeof(T));
        }
    }
}
