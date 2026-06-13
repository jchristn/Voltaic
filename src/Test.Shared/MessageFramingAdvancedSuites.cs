namespace Test.Shared
{
    using System.Text;
    using Touchstone.Core;
    using Voltaic;

    public static class MessageFramingAdvancedSuites
    {
        public static TestSuiteDescriptor EdgeCases()
        {
            const string suiteId = "MessageFraming.Edge";

            return new TestSuiteDescriptor(
                suiteId,
                "Message Framing Edge Cases",
                new List<TestCaseDescriptor>
                {
                    Case(suiteId, "ReadEmptyStream", "ReadMessageAsync returns null for an empty stream", async ct =>
                    {
                        using MemoryStream stream = new MemoryStream(Array.Empty<byte>());
                        (string? message, byte[] _, int offset, int count) = await MessageFraming.ReadMessageAsync(
                            stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);

                        TestAssert.Null(message, "No message should be returned.");
                        TestAssert.Equal(0, offset);
                        TestAssert.Equal(0, count);
                    }),

                    Case(suiteId, "ReadPartialHeaderThenCompletes", "ReadMessageAsync handles a header split across buffered and stream data", async ct =>
                    {
                        string body = "{\"ok\":true}";
                        string frame = Frame(body);
                        byte[] frameBytes = Encoding.UTF8.GetBytes(frame);
                        byte[] initial = Encoding.UTF8.GetBytes("Content-L");
                        byte[] buffer = MessageFraming.CreateBuffer();
                        Buffer.BlockCopy(initial, 0, buffer, 0, initial.Length);

                        using MemoryStream stream = new MemoryStream(frameBytes.Skip(initial.Length).ToArray());
                        (string? message, byte[] _, int offset, int count) = await MessageFraming.ReadMessageAsync(
                            stream, buffer, 0, initial.Length, ct).ConfigureAwait(false);

                        TestAssert.Equal(body, message);
                        TestAssert.True(offset >= 0, "Offset should be valid.");
                        TestAssert.Equal(0, count);
                    }),

                    Case(suiteId, "ReadPartialBodyThenCompletes", "ReadMessageAsync handles a body split across buffered and stream data", async ct =>
                    {
                        string body = "{\"message\":\"partial body\"}";
                        byte[] frameBytes = Encoding.UTF8.GetBytes(Frame(body));
                        int split = frameBytes.Length - 5;
                        byte[] buffer = MessageFraming.CreateBuffer();
                        Buffer.BlockCopy(frameBytes, 0, buffer, 0, split);

                        using MemoryStream stream = new MemoryStream(frameBytes.Skip(split).ToArray());
                        (string? message, byte[] _, int offset, int count) = await MessageFraming.ReadMessageAsync(
                            stream, buffer, 0, split, ct).ConfigureAwait(false);

                        TestAssert.Equal(body, message);
                        TestAssert.True(offset >= 0, "Offset should be valid.");
                        TestAssert.Equal(0, count);
                    }),

                    Case(suiteId, "IncompleteHeaderPreserved", "ReadMessageAsync preserves an incomplete header for a later call", async ct =>
                    {
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes("Content-Length: 12\r\n"));
                        (string? message, byte[] _, int offset, int count) = await MessageFraming.ReadMessageAsync(
                            stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);

                        TestAssert.Null(message, "Partial header should not produce a message.");
                        TestAssert.Equal(0, offset);
                        TestAssert.True(count > 0, "Buffered bytes should be retained.");
                    }),

                    Case(suiteId, "IncompleteBodyPreserved", "ReadMessageAsync preserves an incomplete body for a later call", async ct =>
                    {
                        string wire = "Content-Length: 12\r\n\r\n{\"x\":";
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
                        (string? message, byte[] _, int offset, int count) = await MessageFraming.ReadMessageAsync(
                            stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);

                        TestAssert.Null(message, "Partial body should not produce a message.");
                        TestAssert.Equal(0, offset);
                        TestAssert.True(count > 0, "Buffered bytes should be retained.");
                    }),

                    Case(suiteId, "ReadUnicodePayload", "ReadMessageAsync uses UTF-8 byte length for Unicode payloads", async ct =>
                    {
                        string body = "{\"text\":\"hello \uD83D\uDE80\"}";
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(Frame(body)));

                        (string? message, byte[] _, int _, int count) = await MessageFraming.ReadMessageAsync(
                            stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);

                        TestAssert.Equal(body, message);
                        TestAssert.Equal(0, count);
                    }),

                    Case(suiteId, "WriteUnicodePayloadLength", "WriteMessageAsync writes UTF-8 byte content length", async ct =>
                    {
                        string body = "{\"text\":\"\uD83D\uDE80\"}";
                        using MemoryStream stream = new MemoryStream();
                        await MessageFraming.WriteMessageAsync(stream, body, token: ct).ConfigureAwait(false);

                        string wire = Encoding.UTF8.GetString(stream.ToArray());
                        TestAssert.True(wire.StartsWith($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n"), "Content-Length should be byte based.");
                    }),

                    Case(suiteId, "ReadIgnoresContentTypeHeader", "ReadMessageAsync ignores optional Content-Type headers", async ct =>
                    {
                        string body = "{\"id\":1}";
                        string wire = $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));

                        (string? message, byte[] _, int _, int _) = await MessageFraming.ReadMessageAsync(
                            stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);

                        TestAssert.Equal(body, message);
                    }),

                    Case(suiteId, "ReadCaseInsensitiveContentLength", "ReadMessageAsync accepts case-insensitive Content-Length", async ct =>
                    {
                        string body = "{}";
                        string wire = $"content-length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));

                        (string? message, byte[] _, int _, int _) = await MessageFraming.ReadMessageAsync(
                            stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);

                        TestAssert.Equal(body, message);
                    }),

                    Case(suiteId, "ReadContentLengthWithWhitespace", "ReadMessageAsync accepts whitespace around Content-Length values", async ct =>
                    {
                        string body = "{}";
                        string wire = $"Content-Length:   {Encoding.UTF8.GetByteCount(body)}   \r\n\r\n{body}";
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));

                        (string? message, byte[] _, int _, int _) = await MessageFraming.ReadMessageAsync(
                            stream, MessageFraming.CreateBuffer(), 0, 0, ct).ConfigureAwait(false);

                        TestAssert.Equal(body, message);
                    }),

                    Case(suiteId, "ReadInvalidContentLength", "ReadMessageAsync rejects non-numeric Content-Length", async ct =>
                    {
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes("Content-Length: nope\r\n\r\n{}"));
                        await TestAssert.ThrowsAsync<InvalidDataException>(
                            () => MessageFraming.ReadMessageAsync(stream, MessageFraming.CreateBuffer(), 0, 0, ct),
                            "Non-numeric Content-Length should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "ReadHeaderTooLarge", "ReadMessageAsync rejects oversized headers", async ct =>
                    {
                        byte[] buffer = Encoding.ASCII.GetBytes(new string('A', 1025));
                        await TestAssert.ThrowsAsync<InvalidDataException>(
                            () => MessageFraming.ReadMessageAsync(Stream.Null, buffer, 0, buffer.Length, ct),
                            "Oversized header should fail.").ConfigureAwait(false);
                    }),

                    Case(suiteId, "ReadFromNonZeroOffset", "ReadMessageAsync can extract a message from a non-zero buffer offset", async ct =>
                    {
                        string body = "{\"offset\":true}";
                        byte[] frame = Encoding.UTF8.GetBytes(Frame(body));
                        byte[] buffer = MessageFraming.CreateBuffer();
                        Buffer.BlockCopy(frame, 0, buffer, 7, frame.Length);

                        (string? message, byte[] _, int offset, int count) = await MessageFraming.ReadMessageAsync(
                            Stream.Null, buffer, 7, frame.Length, ct).ConfigureAwait(false);

                        TestAssert.Equal(body, message);
                        TestAssert.Equal(7 + frame.Length, offset);
                        TestAssert.Equal(0, count);
                    }),

                    Case(suiteId, "BufferResizesForLargePayload", "ReadMessageAsync resizes small buffers for large payloads", async ct =>
                    {
                        string body = new string('x', 5000);
                        byte[] frame = Encoding.UTF8.GetBytes(Frame(body));
                        byte[] buffer = new byte[32];
                        Buffer.BlockCopy(frame, 0, buffer, 0, buffer.Length);
                        using MemoryStream stream = new MemoryStream(frame.Skip(buffer.Length).ToArray());

                        string? message = null;
                        int offset = 0;
                        int count = buffer.Length;
                        while (message == null)
                        {
                            (message, buffer, offset, count) = await MessageFraming.ReadMessageAsync(
                                stream, buffer, offset, count, ct).ConfigureAwait(false);

                            if (message == null && count == 0)
                            {
                                throw new InvalidOperationException("Large payload stream ended before message was read.");
                            }
                        }

                        TestAssert.Equal(body, message);
                        TestAssert.True(buffer.Length > 32, "Buffer should grow for large payloads.");
                        TestAssert.Equal(0, count);
                    }),

                    Case(suiteId, "ReadThreeMessagesInOneBuffer", "ReadMessageAsync preserves multiple buffered messages", async ct =>
                    {
                        string first = "{\"id\":1}";
                        string second = "{\"id\":2}";
                        string third = "{\"id\":3}";
                        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(Frame(first) + Frame(second) + Frame(third)));
                        byte[] buffer = MessageFraming.CreateBuffer();

                        (string? message1, byte[] buffer1, int offset1, int count1) = await MessageFraming.ReadMessageAsync(stream, buffer, 0, 0, ct).ConfigureAwait(false);
                        (string? message2, byte[] buffer2, int offset2, int count2) = await MessageFraming.ReadMessageAsync(stream, buffer1, offset1, count1, ct).ConfigureAwait(false);
                        (string? message3, byte[] _, int _, int count3) = await MessageFraming.ReadMessageAsync(stream, buffer2, offset2, count2, ct).ConfigureAwait(false);

                        TestAssert.Equal(first, message1);
                        TestAssert.Equal(second, message2);
                        TestAssert.Equal(third, message3);
                        TestAssert.Equal(0, count3);
                    }),

                    Case(suiteId, "WriteEmptyContentTypeOmitsHeader", "WriteMessageAsync omits empty Content-Type values", async ct =>
                    {
                        using MemoryStream stream = new MemoryStream();
                        await MessageFraming.WriteMessageAsync(stream, "{}", "", ct).ConfigureAwait(false);
                        string wire = Encoding.UTF8.GetString(stream.ToArray());

                        TestAssert.False(wire.Contains("Content-Type:"), "Empty content type should not write a header.");
                    }),

                    Case(suiteId, "WriteFlushesStream", "WriteMessageAsync flushes writable streams", async ct =>
                    {
                        using FlushTrackingStream stream = new FlushTrackingStream();
                        await MessageFraming.WriteMessageAsync(stream, "{}", token: ct).ConfigureAwait(false);

                        TestAssert.True(stream.Flushed, "WriteMessageAsync should flush the stream.");
                    }),
                });
        }

        private static TestCaseDescriptor Case(
            string suiteId,
            string caseId,
            string displayName,
            Func<CancellationToken, Task> executeAsync)
        {
            return new TestCaseDescriptor(suiteId, caseId, displayName, executeAsync, new[] { "api", "framing", "matrix" });
        }

        private static string Frame(string body)
        {
            return $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        }

        private sealed class FlushTrackingStream : MemoryStream
        {
            public bool Flushed { get; private set; }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                Flushed = true;
                return base.FlushAsync(cancellationToken);
            }
        }
    }
}
