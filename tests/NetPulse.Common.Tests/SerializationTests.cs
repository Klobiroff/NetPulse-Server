using System.Numerics;

namespace NetPulse.Common.Tests;

public sealed class SerializationTests
{
    private static object RoundTrip(object value)
    {
        var writer = new NetWriter();
        writer.WriteObject(value);
        var reader = new NetReader(writer.AsSegment());
        var result = reader.ReadObject();
        Assert.Equal(0, reader.Remaining);
        return result;
    }

    [Theory]
    [InlineData(true)]
    [InlineData((byte)200)]
    [InlineData((short)-5)]
    [InlineData(int.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(1.5f)]
    [InlineData(-2.25d)]
    [InlineData("héllo мир")]
    [InlineData("")]
    public void PrimitivesRoundTrip(object value) => Assert.Equal(value, RoundTrip(value));

    [Fact]
    public void NullRoundTrips() => Assert.Null(RoundTrip(null));

    [Fact]
    public void ArraysRoundTrip()
    {
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])RoundTrip(new byte[] { 1, 2, 3 }));
        Assert.Equal(new[] { 1, -2, 3 }, (int[])RoundTrip(new[] { 1, -2, 3 }));
        Assert.Equal(new[] { 1.5f, 2f }, (float[])RoundTrip(new[] { 1.5f, 2f }));
        Assert.Equal(new[] { "a", null, "c" }, (string[])RoundTrip(new[] { "a", null, "c" }));
    }

    [Fact]
    public void ObjectArrayAndDictionaryRoundTrip()
    {
        var input = new object[] { 1, "two", null, new Dictionary<string, object> { ["k"] = 3L } };
        var result = (object[])RoundTrip(input);

        Assert.Equal(1, result[0]);
        Assert.Equal("two", result[1]);
        Assert.Null(result[2]);
        Assert.Equal(3L, ((Dictionary<string, object>)result[3])["k"]);
    }

    [Fact]
    public void GuidRoundTrips()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(guid, RoundTrip(guid));
    }

    [Fact]
    public void PropertyTableRoundTripsIncludingNullTombstones()
    {
        var table = new PropertyTable { ["score"] = 10, ["name"] = "x", ["gone"] = null };
        var result = (PropertyTable)RoundTrip(table);

        Assert.Equal(10, result.Get<int>("score"));
        Assert.Equal("x", result.Get<string>("name"));
        Assert.True(result.ContainsKey("gone"));
    }

    [Fact]
    public void VarUIntRoundTrips()
    {
        var writer = new NetWriter();
        var values = new uint[] { 0, 1, 127, 128, 300, 16384, uint.MaxValue };

        foreach (var value in values)
        {
            writer.WriteVarUInt(value);
        }

        var reader = new NetReader(writer.AsSegment());

        foreach (var value in values)
        {
            Assert.Equal(value, reader.ReadVarUInt());
        }
    }

    [Fact]
    public void NullStringIsPreserved()
    {
        var writer = new NetWriter();
        writer.WriteString(null);
        writer.WriteString(string.Empty);
        var reader = new NetReader(writer.AsSegment());

        Assert.Null(reader.ReadString());
        Assert.Equal(string.Empty, reader.ReadString());
    }

    [Fact]
    public void UnknownCustomTypeSurvivesAsOpaqueValue()
    {
        var writer = new NetWriter();
        writer.WriteByte(NetTypeCode.CustomMin + 1);
        writer.WriteBytes(new byte[] { 9, 8, 7 });
        var original = writer.ToArray();

        var reader = new NetReader(original);
        var value = Assert.IsType<OpaqueValue>(reader.ReadObject());
        Assert.Equal(101, value.TypeCode);
        Assert.Equal(new byte[] { 9, 8, 7 }, value.Data);

        var again = new NetWriter();
        again.WriteObject(value);
        Assert.Equal(original, again.ToArray());
    }

    [Fact]
    public void OpaqueValueInsideContainersIsForwardedUnchanged()
    {
        var table = new PropertyTable { ["pos"] = new OpaqueValue(150, new byte[] { 1, 2, 3, 4 }) };
        var result = (PropertyTable)RoundTrip(table);

        Assert.Equal(new OpaqueValue(150, new byte[] { 1, 2, 3, 4 }), result["pos"]);
    }

    [Fact]
    public void RegisteredCustomTypeRoundTripsAndWritesSameBytesAsOpaque()
    {
        NetTypeRegistry.Register<Vector3>(
            180,
            (w, v) =>
            {
                w.WriteFloat(v.X);
                w.WriteFloat(v.Y);
                w.WriteFloat(v.Z);
            },
            r => new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat()));

        try
        {
            var writer = new NetWriter();
            writer.WriteObject(new Vector3(1, 2, 3));
            var bytes = writer.ToArray();

            Assert.Equal(new Vector3(1, 2, 3), new NetReader(bytes).ReadObject());

            NetTypeRegistry.Unregister(180);
            var opaque = Assert.IsType<OpaqueValue>(new NetReader(bytes).ReadObject());
            var forwarded = new NetWriter();
            forwarded.WriteObject(opaque);
            Assert.Equal(bytes, forwarded.ToArray());
        }
        finally
        {
            NetTypeRegistry.Unregister(180);
        }
    }

    [Fact]
    public void CustomCodeOutsideRangeIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NetTypeRegistry.Register<int>(10, (_, _) => { }, _ => 0));

    [Fact]
    public void UnsupportedTypeThrows() =>
        Assert.Throws<NetSerializationException>(() => new NetWriter().WriteObject(new object()));

    [Fact]
    public void TruncatedAndHostileDataThrowInsteadOfAllocating()
    {
        Assert.Throws<NetSerializationException>(() => new NetReader(new byte[] { NetTypeCode.Int, 1 }).ReadObject());
        Assert.Throws<NetSerializationException>(() => new NetReader(new byte[] { NetTypeCode.IntArray, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }).ReadObject());
        Assert.Throws<NetSerializationException>(() => new NetReader(new byte[] { 99 }).ReadObject());
    }

    [Fact]
    public void DeepNestingIsRejected()
    {
        var bytes = new List<byte>();

        for (var i = 0; i < 40; i++)
        {
            bytes.Add(NetTypeCode.ObjectArray);
            bytes.Add(1);
        }

        Assert.Throws<NetSerializationException>(() => new NetReader(bytes.ToArray()).ReadObject());
    }
}
