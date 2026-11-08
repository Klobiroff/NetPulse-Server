using System;
using System.Collections.Generic;

namespace NetPulse.Common.Security
{
    public enum SecureClientState
    {
        Idle,
        Handshaking,
        Established,
        Failed,
        TimedOut
    }

    public enum SecureClientInboundKind
    {
        Dropped,
        Decrypted,
        Reply
    }

    public readonly struct SecureClientInboundResult
    {
        public SecureClientInboundResult(SecureClientInboundKind kind, int length)
        {
            Kind = kind;
            Length = length;
        }

        public SecureClientInboundKind Kind { get; }

        public int Length { get; }

        public static SecureClientInboundResult Dropped => new SecureClientInboundResult(SecureClientInboundKind.Dropped, 0);
    }

    public sealed class SecureClientSettings
    {
        public IList<byte[]> ServerPublicKeys { get; set; } = new List<byte[]>();

        public byte[] PreSharedKey { get; set; }

        public int HandshakeTimeoutMs { get; set; } = 5000;

        public SecureSessionSettings Session { get; set; } = new();
    }

    public sealed class SecureClientCore : IDisposable
    {
        public const int MaxBadResponses = 3;

        private readonly SecureClientSettings _settings;
        private readonly SecurityCounters _counters;
        private readonly Func<long> _clock;
        private readonly object _gate = new();
        private readonly byte[] _preSharedKey;
        private readonly byte[][] _serverKeys;
        private Attempt[] _attempts = Array.Empty<Attempt>();
        private byte[] _cookie = Array.Empty<byte>();
        private byte[] _stash = Array.Empty<byte>();
        private int _stashLength;
        private int _round;
        private int _badResponses;
        private long _startedMs;
        private volatile SecureSession _session;
        private SecureClientState _state;

        public SecureClientCore(SecureClientSettings settings, SecurityCounters counters, Func<long> clock)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _counters = counters ?? throw new ArgumentNullException(nameof(counters));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            if (settings.ServerPublicKeys == null || settings.ServerPublicKeys.Count == 0)
            {
                throw new ArgumentException("At least one server public key is required", nameof(settings));
            }

            _serverKeys = new byte[settings.ServerPublicKeys.Count][];

            for (var i = 0; i < _serverKeys.Length; i++)
            {
                var key = settings.ServerPublicKeys[i];

                if (key == null || key.Length != X25519.KeySize)
                {
                    throw new ArgumentException("Server public keys must be 32 bytes", nameof(settings));
                }

                _serverKeys[i] = (byte[])key.Clone();
            }

            _preSharedKey = settings.PreSharedKey == null || settings.PreSharedKey.Length == 0 ? Array.Empty<byte>() : (byte[])settings.PreSharedKey.Clone();
        }

        public int ExtraPacketSize => SecureRecord.MaxOverhead(_settings.Session.PaddingBlock);

        public SecureSession Session => _session;

        public SecureClientState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public byte[] HandshakeHash => _session?.HandshakeHash;

        public void Reset()
        {
            lock (_gate)
            {
                DisposeAttempts();
                _session?.Dispose();
                _session = null;
                _cookie = Array.Empty<byte>();
                ConstantTime.Zero(_stash);
                _stash = Array.Empty<byte>();
                _stashLength = 0;
                _round = 0;
                _badResponses = 0;
                _state = SecureClientState.Idle;
            }
        }

        public int BuildInit(byte[] buffer, int offset)
        {
            lock (_gate)
            {
                if (_state == SecureClientState.Established || _state == SecureClientState.Failed || _state == SecureClientState.TimedOut)
                {
                    return -1;
                }

                if (_state == SecureClientState.Idle)
                {
                    StartHandshake();
                }

                return WriteInit(buffer, offset);
            }
        }

        public void Stash(byte[] data, int offset, int length)
        {
            lock (_gate)
            {
                if (_stash.Length < length)
                {
                    _stash = new byte[Math.Max(length, 256)];
                }

                Buffer.BlockCopy(data, offset, _stash, 0, length);
                _stashLength = length;
            }
        }

        public int Protect(byte[] buffer, int offset, int length)
        {
            var session = _session;
            return session == null ? -1 : session.Protect(buffer, offset, length, _clock());
        }

        public SecureClientInboundResult ProcessInbound(byte[] data, int length, byte[] reply)
        {
            if (length <= 0 || !SecureRecord.IsRecord(data[0]))
            {
                _counters.PlaintextDropped();
                return SecureClientInboundResult.Dropped;
            }

            switch (data[0])
            {
                case SecureRecord.Retry:
                    return HandleRetry(data, length, reply);
                case SecureRecord.Response:
                    return HandleResponse(data, length, reply);
                case SecureRecord.Data:
                case SecureRecord.DataPadded:
                    return HandleData(data, length);
                default:
                    return SecureClientInboundResult.Dropped;
            }
        }

