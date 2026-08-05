namespace Voltaic.Core
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Typed view of the built-in <c>echo</c> method arguments.
    /// </summary>
    internal sealed class RpcEchoArguments
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Typed view of the built-in <c>add</c> method arguments.
    /// </summary>
    internal sealed class RpcAddArguments
    {
        [JsonPropertyName("a")]
        public double A { get; set; }

        [JsonPropertyName("b")]
        public double B { get; set; }
    }
}
