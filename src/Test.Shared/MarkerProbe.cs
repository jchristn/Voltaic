namespace Test.Shared
{
    using System.Threading;

    /// <summary>
    /// Counts invocations of a marker tool or method, so security tests can prove a rejected request never
    /// ran any handler code (rather than relying on the response alone).
    /// </summary>
    internal sealed class MarkerProbe
    {
        /// <summary>
        /// The raw invocation counter; increment it with <see cref="Interlocked.Increment(ref int)"/>.
        /// </summary>
        public int Invocations;

        /// <summary>
        /// Gets the number of invocations so far.
        /// </summary>
        public int Count => Volatile.Read(ref Invocations);
    }
}
