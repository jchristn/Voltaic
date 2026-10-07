namespace Voltaic.Core
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.Json.Serialization.Metadata;

    /// <summary>
    /// JSON type metadata used by every Voltaic client and server, for trimmed and Native AOT applications.
    /// Voltaic serializes its own protocol types with source-generated metadata. Values an application passes as
    /// <see cref="object"/> (tool results, call parameters, structured content, error data) and the types it reads
    /// with generic methods such as <c>CallAsync&lt;T&gt;</c> and <see cref="RpcParameters.Deserialize{T}()"/> are
    /// resolved through <see cref="TypeInfoResolver"/>: Voltaic's metadata first, then each resolver added with
    /// <see cref="AddTypeInfoResolver"/>, in the order added, then reflection when it is enabled.
    /// Reflection is enabled by default and disabled in Native AOT and trimmed applications
    /// (<see cref="JsonSerializer.IsReflectionEnabledByDefault"/>); there, every application type Voltaic serializes
    /// must come from an added resolver, typically a <see cref="JsonSerializerContext"/>, or be a JSON DOM type
    /// (<see cref="JsonElement"/>, <see cref="System.Text.Json.Nodes.JsonNode"/>), a primitive, or a Voltaic model.
    /// Anonymous types cannot be source-generated. Thread-safe.
    /// </summary>
    public static class VoltaicJson
    {
        /// <summary>
        /// Gets the resolver Voltaic serializes with. Never null. Applications that build their own
        /// <see cref="JsonSerializerOptions"/> for Voltaic types can use it as their
        /// <see cref="JsonSerializerOptions.TypeInfoResolver"/>. Thread-safe.
        /// </summary>
        public static IJsonTypeInfoResolver TypeInfoResolver => VoltaicTypeInfoResolver.Instance;

        /// <summary>
        /// Adds a resolver, typically an application's <see cref="JsonSerializerContext"/>, that supplies metadata for
        /// application types Voltaic serializes or deserializes. Resolvers are consulted after Voltaic's own metadata,
        /// in the order added; adding the same instance again has no effect. Add resolvers at startup, before the
        /// first server or client is created: Voltaic's serializer options cache metadata per type, including the
        /// failure to find it. Thread-safe.
        /// </summary>
        /// <param name="resolver">The resolver to add. Cannot be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="resolver"/> is null.</exception>
        public static void AddTypeInfoResolver(IJsonTypeInfoResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            VoltaicTypeInfoResolver.Instance.Add(resolver);
        }
    }
}
