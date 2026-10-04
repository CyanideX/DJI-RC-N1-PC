using System.Buffers.Binary;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Tests;

public class CrcTests
{
    [Fact]
    public void Crc8TableMatchesV1() => Assert.Equal(V1Tables.Crc8, Crc.Crc8Table);

    [Fact]
    public void Crc16TableMatchesV1() => Assert.Equal(V1Tables.Crc16, Crc.Crc16Table);

    [Fact]
    public void EmptyInputReturnsSeed()
    {
        Assert.Equal(Crc.Crc8Seed, Crc.Crc8([]));
        Assert.Equal(Crc.Crc16Seed, Crc.Crc16([]));
    }
}

public class PacketTests
{
    [Fact]
    public void PollPacketLayout()
    {
        var packet = RcCommands.BuildPoll(0x34EB);

        Assert.Equal(13, packet.Length);
        Assert.Equal(0x55, packet[0]);
        Assert.Equal(13, packet[1]);
        Assert.Equal(0x04, packet[2]);
        Assert.Equal(Crc.Crc8(packet.AsSpan(0, 3)), packet[3]);
        Assert.Equal(0x0A, packet[4]);
        Assert.Equal(0x06, packet[5]);
        Assert.Equal(0xEB, packet[6]);
        Assert.Equal(0x34, packet[7]);
        Assert.Equal(0x40, packet[8]);
        Assert.Equal(0x06, packet[9]);
        Assert.Equal(0x01, packet[10]);
        Assert.True(TestFrames.HasValidCrcs(packet));
    }

    [Fact]
    public void SimulatorEnableCarriesPayload()
    {
        var packet = RcCommands.BuildSimulatorEnable(1);

        Assert.Equal(14, packet.Length);
        Assert.Equal(0x24, packet[10]);
        Assert.Equal(0x01, packet[11]);
        Assert.True(TestFrames.HasValidCrcs(packet));
    }

    [Fact]
    public void RejectsOversizedPayload() =>
        Assert.Throws<ArgumentException>(() => DumlPacket.Build(0, 0, 0, 0, 0, 0, new byte[DumlPacket.MaxLength]));

    [Fact]
    public void FrameAccessorsReadHeader()
    {
        var packet = DumlPacket.Build(0x0A, 0x06, 0xBEEF, 0x40, 0x06, 0x24, [1, 2, 3]);
        var frame = new DumlFrame(packet);

        Assert.Equal(0x0A, frame.Sender);
        Assert.Equal(0x06, frame.Receiver);
        Assert.Equal(0xBEEF, frame.Sequence);
        Assert.Equal(0x40, frame.CommandType);
        Assert.Equal(0x06, frame.CommandSet);
        Assert.Equal(0x24, frame.CommandId);
        Assert.Equal([1, 2, 3], frame.Payload.ToArray());
    }
}

public class ParserTests
{
    private sealed class Collector
    {
        public readonly List<byte[]> Frames = [];
        public readonly DumlParser Parser;
        public Collector() => Parser = new DumlParser(f => Frames.Add(f.Raw.ToArray()));
    }

    [Fact]
    public void ParsesSingleFrame()
    {
        var c = new Collector();
        var frame = TestFrames.Compact(1, 2, 3, 4);

        c.Parser.Feed(frame);

        Assert.Equal(frame, Assert.Single(c.Frames));
    }

    [Fact]
    public void ParsesFrameSplitAtEveryBoundary()
    {
        var frame = TestFrames.Extended(400, 500, 600, 700, 800);
        for (int split = 1; split < frame.Length; split++)
        {
            var c = new Collector();
            c.Parser.Feed(frame.AsSpan(0, split));
            c.Parser.Feed(frame.AsSpan(split));
            Assert.Equal(frame, Assert.Single(c.Frames));
        }
    }

    [Fact]
    public void ParsesByteAtATime()
    {
        var c = new Collector();
        var stream = Concat(TestFrames.Compact(1, 2, 3, 4), TestFrames.Extended(5, 6, 7, 8, 9));

        foreach (byte b in stream)
            c.Parser.Feed([b]);

        Assert.Equal(2, c.Frames.Count);
    }

    [Fact]
    public void SkipsGarbageBetweenFrames()
    {
        var c = new Collector();
        var a = TestFrames.Compact(1, 2, 3, 4);
        var b = TestFrames.Compact(5, 6, 7, 8);

        c.Parser.Feed(Concat([0x00, 0x55, 0x55, 0xFF], a, [0x55, 0x01, 0x02], b, [0x55]));

        Assert.Equal([a, b], c.Frames);
    }

    [Fact]
    public void DropsFrameWithBadBodyCrc()
    {
        var c = new Collector();
        var bad = TestFrames.Compact(1, 2, 3, 4);
        bad[12] ^= 0xFF;
        var good = TestFrames.Compact(5, 6, 7, 8);

        c.Parser.Feed(Concat(bad, good));

        Assert.Equal(good, Assert.Single(c.Frames));
        Assert.Equal(1, c.Parser.BadFrameCount);
    }

    [Fact]
    public void RecoversFromFakeHeaderClaimingLongFrame()
    {
        var c = new Collector();
        var fake = FakeHeader(200);
        var frames = Enumerable.Range(0, 3).Select(i => TestFrames.Compact((ushort)i, 0, 0, 0)).ToArray();

        c.Parser.Feed(Concat([fake, .. frames, new byte[200]]));

        Assert.Equal(frames, c.Frames);
    }

