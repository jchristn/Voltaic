namespace Voltaic.Core
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// A JSON-RPC batch a client received from a server, sorted into its requests (reserved with the dispatcher in
    /// message order), the responses and notifications to process one by one, and the error responses JSON-RPC 2.0
    /// requires for an empty batch (one Invalid Request) and for each element that is not a valid message.
    /// </summary>
    internal sealed class ClientBatch
    {
        private ClientBatch()
        {
        }

        /// <summary>
        /// Gets the requests to answer.
        /// </summary>
        internal List<JsonRpcRequest> Requests { get; } = new List<JsonRpcRequest>();

        /// <summary>
        /// Gets the raw responses and notifications, to process individually.
        /// </summary>
        internal List<string> Others { get; } = new List<string>();

        /// <summary>
        /// Gets the Invalid Request errors to include in the batch response.
        /// </summary>
        internal List<JsonRpcResponse> Errors { get; } = new List<JsonRpcResponse>();

        /// <summary>
        /// Sorts a batch.
        /// </summary>
        /// <exception cref="JsonException">Thrown when the text is not JSON.</exception>
        internal static ClientBatch Parse(string batchJson, ClientRequestDispatcher dispatcher)
        {
            ClientBatch batch = new ClientBatch();
            using (JsonDocument document = JsonDocument.Parse(batchJson, JsonLimits.Document))
            {
                int count = 0;
                foreach (JsonElement element in document.RootElement.EnumerateArray())
                {
                    count++;
                    string raw = element.GetRawText();
                    JsonRpcRequest? request = element.ValueKind == JsonValueKind.Object ? ClientRequestDispatcher.ParseRequest(raw) : null;
                    if (request != null)
                    {
                        // Reserved in message order, so a cancellation read after the batch applies to it.
                        dispatcher.Reserve(request.Id);
                        batch.Requests.Add(request);
                    }
                    else if (IsNotificationOrResponse(element))
                    {
                        batch.Others.Add(raw);
                    }
                    else
                    {
                        batch.Errors.Add(InvalidRequest("Invalid Request: a batch element is not a JSON-RPC request, notification, or response."));
                    }
                }

                if (count == 0) batch.Errors.Add(InvalidRequest("Invalid Request: an empty batch is not allowed."));
            }

            return batch;
        }

        // A notification has a string method and no id; a response has an id and a result or an error, and no method.
        private static bool IsNotificationOrResponse(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return false;
            bool hasId = element.TryGetProperty("id", out JsonElement _);
            if (element.TryGetProperty("method", out JsonElement method)) return method.ValueKind == JsonValueKind.String && !hasId;
            return hasId && (element.TryGetProperty("result", out JsonElement _) || element.TryGetProperty("error", out JsonElement _));
        }

        private static JsonRpcResponse InvalidRequest(string message)
        {
            JsonRpcError error = JsonRpcError.InvalidRequest();
            error.Message = message;
            return new JsonRpcResponse { Id = null, Error = error };
        }
    }
}
