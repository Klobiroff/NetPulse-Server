using System.Diagnostics;

namespace NetPulse.Server.Core;

public sealed class ServerClock
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public long NowMs => (long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
}
