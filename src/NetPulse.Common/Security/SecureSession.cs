using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;

namespace NetPulse.Common.Security
{
    public enum UnprotectResult
    {
        Ok,
        Malformed,
        AuthFailed,
        Replay,
        UnknownEpoch,
        Closed
    }

    public sealed class SecureSessionSettings
    {
        public long RekeyAfterPackets { get; set; } = 1000000;

        public long RekeyAfterMs { get; set; } = 600000;

        public int ReplayWindow { get; set; } = 4096;

        public int PaddingBlock { get; set; }
    }

    public sealed class SecureSession : IDisposable
    {
        public const ulong CounterLimit = 1UL << 62;

        private const int LookAhead = 4;

        private static readonly byte[] RekeyInfo = Encoding.ASCII.GetBytes("rekey");

        private readonly long _rekeyAfterPackets;
        private readonly long _rekeyAfterMs;
        private readonly int _paddingBlock;
        private readonly SecurityCounters _counters;
        private readonly object _sendGate = new();
        private readonly object _receiveGate = new();
        private readonly ReplayWindow _replay;
        private readonly byte[] _handshakeHash;
        private readonly int _replayWindowSize;
        private readonly RecvKey[] _ahead = new RecvKey[LookAhead];
        private SendKey _send;
        private SendKey _sendPrevious;
        private long _counter;
        private long _epochPackets;
        private RecvKey _receive;
        private RecvKey _receivePrevious;
        private ulong _promotedAt;
        private volatile bool _closed;
        private long _lastReceiveMs;

        public SecureSession(
            byte[] sendKey,
            byte[] receiveKey,
            byte[] handshakeHash,
            SecureSessionSettings settings,
            SecurityCounters counters,
            long nowMs)
            : this(sendKey, receiveKey, handshakeHash, settings, counters, nowMs, 0)
        {
        }

        internal SecureSession(
            byte[] sendKey,
            byte[] receiveKey,
            byte[] handshakeHash,
            SecureSessionSettings settings,
            SecurityCounters counters,
            long nowMs,
            long initialCounter)
        {
            if (sendKey == null || sendKey.Length != 32 || receiveKey == null || receiveKey.Length != 32)
            {
                throw new ArgumentException("Session keys must be 32 bytes");
            }

            settings = settings ?? new SecureSessionSettings();
            _rekeyAfterPackets = settings.RekeyAfterPackets <= 0 ? long.MaxValue : settings.RekeyAfterPackets;
            _rekeyAfterMs = settings.RekeyAfterMs;
            _paddingBlock = settings.PaddingBlock;
            _replayWindowSize = settings.ReplayWindow;
            _counters = counters ?? new SecurityCounters();
            _replay = new ReplayWindow(settings.ReplayWindow);
            _handshakeHash = handshakeHash == null ? Array.Empty<byte>() : (byte[])handshakeHash.Clone();
            _send = new SendKey((byte[])sendKey.Clone(), 0, nowMs);
            _receive = new RecvKey(receiveKey, 0);
            _counter = initialCounter;
            _lastReceiveMs = nowMs;

            var chain = receiveKey;

            for (var i = 0; i < LookAhead; i++)
            {
                chain = DeriveNext(chain);
                _ahead[i] = new RecvKey(chain, (byte)(i + 1));
            }
        }

        public bool IsClosed => _closed;

        public object Tag { get; set; }

        public long ReleasedAtMs { get; set; } = -1;

        public long LastReceiveMs => Interlocked.Read(ref _lastReceiveMs);

        public byte[] HandshakeHash => (byte[])_handshakeHash.Clone();

        public int SendEpoch => _send.Epoch;

        public int ReceiveEpoch
        {
            get
            {
                lock (_receiveGate)
                {
                    return _receive.Epoch;
                }
            }
        }

        public void Touch(long nowMs) => Interlocked.Exchange(ref _lastReceiveMs, nowMs);

