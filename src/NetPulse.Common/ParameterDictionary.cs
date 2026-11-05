using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace NetPulse.Common
{
    internal static class ValueConvert
    {
        public static bool TryConvert<T>(object value, out T result)
        {
            if (value is T typed)
            {
                result = typed;
                return true;
            }

            var target = typeof(T);

            if (value == null)
            {
                result = default;
                return !target.IsValueType || Nullable.GetUnderlyingType(target) != null;
            }

            try
            {
                var underlying = Nullable.GetUnderlyingType(target) ?? target;

                if (underlying.IsEnum && value is IConvertible)
                {
                    result = (T)Enum.ToObject(underlying, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    return true;
                }

                if (value is IConvertible && underlying != typeof(string) && typeof(IConvertible).IsAssignableFrom(underlying))
                {
                    result = (T)Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
                    return true;
                }

                if (value is object[] objects && target.IsArray)
                {
                    var elementType = target.GetElementType();
                    var array = Array.CreateInstance(elementType, objects.Length);

                    for (var i = 0; i < objects.Length; i++)
                    {
                        array.SetValue(Convert.ChangeType(objects[i], elementType, CultureInfo.InvariantCulture), i);
                    }

                    result = (T)(object)array;
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
            {
            }

            result = default;
            return false;
        }

        public static bool IsNumeric(object value) =>
            value is byte or short or int or long or float or double or sbyte or ushort or uint or ulong;

        public static bool ValuesEqual(object a, object b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null)
            {
                return false;
            }

            if (IsNumeric(a) && IsNumeric(b))
            {
                return Convert.ToDouble(a, CultureInfo.InvariantCulture) == Convert.ToDouble(b, CultureInfo.InvariantCulture);
            }

            if (a is not Array arrayA || b is not Array arrayB)
            {
                return a.Equals(b);
            }

            if (arrayA.GetType() != arrayB.GetType() || arrayA.Length != arrayB.Length)
            {
                return false;
            }

            for (var i = 0; i < arrayA.Length; i++)
            {
                if (!ValuesEqual(arrayA.GetValue(i), arrayB.GetValue(i)))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class ParameterDictionary : IEnumerable<KeyValuePair<byte, object>>
    {
        private readonly Dictionary<byte, object> _values;

        public ParameterDictionary()
        {
            _values = new();
        }

        public ParameterDictionary(int capacity)
        {
            _values = new(capacity);
        }

        public int Count => _values.Count;

        public object this[byte key]
        {
            get => _values.TryGetValue(key, out var value) ? value : null;
            set => _values[key] = value;
        }

        public bool ContainsKey(byte key) => _values.ContainsKey(key);

        public bool Remove(byte key) => _values.Remove(key);

        public T Get<T>(byte key) => Get(key, default(T));

        public T Get<T>(byte key, T defaultValue) => TryGet<T>(key, out var value) ? value : defaultValue;

        public bool TryGet<T>(byte key, out T value)
        {
            if (_values.TryGetValue(key, out var raw))
            {
                return ValueConvert.TryConvert(raw, out value);
            }

            value = default;
            return false;
        }

        public void WriteTo(NetWriter writer) => WriteTo(writer, 0);

        public static ParameterDictionary ReadFrom(NetReader reader) => ReadFrom(reader, 0);

        public IEnumerator<KeyValuePair<byte, object>> GetEnumerator() => _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

        internal void WriteTo(NetWriter writer, int depth)
        {
            writer.WriteVarUInt((uint)_values.Count);

            foreach (var pair in _values)
            {
                writer.WriteByte(pair.Key);
                writer.WriteObject(pair.Value, depth);
            }
        }

        internal static ParameterDictionary ReadFrom(NetReader reader, int depth)
        {
            var count = reader.ReadVarUInt();

            if (count > (uint)reader.Remaining / 2)
            {
                throw new NetSerializationException("Declared parameter count exceeds available data.");
            }

            var result = new ParameterDictionary((int)count);

            for (uint i = 0; i < count; i++)
            {
                var key = reader.ReadByte();
                result._values[key] = reader.ReadObject(depth);
            }

            return result;
        }
    }
}
