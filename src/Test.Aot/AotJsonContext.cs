namespace Test.Aot
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The application's source-generated metadata, added to Voltaic with <c>VoltaicJson.AddTypeInfoResolver</c>.
    /// </summary>
    [JsonSerializable(typeof(AddArguments))]
    [JsonSerializable(typeof(SumResult))]
    [JsonSerializable(typeof(Greeting))]
    internal partial class AotJsonContext : JsonSerializerContext
    {
    }
}
