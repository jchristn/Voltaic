namespace Test.Shared
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A raw newline-delimited JSON peer that plays the server side of a TCP connection, so tests can send a client
    /// exactly the bytes a misbehaving or unusual server would.
    /// </summary>
    internal sealed class RawTcpPeer : IDisposable
    {
        private readonly TcpListener _Listener;
        private TcpClient? _Accepted;
        private StreamReader? _Reader;

        private RawTcpPeer(TcpListener listener)
        {
            _Listener = listener;
        }

        /// <summary>
        /// Gets the port the peer listens on.
        /// </summary>
        public int Port => ((IPEndPoint)_Listener.LocalEndpoint).Port;

        /// <summary>
        /// Starts listening on a free loopback port.
        /// </summary>
        public static RawTcpPeer Listen()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new RawTcpPeer(listener);
        }

        /// <summary>
        /// Accepts the client's connection.
        /// </summary>
        public async Task AcceptAsync(CancellationToken token)
        {
            _Accepted = await _Listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            _Reader = new StreamReader(_Accepted.GetStream(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Writes one message line.
        /// </summary>
        public async Task SendAsync(string json, CancellationToken token)
        {
            if (_Accepted == null) throw new InvalidOperationException("No client has connected.");
            byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
            await _Accepted.GetStream().WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the next message line, or returns null when none arrives within the timeout.
        /// </summary>
        public async Task<string?> ReceiveAsync(TimeSpan timeout, CancellationToken token)
        {
            if (_Reader == null) throw new InvalidOperationException("No client has connected.");
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            try
            {
                return await _Reader.ReadLineAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _Reader?.Dispose();
            _Accepted?.Dispose();
            _Listener.Stop();
        }
    }
}
