#pragma warning disable CS1591
namespace Voltaic.A2A
{
    /// <summary>
    /// A2A protocol constants used by Voltaic.
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
        /// Well-known Agent Card discovery path.
        /// </summary>
        public const string AgentCardPath = "/.well-known/agent-card.json";

        /// <summary>
        /// Extended Agent Card path.
        /// </summary>
        public const string ExtendedAgentCardPath = "/extendedAgentCard";

        public const string SendMessage = "SendMessage";
        public const string SendStreamingMessage = "SendStreamingMessage";
        public const string GetTask = "GetTask";
        public const string ListTasks = "ListTasks";
        public const string CancelTask = "CancelTask";
        public const string SubscribeToTask = "SubscribeToTask";
        public const string CreateTaskPushNotificationConfig = "CreateTaskPushNotificationConfig";
        public const string GetTaskPushNotificationConfig = "GetTaskPushNotificationConfig";
        public const string ListTaskPushNotificationConfig = "ListTaskPushNotificationConfig";
        public const string DeleteTaskPushNotificationConfig = "DeleteTaskPushNotificationConfig";
        public const string GetExtendedAgentCard = "GetExtendedAgentCard";

        /// <summary>
        /// Determines if the method produces an SSE response.
        /// </summary>
        /// <param name="method">JSON-RPC method name.</param>
        /// <returns>True if the method is streaming.</returns>
        public static bool IsStreamingMethod(string method)
        {
            return method == SendStreamingMessage || method == SubscribeToTask;
        }

        /// <summary>
        /// Determines if the method is part of the push notification configuration API.
        /// </summary>
        /// <param name="method">JSON-RPC method name.</param>
        /// <returns>True if the method manages push notification configuration.</returns>
        public static bool IsPushNotificationMethod(string method)
        {
            return method == CreateTaskPushNotificationConfig ||
                method == GetTaskPushNotificationConfig ||
                method == ListTaskPushNotificationConfig ||
                method == DeleteTaskPushNotificationConfig;
        }

        /// <summary>
        /// Determines whether a method name belongs to A2A v1.0 JSON-RPC.
        /// </summary>
        /// <param name="method">JSON-RPC method name.</param>
        /// <returns>True if the method is supported by A2A.</returns>
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
