using System;
using System.Collections.Generic;

namespace NetPulse.Common
{
    public sealed class OpaqueValue : IEquatable<OpaqueValue>
    {
        public OpaqueValue(byte typeCode, byte[] data)
        {
            TypeCode = typeCode;
            Data = data ?? Array.Empty<byte>();
        }

        public byte TypeCode { get; set; }

        public byte[] Data { get; set; }

        public bool Equals(OpaqueValue other)
        {
            if (other is null || TypeCode != other.TypeCode || Data.Length != other.Data.Length)
            {
                return false;
            }

            for (var i = 0; i < Data.Length; i++)
            {
                if (Data[i] != other.Data[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj) => Equals(obj as OpaqueValue);

        public override int GetHashCode() => (TypeCode * 397) ^ Data.Length;

        public override string ToString() => $"Opaque({TypeCode}, {Data.Length} bytes)";
    }

    public static class NetTypeRegistry
    {
        private static readonly object Gate = new();
        private static readonly Entry[] ByCode = new Entry[256];
        private static volatile Dictionary<Type, Entry> _byType = new();

        public static void Register<T>(byte code, Action<NetWriter, T> write, Func<NetReader, T> read)
        {
            if (code < NetTypeCode.CustomMin || code > NetTypeCode.CustomMax)
            {
                throw new ArgumentOutOfRangeException(nameof(code), "Custom type codes must be in 100-199.");
            }

            if (write == null)
            {
                throw new ArgumentNullException(nameof(write));
            }

            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }

            var entry = new Entry(typeof(T), code, (w, o) => write(w, (T)o), r => read(r));

            lock (Gate)
            {
                var existing = ByCode[code];

                if (existing != null && existing.Type != typeof(T))
                {
                    throw new InvalidOperationException($"Code {code} is already registered for {existing.Type.FullName}.");
                }

                var copy = new Dictionary<Type, Entry>(_byType);

                if (copy.TryGetValue(typeof(T), out var old) && old.Code != code)
                {
                    throw new InvalidOperationException($"{typeof(T).FullName} is already registered with code {old.Code}.");
                }

                copy[typeof(T)] = entry;
                ByCode[code] = entry;
                _byType = copy;
            }
        }

        public static bool IsRegistered(byte code) => ByCode[code] != null;

        public static void Unregister(byte code)
        {
            lock (Gate)
            {
                var existing = ByCode[code];

                if (existing == null)
                {
                    return;
                }

                var copy = new Dictionary<Type, Entry>(_byType);
                copy.Remove(existing.Type);
                ByCode[code] = null;
                _byType = copy;
            }
        }

        internal static bool TryGetByType(Type type, out Entry entry) => _byType.TryGetValue(type, out entry);

        internal static bool TryGetByCode(byte code, out Entry entry)
        {
            entry = ByCode[code];
            return entry != null;
        }

        internal sealed class Entry
        {
            public Entry(Type type, byte code, Action<NetWriter, object> write, Func<NetReader, object> read)
            {
                Type = type;
                Code = code;
                Write = write;
                Read = read;
            }

            public Type Type { get; }

            public byte Code { get; }

            public Action<NetWriter, object> Write { get; }

            public Func<NetReader, object> Read { get; }
        }
    }
}
