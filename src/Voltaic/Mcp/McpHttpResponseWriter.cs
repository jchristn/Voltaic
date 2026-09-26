namespace Voltaic.Mcp
{
    using System;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Writes the response to one Streamable HTTP POST. It stays a single JSON response unless something must be sent
    /// before the result (a notification related to the request, or a keep-alive), in which case it becomes an SSE
    /// stream (<c>text/event-stream</c>) that ends with the JSON-RPC response. Thread-safe.
    /// </summary>
    internal sealed class McpHttpResponseWriter
    {
        private static readonly byte[] _KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");
        private readonly HttpListenerContext _Context;
        private readonly Action<HttpListenerResponse> _PrepareHeaders;
        private readonly SemaphoreSlim _Lock = new SemaphoreSlim(1, 1);
        private bool _Streaming;
        private bool _Completed;

        /// <param name="context">The request context.</param>
        /// <param name="prepareHeaders">Sets CORS and session headers; called once, just before the first write.</param>
        internal McpHttpResponseWriter(HttpListenerContext context, Action<HttpListenerResponse> prepareHeaders)
        {
            _Context = context;
            _PrepareHeaders = prepareHeaders;
        }

        /// <summary>
        /// Gets whether the response became an SSE stream.
        /// </summary>
        internal bool IsStreaming => _Streaming;

        /// <summary>
        /// Sends a JSON-RPC notification before the response, switching to an SSE stream if needed.
        /// </summary>
        internal async Task NotifyAsync(string json, CancellationToken token)
        {
            await _Lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_Completed) return;
                StartStreaming();
                await WriteAsync(Encoding.UTF8.GetBytes("event: message\ndata: " + json + "\n\n"), token).ConfigureAwait(false);
            }
            finally
            {
                _Lock.Release();
            }
        }

        /// <summary>
        /// Writes an SSE keep-alive comment, switching to an SSE stream if needed. Returns false when the client is gone.
        /// </summary>
        internal async Task<bool> KeepAliveAsync(CancellationToken token)
        {
            await _Lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_Completed) return true;
                StartStreaming();
                await WriteAsync(_KeepAlive, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException || ex is System.IO.IOException || ex is InvalidOperationException)
            {
                return false;
            }
            finally
            {
                _Lock.Release();
            }
        }

        /// <summary>
        /// Completes the response. With <paramref name="json"/> null, a stream simply ends (no response is sent, as
        /// for a request the client cancelled) and a plain response gets <paramref name="statusCode"/> with no body.
        /// </summary>
        internal async Task CompleteAsync(string? json, int statusCode, CancellationToken token)
        {
            await _Lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_Completed) return;
                _Completed = true;

                if (_Streaming)
                {
                    if (json != null) await WriteAsync(Encoding.UTF8.GetBytes("event: message\ndata: " + json + "\n\n"), token).ConfigureAwait(false);
                }
                else
                {
                    _PrepareHeaders(_Context.Response);
                    _Context.Response.StatusCode = statusCode;
                    if (json != null)
                    {
                        byte[] body = Encoding.UTF8.GetBytes(json);
                        _Context.Response.ContentType = "application/json";
                        _Context.Response.ContentLength64 = body.Length;
                        await _Context.Response.OutputStream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException || ex is System.IO.IOException)
            {
                // The client went away.
            }
            finally
            {
                try
                {
                    _Context.Response.Close();
                }
                catch
                {
                }

                _Lock.Release();
            }
        }

        /// <summary>
        /// Ends the response without a JSON-RPC response (for a request the client cancelled): an SSE stream that
        /// closes with no events.
        /// </summary>
        internal async Task EndWithoutResponseAsync(CancellationToken token)
        {
            await _Lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_Completed) return;
                _Completed = true;
                StartStreaming();
            }
            catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
            }
            finally
            {
                try
                {
                    _Context.Response.Close();
                }
                catch
                {
                }

                _Lock.Release();
            }
        }

        private void StartStreaming()
        {
            if (_Streaming) return;
            _Streaming = true;
            _PrepareHeaders(_Context.Response);
            _Context.Response.StatusCode = 200;
            _Context.Response.ContentType = "text/event-stream";
            _Context.Response.AddHeader("Cache-Control", "no-cache");
            _Context.Response.AddHeader("X-Accel-Buffering", "no");
            _Context.Response.SendChunked = true;
        }

        private async Task WriteAsync(byte[] bytes, CancellationToken token)
        {
            await _Context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            await _Context.Response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }
    }
}
