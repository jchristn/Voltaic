namespace Test.Aot
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Result of the <c>add</c> tool and the JSON-RPC <c>add</c> method.
    /// </summary>
    public sealed class SumResult
    {
        /// <summary>
        /// Gets or sets the sum.
        /// </summary>
        [JsonPropertyName("total")]
        public double Total { get; set; }
    }
}
