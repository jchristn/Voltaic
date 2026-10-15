namespace Test.Shared
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A loopback HTTP peer that answers every connection with the same raw response (for example a 302 redirect) and
    /// counts the connections it accepted, so a test can prove a client did or did not reach it.
    /// </summary>
    internal sealed class CannedHttpPeer : IDisposable
    {
        private readonly TcpListener _Listener;
        private readonly CancellationTokenSource _TokenSource = new CancellationTokenSource();
        private readonly Task _AcceptTask;
        private int _Connections;

        private CannedHttpPeer(TcpListener listener, Func<CannedHttpPeer, string> response)
        {
            _Listener = listener;
            _AcceptTask = Task.Run(() => AcceptLoopAsync(response));
        }

        /// <summary>
        /// Gets the port the peer listens on.
        /// </summary>
        public int Port => ((IPEndPoint)_Listener.LocalEndpoint).Port;

        /// <summary>
        /// Gets the number of connections accepted so far.
        /// </summary>
        public int Connections => Volatile.Read(ref _Connections);

        /// <summary>
        /// Starts a peer on a free 127.0.0.1 port that answers every request with <paramref name="response"/>.
        /// </summary>
        public static CannedHttpPeer Start(string response)
        {
            return Start(_ => response);
        }

        /// <summary>
        /// Starts a peer that answers <c>302 Found</c> with a <c>Location</c> pointing at <paramref name="location"/>.
        /// </summary>
        public static CannedHttpPeer Redirect(string location)
        {
            return Start($"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        }

        /// <summary>
        /// Starts a peer that answers <c>404 Not Found</c>.
        /// </summary>
        public static CannedHttpPeer NotFound()
        {
            return Start("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        }

        /// <summary>
        /// Waits up to <paramref name="timeoutMs"/> for at least one connection.
        /// </summary>
        public async Task<bool> WaitForConnectionAsync(int timeoutMs, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Connections == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, token).ConfigureAwait(false);
            return Connections > 0;
        }

        public void Dispose()
        {
            _TokenSource.Cancel();
            _Listener.Stop();
            _AcceptTask.Wait(1000);
            _TokenSource.Dispose();
        }

        private static CannedHttpPeer Start(Func<CannedHttpPeer, string> response)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new CannedHttpPeer(listener, response);
        }

        private async Task AcceptLoopAsync(Func<CannedHttpPeer, string> response)
        {
            while (!_TokenSource.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _Listener.AcceptTcpClientAsync(_TokenSource.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref _Connections);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            NetworkStream stream = client.GetStream();
                            await ReadHeadersAsync(stream, _TokenSource.Token).ConfigureAwait(false);
                            byte[] bytes = Encoding.ASCII.GetBytes(response(this));
                            await stream.WriteAsync(bytes, _TokenSource.Token).ConfigureAwait(false);
                            await stream.FlushAsync(_TokenSource.Token).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The client may hang up first.
                        }
                    }
                });
            }
        }

        private static async Task ReadHeadersAsync(NetworkStream stream, CancellationToken token)
        {
            // Reads up to the blank line that ends the headers; the body (if any) is ignored.
            byte[] one = new byte[1];
            int matched = 0;
            byte[] terminator = { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
            while (matched < terminator.Length)
            {
                int read = await stream.ReadAsync(one, token).ConfigureAwait(false);
                if (read == 0) return;
                matched = one[0] == terminator[matched] ? matched + 1 : (one[0] == terminator[0] ? 1 : 0);
            }
        }
    }
}
