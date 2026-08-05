namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// A DOM-free navigable view of a JSON document for test assertions. Built with the streaming
    /// <see cref="Utf8JsonReader"/> into a small tree of the test project's own node type, so tests
    /// can navigate JSON without using any System.Text.Json document object model type.
    /// </summary>
    internal sealed class JsonProbe
    {
        private enum ProbeKind
        {
            Object,
            Array,
            String,
            Number,
            Boolean,
            Null
        }

        private readonly ProbeKind _Kind;
        private readonly string? _String;
        private readonly double _Number;
        private readonly bool _Boolean;
        private readonly Dictionary<string, JsonProbe>? _Members;
        private readonly List<JsonProbe>? _Items;

        private JsonProbe(Dictionary<string, JsonProbe> members)
        {
            _Kind = ProbeKind.Object;
            _Members = members;
        }

        private JsonProbe(List<JsonProbe> items)
        {
            _Kind = ProbeKind.Array;
            _Items = items;
        }

        private JsonProbe(ProbeKind kind, string? stringValue, double numberValue, bool booleanValue)
        {
            _Kind = kind;
            _String = stringValue;
            _Number = numberValue;
            _Boolean = booleanValue;
        }

        /// <summary>Gets the number of items in an array (or members in an object).</summary>
        public int Length => _Kind == ProbeKind.Array ? _Items!.Count : (_Members?.Count ?? 0);

        /// <summary>Gets a value indicating whether this node is a JSON object.</summary>
        public bool IsObject => _Kind == ProbeKind.Object;

        /// <summary>Gets a value indicating whether this node is a JSON array.</summary>
        public bool IsArray => _Kind == ProbeKind.Array;

        /// <summary>Parses raw JSON into a navigable probe.</summary>
        public static JsonProbe Parse(string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            Utf8JsonReader reader = new Utf8JsonReader(bytes);
            reader.Read();
            return Read(ref reader);
        }

        /// <summary>Builds a navigable probe from a CLR value by serializing it to JSON first.</summary>
        public static JsonProbe From(object? value)
        {
            return Parse(JsonSerializer.Serialize(value));
        }

        /// <summary>Enumerates the elements of an array node.</summary>
        public IEnumerable<JsonProbe> EnumerateArray()
        {
            return _Items ?? new List<JsonProbe>();
        }

        /// <summary>Gets the named object member, throwing when absent.</summary>
        public JsonProbe Get(string name)
        {
            if (_Kind != ProbeKind.Object || _Members == null || !_Members.TryGetValue(name, out JsonProbe? child))
            {
                throw new InvalidOperationException($"JSON property '{name}' was not found.");
            }

            return child;
        }

        /// <summary>Gets the array element at the given index.</summary>
        public JsonProbe this[int index] => _Items![index];

        /// <summary>Determines whether the named object member is present.</summary>
        public bool Has(string name)
        {
            return _Kind == ProbeKind.Object && _Members != null && _Members.ContainsKey(name);
        }

        /// <summary>Attempts to get the named object member.</summary>
        public bool TryGet(string name, out JsonProbe child)
        {
            if (_Kind == ProbeKind.Object && _Members != null && _Members.TryGetValue(name, out JsonProbe? found))
            {
                child = found;
                return true;
            }

            child = Null();
            return false;
        }

        /// <summary>Gets the string value, or null when this node is not a string.</summary>
        public string? String()
        {
            return _Kind == ProbeKind.String ? _String : null;
        }

        /// <summary>Gets the value as a 32-bit integer.</summary>
        public int Int()
        {
            return (int)_Number;
        }

        /// <summary>Gets the value as a 64-bit integer.</summary>
        public long Long()
        {
            return (long)_Number;
        }

        /// <summary>Gets the value as a double.</summary>
        public double Double()
        {
            return _Number;
        }

        /// <summary>Gets the boolean value.</summary>
        public bool Bool()
        {
            return _Boolean;
        }

        private static JsonProbe Null()
        {
            return new JsonProbe(ProbeKind.Null, null, 0, false);
        }

        private static JsonProbe Read(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    Dictionary<string, JsonProbe> members = new Dictionary<string, JsonProbe>(StringComparer.Ordinal);
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        string name = reader.GetString() ?? System.String.Empty;
                        reader.Read();
                        members[name] = Read(ref reader);
                    }
                    return new JsonProbe(members);
                case JsonTokenType.StartArray:
                    List<JsonProbe> items = new List<JsonProbe>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        items.Add(Read(ref reader));
                    }
                    return new JsonProbe(items);
                case JsonTokenType.String:
                    return new JsonProbe(ProbeKind.String, reader.GetString(), 0, false);
                case JsonTokenType.Number:
                    return new JsonProbe(ProbeKind.Number, null, reader.GetDouble(), false);
                case JsonTokenType.True:
                    return new JsonProbe(ProbeKind.Boolean, null, 0, true);
                case JsonTokenType.False:
                    return new JsonProbe(ProbeKind.Boolean, null, 0, false);
                default:
                    return Null();
            }
        }
    }
}
