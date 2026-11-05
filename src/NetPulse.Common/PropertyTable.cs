using System.Collections;
using System.Collections.Generic;

namespace NetPulse.Common
{
    public sealed class PropertyTable : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly Dictionary<string, object> _values = new();

        public PropertyTable()
        {
        }

        public PropertyTable(PropertyTable source)
        {
            if (source == null)
            {
                return;
            }

            foreach (var pair in source._values)
            {
                _values[pair.Key] = pair.Value;
            }
        }

        public int Count => _values.Count;

        public IEnumerable<string> Keys => _values.Keys;

        public object this[string key]
        {
            get => _values.TryGetValue(key, out var value) ? value : null;
            set => _values[key] = value;
        }

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        public bool Remove(string key) => _values.Remove(key);

        public void Clear() => _values.Clear();

        public T Get<T>(string key) => Get(key, default(T));

        public T Get<T>(string key, T defaultValue) => TryGet<T>(key, out var value) ? value : defaultValue;

        public bool TryGet<T>(string key, out T value)
        {
            if (_values.TryGetValue(key, out var raw) && raw != null)
            {
                return ValueConvert.TryConvert(raw, out value);
            }

            value = default;
            return false;
        }

        public void Merge(PropertyTable delta)
        {
            if (delta == null)
            {
                return;
            }

            foreach (var pair in delta._values)
            {
                if (pair.Value == null)
                {
                    _values.Remove(pair.Key);
                }
                else
                {
                    _values[pair.Key] = pair.Value;
                }
            }
        }

        public bool Matches(PropertyTable expected)
        {
            if (expected == null)
            {
                return true;
            }

            foreach (var pair in expected._values)
            {
                _values.TryGetValue(pair.Key, out var actual);

                if (!ValueConvert.ValuesEqual(actual, pair.Value))
                {
                    return false;
                }
            }

            return true;
        }

        public PropertyTable Clone() => new(this);

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

        internal void SetRaw(string key, object value) => _values[key] = value;
    }
}
