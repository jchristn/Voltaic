namespace Test.Shared
{
    using System.Collections.Generic;

    /// <summary>
    /// One measurement seen by <see cref="TelemetryCapture"/>.
    /// </summary>
    internal sealed class CapturedMeasurement
    {
        public CapturedMeasurement(string name, string? unit, double value, IReadOnlyDictionary<string, object?> tags)
        {
            Name = name;
            Unit = unit;
            Value = value;
            Tags = tags;
        }

        public string Name { get; }

        public string? Unit { get; }

        public double Value { get; }

        public IReadOnlyDictionary<string, object?> Tags { get; }

        public object? Tag(string key)
        {
            return Tags.TryGetValue(key, out object? value) ? value : null;
        }

        public override string ToString()
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, object?> tag in Tags) parts.Add(tag.Key + "=" + tag.Value);
            return Name + "{" + string.Join(",", parts) + "}=" + Value;
        }
    }
}
