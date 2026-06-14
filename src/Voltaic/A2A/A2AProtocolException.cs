#pragma warning disable CS1591
namespace Voltaic.A2A
{
    using System;
    using Voltaic.Core;

    /// <summary>
    /// Standard A2A JSON-RPC error codes.
    /// </summary>
    public enum A2AErrorCode
    {
        TaskNotFound = -32001,
        TaskNotCancelable = -32002,
        PushNotificationNotSupported = -32003,
        UnsupportedOperation = -32004,
        ContentTypeNotSupported = -32005,
        InvalidAgentResponse = -32006,
        ExtendedAgentCardNotConfigured = -32007,
        ExtensionSupportRequired = -32008,
        VersionNotSupported = -32009,
        InvalidRequest = -32600,
        MethodNotFound = -32601,
        InvalidParams = -32602,
        InternalError = -32603,
        ParseError = -32700
    }

    /// <summary>
    /// Represents an A2A protocol error that should be surfaced as JSON-RPC.
    /// </summary>
    public class A2AProtocolException : Exception, IJsonRpcErrorProvider
    {
        public A2AErrorCode ErrorCode { get; }

        public object? ErrorData { get; }

        public A2AProtocolException(A2AErrorCode code, string message, object? data = null)
            : base(message)
        {
            ErrorCode = code;
            ErrorData = data;
        }

        public JsonRpcError ToJsonRpcError()
        {
            return new JsonRpcError
            {
                Code = (int)ErrorCode,
                Message = Message,
                Data = ErrorData
            };
        }

        public static A2AProtocolException InvalidParams(string message, object? data = null)
        {
            return new A2AProtocolException(A2AErrorCode.InvalidParams, message, data);
        }

        public static A2AProtocolException TaskNotFound(string taskId)
        {
            return new A2AProtocolException(A2AErrorCode.TaskNotFound, $"Task '{taskId}' was not found.", new { taskId });
        }

        public static A2AProtocolException UnsupportedOperation(string message)
        {
            return new A2AProtocolException(A2AErrorCode.UnsupportedOperation, message);
        }
    }
}
