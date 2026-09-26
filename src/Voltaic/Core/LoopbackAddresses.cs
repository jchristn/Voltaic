namespace Voltaic.Core
{
    using System;
    using System.Net;

    /// <summary>
    /// Helpers that decide whether a host name or remote address is loopback. HTTP and WebSocket servers
    /// use them to keep a server bound to <c>localhost</c> local-only on every platform, and to recognize
    /// loopback browser origins. All members are thread-safe.
    /// </summary>
    public static class LoopbackAddresses
    {
        /// <summary>
        /// Returns true when <paramref name="address"/> is a loopback address: any <c>127.0.0.0/8</c> address,
        /// <c>::1</c>, or an IPv4-mapped IPv6 form of a <c>127.0.0.0/8</c> address (for example
        /// <c>::ffff:127.0.0.1</c>). Returns false for null and for every other address, including the
        /// machine's own LAN addresses.
        /// </summary>
        /// <param name="address">The address to test. May be null.</param>
        /// <returns>True for loopback addresses; otherwise false.</returns>
        public static bool IsLoopback(IPAddress? address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }

        /// <summary>
        /// Returns true when <paramref name="hostname"/> names the loopback interface: <c>localhost</c>
        /// (case-insensitive), an IPv4 literal in <c>127.0.0.0/8</c>, or <c>::1</c> with or without brackets.
        /// Returns false for null, empty, wildcard (<c>*</c>, <c>+</c>), and every other name or address.
        /// </summary>
        /// <param name="hostname">The configured host name. May be null.</param>
        /// <returns>True for loopback host names; otherwise false.</returns>
        public static bool IsLoopbackHostname(string? hostname)
        {
            if (String.IsNullOrWhiteSpace(hostname)) return false;

            string trimmed = hostname!.Trim();
            if (StringComparer.OrdinalIgnoreCase.Equals(trimmed, "localhost")) return true;

            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }

            return IPAddress.TryParse(trimmed, out IPAddress? parsed) && IsLoopback(parsed);
        }
    }
}