        public int Protect(byte[] buffer, int offset, int length, long nowMs)
        {
            if (_closed)
            {
                return -1;
            }

            var padded = _paddingBlock > 0;
            var paddingLength = 0;
            var cipherLength = length;

            if (padded)
            {
                var total = SecureRecord.HeaderSize + length + 2 + SecureRecord.TagSize;
                var remainder = total % _paddingBlock;
                paddingLength = remainder == 0 ? 0 : _paddingBlock - remainder;
                cipherLength = length + paddingLength + 2;
            }

            var recordLength = SecureRecord.HeaderSize + cipherLength + SecureRecord.TagSize;

            if (offset + recordLength > buffer.Length)
            {
                return -1;
            }

            Span<byte> nonce = stackalloc byte[ChaCha20Poly1305.NonceSize];

            lock (_sendGate)
            {
                if (_closed)
                {
                    return -1;
                }

                var key = AcquireSendKey(nowMs);
                var counter = (ulong)(Interlocked.Increment(ref _counter) - 1);

                if (counter >= CounterLimit)
                {
                    _closed = true;
                    return -1;
                }

                Buffer.BlockCopy(buffer, offset, buffer, offset + SecureRecord.HeaderSize, length);

                if (padded)
                {
                    var padStart = offset + SecureRecord.HeaderSize + length;
                    Array.Clear(buffer, padStart, paddingLength);
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(padStart + paddingLength, 2), (ushort)paddingLength);
                }

                buffer[offset] = padded ? SecureRecord.DataPadded : SecureRecord.Data;
                buffer[offset + 1] = key.Epoch;
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset + 2, 8), counter);

