using System.Diagnostics;

namespace NetPulse.Common.Security
{
    public static class MonotonicClock
    {
        private static readonly long Start = Stopwatch.GetTimestamp();

        public static long NowMs => (Stopwatch.GetTimestamp() - Start) * 1000 / Stopwatch.Frequency;
    }
}
