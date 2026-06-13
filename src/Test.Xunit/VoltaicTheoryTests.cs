namespace Test.Xunit
{
    using System.Threading;
    using System.Threading.Tasks;
    using Test.Shared;
    using Touchstone.Core;
    using Touchstone.XunitAdapter;
    using global::Xunit;

    public sealed class VoltaicTheoryTests
    {
        public static TouchstoneTheoryData TestCases()
        {
            return new TouchstoneTheoryData(VoltaicSuites.All());
        }

        [Theory]
        [MemberData(nameof(TestCases))]
        public async Task RunTest(TestCaseDescriptor testCase)
        {
            if (testCase.Skip)
            {
                return;
            }

            await testCase.ExecuteAsync(CancellationToken.None);
        }
    }
}
