namespace Test.Shared
{
    using Touchstone.Core;

    public static class VoltaicSuites
    {
        public static IReadOnlyList<TestSuiteDescriptor> All(HashSet<string>? tags = null)
        {
            List<TestSuiteDescriptor> suites = new List<TestSuiteDescriptor>
            {
                ModelApiSuites.JsonRpcModels(),
                ModelApiSuites.EventArgsAndSupportingModels(),
                ComprehensiveModelSuites.JsonRpcSerializationMatrix(),
                ComprehensiveModelSuites.McpModelSerializationMatrix(),
                McpProtocolModelSuites.ProtocolAndCapabilities(),
                McpProtocolModelSuites.ContentResourcesPromptsAndTools(),
                ApiSurfaceCoverageSuites.PublicApiInventory(),
                McpServerApiSuites.PublicRegistrationApi(),
                PublicApiValidationSuites.ClientApiValidation(),
                PublicApiValidationSuites.ServerApiValidation(),
                McpHttpProtocolSuites.StreamableHttpCoreProtocol(),
                McpHttpAdvancedProtocolSuites.StreamableHttpMatrix(),
                McpHttpAdvancedProtocolSuites.RegistryProtocolMatrix(),
                McpHttpAdvancedProtocolSuites.HttpClientMatrix(),
                MessageFramingSuites.Framing(),
                MessageFramingAdvancedSuites.EdgeCases(),
                ClientConnectionSuites.QueueAndLifecycle(),
                ClientConnectionAdvancedSuites.ComprehensiveQueueAndLifecycle(),
                JsonRpcTcpIntegrationSuites.TcpClientServerProtocol(),
                McpStdioIntegrationSuites.StdioMcpParity(),
                McpTransportParitySuites.TcpMcpParity(),
                McpTransportParitySuites.WebSocketMcpParity(),
                A2AProtocolSuites.ProtocolAndTransports(),
                A2ACompatibilitySuites.OfficialSdkOracle(),
            };

            if (tags == null || tags.Count == 0)
            {
                return suites;
            }

            return suites
                .Select(suite => new TestSuiteDescriptor(
                    suite.SuiteId,
                    suite.DisplayName,
                    suite.Cases
                        .Where(testCase => testCase.Tags.Any(tag => tags.Contains(tag)))
                        .ToList(),
                    suite.BeforeSuiteAsync,
                    suite.AfterSuiteAsync))
                .Where(suite => suite.Cases.Count > 0)
                .ToList();
        }
    }
}
