namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A raw newline-delimited JSON client for TCP tests, so a test controls exactly what is sent and sees every
    /// message the server writes.
    /// </summary>
    internal sealed class RawLineClient : IDisposable
    {
        private readonly TcpClient _Client = new TcpClient();
        private readonly BlockingCollection<string> _Received = new BlockingCollection<string>();
        private StreamWriter? _Writer;
        private Task? _ReadTask;

        public static async Task<RawLineClient> ConnectAsync(int port, CancellationToken token)
        {
            RawLineClient client = new RawLineClient();
            await client._Client.ConnectAsync("127.0.0.1", port, token).ConfigureAwait(false);
            NetworkStream stream = client._Client.GetStream();
            client._Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            StreamReader reader = new StreamReader(stream, new UTF8Encoding(false));
            client._ReadTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (!String.IsNullOrWhiteSpace(line)) client._Received.Add(line);
                    }
                }
                catch
                {
                }
                finally
                {
                    client._Received.CompleteAdding();
                }
            });
            return client;
        }

        /// <summary>
        /// Gets whether the server closed the connection.
        /// </summary>
        public bool IsClosed => _Received.IsAddingCompleted;

        public async Task SendAsync(string line)
        {
            await _Writer!.WriteLineAsync(line).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends a standard initialize and notifications/initialized, and returns the initialize response.
        /// </summary>
        public async Task<string> InitializeAsync(string version, CancellationToken token)
        {
            await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + version + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw\",\"version\":\"1\"}}}").ConfigureAwait(false);
            string response = Receive(TimeSpan.FromSeconds(10)) ?? throw new TimeoutException("No initialize response.");
            await SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}").ConfigureAwait(false);
            return response;
        }

        /// <summary>
        /// Returns the next message, or null when none arrives in time or the connection closed.
        /// </summary>
        public string? Receive(TimeSpan timeout)
        {
            try
            {
                return _Received.TryTake(out string? line, timeout) ? line : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                _Client.Close();
            }
            catch
            {
            }

            _Client.Dispose();
        }
    }
}
