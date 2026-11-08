using System.Buffers.Binary;
using NetPulse.Common.Security;

namespace NetPulse.Common.Tests;

public sealed class SecureSessionTests
{
    private static (SecureSession Left, SecureSession Right, SecurityCounters Counters) CreatePair(SecureSessionSettings settings = null, long counterStart = 0)
    {
        var a = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var b = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        var counters = new SecurityCounters();
        var left = new SecureSession(a, b, new byte[32], settings ?? new SecureSessionSettings(), counters, 0, counterStart);
        var right = new SecureSession(b, a, new byte[32], settings ?? new SecureSessionSettings(), counters, 0, 0);
        return (left, right, counters);
    }

    private static byte[] Seal(SecureSession session, byte[] plaintext, long now = 0, int extra = 64)
    {
        var buffer = new byte[plaintext.Length + extra];
        plaintext.CopyTo(buffer, 0);
        var length = session.Protect(buffer, 0, plaintext.Length, now);
        Assert.True(length > 0);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static (UnprotectResult Result, byte[] Plain) Open(SecureSession session, byte[] record)
    {
        var buffer = (byte[])record.Clone();
        var result = session.Unprotect(buffer, 0, buffer.Length, out var length);
        return (result, buffer.AsSpan(0, length).ToArray());
    }

    [Fact]
    public void RoundTripAddsExactlyTwentySixBytes()
    {
        var (left, right, _) = CreatePair();
        var plaintext = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        var record = Seal(left, plaintext);
        Assert.Equal(plaintext.Length + 26, record.Length);
        Assert.Equal(0x7F, record[0]);
        Assert.Equal(0, record[1]);
        var (result, plain) = Open(right, record);
        Assert.Equal(UnprotectResult.Ok, result);
        Assert.Equal(plaintext, plain);
    }

    [Fact]
    public void EmptyPayloadRoundTrips()
    {
        var (left, right, _) = CreatePair();
        var record = Seal(left, Array.Empty<byte>());
        Assert.Equal(26, record.Length);
        var (result, plain) = Open(right, record);
        Assert.Equal(UnprotectResult.Ok, result);
        Assert.Empty(plain);
    }

    [Fact]
    public void DirectionsUseDifferentKeys()
    {
        var (left, _, _) = CreatePair();
        var record = Seal(left, new byte[20]);
        var (result, _) = Open(left, record);
        Assert.NotEqual(UnprotectResult.Ok, result);
    }

    [Fact]
    public void EveryModifiedByteOfARecordIsRejected()
    {
        var (left, right, counters) = CreatePair();
        var record = Seal(left, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());

        for (var i = 0; i < record.Length; i++)
        {
            for (var bit = 0; bit < 8; bit += 7)
            {
                var copy = (byte[])record.Clone();
                copy[i] ^= (byte)(1 << bit);
                var (result, _) = Open(right, copy);
                Assert.NotEqual(UnprotectResult.Ok, result);
            }
        }

        Assert.True(counters.DecryptFailures > 0);
        var (good, _) = Open(right, record);
        Assert.Equal(UnprotectResult.Ok, good);
    }

    [Fact]
    public void TruncatedRecordsAreRejected()
    {
        var (left, right, _) = CreatePair();
        var record = Seal(left, new byte[50]);

        for (var length = 0; length < record.Length; length++)
        {
            var (result, _) = Open(right, record.AsSpan(0, length).ToArray());
            Assert.NotEqual(UnprotectResult.Ok, result);
        }
    }

    [Fact]
    public void ReplayIsRejectedAndCounted()
    {
        var (left, right, counters) = CreatePair();
        var record = Seal(left, new byte[10]);
        Assert.Equal(UnprotectResult.Ok, Open(right, record).Result);
        Assert.Equal(UnprotectResult.Replay, Open(right, record).Result);
        Assert.Equal(1, counters.Replays);
    }

    [Fact]
    public void ForgedPacketDoesNotConsumeTheCounter()
    {
        var (left, right, _) = CreatePair();
        var record = Seal(left, new byte[10]);
        var forged = (byte[])record.Clone();
        forged[record.Length - 1] ^= 0xFF;
        Assert.Equal(UnprotectResult.AuthFailed, Open(right, forged).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, record).Result);
    }

