namespace Voltaic.JsonRpc
{
    using System.Net.Sockets;

    /// <summary>
    /// Represents a connected client with its associated TCP resources.
    /// </summary>
    internal class ClientConnection : IDisposable
    {
        /// <summary>
        /// Gets the unique identifier for this client connection.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the underlying TCP client.
        /// </summary>
        public TcpClient TcpClient { get; }

        /// <summary>
        /// Gets the network stream for reading and writing data.
        /// </summary>
        public NetworkStream Stream { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientConnection"/> class.
        /// </summary>
        /// <param name="id">The unique identifier for this client.</param>
        /// <param name="tcpClient">The TCP client connection.</param>
        public ClientConnection(string id, TcpClient tcpClient)
        {
            Id = id;
            TcpClient = tcpClient;
            Stream = tcpClient.GetStream();
        }

        /// <summary>
        /// Releases all resources used by the <see cref="ClientConnection"/>.
        /// </summary>
        public void Dispose()
        {
            Stream?.Dispose();
            TcpClient?.Close();
        }
    }
}
