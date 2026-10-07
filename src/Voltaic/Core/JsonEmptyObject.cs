namespace Voltaic.Core
{
    /// <summary>
    /// Serializes as an empty JSON object (<c>{}</c>). Used where Voltaic writes an empty object, because anonymous
    /// types cannot be serialized under Native AOT. Immutable and thread-safe.
    /// </summary>
    internal sealed class JsonEmptyObject
    {
        /// <summary>
        /// The shared instance.
        /// </summary>
        internal static readonly JsonEmptyObject Instance = new JsonEmptyObject();

        private JsonEmptyObject()
        {
        }
    }
}
