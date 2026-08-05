namespace Test.Shared
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Voltaic.Core;

    /// <summary>
    /// Deserializes JSON-RPC response bodies into typed models without using
    /// System.Text.Json DOM types, so test suites can assert against strongly-typed results.
    /// </summary>
    internal static class RpcResponseHelpers
    {
        public static T? ResultAs<T>(string body)
        {
            JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(body);
            if (response == null || response.Result == null)
            {
                return default;
            }

            string raw = JsonSerializer.Serialize(response.Result);
            return JsonSerializer.Deserialize<T>(raw);
        }

        public static int ErrorCode(string body)
        {
            JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(body);
            return response != null && response.Error != null ? response.Error.Code : 0;
        }

        public static string? ResultType(string body)
        {
            JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(body);
            if (response == null || response.Result == null)
            {
                return null;
            }

            string raw = JsonSerializer.Serialize(response.Result);
            ResultTypeProbe? probe = JsonSerializer.Deserialize<ResultTypeProbe>(raw);
            return probe?.ResultType;
        }

        private sealed class ResultTypeProbe
        {
            [JsonPropertyName("resultType")]
            public string? ResultType { get; set; }
        }
    }
}
