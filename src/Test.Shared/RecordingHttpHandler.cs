namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A delegating handler that records every request it sends ("METHOD path [protocol version]") and every exception
    /// the inner handler throws, and remembers whether it was disposed. Thread-safe.
    /// </summary>
    internal sealed class RecordingHttpHandler : DelegatingHandler
    {
        private readonly List<string> _Requests = new List<string>();
        private readonly List<Exception> _Failures = new List<Exception>();
        private readonly object _Lock = new object();
        private int _Disposed;

        /// <summary>
        /// Wraps <paramref name="inner"/>, or a new <see cref="SocketsHttpHandler"/> when null.
        /// </summary>
        public RecordingHttpHandler(HttpMessageHandler? inner = null) : base(inner ?? new SocketsHttpHandler())
        {
        }

        /// <summary>
        /// Gets a snapshot of the recorded requests.
        /// </summary>
        public List<string> Requests
        {
            get
            {
                lock (_Lock) return _Requests.ToList();
            }
        }

        /// <summary>
        /// Gets a snapshot of the exceptions the inner handler threw.
        /// </summary>
        public List<Exception> Failures
        {
            get
            {
                lock (_Lock) return _Failures.ToList();
            }
        }

        /// <summary>
        /// Gets whether the handler was disposed.
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref _Disposed) == 1;

        /// <summary>
        /// Returns true when a recorded request starts with <paramref name="prefix"/>.
        /// </summary>
        public bool Saw(string prefix)
        {
            return Requests.Any(request => request.StartsWith(prefix, StringComparison.Ordinal));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string version = request.Headers.TryGetValues("MCP-Protocol-Version", out IEnumerable<string>? values) ? " " + String.Join(",", values) : "";
            lock (_Lock) _Requests.Add($"{request.Method.Method} {request.RequestUri?.AbsolutePath}{version}");
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_Lock) _Failures.Add(ex);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _Disposed, 1);
            base.Dispose(disposing);
        }
    }
}
