namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// Provides a TCP-based MCP (Model Context Protocol) client implementation for making remote procedure calls over a network.
    /// This class extends JsonRpcClient with MCP semantics:
    /// <list type="bullet">
    /// <item><description>Messages are newline-delimited JSON, the stdio framing MCP asks custom stream transports to reuse (set <see cref="NewlineDelimited"/> to false for Content-Length framing, which Voltaic TCP servers before 2.1.5 require).</description></item>
    /// <item><description>Connecting performs the <c>initialize</c> handshake unless <see cref="AutoInitialize"/> is false.</description></item>
    /// <item><description>A <c>ping</c> from the server is answered with <c>{}</c>, other server requests by handlers registered with <see cref="JsonRpcClient.RegisterRequestHandler"/> or with <c>-32601</c>.</description></item>
    /// <item><description>A call that times out or is cancelled sends <c>notifications/cancelled</c>.</description></item>
    /// </list>
    /// </summary>
    public class McpTcpClient : JsonRpcClient
    {
        private int _InitializeTimeoutMs = 30000;
        private int _PingIntervalMs = 30000;
        private int _PingTimeoutMs = 10000;
        private int _PingFailureThreshold = 1;
        private McpPinger? _Pinger;

        private string _ProtocolVersion = McpProtocol.LatestProtocolVersion;
        private string _ClientName = "Voltaic.Mcp.TcpClient";
        private string _ClientVersion = "1.0.0";
        private JsonElement? _InitializeResult;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpTcpClient"/> class.
        /// </summary>
        public McpTcpClient()
            : base()
        {
            AnswerPingRequests();
            RequestDispatcher.AcceptsBatches = () => McpClientHandshake.AllowsBatches(_ProtocolVersion);
            NewlineFraming = true;
        }

        /// <summary>
        /// Gets or sets whether messages are newline-delimited JSON (true, the default) or use Content-Length framing
        /// (false, for Voltaic TCP servers before 2.1.5). Set before connecting.
        /// </summary>
        public bool NewlineDelimited
        {
            get => NewlineFraming;
            set => NewlineFraming = value;
        }

        /// <summary>
        /// Gets or sets the MCP protocol version requested in <c>initialize</c>; after the handshake it holds the version
        /// the server negotiated. Must be a handshake-era revision (<c>2024-11-05</c> through <c>2025-11-25</c>), since a
        /// client must request a version it can negotiate. Default is <see cref="McpProtocol.LatestProtocolVersion"/>.
        /// Setting null or whitespace restores the default.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the value is not a handshake-era protocol version.</exception>
        public string ProtocolVersion
        {
            get => _ProtocolVersion;
            set
            {
                string version = String.IsNullOrWhiteSpace(value) ? McpProtocol.LatestProtocolVersion : value;
                if (!McpProtocol.IsHandshakeVersion(version))
                {
                    throw new ArgumentException($"'{version}' is not a handshake-era MCP protocol version that initialize can negotiate.", nameof(value));
                }

                _ProtocolVersion = version;
            }
        }

        /// <summary>
        /// Gets or sets how long <c>initialize</c> may take, in milliseconds, when <see cref="InitializeAsync(CancellationToken)"/>
        /// runs (explicitly or on connect). Default is 30000. Minimum is 100; maximum is 600000.
        /// <see cref="InitializeAsync(int, CancellationToken)"/> sets it for one call.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 100 to 600000.</exception>
        public int InitializeTimeoutMs
        {
            get => _InitializeTimeoutMs;
            set
            {
                if (value < 100 || value > 600000) throw new ArgumentOutOfRangeException(nameof(value), "InitializeTimeoutMs must be between 100 and 600000.");
                _InitializeTimeoutMs = value;
            }
        }

        /// <summary>
        /// Performs the <c>initialize</c> handshake as <see cref="InitializeAsync(CancellationToken)"/> does, waiting at most
        /// <paramref name="timeoutMs"/> milliseconds for the response.
        /// </summary>
        /// <param name="timeoutMs">How long <c>initialize</c> may take, in milliseconds. Minimum is 100; maximum is 600000.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the handshake is done.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="timeoutMs"/> is outside 100 to 600000.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the client is not connected, or the server rejects initialize or chooses a version this client cannot use.</exception>
        public Task InitializeAsync(int timeoutMs, CancellationToken token = default)
        {
            if (timeoutMs < 100 || timeoutMs > 600000) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "The initialize timeout must be between 100 and 600000 ms.");
            return InitializeCoreAsync(timeoutMs, token);
        }

        /// <summary>
        /// Gets or sets the minimum interval, in milliseconds, between progress notifications for one request that reach
        /// <see cref="JsonRpcClient.NotificationReceived"/>; faster updates are dropped, except the final one (progress equal to the
        /// total). Progress for a token that no request in flight carries is always dropped. Default is 20. 0 delivers
        /// every update. Maximum is 60000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 60000.</exception>
        public int ProgressIntervalMs
        {
            get => ProgressTracker.MinIntervalMs;
            set => ProgressTracker.MinIntervalMs = value;
        }

        /// <summary>
        /// Gets or sets how often the client pings the server after <c>initialize</c>, in milliseconds, to check that
        /// the connection is healthy (MCP ping utility); a ping that is not answered within <see cref="PingTimeoutMs"/>
        /// is logged. Default is 30000. 0 disables pinging. Maximum is 3600000. Takes effect at the next
        /// <c>initialize</c>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 3600000.</exception>
        public int PingIntervalMs
        {
            get => _PingIntervalMs;
            set
            {
                if (value < 0 || value > 3600000) throw new ArgumentOutOfRangeException(nameof(value), "PingIntervalMs must be between 0 and 3600000.");
                _PingIntervalMs = value;
            }
        }

        /// <summary>
        /// Gets or sets how long the client waits for the server to answer its periodic ping, in milliseconds. Default
        /// is 10000. Minimum is 100; maximum is 600000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 100 to 600000.</exception>
        public int PingTimeoutMs
        {
            get => _PingTimeoutMs;
            set
            {
                if (value < 100 || value > 600000) throw new ArgumentOutOfRangeException(nameof(value), "PingTimeoutMs must be between 100 and 600000.");
                _PingTimeoutMs = value;
            }
        }

        /// <summary>
        /// Gets or sets how many consecutive pings the server may leave unanswered before the client treats the
        /// connection as failed and disconnects (MCP: ping timeouts are connection failures). Default is 1. 0 only logs
        /// unanswered pings. Maximum is 100.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when set outside 0 to 100.</exception>
        public int PingFailureThreshold
        {
            get => _PingFailureThreshold;
            set
            {
                if (value < 0 || value > 100) throw new ArgumentOutOfRangeException(nameof(value), "PingFailureThreshold must be between 0 and 100.");
                _PingFailureThreshold = value;
            }
        }

        /// <summary>
        /// Gets or sets the client name reported in <c>initialize</c>. Default is <c>Voltaic.Mcp.TcpClient</c>.
        /// </summary>
        public string ClientName
        {
            get => _ClientName;
            set => _ClientName = String.IsNullOrWhiteSpace(value) ? "Voltaic.Mcp.TcpClient" : value;
        }

        /// <summary>
        /// Gets or sets the client version reported in <c>initialize</c>. Default is <c>1.0.0</c>.
        /// </summary>
        public string ClientVersion
        {
            get => _ClientVersion;
            set => _ClientVersion = String.IsNullOrWhiteSpace(value) ? "1.0.0" : value;
        }

        /// <summary>
        /// Gets or sets whether connecting performs the MCP <c>initialize</c> handshake automatically, as the
        /// specification requires before any other request. Default is true.
        /// </summary>
        public bool AutoInitialize { get; set; } = true;

        /// <summary>
        /// Gets additional client capabilities to declare in <c>initialize</c>, merged with the capabilities implied by
        /// registered request handlers. Never null. Change it before connecting.
        /// </summary>
        public Dictionary<string, object?> ClientCapabilities { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the server's <c>initialize</c> result, or null before the handshake completed.
        /// </summary>
        public JsonElement? InitializeResult => _InitializeResult;

        /// <summary>
        /// Performs the MCP <c>initialize</c> handshake and sends <c>notifications/initialized</c>. Connecting calls this
        /// automatically unless <see cref="AutoInitialize"/> is false.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the handshake is done.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client is not connected, or the server rejects initialize or chooses a version this client cannot use.</exception>
        public Task InitializeAsync(CancellationToken token = default)
        {
            return InitializeCoreAsync(_InitializeTimeoutMs, token);
        }

        private async Task InitializeCoreAsync(int timeoutMs, CancellationToken token)
        {
            McpInitializeOutcome outcome = await McpClientHandshake.RunAsync(
                async (parameters, ct) => McpClientHandshake.ToElement(await CallAsync<object?>("initialize", parameters, timeoutMs, ct).ConfigureAwait(false)),
                ct => NotifyAsync("notifications/initialized", null, ct),
                _ProtocolVersion,
                _ClientName,
                _ClientVersion,
                McpClientHandshake.CapabilitiesFor(RequestDispatcher, ClientCapabilities, _ProtocolVersion),
                token).ConfigureAwait(false);

            _ProtocolVersion = outcome.ProtocolVersion;
            _InitializeResult = outcome.Result;

            // Check the connection's health periodically (MCP ping utility).
            _Pinger?.Dispose();
            _Pinger = McpPinger.Start(_PingIntervalMs, async ct =>
            {
                try
                {
                    await CallAsync<object?>("ping", new { }, _PingTimeoutMs, ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    return false;
                }
            }, LogMessage, () => Task.Run(() => Disconnect()), _PingFailureThreshold);
        }

        private protected override void OnDisconnected()
        {
            _Pinger?.Dispose();
            _Pinger = null;
        }

        private protected override async Task<bool> OnConnectedAsync(CancellationToken token)
        {
            if (!AutoInitialize) return true;
            try
            {
                await InitializeAsync(token).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private protected override void OnCallAbandoned(string method, int id, bool cancelledByCaller)
        {
            if (method == "initialize") return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await NotifyAsync("notifications/cancelled", new McpCancelledNotification
                    {
                        RequestId = id,
                        Reason = cancelledByCaller ? "The request was cancelled by the client." : "The request timed out."
                    }).ConfigureAwait(false);
                }
                catch
                {
                    // The connection may be closed.
                }
            });
        }
    }
}
