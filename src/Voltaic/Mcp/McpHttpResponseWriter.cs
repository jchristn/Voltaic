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
    /// stream (<c>text/event-stream</c>) that ends with the JSON-RPC response. On a session, the stream is resumable:
    /// every event carries an ID and is logged, a priming event (an ID with empty data) comes first where the session's
    /// revision asks for one, and a client that loses the connection can resume with <c>GET</c> and
    /// <c>Last-Event-ID</c>; events keep being logged after the connection breaks. Thread-safe.
    /// </summary>
    internal sealed class McpHttpResponseWriter
    {
        private static readonly byte[] _KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");
        private readonly HttpListenerContext _Context;
        private readonly Action<HttpListenerResponse> _PrepareHeaders;
        private readonly Func<SseStreamLog>? _OpenLog;
        private readonly bool _Prime;
        private readonly int _RetryMs;
        private readonly SemaphoreSlim _Lock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _WriterToken = new CancellationTokenSource();
        private SseStreamLog? _Log;
        private bool _Streaming;
        private bool _Completed;
        private bool _Broken;

        /// <param name="context">The request context.</param>
        /// <param name="prepareHeaders">Sets CORS and session headers; called once, just before the first write.</param>
        /// <param name="openLog">Opens the session stream log that makes the SSE stream resumable, or null for a stream without event IDs (stateless requests).</param>
        /// <param name="prime">True to start the stream with a priming event (MCP 2025-11-25 and later).</param>
        /// <param name="retryMs">The reconnection delay announced with the priming event.</param>
        internal McpHttpResponseWriter(HttpListenerContext context, Action<HttpListenerResponse> prepareHeaders, Func<SseStreamLog>? openLog = null, bool prime = false, int retryMs = 1000)
        {
            _Context = context;
            _PrepareHeaders = prepareHeaders;
            _OpenLog = openLog;
            _Prime = prime;
            _RetryMs = retryMs;
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
                await StartStreamingAsync(token).ConfigureAwait(false);
                await WriteEventAsync(json, token).ConfigureAwait(false);
            }
            finally
            {
                _Lock.Release();
            }
        }

        /// <summary>
        /// Writes an SSE comment to keep the stream open. Returns false when the client has gone away.
        /// </summary>
        internal async Task<bool> KeepAliveAsync(CancellationToken token)
        {
            await _Lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_Completed) return true;
                await StartStreamingAsync(token).ConfigureAwait(false);
                if (_Broken) return false;
                await WriteAsync(_KeepAlive, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (IsDisconnect(ex))
            {
                _Broken = true;
                return false;
            }
            finally
            {
                _Lock.Release();
            }
        }

        /// <summary>
        /// Sends the JSON-RPC response (or nothing when <paramref name="json"/> is null) and ends the response: as the
        /// final SSE event when streaming, otherwise as a JSON body with <paramref name="statusCode"/>.
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
                    if (json != null) await WriteEventAsync(json, token).ConfigureAwait(false);
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
            catch (Exception ex) when (IsDisconnect(ex))
            {
                // The client went away.
            }
            finally
            {
                FinishLog();
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
        /// Ends the response without a JSON-RPC response (for example when the client cancelled the request).
        /// </summary>
        internal async Task EndWithoutResponseAsync(CancellationToken token)
        {
            await _Lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_Completed) return;
                _Completed = true;
                await StartStreamingAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDisconnect(ex))
            {
            }
            finally
            {
                FinishLog();
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

        private async Task StartStreamingAsync(CancellationToken token)
        {
            if (_Streaming) return;
            _Streaming = true;
            _PrepareHeaders(_Context.Response);
            _Context.Response.StatusCode = 200;
            _Context.Response.ContentType = "text/event-stream";
            _Context.Response.AddHeader("Cache-Control", "no-cache");
            _Context.Response.AddHeader("X-Accel-Buffering", "no");
            _Context.Response.SendChunked = true;

            if (_OpenLog == null) return;
            _Log = _OpenLog();
            _Log.Claim(_WriterToken);
            if (!_Prime) return;

            // Priming event: an event ID and an empty data field, so the client can reconnect with Last-Event-ID.
            try
            {
                await WriteAsync(Encoding.UTF8.GetBytes($"id: {_Log.CurrentEventId}\nretry: {_RetryMs}\ndata:\n\n"), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDisconnect(ex))
            {
                _Broken = true;
            }
        }

        private async Task WriteEventAsync(string json, CancellationToken token)
        {
            if (_Log == null)
            {
                await WriteAsync(Encoding.UTF8.GetBytes("event: message\ndata: " + json + "\n\n"), token).ConfigureAwait(false);
                return;
            }

            // Logged before writing, so an event lost to a broken connection can be replayed on resume. Once the client
            // resumes on a GET stream, that stream owns the log and delivers the events instead.
            SseLoggedEvent logged = _Log.Append(json, _WriterToken);
            if (_Broken || _WriterToken.IsCancellationRequested) return;
            try
            {
                await WriteAsync(Encoding.UTF8.GetBytes($"id: {logged.EventId}\nevent: message\ndata: {json}\n\n"), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDisconnect(ex))
            {
                _Broken = true;
            }
        }

        private void FinishLog()
        {
            if (_Log == null) return;
            _Log.Complete();
            _Log.Release(_WriterToken);
        }

        private async Task WriteAsync(byte[] bytes, CancellationToken token)
        {
            await _Context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            await _Context.Response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }

        private static bool IsDisconnect(Exception ex)
        {
            return ex is HttpListenerException || ex is ObjectDisposedException || ex is System.IO.IOException || ex is InvalidOperationException;
        }
    }
}
