using System;

namespace NetPulse.Common.Security
{
    public static class ConstantTime
    {
        public static bool AreEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            var difference = 0;

            for (var i = 0; i < left.Length; i++)
            {
                difference |= left[i] ^ right[i];
            }

            return difference == 0;
        }

        public static bool IsZero(ReadOnlySpan<byte> value)
        {
            var accumulator = 0;

            for (var i = 0; i < value.Length; i++)
            {
                accumulator |= value[i];
            }

            return accumulator == 0;
        }

        public static void Zero(Span<byte> value) => value.Clear();

        public static void Zero(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