    [Fact]
    public void EveryLengthFieldIsSafe()
    {
        for (int length = 0; length <= DumlPacket.MaxLength; length++)
        {
            var c = new Collector();
            c.Parser.Feed(Concat(FakeHeader(length), new byte[length + 4], TestFrames.Compact(1, 2, 3, 4)));
            Assert.All(c.Frames, f => Assert.True(TestFrames.HasValidCrcs(f)));
        }
    }

    [Fact]
    public void FuzzNeverThrowsOrEmitsBadFrames()
    {
        var rng = new Random(1234);
        var c = new Collector();
        var chunk = new byte[257];
        var valid = TestFrames.Extended(1, 2, 3, 4, 5);
        int inserted = 0;

        for (int total = 0; total < 1_000_000; total += chunk.Length)
        {
            rng.NextBytes(chunk);
            // Lots of start bytes so the header path actually gets exercised
            for (int i = 0; i < chunk.Length; i += rng.Next(1, 16))
                chunk[i] = DumlPacket.StartByte;
            c.Parser.Feed(chunk.AsSpan(0, rng.Next(1, chunk.Length)));

            if (rng.Next(8) == 0)
            {
                c.Parser.Feed(valid);
                inserted++;
            }
        }

        Assert.All(c.Frames, f => Assert.True(TestFrames.HasValidCrcs(f)));
        Assert.True(c.Frames.Count(f => f.AsSpan().SequenceEqual(valid)) > inserted / 2,
            "Too many valid frames lost to resync");
    }

    private static byte[] FakeHeader(int length)
    {
        var header = new byte[4];
        header[0] = DumlPacket.StartByte;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)(length | 0x400));
        header[3] = Crc.Crc8(header.AsSpan(0, 3));
        return header;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}

public class ButtonDecoderTests
{
    [Theory]
    [InlineData(0x0000, FlightMode.Sport)]
    [InlineData(0x1000, FlightMode.Normal)]
    [InlineData(0x2000, FlightMode.Cine)]
    [InlineData(0x3000, FlightMode.Unknown)]
    public void FlightModes(int bits, FlightMode mode) =>
        Assert.Equal(mode, ButtonDecoder.Decode((ushort)bits).Mode);

    [Fact]
    public void Buttons()
    {
        var b = ButtonDecoder.Decode(0x1000 | 0x0002 | 0x0080);
        Assert.True(b.Fn);
        Assert.True(b.ReturnHome);
        Assert.False(b.PhotoVideo);
        Assert.False(b.Capture);
        Assert.True(ButtonDecoder.Decode(0x0020).Capture);
        Assert.True(ButtonDecoder.Decode(0x0040).Capture);
        Assert.True(ButtonDecoder.Decode(0x0004).PhotoVideo);
    }

    [Fact]
    public void ReadsBigEndianBitsFromFrame()
    {
        Assert.True(ButtonDecoder.TryDecode(new DumlFrame(TestFrames.Buttons(0x2080)), out var b));
        Assert.Equal(0x2080, b.Bits);
        Assert.Equal(FlightMode.Cine, b.Mode);
        Assert.True(b.ReturnHome);
    }

    [Fact]
    public void IgnoresOtherFrames()
    {
        Assert.False(ButtonDecoder.TryDecode(new DumlFrame(TestFrames.Extended(1, 2, 3, 4, 5)), out _));
        Assert.False(ButtonDecoder.TryDecode(new DumlFrame(TestFrames.Reply(new byte[10], cmdId: RcCommands.GetButtons)), out _));
    }
}

public class StickDecoderTests
{
    [Fact]
    public void DecodesCompactFrame()
    {
        var frame = TestFrames.Compact(rh: 1100, rv: 1200, lv: 1300, lh: 1400);

        Assert.True(StickDecoder.TryDecode(new DumlFrame(frame), out var s));
        Assert.Equal(new RawSticks(1100, 1200, 1400, 1300, 1024, false), s);
    }

    [Fact]
    public void DecodesExtendedFrame()
    {
        var frame = TestFrames.Extended(rh: 400, rv: 500, lv: 600, lh: 700, dial: 1684);

        Assert.True(StickDecoder.TryDecode(new DumlFrame(frame), out var s));
        Assert.Equal(new RawSticks(400, 500, 700, 600, 1684, true), s);
    }

    [Fact]
    public void IgnoresOtherCommands()
    {
        var frame = TestFrames.Reply(new byte[8], cmdId: 0x05);
        Assert.False(StickDecoder.TryDecode(new DumlFrame(frame), out _));
    }

    [Fact]
    public void LayoutMustMatchCommand()
    {
        var pollSized21 = TestFrames.Reply(new byte[8]);
        var aetrSized38 = TestFrames.Reply(new byte[25], cmdId: RcCommands.AetrPush);
        Assert.False(StickDecoder.TryDecode(new DumlFrame(pollSized21), out _));
        Assert.False(StickDecoder.TryDecode(new DumlFrame(aetrSized38), out _));
    }

    [Fact]
    public void IgnoresUnknownSizes()
    {
        var frame = TestFrames.Reply(new byte[9]);
        Assert.False(StickDecoder.TryDecode(new DumlFrame(frame), out _));
    }
}
