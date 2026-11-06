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

        public ushort ExtendedId { get; set; }

        public byte[] Data { get; set; }

        public static OpaqueValue ForExtended(ushort id, byte[] data) => new(NetTypeCode.Extended, data)
        {
            ExtendedId = id
        };

        public bool Equals(OpaqueValue other)
        {
            if (other is null || TypeCode != other.TypeCode || ExtendedId != other.ExtendedId || Data.Length != other.Data.Length)
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

        public override int GetHashCode() => (((TypeCode * 397) ^ ExtendedId) * 397) ^ Data.Length;

        public override string ToString() => TypeCode == NetTypeCode.Extended
            ? $"Opaque(Extended {ExtendedId}, {Data.Length} bytes)"
            : $"Opaque({TypeCode}, {Data.Length} bytes)";
    }

    public static class NetTypeRegistry
    {
        private static readonly object Gate = new();
        private static readonly Entry[] ByCode = new Entry[256];
        private static volatile Dictionary<Type, Entry> _byType = new();
        private static volatile Dictionary<ushort, Entry> _extendedById = new();

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

        public static void RegisterExtended<T>(ushort id, Action<NetWriter, T> write, Func<NetReader, T> read)
        {
            if (write == null)
            {
                throw new ArgumentNullException(nameof(write));
            }

            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }

            var entry = new Entry(typeof(T), NetTypeCode.Extended, (w, o) => write(w, (T)o), r => read(r))
            {
                ExtendedId = id
            };

            lock (Gate)
            {
                if (_extendedById.TryGetValue(id, out var existing) && existing.Type != typeof(T))
                {
                    throw new InvalidOperationException($"Extended id {id} is already registered for {existing.Type.FullName}.");
                }

                var byType = new Dictionary<Type, Entry>(_byType);

                if (byType.TryGetValue(typeof(T), out var old) && (!old.IsExtended || old.ExtendedId != id))
                {
                    throw new InvalidOperationException($"{typeof(T).FullName} is already registered with a different code.");
                }

                var byId = new Dictionary<ushort, Entry>(_extendedById)
                {
                    [id] = entry
                };

                byType[typeof(T)] = entry;
                _extendedById = byId;
                _byType = byType;
            }
        }

        public static bool IsRegistered(byte code) => ByCode[code] != null;

        public static bool IsExtendedRegistered(ushort id) => _extendedById.ContainsKey(id);

        public static void UnregisterExtended(ushort id)
        {
            lock (Gate)
            {
                if (!_extendedById.TryGetValue(id, out var existing))
                {
                    return;
                }

                var byType = new Dictionary<Type, Entry>(_byType);
                byType.Remove(existing.Type);
                var byId = new Dictionary<ushort, Entry>(_extendedById);
                byId.Remove(id);
                _byType = byType;
                _extendedById = byId;
            }
        }

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

        internal static bool TryGetExtended(ushort id, out Entry entry) => _extendedById.TryGetValue(id, out entry);

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

            public ushort ExtendedId { get; set; }

            public bool IsExtended => Code == NetTypeCode.Extended;
        }
    }
}
