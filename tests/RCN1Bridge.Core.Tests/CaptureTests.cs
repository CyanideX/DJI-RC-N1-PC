using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Tests;

public class CaptureTests
{
    private static byte[] Frame(ushort slot6 = 1024, ushort lh = 1024, ushort seq = 1)
    {
        var payload = new byte[25];
        for (int slot = 0; slot < 8; slot++)
            payload[1 + slot * 3] = (byte)slot;
        BitConverter.TryWriteBytes(payload.AsSpan(1 + 3 * 3 + 1), lh);
        BitConverter.TryWriteBytes(payload.AsSpan(1 + 6 * 3 + 1), slot6);
        return TestFrames.Reply(payload, seq: seq);
    }

    private static IReadOnlyList<ByteWindow> Capture(FrameStats stats, params byte[][] frames)
    {
        stats.BeginWindow();
        foreach (var f in frames)
            stats.Record(new DumlFrame(f));
        return stats.EndWindow();
    }

    [Fact]
    public void DecodesAllEightSlots()
    {
        var slots = new ChannelSlot[8];
        Assert.Equal(8, StickDecoder.DecodeSlots(Frame(slot6: 1684, lh: 400), slots));
        Assert.Equal(new ChannelSlot(3, 400), slots[3]);
        Assert.Equal(new ChannelSlot(6, 1684), slots[6]);
    }

    [Fact]
    public void TracksWhichBytesChanged()
    {
        var stats = new FrameStats();
        stats.Record(new DumlFrame(Frame()));
        stats.Record(new DumlFrame(Frame(slot6: 1684)));

        var stat = Assert.Single(stats.Snapshot());
        int slot6Low = 12 + 6 * 3 + 1;
        Assert.NotEqual(0, stat.ChangedAt[slot6Low]);
        Assert.Equal(0, stat.ChangedAt[12]);
        Assert.Equal(2, stat.Count);
    }

    [Fact]
    public void WindowOnlyCoversFramesInsideIt()
    {
        var stats = new FrameStats();
        stats.Record(new DumlFrame(Frame(slot6: 364)));

        var window = Assert.Single(Capture(stats, Frame(slot6: 1684)));

        Assert.Equal(StickDecoder.ExtendedFrameLength, window.Length);
        Assert.Equal(window.Min, window.Max);
    }

    [Fact]
    public void ReportFindsTheHeldButton()
    {
        var stats = new FrameStats();
        var capture = new ButtonCapture();
        capture.Store(ButtonCapture.Rest, Capture(stats, Frame(seq: 1), Frame(seq: 2)));
        capture.Store("Mode S", Capture(stats, Frame(slot6: 1684, seq: 3), Frame(slot6: 1684, seq: 4)));

        string report = capture.Report();

        Assert.Contains("Mode S:", report);
        Assert.Contains("byte 31", report);
        Assert.DoesNotContain("byte 6:", report);
        Assert.Contains("06:1684", report);
    }

    [Fact]
    public void StickJitterIsNotADifference()
    {
        var stats = new FrameStats();
        var rest = Capture(stats, Frame(lh: 1022), Frame(lh: 1026));
        var held = Capture(stats, Frame(lh: 1023), Frame(lh: 1025));

        Assert.Empty(ButtonCapture.Differences(rest, held));
    }

    [Fact]
    public void SlowMessageMissedByRestStillHasABaseline()
    {
        var stats = new FrameStats();
        var battery = TestFrames.Reply([0x28, 0x0A, 0, 0, 0x64, 0], cmdId: 0x1E);
        stats.Record(new DumlFrame(battery));

        var rest = Capture(stats, Frame());
        var held = Capture(stats, Frame(), battery);

        Assert.Empty(ButtonCapture.Differences(rest, held));
    }

    [Fact]
    public void NothingReceivedMeansNoCapture()
    {
        var stats = new FrameStats();
        stats.Record(new DumlFrame(Frame()));
        Assert.Empty(Capture(stats));
    }

    [Fact]
    public void NewMessageTypeIsReported()
    {
        var stats = new FrameStats();
        var rest = Capture(stats, Frame());
        var held = Capture(stats, Frame(), TestFrames.Reply(new byte[11], cmdId: 0x05));

        Assert.Contains(ButtonCapture.Differences(rest, held), line => line.Contains("06/05") && line.Contains("only appeared"));
    }
}
