using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace NetPulse.Common.Security
{
    public enum SecureInboundKind
    {
        Plain,
        Dropped,
        Decrypted,
        Reply
    }

    public readonly struct SecureInboundResult
    {
        public SecureInboundResult(SecureInboundKind kind, int length, SecureSession session)
        {
            Kind = kind;
            Length = length;
            Session = session;
        }

        public SecureInboundKind Kind { get; }

        public int Length { get; }

        public SecureSession Session { get; }

        public static SecureInboundResult Dropped => new SecureInboundResult(SecureInboundKind.Dropped, 0, null);

        public static SecureInboundResult Plain => new SecureInboundResult(SecureInboundKind.Plain, 0, null);
    }

    public sealed class SecureServerSettings
    {
        public bool AllowPlaintext { get; set; }

        public IList<byte[]> PrivateKeys { get; set; } = new List<byte[]>();

        public byte[] PreSharedKey { get; set; }

        public bool RequireCookie { get; set; } = true;

        public int CookieLifetimeMs { get; set; } = 30000;

        public int SessionIdleTimeoutMs { get; set; } = 30000;

        public int ReplaceIdleMs { get; set; } = 3000;

        public int HandshakeTimeoutMs { get; set; } = 5000;

        public int ReleaseGraceMs { get; set; } = 1000;

        public int HandshakesPerIpPerSecond { get; set; } = 10;

        public int MaxHandshakesPerSecond { get; set; } = 2000;

        public int MaxSessions { get; set; } = 100000;

        public SecureSessionSettings Session { get; set; } = new();
    }

    public sealed class SecureServerCore
    {
        private readonly SecureServerSettings _settings;
        private readonly SecurityCounters _counters;
        private readonly CookieJar _cookies;
        private readonly KeyedRateLimiter<SecureEndpoint> _perIp;
        private readonly TokenBucket _budget;
        private readonly Func<long> _clock;
        private readonly ConcurrentDictionary<SecureEndpoint, EndpointState> _states = new();
        private readonly KeyEntry[] _keys;
        private readonly byte[] _preSharedKey;

        public SecureServerCore(SecureServerSettings settings, SecurityCounters counters, Func<long> clock)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _counters = counters ?? throw new ArgumentNullException(nameof(counters));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            if (settings.PrivateKeys == null || settings.PrivateKeys.Count == 0)
            {
                throw new ArgumentException("At least one private key is required", nameof(settings));
            }

            _keys = new KeyEntry[settings.PrivateKeys.Count];

            for (var i = 0; i < _keys.Length; i++)
            {
                _keys[i] = new KeyEntry(settings.PrivateKeys[i]);
            }

            _preSharedKey = settings.PreSharedKey == null || settings.PreSharedKey.Length == 0 ? Array.Empty<byte>() : (byte[])settings.PreSharedKey.Clone();
            _cookies = new CookieJar(settings.CookieLifetimeMs, clock());
            _perIp = new KeyedRateLimiter<SecureEndpoint>(settings.HandshakesPerIpPerSecond, settings.HandshakesPerIpPerSecond, 100000);
            _budget = new TokenBucket(Math.Max(1, settings.MaxHandshakesPerSecond), clock());
        }

        public event Action<object> PeerEvicted;

        public int ExtraPacketSize => SecureRecord.MaxOverhead(_settings.Session.PaddingBlock);

        public int SessionCount => _states.Count;

        public bool AllowPlaintext => _settings.AllowPlaintext;

        public byte[] CurrentPublicKey => (byte[])_keys[0].Public.Clone();

        public SecureInboundResult ProcessInbound(in SecureEndpoint endpoint, byte[] data, int length, byte[] reply)
        {
            var now = _clock();

            if (length <= 0 || !SecureRecord.IsRecord(data[0]))
            {
                return HandlePlain(endpoint);
            }

            switch (data[0])
            {
                case SecureRecord.Init:
                    return HandleInit(endpoint, data, length, reply, now);
                case SecureRecord.Data:
                case SecureRecord.DataPadded:
                    return HandleData(endpoint, data, length, now);
                default:
                    _counters.DecryptFailed();
                    return SecureInboundResult.Dropped;
            }
        }

        public int ProtectOutbound(in SecureEndpoint endpoint, byte[] data, int offset, int length)
        {
            if (!_states.TryGetValue(endpoint, out var state))
            {
                return -1;
            }

            var active = state.Active;

            if (active == null)
            {
                return -1;
            }

            var written = active.Protect(data, offset, length, _clock());
            return written < 0 ? -2 : written;
        }

        public bool HasActiveSession(in SecureEndpoint endpoint) => _states.TryGetValue(endpoint, out var state) && state.Active != null;

        public SecureSession Bind(in SecureEndpoint endpoint, object peer)
        {
            if (!_states.TryGetValue(endpoint, out var state))
            {
                return null;
            }

            lock (state.Gate)
            {
                var active = state.Active;

                if (active == null || active.Tag != null)
                {
                    return null;
                }

                active.Tag = peer;
                return active;
            }
        }

        public void Release(SecureSession session)
        {
            if (session == null)
            {
                return;
            }

            session.ReleasedAtMs = _clock();
        }

        public void Sweep()
        {
            var now = _clock();
            List<object> evicted = null;

            foreach (var pair in _states)
            {
                var state = pair.Value;

                lock (state.Gate)
                {
                    var pending = state.Pending;

                    if (pending != null && now - pending.CreatedMs > _settings.HandshakeTimeoutMs)
                    {
                        pending.Session.Dispose();
                        state.Pending = null;
                    }

                    var active = state.Active;

                    if (active != null)
                    {
                        var released = active.ReleasedAtMs >= 0 && now - active.ReleasedAtMs > _settings.ReleaseGraceMs;
                        var idle = now - active.LastReceiveMs > _settings.SessionIdleTimeoutMs;

                        if (released || idle)
                        {
                            if (idle && !released && active.Tag != null)
                            {
                                evicted = evicted ?? new List<object>();
                                evicted.Add(active.Tag);
                            }

                            active.Dispose();
                            state.Active = null;
                        }
                    }

                    if (state.Active == null && state.Pending == null)
                    {
                        state.Removed = true;
                        _states.TryRemove(pair.Key, out _);
                    }
                }
            }

            _perIp.Sweep(now);

            if (evicted != null)
            {
                foreach (var peer in evicted)
                {
                    PeerEvicted?.Invoke(peer);
                }
            }
        }

        private SecureInboundResult HandlePlain(in SecureEndpoint endpoint)
        {
            if (_settings.AllowPlaintext && !HasActiveSession(endpoint))
            {
                return SecureInboundResult.Plain;
            }

            _counters.PlaintextDropped();
            return SecureInboundResult.Dropped;
        }

        private SecureInboundResult HandleInit(in SecureEndpoint endpoint, byte[] data, int length, byte[] reply, long now)
        {
            var record = new ReadOnlySpan<byte>(data, 0, length);

            if (!SecureRecord.TryParseInit(record, out var keyId, out var cookie, out var noiseMessage))
            {
                _counters.HandshakeFailed();
                return SecureInboundResult.Dropped;
            }

            var key = FindKey(keyId);

            if (key == null)
            {
                _counters.HandshakeFailed();
                return SecureInboundResult.Dropped;
            }

            if (_settings.RequireCookie && !_cookies.Verify(endpoint, cookie, now))
            {
                Span<byte> fresh = stackalloc byte[SecureRecord.CookieSize];
                _cookies.Compute(endpoint, now, fresh);
                _counters.CookieSent();
                return new SecureInboundResult(SecureInboundKind.Reply, SecureRecord.WriteRetry(reply, 0, fresh), null);
            }

            if (!_perIp.TryAcquire(endpoint.AddressOnly, now))
            {
                _counters.HandshakeRateLimited();
                return SecureInboundResult.Dropped;
            }

            var state = AcquireState(endpoint);

            if (state == null)
            {
                _counters.HandshakeRateLimited();
                return SecureInboundResult.Dropped;
            }

            lock (state.Gate)
            {
                if (state.Removed)
                {
                    return SecureInboundResult.Dropped;
                }

                var pending = state.Pending;

                if (pending != null && ConstantTime.AreEqual(pending.ClientEphemeral, noiseMessage.Slice(0, 32)))
                {
                    pending.Response.CopyTo(reply, 0);
                    return new SecureInboundResult(SecureInboundKind.Reply, pending.Response.Length, null);
                }

                var active = state.Active;

                if (active != null && now - active.LastReceiveMs < _settings.ReplaceIdleMs)
                {
                    _counters.HandshakeRateLimited();
                    return SecureInboundResult.Dropped;
                }

                if (!_budget.TryTake(1, _settings.MaxHandshakesPerSecond, _settings.MaxHandshakesPerSecond, now))
                {
                    _counters.HandshakeRateLimited();
                    return SecureInboundResult.Dropped;
                }

                var prologue = SecureRecord.CreatePrologue(keyId);

                using (var noise = NoiseHandshake.CreateResponder(key.Private, key.Public, _preSharedKey, prologue))
                {
                    var message = new byte[SecureRecord.NoiseMessageSize];

                    if (!noise.ReadMessage1(noiseMessage) || !noise.WriteMessage2(message))
                    {
                        _counters.HandshakeFailed();
                        return SecureInboundResult.Dropped;
                    }

                    noise.Split(out var sendKey, out var receiveKey);
                    var session = new SecureSession(sendKey, receiveKey, noise.HandshakeHash, _settings.Session, _counters, now);
                    ConstantTime.Zero(sendKey);
                    ConstantTime.Zero(receiveKey);

                    var response = new byte[SecureRecord.ResponseSize];
                    SecureRecord.WriteResponse(response, 0, message);
                    pending?.Session.Dispose();
                    state.Pending = new PendingSession(session, noiseMessage.Slice(0, 32).ToArray(), response, now);
                    response.CopyTo(reply, 0);
                    return new SecureInboundResult(SecureInboundKind.Reply, response.Length, null);
                }
            }
        }

        private SecureInboundResult HandleData(in SecureEndpoint endpoint, byte[] data, int length, long now)
        {
            if (!_states.TryGetValue(endpoint, out var state))
            {
                _counters.DecryptFailed();
                return SecureInboundResult.Dropped;
            }

            object evicted;
            SecureSession replaced;
            SecureInboundResult result;

            lock (state.Gate)
            {
                var active = state.Active;

                if (active != null)
                {
                    var outcome = active.Unprotect(data, 0, length, out var plainLength);

                    if (outcome == UnprotectResult.Ok)
                    {
                        active.Touch(now);
                        return new SecureInboundResult(SecureInboundKind.Decrypted, plainLength, active);
                    }

                    if (outcome == UnprotectResult.Replay)
                    {
                        return SecureInboundResult.Dropped;
                    }
                }

                var pending = state.Pending;

                if (pending == null)
                {
                    return SecureInboundResult.Dropped;
                }

                var pendingOutcome = pending.Session.Unprotect(data, 0, length, out var pendingLength);

                if (pendingOutcome != UnprotectResult.Ok)
                {
                    return SecureInboundResult.Dropped;
                }

                if (active != null && now - active.LastReceiveMs < _settings.ReplaceIdleMs)
                {
                    _counters.HandshakeRateLimited();
                    return SecureInboundResult.Dropped;
                }

                replaced = active;
                evicted = active?.Tag;
                state.Active = pending.Session;
                state.Pending = null;
                pending.Session.Touch(now);
                _counters.HandshakeSucceeded();
                result = new SecureInboundResult(SecureInboundKind.Decrypted, pendingLength, pending.Session);
            }

            if (evicted != null)
            {
                PeerEvicted?.Invoke(evicted);
            }

            replaced?.Dispose();
            return result;
        }

        private EndpointState AcquireState(in SecureEndpoint endpoint)
        {
            if (_states.TryGetValue(endpoint, out var existing))
            {
                return existing;
            }

            if (_states.Count >= _settings.MaxSessions)
            {
                return null;
            }

            var state = new EndpointState();
            return _states.GetOrAdd(endpoint, state);
        }

        private KeyEntry FindKey(ReadOnlySpan<byte> keyId)
        {
            KeyEntry match = null;

            for (var i = 0; i < _keys.Length; i++)
            {
                if (ConstantTime.AreEqual(_keys[i].KeyId, keyId))
                {
                    match = _keys[i];
                }
            }

            return match;
        }

        private sealed class KeyEntry
        {
            public KeyEntry(byte[] privateKey)
            {
                if (privateKey == null || privateKey.Length != X25519.KeySize)
                {
                    throw new ArgumentException("Private keys must be 32 bytes");
                }

                Private = (byte[])privateKey.Clone();
                Public = X25519.GetPublicKey(Private);
                KeyId = SecureRecord.ComputeKeyId(Public);
            }

            public byte[] Private { get; }

            public byte[] Public { get; }

            public byte[] KeyId { get; }
        }

        private sealed class PendingSession
        {
            public PendingSession(SecureSession session, byte[] clientEphemeral, byte[] response, long createdMs)
            {
                Session = session;
                ClientEphemeral = clientEphemeral;
                Response = response;
                CreatedMs = createdMs;
            }

            public SecureSession Session { get; }

            public byte[] ClientEphemeral { get; }

            public byte[] Response { get; }

            public long CreatedMs { get; }
        }

        private sealed class EndpointState
        {
            private SecureSession _active;

            public object Gate { get; } = new();

            public SecureSession Active
            {
                get => Volatile.Read(ref _active);
                set => Volatile.Write(ref _active, value);
            }

            public PendingSession Pending { get; set; }

            public bool Removed { get; set; }
        }
    }
}
