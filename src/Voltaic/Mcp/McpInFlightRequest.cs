namespace Voltaic.Mcp
{
    using System;
    using System.Text.Json;
    using System.Threading;

    /// <summary>
    /// A request a server is processing: its cancellation source, its progress token, and whether the client
    /// cancelled it (in which case no response is sent).
    /// </summary>
    internal sealed class McpInFlightRequest : IDisposable
    {
        private int _Cancelled;
        private int _Completed;
        private double? _LastProgress;
        private readonly object _ProgressLock = new object();

        internal McpInFlightRequest(string idKey, string method, JsonElement? progressToken, CancellationToken parent)
        {
            IdKey = idKey;
            Method = method;
            ProgressToken = progressToken;
            TokenSource = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        /// <summary>
        /// Gets the request ID as raw JSON (so <c>1</c> and <c>"1"</c> differ).
        /// </summary>
        internal string IdKey { get; }

        /// <summary>
        /// Gets the request method.
        /// </summary>
        internal string Method { get; }

        /// <summary>
        /// Gets the progress token the request carried in <c>_meta.progressToken</c>, if any.
        /// </summary>
        internal JsonElement? ProgressToken { get; }

        /// <summary>
        /// Gets or sets the channel for notifications related to this request (progress, log messages), or null.
        /// </summary>
        internal Func<Voltaic.Core.JsonRpcRequest, CancellationToken, System.Threading.Tasks.Task>? Notify { get; set; }

        /// <summary>
        /// Gets the source whose token the handler receives.
        /// </summary>
        internal CancellationTokenSource TokenSource { get; }

        /// <summary>
        /// Gets whether the client cancelled the request.
        /// </summary>
        internal bool IsCancelled => Volatile.Read(ref _Cancelled) == 1;

        // True while the request may still produce messages: neither cancelled nor finished.
        internal bool IsActive => Volatile.Read(ref _Cancelled) == 0 && Volatile.Read(ref _Completed) == 0;

        internal void MarkCompleted()
        {
            Volatile.Write(ref _Completed, 1);
        }

        /// <summary>
        /// Marks the request cancelled by the client and cancels its handler token.
        /// </summary>
        internal void Cancel()
        {
            if (Interlocked.Exchange(ref _Cancelled, 1) == 1) return;
            try
            {
                TokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Records a progress value. Returns false when it does not increase, as progress values must.
        /// </summary>
        internal bool TryRecordProgress(double progress)
        {
            lock (_ProgressLock)
            {
                if (_LastProgress.HasValue && progress <= _LastProgress.Value) return false;
                _LastProgress = progress;
                return true;
            }
        }

        public void Dispose()
        {
            TokenSource.Dispose();
        }
    }
}
