using System.Threading;

namespace NetPulse.Common.Security
{
    public sealed class SecurityCounters
    {
        private long _handshakesSucceeded;
        private long _handshakesFailed;
        private long _handshakesRateLimited;
        private long _cookiesSent;
        private long _decryptFailures;
        private long _replays;
        private long _rekeys;
        private long _plaintextRejected;

        public long HandshakesSucceeded => Interlocked.Read(ref _handshakesSucceeded);

        public long HandshakesFailed => Interlocked.Read(ref _handshakesFailed);

        public long HandshakesRateLimited => Interlocked.Read(ref _handshakesRateLimited);

        public long CookiesSent => Interlocked.Read(ref _cookiesSent);

        public long DecryptFailures => Interlocked.Read(ref _decryptFailures);

        public long Replays => Interlocked.Read(ref _replays);

        public long Rekeys => Interlocked.Read(ref _rekeys);

        public long PlaintextRejected => Interlocked.Read(ref _plaintextRejected);

        public void HandshakeSucceeded() => Interlocked.Increment(ref _handshakesSucceeded);

        public void HandshakeFailed() => Interlocked.Increment(ref _handshakesFailed);

        public void HandshakeRateLimited() => Interlocked.Increment(ref _handshakesRateLimited);

        public void CookieSent() => Interlocked.Increment(ref _cookiesSent);

        public void DecryptFailed() => Interlocked.Increment(ref _decryptFailures);

        public void ReplayDetected() => Interlocked.Increment(ref _replays);

        public void Rekeyed() => Interlocked.Increment(ref _rekeys);

        public void PlaintextDropped() => Interlocked.Increment(ref _plaintextRejected);
    }
}
