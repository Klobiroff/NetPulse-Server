using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace NetPulse.Common
{
    public sealed class NetSerializationException : Exception
    {
        public NetSerializationException(string message)
            : base(message)
        {
        }
    }

    public sealed class NetWriter
    {
        public const int MaxDepth = 16;

        private byte[] _buffer;
        private int _length;

        public NetWriter(int capacity = 256)
        {
            _buffer = new byte[capacity < 16 ? 16 : capacity];
        }

        public int Length => _length;

        public void Reset() => _length = 0;

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        public ArraySegment<byte> AsSegment() => new(_buffer, 0, _length);

        public ReadOnlySpan<byte> AsSpan() => new(_buffer, 0, _length);

        public void WriteByte(byte value) => Reserve(1)[0] = value;

        public void WriteBool(bool value) => Reserve(1)[0] = value ? (byte)1 : (byte)0;

        public void WriteShort(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);

        public void WriteUShort(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

        public void WriteInt(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

        public void WriteUInt(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

        public void WriteLong(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

        public void WriteFloat(float value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), BitConverter.SingleToInt32Bits(value));

        public void WriteDouble(double value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), BitConverter.DoubleToInt64Bits(value));

        public void WriteGuid(Guid value) => value.TryWriteBytes(Reserve(16));

        public void WriteVarUInt(uint value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteVarUInt(0);
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            WriteVarUInt((uint)byteCount + 1);

            if (byteCount == 0)
            {
                return;
            }

            Encoding.UTF8.GetBytes(value.AsSpan(), Reserve(byteCount));
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                WriteVarUInt(0);
                return;
            }

            WriteBytes(new ReadOnlySpan<byte>(value));
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            WriteVarUInt((uint)value.Length);
            WriteRaw(value);
        }

        public void WriteRaw(ReadOnlySpan<byte> value)
        {
            if (value.Length == 0)
            {
                return;
            }

            value.CopyTo(Reserve(value.Length));
        }

        public void WriteIntArray(int[] value)
        {
            WriteVarUInt((uint)(value?.Length ?? 0));

            if (value == null)
            {
                return;
            }

            for (var i = 0; i < value.Length; i++)
            {
                WriteInt(value[i]);
            }
        }

        public void WriteFloatArray(float[] value)
        {
            WriteVarUInt((uint)(value?.Length ?? 0));

            if (value == null)
            {
                return;
            }

            for (var i = 0; i < value.Length; i++)
            {
                WriteFloat(value[i]);
            }
        }

        public void WriteStringArray(string[] value)
        {
            WriteVarUInt((uint)(value?.Length ?? 0));

            if (value == null)
            {
                return;
            }

            for (var i = 0; i < value.Length; i++)
            {
                WriteString(value[i]);
            }
        }

        public void WriteObject(object value) => WriteObject(value, 0);

        internal void WriteObject(object value, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new NetSerializationException("Maximum nesting depth exceeded.");
            }

            switch (value)
            {
                case null:
                    WriteByte(NetTypeCode.Null);
                    return;
                case bool b:
                    WriteByte(NetTypeCode.Bool);
                    WriteBool(b);
                    return;
                case byte by:
                    WriteByte(NetTypeCode.Byte);
                    WriteByte(by);
                    return;
                case short s:
                    WriteByte(NetTypeCode.Short);
                    WriteShort(s);
                    return;
                case int i:
                    WriteByte(NetTypeCode.Int);
                    WriteInt(i);
                    return;
                case long l:
                    WriteByte(NetTypeCode.Long);
                    WriteLong(l);
                    return;
                case float f:
                    WriteByte(NetTypeCode.Float);
                    WriteFloat(f);
                    return;
                case double d:
                    WriteByte(NetTypeCode.Double);
                    WriteDouble(d);
                    return;
                case string str:
                    WriteByte(NetTypeCode.String);
                    WriteString(str);
                    return;
                case byte[] ba:
                    WriteByte(NetTypeCode.ByteArray);
                    WriteBytes(ba);
                    return;
                case int[] ia:
                    WriteByte(NetTypeCode.IntArray);
                    WriteIntArray(ia);
                    return;
                case float[] fa:
                    WriteByte(NetTypeCode.FloatArray);
                    WriteFloatArray(fa);
                    return;
                case string[] sa:
                    WriteByte(NetTypeCode.StringArray);
                    WriteStringArray(sa);
                    return;
                case object[] oa:
                    WriteByte(NetTypeCode.ObjectArray);
                    WriteVarUInt((uint)oa.Length);

                    for (var k = 0; k < oa.Length; k++)
                    {
                        WriteObject(oa[k], depth + 1);
                    }

                    return;
                case PropertyTable pt:
                    WriteByte(NetTypeCode.PropertyTable);
                    WriteVarUInt((uint)pt.Count);

                    foreach (var kv in pt)
                    {
                        WriteString(kv.Key);
                        WriteObject(kv.Value, depth + 1);
                    }

                    return;
                case ParameterDictionary pd:
                    WriteByte(NetTypeCode.ParameterDictionary);
                    pd.WriteTo(this, depth + 1);
                    return;
                case IDictionary<string, object> dict:
                    WriteByte(NetTypeCode.Dictionary);
                    WriteVarUInt((uint)dict.Count);

                    foreach (var kv in dict)
                    {
                        WriteString(kv.Key);
                        WriteObject(kv.Value, depth + 1);
                    }

                    return;
                case Guid g:
                    WriteByte(NetTypeCode.Guid);
                    WriteGuid(g);
                    return;
                case OpaqueValue ov:
                    WriteByte(ov.TypeCode);
                    WriteBytes(ov.Data);
                    return;
            }

            if (!NetTypeRegistry.TryGetByType(value.GetType(), out var entry))
            {
                throw new NetSerializationException($"Type is not serializable: {value.GetType().FullName}. Register it with NetTypeRegistry.Register (codes 100-199).");
            }

            WriteByte(entry.Code);
            var start = _length;
            entry.Write(this, value);
            var payload = _length - start;
            var prefix = 1;

            for (var v = (uint)payload; v >= 0x80; v >>= 7)
            {
                prefix++;
            }

            Reserve(prefix);
            Array.Copy(_buffer, start, _buffer, start + prefix, payload);
            _length = start;
            WriteVarUInt((uint)payload);
            _length = start + prefix + payload;
        }

        private Span<byte> Reserve(int count)
        {
            var needed = _length + count;

            if (needed > _buffer.Length)
            {
                var size = _buffer.Length * 2;
                Array.Resize(ref _buffer, size < needed ? needed : size);
            }

            var span = new Span<byte>(_buffer, _length, count);
            _length = needed;
            return span;
        }
    }
}
