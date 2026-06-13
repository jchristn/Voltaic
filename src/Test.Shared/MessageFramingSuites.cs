namespace Test.Shared
{
    using System.Text;
    using Touchstone.Core;
    using Voltaic;

    public static class MessageFramingSuites
    {
        public static TestSuiteDescriptor Framing()
        {
            const string suiteId = "MessageFraming";

            return new TestSuiteDescriptor(
                suiteId,
                "Message Framing API",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "CreateBuffer", "CreateBuffer returns reusable default buffer", ct =>
                    {
                        byte[] buffer = MessageFraming.CreateBuffer();
                        TestAssert.Equal(4096, buffer.Length);
                        return Task.CompletedTask;
                    }),

                    Case(suiteId, "WriteMessageWithoutContentType", "WriteMessageAsync writes required Content-Length header", async ct =>
                    {
                        using MemoryStream stream = new MemoryStream();
                        await MessageFraming.WriteMessageAsync(stream, "{\"ok\":true}", token: ct).ConfigureAwait(false);
                        string wire = Encoding.UTF8.GetString(stream.ToArray());

                        TestAssert.True(wire.StartsWith("Content-Length: 11\r\n\r\n"), "Wire message should include content length and header delimiter.");
                        TestAssert.True(wire.EndsWith("{\"ok\":true}"), "Wire message should include body.");
                    }),

                    Case(suiteId, "WriteMessageWithContentType", "WriteMessageAsync includes optional Content-Type header", async ct =>
                    {
                        using MemoryStream stream = new MemoryStream();
                        await MessageFraming.WriteMessageAsync(stream, "{}", "application/json", ct).ConfigureAwait(false);
                        string wire = Encoding.UTF8.GetString(stream.ToArray());

                        TestAssert.True(wire.Contains("Content-Type: application/json\r\n"), "Wire message should include content type.");
                    }),

                    Case(suiteId, "ReadMessage", "ReadMessageAsync reads a complete framed message", async ct =>
                    {
                        string body = "{\"jsonrpc\":\"2.0\"}";
                        byte[] bytes = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
                        using MemoryStream stream = new MemoryStream(bytes);

                        (string? message, byte[] _, int offset, int count) = await MessageFraming.ReadMessageAsync(stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);
                        TestAssert.Equal(body, message);
                        TestAssert.True(offset >= 0, "Offset should remain valid.");
                        TestAssert.Equal(0, count);
                    }),

                    Case(suiteId, "ReadMultipleMessages", "ReadMessageAsync preserves buffered second message", async ct =>
                    {
                        string first = "{\"id\":1}";
                        string second = "{\"id\":2}";
                        string wire = Frame(first) + Frame(second);
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
                        byte[] buffer = MessageFraming.CreateBuffer();

                        (string? message1, byte[] buffer1, int offset1, int count1) = await MessageFraming.ReadMessageAsync(stream, buffer, 0, 0, ct).ConfigureAwait(false);
                        (string? message2, byte[] _, int offset2, int count2) = await MessageFraming.ReadMessageAsync(stream, buffer1, offset1, count1, ct).ConfigureAwait(false);

                        TestAssert.Equal(first, message1);
                        TestAssert.Equal(second, message2);
                        TestAssert.Equal(0, count2);
                        TestAssert.True(offset2 >= 0, "Offset should remain valid.");
                    }),

                    Case(suiteId, "ReadInvalidHeader", "ReadMessageAsync rejects missing Content-Length", async ct =>
                    {
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes("Bad: 1\r\n\r\n{}"));
                        await TestAssert.ThrowsAsync<InvalidDataException>(
                            () => MessageFraming.ReadMessageAsync(stream, MessageFraming.CreateBuffer(), 0, 0, ct),
                            "Missing content length should fail.").ConfigureAwait(false);
                    }),
                });
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "framing" });
        }

        private static string Frame(string body)
        {
            return $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        }
    }
}