    [Fact]
    public void ForgedPacketWithFarFutureCounterDoesNotMoveTheWindow()
    {
        var (left, right, _) = CreatePair();
        var first = Seal(left, new byte[10]);
        var forged = (byte[])first.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(forged.AsSpan(2, 8), 1UL << 40);
        Assert.NotEqual(UnprotectResult.Ok, Open(right, forged).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, first).Result);
        var second = Seal(left, new byte[10]);
        Assert.Equal(UnprotectResult.Ok, Open(right, second).Result);
    }

    [Fact]
    public void ReorderingInsideTheWindowIsAcceptedAndOutsideIsRejected()
    {
        var (left, right, _) = CreatePair(new SecureSessionSettings { ReplayWindow = 64 });
        var records = Enumerable.Range(0, 200).Select(_ => Seal(left, new byte[8])).ToList();
        Assert.Equal(UnprotectResult.Ok, Open(right, records[100]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[60]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[99]).Result);
        Assert.Equal(UnprotectResult.Replay, Open(right, records[99]).Result);
        Assert.NotEqual(UnprotectResult.Ok, Open(right, records[30]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[150]).Result);
        Assert.NotEqual(UnprotectResult.Ok, Open(right, records[60]).Result);
        Assert.NotEqual(UnprotectResult.Ok, Open(right, records[80]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[120]).Result);
    }

    [Fact]
    public void ReplayWindowRejectsDuplicatesOnly()
    {
        var window = new ReplayWindow(128);
        Assert.True(window.Check(5));
        window.Commit(5);
        Assert.False(window.Check(5));
        Assert.True(window.Check(4));
        window.Commit(200);
        Assert.False(window.Check(5));
        Assert.False(window.Check(72));
        Assert.True(window.Check(73));
        Assert.True(window.Check(199));
        window.Commit(199);
        Assert.False(window.Check(199));
        window.Commit(10000);
        Assert.False(window.Check(200));
        Assert.True(window.Check(9999));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(100)]
    public void PaddingMakesRecordsMultiplesOfTheBlock(int block)
    {
        var (left, right, _) = CreatePair(new SecureSessionSettings { PaddingBlock = block });

        for (var length = 0; length < 300; length += 7)
        {
            var plaintext = Enumerable.Range(0, length).Select(i => (byte)(i * 3)).ToArray();
            var buffer = new byte[length + SecureRecord.MaxOverhead(block)];
            plaintext.CopyTo(buffer, 0);
            var written = left.Protect(buffer, 0, length, 0);
            Assert.True(written > 0);
            Assert.Equal(0, written % block);
            Assert.Equal(0xFF, buffer[0]);
            var record = buffer.AsSpan(0, written).ToArray();
            var (result, plain) = Open(right, record);
            Assert.Equal(UnprotectResult.Ok, result);
            Assert.Equal(plaintext, plain);
        }
    }

    [Fact]
    public void ProtectFailsWhenThereIsNoRoom()
    {
        var (left, _, _) = CreatePair();
        var buffer = new byte[40];
        Assert.Equal(-1, left.Protect(buffer, 0, 30, 0));
    }

    [Fact]
    public void RekeyHappensByPacketCountAndOldKeysStillWorkForLatePackets()
    {
        var (left, right, counters) = CreatePair(new SecureSessionSettings { RekeyAfterPackets = 10, ReplayWindow = 64 });
        var records = Enumerable.Range(0, 35).Select(_ => Seal(left, new byte[8])).ToList();
        Assert.True(left.SendEpoch >= 3);
        Assert.True(counters.Rekeys >= 3);
        Assert.Equal(0, records[0][1]);
        Assert.Equal(1, records[10][1]);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[5]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[12]).Result);
        Assert.Equal(1, right.ReceiveEpoch);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[8]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[22]).Result);
        Assert.Equal(2, right.ReceiveEpoch);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[34]).Result);
        Assert.Equal(3, right.ReceiveEpoch);
    }

    [Fact]
    public void RekeyByTime()
    {
        var (left, right, _) = CreatePair(new SecureSessionSettings { RekeyAfterMs = 1000, RekeyAfterPackets = 0 });
        var first = Seal(left, new byte[8], 10);
        var second = Seal(left, new byte[8], 2000);
        Assert.Equal(0, first[1]);
        Assert.Equal(1, second[1]);
        Assert.Equal(UnprotectResult.Ok, Open(right, first).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, second).Result);
    }

    [Fact]
    public void RekeySurvivesHeavyPacketLoss()
    {
        var (left, right, counters) = CreatePair(new SecureSessionSettings { RekeyAfterPackets = 7, ReplayWindow = 256 });
        var random = new Random(3);
        var delivered = 0;

        for (var i = 0; i < 2000; i++)
        {
            var record = Seal(left, BitConverter.GetBytes(i));

            if (random.NextDouble() < 0.6)
            {
                continue;
            }

            var (result, plain) = Open(right, record);
            Assert.Equal(UnprotectResult.Ok, result);
            Assert.Equal(i, BitConverter.ToInt32(plain));
            delivered++;
        }

        Assert.True(delivered > 400);
        Assert.True(counters.Rekeys > 200);
    }

    [Fact]
    public void WholeEpochLossWithinLookAheadStillRecovers()
    {
        var (left, right, _) = CreatePair(new SecureSessionSettings { RekeyAfterPackets = 5, ReplayWindow = 256 });
        var all = Enumerable.Range(0, 30).Select(_ => Seal(left, new byte[4])).ToList();
        Assert.Equal(UnprotectResult.Ok, Open(right, all[0]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, all[17]).Result);
        Assert.Equal(3, right.ReceiveEpoch);
    }

    [Fact]
    public void LossBeyondLookAheadIsRejectedWithoutBreakingTheSession()
    {
        var (left, right, _) = CreatePair(new SecureSessionSettings { RekeyAfterPackets = 2, ReplayWindow = 256 });
        var all = Enumerable.Range(0, 40).Select(_ => Seal(left, new byte[4])).ToList();
        Assert.Equal(UnprotectResult.UnknownEpoch, Open(right, all[39]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, all[0]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, all[3]).Result);
    }

    [Fact]
    public void OldKeyIsRetiredAfterTheWindowPasses()
    {
        var (left, right, _) = CreatePair(new SecureSessionSettings { RekeyAfterPackets = 10, ReplayWindow = 64 });
        var records = Enumerable.Range(0, 200).Select(_ => Seal(left, new byte[4])).ToList();
        Assert.Equal(UnprotectResult.Ok, Open(right, records[5]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[12]).Result);
        Assert.Equal(UnprotectResult.Ok, Open(right, records[9]).Result);

        for (var i = 13; i < 90; i++)
        {
            Assert.Equal(UnprotectResult.Ok, Open(right, records[i]).Result);
        }

        Assert.NotEqual(UnprotectResult.Ok, Open(right, records[8]).Result);
    }

    [Fact]
    public void CounterNeverRepeatsAcrossThreads()
    {
        var (left, _, _) = CreatePair(new SecureSessionSettings { RekeyAfterPackets = 50 });
        var counters = new System.Collections.Concurrent.ConcurrentBag<ulong>();

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 2000; i++)
            {
                var buffer = new byte[64];
                var length = left.Protect(buffer, 0, 8, 0);
                Assert.True(length > 0);
                counters.Add(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(2, 8)));
            }
        });

        Assert.Equal(16000, counters.Distinct().Count());
        Assert.Equal(16000UL - 1, counters.Max());
    }

    [Fact]
    public void ConcurrentSendersProduceRecordsTheReceiverCanOpen()
    {
        var (left, right, _) = CreatePair(new SecureSessionSettings { RekeyAfterPackets = 64, ReplayWindow = 4096 });
        var records = new System.Collections.Concurrent.ConcurrentBag<byte[]>();

        Parallel.For(0, 4, _ =>
        {
            for (var i = 0; i < 500; i++)
            {
                records.Add(Seal(left, new byte[16]));
            }
        });

        var ordered = records.OrderBy(r => BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(2, 8))).ToList();
        var failures = 0;

        foreach (var record in ordered)
        {
            if (Open(right, record).Result != UnprotectResult.Ok)
            {
                failures++;
            }
        }

        Assert.Equal(0, failures);
    }

    [Fact]
    public void SessionClosesNearTheCounterLimit()
    {
        var (left, _, _) = CreatePair(counterStart: (long)SecureSession.CounterLimit - 2);
        var buffer = new byte[64];
        Assert.True(left.Protect(buffer, 0, 4, 0) > 0);
        Assert.True(left.Protect(buffer, 0, 4, 0) > 0);
        Assert.Equal(-1, left.Protect(buffer, 0, 4, 0));
        Assert.True(left.IsClosed);
        Assert.Equal(-1, left.Protect(buffer, 0, 4, 0));
    }

    [Fact]
    public void EncryptionAndDecryptionDoNotAllocate()
    {
        var (left, right, _) = CreatePair(new SecureSessionSettings { RekeyAfterPackets = 0, RekeyAfterMs = 0, PaddingBlock = 16 });
        var buffer = new byte[1500];
        var length = left.Protect(buffer, 0, 1000, 0);
        var copy = new byte[1500];
        buffer.AsSpan(0, length).CopyTo(copy);
        right.Unprotect(copy, 0, length, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();

        var total = 0;

        for (var i = 0; i < 2000; i++)
        {
            length = left.Protect(buffer, 0, 1000, 0);
            right.Unprotect(buffer, 0, length, out var plain);
            total += plain;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2000000, total);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DisposeWipesKeys()
    {
        var (left, right, _) = CreatePair();
        var record = Seal(left, new byte[10]);
        right.Dispose();
        Assert.Equal(UnprotectResult.Closed, Open(right, record).Result);
        left.Dispose();
        Assert.Equal(-1, left.Protect(new byte[64], 0, 4, 0));
    }
}
