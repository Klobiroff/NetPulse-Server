using System;

namespace NetPulse.Common.Security
{
    public sealed class ReplayWindow
    {
        private readonly ulong[] _bits;
        private readonly ulong _size;
        private ulong _top;
        private bool _any;

        public ReplayWindow(int size)
        {
            if (size < 64 || size % 64 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Replay window must be a positive multiple of 64");
            }

            _size = (ulong)size;
            _bits = new ulong[size / 64];
        }

        public ulong Top => _top;

        public bool HasReceived => _any;

        public bool Check(ulong counter)
        {
            if (!_any || counter > _top)
            {
                return true;
            }

            if (_top - counter >= _size)
            {
                return false;
            }

            return !IsSet(counter);
        }

        public void Commit(ulong counter)
        {
            if (!_any)
            {
                _any = true;
                _top = counter;
                Set(counter);
                return;
            }

            if (counter > _top)
            {
                var gap = counter - _top;

                if (gap >= _size)
                {
                    Array.Clear(_bits, 0, _bits.Length);
                }
                else
                {
                    for (var c = _top + 1; c <= counter; c++)
                    {
                        Clear(c);
                    }
                }

                _top = counter;
                Set(counter);
                return;
            }

            Set(counter);
        }

        private bool IsSet(ulong counter)
        {
            var index = counter % _size;
            return (_bits[(int)(index >> 6)] & (1UL << (int)(index & 63))) != 0;
        }

        private void Set(ulong counter)
        {
            var index = counter % _size;
            _bits[(int)(index >> 6)] |= 1UL << (int)(index & 63);
        }

        private void Clear(ulong counter)
        {
            var index = counter % _size;
            _bits[(int)(index >> 6)] &= ~(1UL << (int)(index & 63));
        }
    }
}
