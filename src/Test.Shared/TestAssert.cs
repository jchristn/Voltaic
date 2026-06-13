namespace Test.Shared
{
    using System;
    using System.Collections.Generic;

    internal static class TestAssert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void False(bool condition, string message)
        {
            if (condition) throw new InvalidOperationException(message);
        }

        public static void Equal<T>(T expected, T actual, string? message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message ?? $"Expected '{expected}' but got '{actual}'.");
            }
        }

        public static void NotNull(object? value, string message)
        {
            if (value == null) throw new InvalidOperationException(message);
        }

        public static void Null(object? value, string message)
        {
            if (value != null) throw new InvalidOperationException(message);
        }

        public static async Task ThrowsAsync<TException>(Func<Task> action, string message)
            where TException : Exception
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{message} Expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}",
                    ex);
            }

            throw new InvalidOperationException($"{message} Expected {typeof(TException).Name}, but no exception was thrown.");
        }

        public static void Throws<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{message} Expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}",
                    ex);
            }

            throw new InvalidOperationException($"{message} Expected {typeof(TException).Name}, but no exception was thrown.");
        }
    }
}
