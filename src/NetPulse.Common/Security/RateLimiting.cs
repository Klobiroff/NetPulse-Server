using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NetPulse.Common.Security
{
    public sealed class TokenBucket
    {
        private readonly object _gate = new();
        private double _tokens;
        private long _lastMs;

        public TokenBucket(double burst, long nowMs)
        {
            _tokens = burst;
            _lastMs = nowMs;
        }

        public long LastMs
        {
            get
            {
                lock (_gate)
                {
                    return _lastMs;
                }
            }
        }

        public bool TryTake(double cost, double ratePerSecond, double burst, long nowMs)
        {
            if (ratePerSecond <= 0)
            {
                return true;
            }

            lock (_gate)
            {
                var elapsed = nowMs - _lastMs;

                if (elapsed > 0)
                {
                    _tokens = Math.Min(burst, _tokens + (elapsed * ratePerSecond / 1000.0));
                    _lastMs = nowMs;
                }

                if (_tokens < cost)
                {
                    return false;
                }

                _tokens -= cost;
                return true;
            }
        }
    }

    public sealed class KeyedRateLimiter<TKey>
    {
        private readonly ConcurrentDictionary<TKey, TokenBucket> _buckets = new();
        private readonly double _ratePerSecond;
        private readonly double _burst;
        private readonly int _maxKeys;

        public KeyedRateLimiter(double ratePerSecond, double burst, int maxKeys)
        {
            _ratePerSecond = ratePerSecond;
            _burst = Math.Max(1, burst);
            _maxKeys = maxKeys;
        }

        public int Count => _buckets.Count;

        public bool TryAcquire(TKey key, long nowMs)
        {
            if (_ratePerSecond <= 0)
            {
                return true;
            }

            if (!_buckets.TryGetValue(key, out var bucket))
            {
                if (_buckets.Count >= _maxKeys)
                {
                    Sweep(nowMs);

                    if (_buckets.Count >= _maxKeys)
                    {
                        return false;
                    }
                }

                bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(_burst, nowMs));
            }

            return bucket.TryTake(1, _ratePerSecond, _burst, nowMs);
        }

        public void Sweep(long nowMs)
        {
            var idleMs = (long)(_burst / _ratePerSecond * 1000.0) + 1000;
            List<TKey> stale = null;

            foreach (var pair in _buckets)
            {
                if (nowMs - pair.Value.LastMs > idleMs)
                {
                    stale = stale ?? new List<TKey>();
                    stale.Add(pair.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            foreach (var key in stale)
            {
                _buckets.TryRemove(key, out _);
            }
        }
    }
}
