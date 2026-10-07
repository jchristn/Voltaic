namespace Voltaic.Core
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization.Metadata;

    /// <summary>
    /// The resolver behind every Voltaic serializer option set: Voltaic's source-generated metadata, then the
    /// application's resolvers in the order added, then reflection when <see cref="JsonSerializer.IsReflectionEnabledByDefault"/>
    /// is true. Thread-safe.
    /// </summary>
    internal sealed class VoltaicTypeInfoResolver : IJsonTypeInfoResolver
    {
        /// <summary>
        /// The shared instance.
        /// </summary>
        internal static readonly VoltaicTypeInfoResolver Instance = new VoltaicTypeInfoResolver();

        private readonly object _Lock = new object();
        private volatile IJsonTypeInfoResolver[] _ApplicationResolvers = Array.Empty<IJsonTypeInfoResolver>();
        private IJsonTypeInfoResolver? _Reflection;

        private VoltaicTypeInfoResolver()
        {
        }

        /// <summary>
        /// Adds an application resolver; an instance already added is ignored.
        /// </summary>
        internal void Add(IJsonTypeInfoResolver resolver)
        {
            lock (_Lock)
            {
                if (_ApplicationResolvers.Contains(resolver)) return;
                _ApplicationResolvers = _ApplicationResolvers.Append(resolver).ToArray();
            }
        }

        /// <inheritdoc />
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            JsonTypeInfo? info = ((IJsonTypeInfoResolver)VoltaicJsonContext.Default).GetTypeInfo(type, options);
            if (info != null) return info;

            foreach (IJsonTypeInfoResolver resolver in _ApplicationResolvers)
            {
                info = resolver.GetTypeInfo(type, options);
                if (info != null) return info;
            }

            if (JsonSerializer.IsReflectionEnabledByDefault)
            {
                return Reflection().GetTypeInfo(type, options);
            }

            return null;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reached only when JsonSerializer.IsReflectionEnabledByDefault is true, which trimmed and Native AOT applications set to false.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Reached only when JsonSerializer.IsReflectionEnabledByDefault is true, which Native AOT applications set to false.")]
        private IJsonTypeInfoResolver Reflection()
        {
            return _Reflection ??= new DefaultJsonTypeInfoResolver();
        }
    }
}
