namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.JsonRpc;

    /// <summary>
    /// Represents a notification queue for a specific client session.
    /// Used by the HTTP server to queue notifications that will be delivered via Server-Sent Events.
    /// </summary>
    public class NotificationQueue : IDisposable
    {
        /// <summary>
        /// Gets the session ID associated with this queue.
        /// </summary>
        public string SessionId { get; }

        /// <summary>
        /// Gets the timestamp of the last activity on this queue.
        /// Updated when notifications are enqueued or dequeued.
        /// </summary>
        public DateTime LastActivity { get; private set; }

        /// <summary>
        /// Gets the maximum number of notifications that can be queued.
        /// When the limit is reached, oldest notifications are discarded.
        /// Default is 100 notifications.
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

        private readonly ConcurrentQueue<JsonRpcRequest> _Queue;
        private readonly SemaphoreSlim _Semaphore;
        private int _MaxQueueSize = 100;
        private bool _IsDisposed = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="NotificationQueue"/> class.
        /// </summary>
        /// <param name="sessionId">The session ID for this queue.</param>
        /// <exception cref="ArgumentNullException">Thrown when sessionId is null or empty.</exception>
        public NotificationQueue(string sessionId)
        {
            if (String.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));

            SessionId = sessionId;
            LastActivity = DateTime.UtcNow;
            _Queue = new ConcurrentQueue<JsonRpcRequest>();
            _Semaphore = new SemaphoreSlim(0);
        }

        /// <summary>
        /// Enqueues a notification into the queue.
        /// If the queue is at maximum capacity, the oldest notification will be discarded.
        /// </summary>
        /// <param name="notification">The notification to enqueue.</param>
        /// <exception cref="ArgumentNullException">Thrown when notification is null.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the queue has been disposed.</exception>
        public void Enqueue(JsonRpcRequest notification)
        {
            if (_IsDisposed) throw new ObjectDisposedException(nameof(NotificationQueue));
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
        /// Releases all resources used by the <see cref="NotificationQueue"/>.
        /// </summary>
        public void Dispose()
        {
            if (!_IsDisposed)
            {
                _IsDisposed = true;
                _Semaphore.Dispose();
            }
        }
    }
}
