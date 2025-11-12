namespace Voltaic
{
    /// <summary>
    /// Specifies the type of client connection.
    /// </summary>
    public enum ClientConnectionTypeEnum
    {
        /// <summary>
        /// Standard input/output connection.
        /// </summary>
        Stdio,

        /// <summary>
        /// TCP socket connection.
        /// </summary>
        Tcp,

        /// <summary>
        /// HTTP-based connection with Server-Sent Events for notifications.
        /// </summary>
        Http,

        /// <summary>
        /// WebSocket connection.
        /// </summary>
        Websockets
    }
}
