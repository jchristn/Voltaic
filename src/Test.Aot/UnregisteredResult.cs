namespace Test.Aot
{
    /// <summary>
    /// A tool result type deliberately left out of <see cref="AotJsonContext"/>, to prove an unregistered type fails
    /// the one call and nothing else.
    /// </summary>
    public sealed class UnregisteredResult
    {
        /// <summary>
        /// Gets or sets a value.
        /// </summary>
        public int Value { get; set; }
    }
}
