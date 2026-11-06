namespace NetPulse.Common.Tests;

public sealed class ExtendedTypeTests
{
    private sealed record Stats(int Hp, string Name);

    private sealed record Marker(byte Value);

    private sealed record Orphan(int Value);

    private static void RegisterStats(ushort id) => NetTypeRegistry.RegisterExtended<Stats>(
        id,
        (w, v) =>
        {
            w.WriteInt(v.Hp);
            w.WriteString(v.Name);
        },
        r => new Stats(r.ReadInt(), r.ReadString()));

    [Fact]
    public void RegisteredExtendedTypeRoundTrips()
    {
        RegisterStats(60001);

        try
        {
            var writer = new NetWriter();
            writer.WriteObject(new Stats(7, "hero"));
            var bytes = writer.ToArray();

            Assert.Equal(NetTypeCode.Extended, bytes[0]);
            Assert.Equal(60001, bytes[1] | (bytes[2] << 8));
            Assert.Equal(new Stats(7, "hero"), new NetReader(bytes).ReadObject());
        }
        finally
        {
            NetTypeRegistry.UnregisterExtended(60001);
        }
    }

    [Fact]
    public void UnknownExtendedIdSurvivesAsOpaqueValueAndIsForwardedUnchanged()
    {
        RegisterStats(60002);
        byte[] bytes;

        try
        {
            var writer = new NetWriter();
            writer.WriteObject(new Stats(3, "x"));
            bytes = writer.ToArray();
        }
        finally
        {
            NetTypeRegistry.UnregisterExtended(60002);
        }

        var reader = new NetReader(bytes);
        var opaque = Assert.IsType<OpaqueValue>(reader.ReadObject());
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(NetTypeCode.Extended, opaque.TypeCode);
        Assert.Equal(60002, opaque.ExtendedId);

        var forwarded = new NetWriter();
        forwarded.WriteObject(opaque);
        Assert.Equal(bytes, forwarded.ToArray());
    }

    [Fact]
    public void ExtendedValuesInsideContainersKeepTheirBoundaries()
    {
        NetTypeRegistry.RegisterExtended<Marker>(60003, (w, v) => w.WriteByte(v.Value), r => new Marker(r.ReadByte()));

        try
        {
            var table = new PropertyTable
            {
                ["a"] = new Marker(5),
                ["b"] = 42,
                ["c"] = OpaqueValue.ForExtended(60999, new byte[] { 1, 2 })
            };

            var writer = new NetWriter();
            writer.WriteObject(table);
            var reader = new NetReader(writer.AsSegment());
            var result = (PropertyTable)reader.ReadObject();

            Assert.Equal(0, reader.Remaining);
            Assert.Equal(new Marker(5), result["a"]);
            Assert.Equal(42, result["b"]);
            Assert.Equal(60999, ((OpaqueValue)result["c"]).ExtendedId);
        }
        finally
        {
            NetTypeRegistry.UnregisterExtended(60003);
        }
    }

    [Fact]
    public void OpaqueValuesWithDifferentExtendedIdsAreNotEqual()
    {
        var a = OpaqueValue.ForExtended(1, new byte[] { 1 });
        var b = OpaqueValue.ForExtended(2, new byte[] { 1 });

        Assert.NotEqual(a, b);
        Assert.Equal(a, OpaqueValue.ForExtended(1, new byte[] { 1 }));
    }

    [Fact]
    public void RegisteringTheSameIdForAnotherTypeIsRejected()
    {
        RegisterStats(60004);

        try
        {
            Assert.Throws<InvalidOperationException>(() => NetTypeRegistry.RegisterExtended<Orphan>(60004, (w, v) => w.WriteInt(v.Value), r => new Orphan(r.ReadInt())));
        }
        finally
        {
            NetTypeRegistry.UnregisterExtended(60004);
        }
    }

    [Fact]
    public void RegisteringTheSameTypeAndIdTwiceIsIdempotent()
    {
        RegisterStats(60005);
        RegisterStats(60005);

        try
        {
            Assert.True(NetTypeRegistry.IsExtendedRegistered(60005));
        }
        finally
        {
            NetTypeRegistry.UnregisterExtended(60005);
        }

        Assert.False(NetTypeRegistry.IsExtendedRegistered(60005));
    }

    [Fact]
    public void TruncatedExtendedPayloadThrows()
    {
        var writer = new NetWriter();
        writer.WriteByte(NetTypeCode.Extended);
        writer.WriteUShort(60006);
        writer.WriteVarUInt(50);
        writer.WriteByte(1);

        Assert.Throws<NetSerializationException>(() => new NetReader(writer.ToArray()).ReadObject());
    }

    [Fact]
    public void SByteULongAndCharRoundTrip()
    {
        var writer = new NetWriter();
        writer.WriteSByte(-5);
        writer.WriteSByte(sbyte.MaxValue);
        writer.WriteULong(ulong.MaxValue);
        writer.WriteULong(1UL);
        writer.WriteChar('Ж');
        writer.WriteChar('a');

        var reader = new NetReader(writer.AsSegment());
        Assert.Equal((sbyte)-5, reader.ReadSByte());
        Assert.Equal(sbyte.MaxValue, reader.ReadSByte());
        Assert.Equal(ulong.MaxValue, reader.ReadULong());
        Assert.Equal(1UL, reader.ReadULong());
        Assert.Equal('Ж', reader.ReadChar());
        Assert.Equal('a', reader.ReadChar());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void NewCodesAndRequestFieldsRoundTrip()
    {
        Assert.Equal(244, EventCode.Message);
        Assert.Equal(245, EventCode.SyncVarSet);
        Assert.Equal("_scene", RoomPropertyKeys.Scene);

        var request = RaiseEventRequest.FromParameters(new RaiseEventRequest { EventCode = EventCode.ObjectInstantiate, ViewId = 1001, ObjectFlags = ObjectFlag.PersistAcrossScenes }.ToParameters());
        Assert.Equal(ObjectFlag.PersistAcrossScenes, request.ObjectFlags);
        Assert.Equal(0, RaiseEventRequest.FromParameters(new RaiseEventRequest().ToParameters()).ObjectFlags);

        var response = JoinRoomResponse.FromParameters(new JoinRoomResponse { RoomName = "r", TickRate = 30 }.ToParameters());
        Assert.Equal(30, response.TickRate);
    }
}
