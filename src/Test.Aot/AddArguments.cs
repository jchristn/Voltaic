namespace Test.Aot
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Arguments of the <c>add</c> tool and the JSON-RPC <c>add</c> method.
    /// </summary>
    public sealed class AddArguments
    {
        /// <summary>
        /// Gets or sets the first addend.
        /// </summary>
        [JsonPropertyName("a")]
        public double A { get; set; }

        /// <summary>
        /// Gets or sets the second addend.
        /// </summary>
        [JsonPropertyName("b")]
        public double B { get; set; }
    }
}
