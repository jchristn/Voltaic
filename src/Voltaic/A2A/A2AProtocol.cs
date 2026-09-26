namespace Voltaic.A2A
{
    /// <summary>
    /// A2A protocol constants used by Voltaic: the targeted protocol version, HTTP header and discovery paths,
    /// and the A2A v1.0 JSON-RPC method names. All members are static and thread-safe.
    /// </summary>
    public static class A2AProtocol
    {
        /// <summary>
        /// A2A protocol version targeted by Voltaic.
        /// </summary>
        public const string ProtocolVersion = "1.0";

        /// <summary>
        /// A2A HTTP version header.
        /// </summary>
        public const string VersionHeader = "A2A-Version";

        /// <summary>
        /// Header carrying <see cref="PushNotificationConfig.Token"/> on push notification requests, so the receiving
        /// webhook can check that a notification belongs to a configuration it created: <c>X-A2A-Notification-Token</c>.
        /// </summary>
        public const string NotificationTokenHeader = "X-A2A-Notification-Token";

        /// <summary>
        /// Well-known Agent Card discovery path.
        /// </summary>
        public const string AgentCardPath = "/.well-known/agent-card.json";

        /// <summary>
        /// Extended Agent Card path.
        /// </summary>
        public const string ExtendedAgentCardPath = "/extendedAgentCard";

        /// <summary>
        /// JSON-RPC method that sends a message to the agent and returns a task or message (<c>SendMessage</c>).
        /// </summary>
        public const string SendMessage = "SendMessage";
        /// <summary>
        /// JSON-RPC method that sends a message and streams task events over SSE (<c>SendStreamingMessage</c>).
        /// </summary>
        public const string SendStreamingMessage = "SendStreamingMessage";
        /// <summary>
        /// JSON-RPC method that returns a task by ID (<c>GetTask</c>).
        /// </summary>
        public const string GetTask = "GetTask";
        /// <summary>
        /// JSON-RPC method that lists tasks with optional filtering and paging (<c>ListTasks</c>).
        /// </summary>
        public const string ListTasks = "ListTasks";
        /// <summary>
        /// JSON-RPC method that requests cancellation of a non-terminal task (<c>CancelTask</c>).
        /// </summary>
        public const string CancelTask = "CancelTask";
        /// <summary>
        /// JSON-RPC method that streams updates for an existing, non-terminal task over SSE (<c>SubscribeToTask</c>).
        /// </summary>
        public const string SubscribeToTask = "SubscribeToTask";
        /// <summary>
        /// JSON-RPC method that creates or replaces a push notification configuration for a task (<c>CreateTaskPushNotificationConfig</c>).
        /// </summary>
        public const string CreateTaskPushNotificationConfig = "CreateTaskPushNotificationConfig";
        /// <summary>
        /// JSON-RPC method that returns one push notification configuration for a task (<c>GetTaskPushNotificationConfig</c>).
        /// </summary>
        public const string GetTaskPushNotificationConfig = "GetTaskPushNotificationConfig";
        /// <summary>
        /// JSON-RPC method that lists the push notification configurations for a task (<c>ListTaskPushNotificationConfigs</c>,
        /// the A2A v1.0 name). Servers also accept <see cref="ListTaskPushNotificationConfigLegacy"/>.
        /// </summary>
        public const string ListTaskPushNotificationConfig = "ListTaskPushNotificationConfigs";

        /// <summary>
        /// The singular method name <c>ListTaskPushNotificationConfig</c> that Voltaic 2.1.2 and earlier used for
        /// <see cref="ListTaskPushNotificationConfig"/>. Voltaic servers still accept it; clients send the v1.0 name.
        /// </summary>
        public const string ListTaskPushNotificationConfigLegacy = "ListTaskPushNotificationConfig";
        /// <summary>
        /// JSON-RPC method that deletes a push notification configuration for a task (<c>DeleteTaskPushNotificationConfig</c>).
        /// </summary>
        public const string DeleteTaskPushNotificationConfig = "DeleteTaskPushNotificationConfig";
        /// <summary>
        /// JSON-RPC method that returns the authenticated, extended Agent Card (<c>GetExtendedAgentCard</c>).
        /// </summary>
        public const string GetExtendedAgentCard = "GetExtendedAgentCard";

        /// <summary>
        /// Determines if the method produces an SSE response.
        /// </summary>
        /// <param name="method">JSON-RPC method name. Null or unknown names return false.</param>
        /// <returns>True if the method is <see cref="SendStreamingMessage"/> or <see cref="SubscribeToTask"/>.</returns>
        public static bool IsStreamingMethod(string method)
        {
            return method == SendStreamingMessage || method == SubscribeToTask;
        }

        /// <summary>
        /// Determines if the method is part of the push notification configuration API.
        /// </summary>
        /// <param name="method">JSON-RPC method name. Null or unknown names return false.</param>
        /// <returns>True if the method manages push notification configuration.</returns>
        public static bool IsPushNotificationMethod(string method)
        {
            return method == CreateTaskPushNotificationConfig ||
                method == GetTaskPushNotificationConfig ||
                method == ListTaskPushNotificationConfig ||
                method == ListTaskPushNotificationConfigLegacy ||
                method == DeleteTaskPushNotificationConfig;
        }

        /// <summary>
        /// Determines whether a method name belongs to A2A v1.0 JSON-RPC.
        /// </summary>
        /// <param name="method">JSON-RPC method name. Comparison is case-sensitive; null returns false.</param>
        /// <returns>True if the method is one of the A2A v1.0 JSON-RPC methods defined in this class.</returns>
        public static bool IsValidMethod(string method)
        {
            return method == SendMessage ||
                method == GetTask ||
                method == ListTasks ||
                method == CancelTask ||
                method == GetExtendedAgentCard ||
                IsStreamingMethod(method) ||
                IsPushNotificationMethod(method);
        }
    }
}
