namespace Test.Shared
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Metrics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// An in-memory collector for Voltaic's telemetry, the way a host's exporter would subscribe: a
    /// <see cref="MeterListener"/> on the <c>Voltaic</c> meter and an <see cref="ActivityListener"/> on the
    /// <c>Voltaic</c> activity source (and <c>Watson</c>, when asked). Disposing it unsubscribes both.
    /// </summary>
    internal sealed class TelemetryCapture : IDisposable
    {
        private readonly MeterListener _Meters = new MeterListener();
        private readonly ActivityListener _Spans;
        private readonly ConcurrentQueue<CapturedMeasurement> _Measurements = new ConcurrentQueue<CapturedMeasurement>();
        private readonly ConcurrentQueue<Activity> _Stopped = new ConcurrentQueue<Activity>();
        private readonly Func<Instrument, bool> _ThrowFor;

        public TelemetryCapture(bool includeWatson = false, Func<Instrument, bool>? throwFor = null)
        {
            _ThrowFor = throwFor ?? (_ => false);
            _Meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == VoltaicTelemetryNames.MeterName || (includeWatson && instrument.Meter.Name == "Watson"))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _Meters.SetMeasurementEventCallback<double>((instrument, value, tags, state) => Record(instrument, value, tags));
            _Meters.SetMeasurementEventCallback<long>((instrument, value, tags, state) => Record(instrument, value, tags));
            _Meters.SetMeasurementEventCallback<int>((instrument, value, tags, state) => Record(instrument, value, tags));
            _Meters.Start();

            _Spans = new ActivityListener
            {
                ShouldListenTo = source => source.Name == VoltaicTelemetryNames.ActivitySourceName || (includeWatson && source.Name == "Watson"),
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _Stopped.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(_Spans);
        }

        public IReadOnlyList<CapturedMeasurement> Measurements => _Measurements.ToList();

        public IReadOnlyList<Activity> Spans => _Stopped.ToList();

        /// <summary>
        /// Polls observable instruments (gauges) once.
        /// </summary>
        public void Observe()
        {
            _Meters.RecordObservableInstruments();
        }

        /// <summary>
        /// Measurements of an instrument whose labels match every pair in <paramref name="keyValues"/> (key, value,
        /// key, value, and so on), compared as strings.
        /// </summary>
        public IReadOnlyList<CapturedMeasurement> Find(string name, params string[] keyValues)
        {
            if (keyValues.Length % 2 != 0) throw new ArgumentException("Labels are key and value pairs.", nameof(keyValues));
            return _Measurements.Where(measurement => measurement.Name == name && Matches(measurement, keyValues)).ToList();
        }

        public async Task<IReadOnlyList<CapturedMeasurement>> WaitForAsync(string name, CancellationToken token, params string[] tags)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                IReadOnlyList<CapturedMeasurement> found = Find(name, tags);
                if (found.Count > 0 || DateTime.UtcNow > deadline) return found;
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }

        public async Task<Activity?> WaitForSpanAsync(Func<Activity, bool> predicate, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                Activity? found = _Stopped.FirstOrDefault(predicate);
                if (found != null || DateTime.UtcNow > deadline) return found;
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }

        private static bool Matches(CapturedMeasurement measurement, string[] keyValues)
        {
            for (int i = 0; i < keyValues.Length; i += 2)
            {
                if (!String.Equals(measurement.Tag(keyValues[i])?.ToString(), keyValues[i + 1], StringComparison.Ordinal)) return false;
            }

            return true;
        }

        public void Dispose()
        {
            _Meters.Dispose();
            _Spans.Dispose();
        }

        private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct
        {
            if (_ThrowFor(instrument)) throw new InvalidOperationException("A deliberately failing listener.");
            Dictionary<string, object?> copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags) copy[tag.Key] = tag.Value;
            _Measurements.Enqueue(new CapturedMeasurement(instrument.Name, instrument.Unit, Convert.ToDouble(value), copy));
        }
    }
}
