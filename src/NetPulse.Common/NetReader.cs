using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace NetPulse.Common
{
    public sealed class NetReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;

        public NetReader(byte[] data, int offset, int count)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _position = offset;
            _end = offset + count;
        }

        public NetReader(byte[] data)
            : this(data, 0, (data ?? throw new ArgumentNullException(nameof(data))).Length)
        {
        }

        public NetReader(ArraySegment<byte> segment)
            : this(segment.Array, segment.Offset, segment.Count)
        {
        }

        public int Remaining => _end - _position;

        public byte ReadByte() => _data[Take(1)];

        public bool ReadBool() => ReadByte() != 0;

        public short ReadShort() => BinaryPrimitives.ReadInt16LittleEndian(TakeSpan(2));

        public ushort ReadUShort() => BinaryPrimitives.ReadUInt16LittleEndian(TakeSpan(2));

        public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(TakeSpan(4));

        public uint ReadUInt() => BinaryPrimitives.ReadUInt32LittleEndian(TakeSpan(4));

        public long ReadLong() => BinaryPrimitives.ReadInt64LittleEndian(TakeSpan(8));

        public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());

        public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadLong());

        public Guid ReadGuid() => new(TakeSpan(16));

        public uint ReadVarUInt()
        {
            uint result = 0;
            var shift = 0;

            while (true)
            {
                var b = ReadByte();

                if (shift == 28 && (b & 0xF0) != 0)
                {
                    throw new NetSerializationException("Var-uint overflow.");
                }

                result |= (uint)(b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }
        }

        public string ReadString()
        {
            var prefix = ReadVarUInt();

            if (prefix == 0)
            {
                return null;
            }

            var byteCount = checked((int)(prefix - 1));
            return byteCount == 0 ? string.Empty : Encoding.UTF8.GetString(TakeSpan(byteCount));
        }

        public byte[] ReadBytes()
        {
            var count = ReadCount(1);
            return ReadRaw(count);
        }

        public byte[] ReadRaw(int count)
        {
            var result = new byte[count];
            TakeSpan(count).CopyTo(result);
            return result;
        }

        public int[] ReadIntArray()
        {
            var count = ReadCount(4);
            var result = new int[count];

            for (var i = 0; i < count; i++)
            {
                result[i] = ReadInt();
            }

            return result;
        }

        public float[] ReadFloatArray()
        {
            var count = ReadCount(4);
            var result = new float[count];

            for (var i = 0; i < count; i++)
            {
                result[i] = ReadFloat();
            }

            return result;
        }

        public string[] ReadStringArray()
        {
            var count = ReadCount(1);
            var result = new string[count];

            for (var i = 0; i < count; i++)
            {
                result[i] = ReadString();
            }

            return result;
        }

        public object ReadObject() => ReadObject(0);

        internal object ReadObject(int depth)
        {
            if (depth > NetWriter.MaxDepth)
            {
                throw new NetSerializationException("Maximum nesting depth exceeded.");
            }

            var code = ReadByte();

            switch (code)
            {
                case NetTypeCode.Null:
                    return null;
                case NetTypeCode.Bool:
                    return ReadBool();
                case NetTypeCode.Byte:
                    return ReadByte();
                case NetTypeCode.Short:
                    return ReadShort();
                case NetTypeCode.Int:
                    return ReadInt();
                case NetTypeCode.Long:
                    return ReadLong();
                case NetTypeCode.Float:
                    return ReadFloat();
                case NetTypeCode.Double:
                    return ReadDouble();
                case NetTypeCode.String:
                    return ReadString();
                case NetTypeCode.ByteArray:
                    return ReadBytes();
                case NetTypeCode.IntArray:
                    return ReadIntArray();
                case NetTypeCode.FloatArray:
                    return ReadFloatArray();
                case NetTypeCode.StringArray:
                    return ReadStringArray();
                case NetTypeCode.Guid:
                    return ReadGuid();
                case NetTypeCode.ObjectArray:
                    return ReadObjectArray(depth);
                case NetTypeCode.Dictionary:
                    return ReadDictionary(depth);
                case NetTypeCode.PropertyTable:
                    return ReadPropertyTable(depth);
                case NetTypeCode.ParameterDictionary:
                    return ParameterDictionary.ReadFrom(this, depth + 1);
            }

            if (code < NetTypeCode.CustomMin || code > NetTypeCode.CustomMax)
            {
                throw new NetSerializationException($"Unknown type code {code}.");
            }

            var length = ReadCount(1);

            if (NetTypeRegistry.TryGetByCode(code, out var entry))
            {
                var start = Take(length);
                return entry.Read(new NetReader(_data, start, length));
            }

            return new OpaqueValue(code, ReadRaw(length));
        }

        private object[] ReadObjectArray(int depth)
        {
            var count = ReadCount(1);
            var result = new object[count];

            for (var i = 0; i < count; i++)
            {
                result[i] = ReadObject(depth + 1);
            }

            return result;
        }

        private Dictionary<string, object> ReadDictionary(int depth)
        {
            var count = ReadCount(2);
            var result = new Dictionary<string, object>(count);

            for (var i = 0; i < count; i++)
            {
                var key = ReadString() ?? string.Empty;
                result[key] = ReadObject(depth + 1);
            }

            return result;
        }

        private PropertyTable ReadPropertyTable(int depth)
        {
            var count = ReadCount(2);
            var result = new PropertyTable();

            for (var i = 0; i < count; i++)
            {
                var key = ReadString() ?? string.Empty;
                result.SetRaw(key, ReadObject(depth + 1));
            }

            return result;
        }

        private int ReadCount(int minElementSize)
        {
            var count = ReadVarUInt();

            if ((long)count * minElementSize > Remaining)
            {
                throw new NetSerializationException("Declared length exceeds available data.");
            }

            return (int)count;
        }

        private int Take(int count)
        {
            if (count < 0 || count > _end - _position)
            {
                throw new NetSerializationException("Unexpected end of data.");
            }

            var start = _position;
            _position += count;
            return start;
        }

        private ReadOnlySpan<byte> TakeSpan(int count) => new(_data, Take(count), count);
    }
}
