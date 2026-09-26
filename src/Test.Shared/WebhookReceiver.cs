namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A local HTTP endpoint that records every request it receives, for push notification tests. Responses use the
    /// queued status codes in order, then 200.
    /// </summary>
    internal sealed class WebhookReceiver : IDisposable
    {
        private readonly HttpListener _Listener = new HttpListener();
        private readonly ConcurrentQueue<int> _StatusCodes = new ConcurrentQueue<int>();
        private readonly CancellationTokenSource _TokenSource = new CancellationTokenSource();
        private readonly SemaphoreSlim _Received = new SemaphoreSlim(0);

        public WebhookReceiver()
        {
            Port = TestPorts.GetFreePort();
            _Listener.Prefixes.Add($"http://localhost:{Port}/");
            _Listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public string Url => $"http://localhost:{Port}/hook";

        public ConcurrentQueue<ReceivedWebhook> Requests { get; } = new ConcurrentQueue<ReceivedWebhook>();

        public void EnqueueStatus(params int[] statusCodes)
        {
            foreach (int code in statusCodes) _StatusCodes.Enqueue(code);
        }

        /// <summary>
        /// Waits until at least <paramref name="count"/> requests have arrived, or the timeout elapses.
        /// </summary>
        public async Task<bool> WaitForAsync(int count, TimeSpan timeout, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (Requests.Count < count)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                await _Received.WaitAsync(remaining, token).ConfigureAwait(false);
            }

            return true;
        }

        public void Dispose()
        {
            _TokenSource.Cancel();
            try
            {
                _Listener.Stop();
                _Listener.Close();
            }
            catch
            {
            }

            _TokenSource.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_TokenSource.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _Listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                string body;
                using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string? name in context.Request.Headers.AllKeys)
                {
                    if (name != null) headers[name] = context.Request.Headers[name] ?? String.Empty;
                }

                int status = _StatusCodes.TryDequeue(out int queued) ? queued : 200;
                Requests.Enqueue(new ReceivedWebhook(context.Request.HttpMethod, context.Request.Url?.AbsolutePath ?? "", headers, body, status));
                context.Response.StatusCode = status;
                context.Response.Close();
                _Received.Release();
            }
        }
    }
}
