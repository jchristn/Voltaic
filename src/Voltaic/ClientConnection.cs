namespace Voltaic
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Net.Sockets;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Represents a connected client with its associated resources and metadata.
    /// Supports multiple connection types: TCP, WebSocket, HTTP, and Stdio.
    /// </summary>
    public class ClientConnection : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// Gets the type of client connection.
        /// </summary>
        public ClientConnectionTypeEnum Type { get; }

        /// <summary>
        /// Gets the unique identifier or session ID for this client connection.
        /// </summary>
        public string SessionId { get; }

        /// <summary>
        /// Gets the timestamp of the last activity on this connection.
        /// Updated when notifications are enqueued or dequeued.
        /// </summary>
        public DateTime LastActivity { get; private set; }

        /// <summary>
        /// Gets or sets the maximum number of notifications that can be queued.
        /// When the limit is reached, oldest notifications are discarded.
        /// Default is 100 notifications. Minimum is 1.
        /// </summary>
        public int MaxQueueSize
        {
            get => _MaxQueueSize;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Max queue size must be at least 1");
                _MaxQueueSize = value;
            }
        }

        /// <summary>
        /// Gets the underlying TCP client.
        /// Only applicable for TCP connection types.
        /// </summary>
        public TcpClient? TcpClient { get; }

        /// <summary>
        /// Gets the network stream for reading and writing data.
        /// Only applicable for TCP connection types.
        /// </summary>
        public NetworkStream? Stream { get; }

        /// <summary>
        /// Gets the underlying WebSocket instance.
        /// Only applicable for WebSocket connection types.
        /// </summary>
        public WebSocket? WebSocket { get; }

        /// <summary>
        /// Gets the cancellation token source for this connection.
        /// </summary>
        public CancellationTokenSource? TokenSource { get; }

        #endregion Public-Members

        #region Private-Members

        private readonly ConcurrentQueue<JsonRpcRequest> _Queue;
        private readonly SemaphoreSlim _Semaphore;
        private int _MaxQueueSize = 100;
        private bool _IsDisposed = false;

        #endregion Private-Members

        #region Constructors-and-Factories

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientConnection"/> class for TCP connections.
        /// </summary>
        /// <param name="sessionId">The unique identifier for this client.</param>
        /// <param name="tcpClient">The TCP client connection.</param>
        /// <exception cref="ArgumentNullException">Thrown when sessionId is null or empty, or tcpClient is null.</exception>
        public ClientConnection(string sessionId, TcpClient tcpClient)
        {
            if (String.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));
            if (tcpClient == null) throw new ArgumentNullException(nameof(tcpClient));

            Type = ClientConnectionTypeEnum.Tcp;
            SessionId = sessionId;
            TcpClient = tcpClient;
            Stream = tcpClient.GetStream();
            LastActivity = DateTime.UtcNow;
            _Queue = new ConcurrentQueue<JsonRpcRequest>();
            _Semaphore = new SemaphoreSlim(0);
            TokenSource = null;
            WebSocket = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientConnection"/> class for WebSocket connections.
        /// </summary>
        /// <param name="sessionId">The unique identifier for this connection.</param>
        /// <param name="webSocket">The WebSocket instance.</param>
        /// <exception cref="ArgumentNullException">Thrown when sessionId is null or empty, or webSocket is null.</exception>
        public ClientConnection(string sessionId, WebSocket webSocket)
        {
            if (String.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));
            if (webSocket == null) throw new ArgumentNullException(nameof(webSocket));

            Type = ClientConnectionTypeEnum.Websockets;
            SessionId = sessionId;
            WebSocket = webSocket;
            TokenSource = new CancellationTokenSource();
            LastActivity = DateTime.UtcNow;
            _Queue = new ConcurrentQueue<JsonRpcRequest>();
            _Semaphore = new SemaphoreSlim(0);
            TcpClient = null;
            Stream = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientConnection"/> class for HTTP sessions.
        /// </summary>
        /// <param name="sessionId">The unique identifier for this session.</param>
        /// <exception cref="ArgumentNullException">Thrown when sessionId is null or empty.</exception>
        public ClientConnection(string sessionId)
        {
            if (String.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));

            Type = ClientConnectionTypeEnum.Http;
            SessionId = sessionId;
            LastActivity = DateTime.UtcNow;
            _Queue = new ConcurrentQueue<JsonRpcRequest>();
            _Semaphore = new SemaphoreSlim(0);
            TcpClient = null;
            Stream = null;
            WebSocket = null;
            TokenSource = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientConnection"/> class with a specified connection type.
        /// </summary>
        /// <param name="sessionId">The unique identifier for this connection.</param>
        /// <param name="type">The type of connection.</param>
        /// <exception cref="ArgumentNullException">Thrown when sessionId is null or empty.</exception>
        public ClientConnection(string sessionId, ClientConnectionTypeEnum type)
        {
            if (String.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));

            Type = type;
            SessionId = sessionId;
            LastActivity = DateTime.UtcNow;
            _Queue = new ConcurrentQueue<JsonRpcRequest>();
            _Semaphore = new SemaphoreSlim(0);
            TcpClient = null;
            Stream = null;
            WebSocket = null;
            TokenSource = null;
        }

        #endregion Constructors-and-Factories

        #region Public-Methods

        /// <summary>
        /// Enqueues a notification into the queue.
        /// If the queue is at maximum capacity, the oldest notification will be discarded.
        /// </summary>
        /// <param name="notification">The notification to enqueue.</param>
        /// <exception cref="ArgumentNullException">Thrown when notification is null.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the connection has been disposed.</exception>
        public void Enqueue(JsonRpcRequest notification)
        {
            if (_IsDisposed) throw new ObjectDisposedException(nameof(ClientConnection));
            if (notification == null) throw new ArgumentNullException(nameof(notification));

            _Queue.Enqueue(notification);
            LastActivity = DateTime.UtcNow;

            // Enforce max queue size
            while (_Queue.Count > _MaxQueueSize)
            {
                _Queue.TryDequeue(out JsonRpcRequest? _);
            }

            _Semaphore.Release();
        }

        /// <summary>
        /// Asynchronously waits for and dequeues a notification from the queue.
        /// This method will block until a notification is available or the cancellation token is triggered.
        /// </summary>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the dequeued notification, or null if the operation was cancelled.</returns>
        public async Task<JsonRpcRequest?> DequeueAsync(CancellationToken token = default)
        {
            if (_IsDisposed) return null;

            try
            {
                await _Semaphore.WaitAsync(token).ConfigureAwait(false);
                if (_Queue.TryDequeue(out JsonRpcRequest? notification))
                {
                    LastActivity = DateTime.UtcNow;
                    return notification;
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets all notifications currently in the queue and clears it.
        /// </summary>
        /// <returns>A list of all notifications that were in the queue.</returns>
        public List<JsonRpcRequest> DequeueAll()
        {
            List<JsonRpcRequest> notifications = new List<JsonRpcRequest>();

            while (_Queue.TryDequeue(out JsonRpcRequest? notification))
            {
                notifications.Add(notification);
            }

            LastActivity = DateTime.UtcNow;
            return notifications;
        }

        /// <summary>
        /// Gets the number of notifications currently in the queue.
        /// </summary>
        /// <returns>The number of notifications in the queue.</returns>
        public int Count()
        {
            return _Queue.Count;
        }

        /// <summary>
        /// Releases all resources used by the <see cref="ClientConnection"/>.
        /// </summary>
        public void Dispose()
        {
            if (!_IsDisposed)
            {
                _IsDisposed = true;

                // Cancel any pending operations
                TokenSource?.Cancel();
                TokenSource?.Dispose();

                // Dispose connection-specific resources
                Stream?.Dispose();
                TcpClient?.Close();
                WebSocket?.Dispose();

                // Dispose queue resources
                _Semaphore.Dispose();
            }
        }

        #endregion Public-Methods
    }
}