        public bool CheckTimeout()
        {
            lock (_gate)
            {
                if (_state == SecureClientState.Handshaking && _clock() - _startedMs >= _settings.HandshakeTimeoutMs)
                {
                    _state = SecureClientState.TimedOut;
                    DisposeAttempts();
                }

                return _state == SecureClientState.TimedOut;
            }
        }

        public void Dispose() => Reset();

        private SecureClientInboundResult HandleRetry(byte[] data, int length, byte[] reply)
        {
            lock (_gate)
            {
                if (_state != SecureClientState.Handshaking || !SecureRecord.TryParseRetry(new ReadOnlySpan<byte>(data, 0, length), out var cookie))
                {
                    return SecureClientInboundResult.Dropped;
                }

                _cookie = cookie.ToArray();
                var written = WriteInit(reply, 0);
                return written < 0 ? SecureClientInboundResult.Dropped : new SecureClientInboundResult(SecureClientInboundKind.Reply, written);
            }
        }

        private SecureClientInboundResult HandleResponse(byte[] data, int length, byte[] reply)
        {
            lock (_gate)
            {
                if (_state != SecureClientState.Handshaking || length < SecureRecord.ResponseSize)
                {
                    return SecureClientInboundResult.Dropped;
                }

                var message = new ReadOnlySpan<byte>(data, 1, SecureRecord.NoiseMessageSize);

                for (var i = 0; i < _attempts.Length; i++)
                {
                    using (var trial = _attempts[i].Noise.Clone())
                    {
                        if (!trial.ReadMessage2(message))
                        {
                            continue;
                        }

                        trial.Split(out var sendKey, out var receiveKey);
                        _session = new SecureSession(sendKey, receiveKey, trial.HandshakeHash, _settings.Session, _counters, _clock());
                        ConstantTime.Zero(sendKey);
                        ConstantTime.Zero(receiveKey);
                    }

                    DisposeAttempts();
                    _state = SecureClientState.Established;
                    _counters.HandshakeSucceeded();

                    if (_stashLength == 0)
                    {
                        return new SecureClientInboundResult(SecureClientInboundKind.Dropped, 0);
                    }

                    Buffer.BlockCopy(_stash, 0, reply, 0, _stashLength);
                    var written = _session.Protect(reply, 0, _stashLength, _clock());
                    return written < 0 ? SecureClientInboundResult.Dropped : new SecureClientInboundResult(SecureClientInboundKind.Reply, written);
                }

                _counters.HandshakeFailed();
                _badResponses++;

                if (_badResponses >= MaxBadResponses)
                {
                    _state = SecureClientState.Failed;
                    DisposeAttempts();
                }

                return SecureClientInboundResult.Dropped;
            }
        }

        private SecureClientInboundResult HandleData(byte[] data, int length)
        {
            var session = _session;

            if (session == null)
            {
                return SecureClientInboundResult.Dropped;
            }

            var outcome = session.Unprotect(data, 0, length, out var plainLength);
            return outcome == UnprotectResult.Ok
                ? new SecureClientInboundResult(SecureClientInboundKind.Decrypted, plainLength)
                : SecureClientInboundResult.Dropped;
        }

        private void StartHandshake()
        {
            DisposeAttempts();
            _attempts = new Attempt[_serverKeys.Length];

            for (var i = 0; i < _attempts.Length; i++)
            {
                var keyId = SecureRecord.ComputeKeyId(_serverKeys[i]);
                var noise = NoiseHandshake.CreateInitiator(_serverKeys[i], _preSharedKey, SecureRecord.CreatePrologue(keyId));
                var message = new byte[SecureRecord.NoiseMessageSize];

                if (!noise.WriteMessage1(message))
                {
                    throw new InvalidOperationException("Could not build the handshake message");
                }

                _attempts[i] = new Attempt(keyId, noise, message);
            }

            _startedMs = _clock();
            _round = 0;
            _state = SecureClientState.Handshaking;
        }

        private int WriteInit(byte[] buffer, int offset)
        {
            var attempt = _attempts[_round % _attempts.Length];
            _round++;
            return SecureRecord.WriteInit(buffer, offset, attempt.KeyId, _cookie, attempt.Message);
        }

        private void DisposeAttempts()
        {
            for (var i = 0; i < _attempts.Length; i++)
            {
                _attempts[i]?.Noise.Dispose();
            }

            _attempts = Array.Empty<Attempt>();
        }

        private sealed class Attempt
        {
            public Attempt(byte[] keyId, NoiseHandshake noise, byte[] message)
            {
                KeyId = keyId;
                Noise = noise;
                Message = message;
            }

            public byte[] KeyId { get; }

            public NoiseHandshake Noise { get; }

            public byte[] Message { get; }
        }
    }
}