                nonce.Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4, 8), counter);

                ChaCha20Poly1305.Encrypt(
                    key.Key,
                    nonce,
                    buffer.AsSpan(offset, SecureRecord.HeaderSize),
                    buffer.AsSpan(offset + SecureRecord.HeaderSize, cipherLength),
                    buffer.AsSpan(offset + SecureRecord.HeaderSize + cipherLength, SecureRecord.TagSize));
            }

            return recordLength;
        }

        public UnprotectResult Unprotect(byte[] buffer, int offset, int length, out int plainLength)
        {
            plainLength = 0;

            if (length < SecureRecord.Overhead)
            {
                _counters.DecryptFailed();
                return UnprotectResult.Malformed;
            }

            var type = buffer[offset];

            if (type != SecureRecord.Data && type != SecureRecord.DataPadded)
            {
                _counters.DecryptFailed();
                return UnprotectResult.Malformed;
            }

            var padded = type == SecureRecord.DataPadded;
            var cipherLength = length - SecureRecord.Overhead;

            if (padded && cipherLength < 2)
            {
                _counters.DecryptFailed();
                return UnprotectResult.Malformed;
            }

            var epoch = buffer[offset + 1];
            var counter = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset + 2, 8));

            if (counter >= CounterLimit)
            {
                _counters.DecryptFailed();
                return UnprotectResult.Malformed;
            }

            Span<byte> nonce = stackalloc byte[ChaCha20Poly1305.NonceSize];
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4, 8), counter);

            lock (_receiveGate)
            {
                if (_closed)
                {
                    return UnprotectResult.Closed;
                }

                var key = SelectReceiveKey(epoch, out var aheadIndex);

                if (key == null)
                {
                    _counters.DecryptFailed();
                    return UnprotectResult.UnknownEpoch;
                }

                var header = buffer.AsSpan(offset, SecureRecord.HeaderSize);
                var data = buffer.AsSpan(offset + SecureRecord.HeaderSize, cipherLength);
                var tag = buffer.AsSpan(offset + length - SecureRecord.TagSize, SecureRecord.TagSize);

                if (!_replay.Check(counter))
                {
                    if (ChaCha20Poly1305.Verify(key.Key, nonce, header, data, tag))
                    {
                        _counters.ReplayDetected();
                        return UnprotectResult.Replay;
                    }

                    _counters.DecryptFailed();
                    return UnprotectResult.AuthFailed;
                }

                if (!ChaCha20Poly1305.Decrypt(key.Key, nonce, header, data, tag))
                {
                    _counters.DecryptFailed();
                    return UnprotectResult.AuthFailed;
                }

                _replay.Commit(counter);

                if (aheadIndex >= 0)
                {
                    Promote(aheadIndex, counter);
                }
                else
                {
                    RetirePrevious();
                }
            }

            plainLength = cipherLength;

            if (padded)
            {
                int paddingLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + SecureRecord.HeaderSize + cipherLength - 2, 2));

                if (paddingLength + 2 > cipherLength)
                {
                    plainLength = 0;
                    return UnprotectResult.Malformed;
                }

                plainLength = cipherLength - 2 - paddingLength;
            }

            Buffer.BlockCopy(buffer, offset + SecureRecord.HeaderSize, buffer, offset, plainLength);
            return UnprotectResult.Ok;
        }

        public void Dispose()
        {
            _closed = true;

            lock (_sendGate)
            {
                _send?.Wipe();
                _sendPrevious?.Wipe();
            }

            lock (_receiveGate)
            {
                _receive?.Wipe();
                _receivePrevious?.Wipe();

                for (var i = 0; i < LookAhead; i++)
                {
                    _ahead[i]?.Wipe();
                }
            }
        }

        private static byte[] DeriveNext(byte[] key)
        {
            var next = new byte[32];
            Hkdf.Expand(key, RekeyInfo, next);
            return next;
        }

        private SendKey AcquireSendKey(long nowMs)
        {
            var current = _send;
            var exhausted = Interlocked.Increment(ref _epochPackets) > _rekeyAfterPackets;
            var expired = _rekeyAfterMs > 0 && nowMs - current.StartedMs >= _rekeyAfterMs;

            if (!exhausted && !expired)
            {
                return current;
            }

            lock (_sendGate)
            {
                if (ReferenceEquals(_send, current))
                {
                    var next = new SendKey(DeriveNext(current.Key), (byte)(current.Epoch + 1), nowMs);
                    _sendPrevious?.Wipe();
                    _sendPrevious = current;
                    _send = next;
                    Interlocked.Exchange(ref _epochPackets, 1);
                    _counters.Rekeyed();
                }

                return _send;
            }
        }

        private RecvKey SelectReceiveKey(byte epoch, out int aheadIndex)
        {
            aheadIndex = -1;

            if (_receive.Epoch == epoch)
            {
                return _receive;
            }

            if (_receivePrevious != null && !_receivePrevious.Wiped && _receivePrevious.Epoch == epoch)
            {
                return _receivePrevious;
            }

            for (var i = 0; i < LookAhead; i++)
            {
                if (_ahead[i] != null && _ahead[i].Epoch == epoch)
                {
                    aheadIndex = i;
                    return _ahead[i];
                }
            }

            return null;
        }

        private void Promote(int aheadIndex, ulong counter)
        {
            _receivePrevious?.Wipe();
            _receivePrevious = _receive;
            _receive = _ahead[aheadIndex];

            for (var i = 0; i < aheadIndex; i++)
            {
                _ahead[i].Wipe();
            }

            var remaining = LookAhead - aheadIndex - 1;

            for (var i = 0; i < remaining; i++)
            {
                _ahead[i] = _ahead[aheadIndex + 1 + i];
            }

            var chain = remaining > 0 ? _ahead[remaining - 1].Key : _receive.Key;

            for (var i = remaining; i < LookAhead; i++)
            {
                chain = DeriveNext(chain);
                _ahead[i] = new RecvKey(chain, (byte)(_receive.Epoch + 1 + i));
            }

            _promotedAt = counter;
            _counters.Rekeyed();
        }

        private void RetirePrevious()
        {
            if (_receivePrevious == null || _receivePrevious.Wiped)
            {
                return;
            }

            if (_replay.Top >= _promotedAt + (ulong)_replayWindowSize)
            {
                _receivePrevious.Wipe();
            }
        }

        private sealed class SendKey
        {
            public SendKey(byte[] key, byte epoch, long startedMs)
            {
                Key = key;
                Epoch = epoch;
                StartedMs = startedMs;
            }

            public byte[] Key { get; }

            public byte Epoch { get; }

            public long StartedMs { get; }

            public void Wipe() => ConstantTime.Zero(Key);
        }

        private sealed class RecvKey
        {
            public RecvKey(byte[] key, byte epoch)
            {
                Key = (byte[])key.Clone();
                Epoch = epoch;
            }

            public byte[] Key { get; }

            public byte Epoch { get; }

            public bool Wiped { get; private set; }

            public void Wipe()
            {
                ConstantTime.Zero(Key);
                Wiped = true;
            }
        }
    }
}
