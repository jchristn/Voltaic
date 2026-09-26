namespace Voltaic.A2A
{
    using System;
    using System.Net;
    using System.Net.Sockets;

    /// <summary>
    /// Decides whether a push notification webhook may be called, following the A2A security guidance: reject
    /// loopback, private, link-local, and other non-public addresses to prevent server-side request forgery.
    /// </summary>
    internal static class A2AWebhookAddressPolicy
    {
        /// <summary>
        /// Validates a webhook URL's shape: absolute http or https, no user info, and, when the host is an IP
        /// literal or <c>localhost</c>, a public address. Returns null when valid, or the reason it is not.
        /// </summary>
        internal static string? ValidateUrl(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "The webhook URL must use http or https.";
            if (!String.IsNullOrEmpty(uri.UserInfo)) return "The webhook URL must not contain user information.";
            if (StringComparer.OrdinalIgnoreCase.Equals(uri.Host, "localhost") || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                return "The webhook URL must not target localhost.";
            }

            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address) && !IsPublic(address))
            {
                return "The webhook URL must not target a loopback, private, or link-local address.";
            }

            return null;
        }

        /// <summary>
        /// Returns true when <paramref name="address"/> is a public unicast address.
        /// </summary>
        internal static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return false;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
                if (b[0] == 169 && b[1] == 254) return false;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
                if (b[0] == 192 && b[1] == 168) return false;
                if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;
                if (b[0] >= 224) return false;
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
                byte[] b = address.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return false;
                return true;
            }

            return false;
        }
    }
}
