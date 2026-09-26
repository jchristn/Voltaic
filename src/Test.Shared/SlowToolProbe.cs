namespace Test.Shared
{
    using System.Threading;

    /// <summary>
    /// Records whether a slow test tool observed cancellation of its token.
    /// </summary>
    internal sealed class SlowToolProbe
    {
        private int _Cancelled;

        public bool Cancelled
        {
            get => Volatile.Read(ref _Cancelled) == 1;
            set => Volatile.Write(ref _Cancelled, value ? 1 : 0);
        }
    }
}
