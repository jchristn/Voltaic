namespace Test.Shared
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.NetworkInformation;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Sends hand-written bytes over a raw TCP socket, for tests that need exact control over what reaches a
    /// server: spoofed Host headers from a non-loopback address, WebSocket upgrades with a chosen Origin, and
    /// HTTP requests aimed at LSP-framed TCP servers.
    /// </summary>
    internal static class RawSocketRequests
    {
        /// <summary>
        /// Sends <paramref name="request"/> and returns everything the server writes until it closes the
        /// connection, the response headers complete (when <paramref name="stopAtHeaders"/>), or the timeout
        /// elapses. Returns null when the connection is refused or reset before any data arrives.
        /// </summary>
        public static async Task<string?> SendAsync(IPAddress address, int port, string request, bool stopAtHeaders, CancellationToken token, int timeoutMs = 3000)
        {
            using TcpClient client = new TcpClient(address.AddressFamily);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);

            try
            {
                await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                byte[] payload = Encoding.UTF8.GetBytes(request);
                await stream.WriteAsync(payload, 0, payload.Length, timeout.Token).ConfigureAwait(false);

                StringBuilder received = new StringBuilder();
                byte[] buffer = new byte[4096];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    if (stopAtHeaders && received.ToString().Contains("\r\n\r\n")) break;
                }

                return received.ToString();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (System.IO.IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the HTTP status code from the first line of a raw response, or 0 when there is none.
        /// </summary>
        public static int StatusCode(string? response)
        {
            if (String.IsNullOrEmpty(response) || !response.StartsWith("HTTP/", StringComparison.Ordinal)) return 0;
            string[] parts = response.Split(' ');
            return parts.Length > 1 && Int32.TryParse(parts[1], out int code) ? code : 0;
        }

        /// <summary>
        /// Returns true when the raw response headers contain <paramref name="name"/> (case-insensitive).
        /// </summary>
        public static bool HasHeader(string? response, string name)
        {
            if (String.IsNullOrEmpty(response)) return false;
            int headerEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headers = headerEnd >= 0 ? response.Substring(0, headerEnd) : response;
            return headers.Split("\r\n").Any(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns an up, non-loopback IPv4 address of this machine, or null when there is none.
        /// </summary>
        public static IPAddress? FindNonLoopbackIPv4()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                    .Select(unicast => unicast.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
            }
            catch (NetworkInformationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Builds a WebSocket upgrade request with the given Host and optional Origin and Authorization.
        /// </summary>
        public static string WebSocketUpgrade(string path, string host, string? origin, string? authorization = null)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append($"GET {path} HTTP/1.1\r\n");
            builder.Append($"Host: {host}\r\n");
            builder.Append("Upgrade: websocket\r\nConnection: Upgrade\r\n");
            builder.Append("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n");
            if (origin != null) builder.Append($"Origin: {origin}\r\n");
            if (authorization != null) builder.Append($"Authorization: {authorization}\r\n");
            builder.Append("\r\n");
            return builder.ToString();
        }

        /// <summary>
        /// Builds an HTTP POST whose body is exactly <paramref name="body"/>, as a browser <c>fetch()</c> sends it.
        /// </summary>
        public static string HttpPost(string path, string host, string body, string contentType = "text/plain", string? origin = "https://evil.example")
        {
            StringBuilder builder = new StringBuilder();
            builder.Append($"POST {path} HTTP/1.1\r\n");
            builder.Append($"Host: {host}\r\n");
            if (origin != null) builder.Append($"Origin: {origin}\r\n");
            builder.Append($"Content-Type: {contentType}\r\n");
            builder.Append("Accept: application/json, text/event-stream\r\n");
            builder.Append($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
            builder.Append("Connection: close\r\n\r\n");
            builder.Append(body);
            return builder.ToString();
        }
    }
}
