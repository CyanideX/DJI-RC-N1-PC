using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Tests;

internal readonly record struct BlockState(
    uint Flags, long Time, uint Sample, byte Switch, ushort Held, float[] Axes, float[] Calibrated, byte[] Presses, ushort[] Raw, ushort Word);

// Reads the block the way the game plugin does, so the layout in docs/GameLink.md stays honest
internal sealed class TestReader : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private int _slot = -1;

    public TestReader(string name)
    {
        _file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        _view = _file.CreateViewAccessor(0, GameLink.Size);
    }

    public uint Magic => _view.ReadUInt32(0);
    public ushort Major => _view.ReadUInt16(GameLink.MajorOffset);
    public ushort Minor => _view.ReadUInt16(GameLink.MinorOffset);
    public uint Size => _view.ReadUInt32(GameLink.SizeOffset);
    public int WriterPid => _view.ReadInt32(GameLink.WriterPidOffset);
    public long WriterBeat => _view.ReadInt64(GameLink.WriterBeatOffset);
    public uint Device => _view.ReadUInt32(GameLink.DeviceOffset);

    public BlockState Read()
    {
        while (true)
        {
            uint before = _view.ReadUInt32(GameLink.SequenceOffset);
            if ((before & 1) != 0)
                continue;
            var axes = new float[6];
            var calibrated = new float[6];
            var raw = new ushort[5];
            var presses = new byte[8];
            for (int i = 0; i < 6; i++)
            {
                axes[i] = _view.ReadSingle(GameLink.AxesOffset + i * 4);
                calibrated[i] = _view.ReadSingle(GameLink.CalibratedOffset + i * 4);
            }
            for (int i = 0; i < 5; i++)
                raw[i] = _view.ReadUInt16(GameLink.RawOffset + i * 2);
            for (int i = 0; i < 8; i++)
                presses[i] = _view.ReadByte(GameLink.PressOffset + i);
            var state = new BlockState(
                _view.ReadUInt32(GameLink.FlagsOffset), _view.ReadInt64(GameLink.TimestampOffset), _view.ReadUInt32(GameLink.SampleOffset),
                _view.ReadByte(GameLink.SwitchOffset), _view.ReadUInt16(GameLink.HeldOffset), axes, calibrated, presses, raw,
                _view.ReadUInt16(GameLink.ButtonWordOffset));
            if (_view.ReadUInt32(GameLink.SequenceOffset) == before)
                return state;
        }
    }

    public void Claim(string name, bool exclusive, int pid = 4242)
    {
        for (int i = 0; i < GameLink.SlotCount && _slot < 0; i++)
        {
            if (_view.ReadInt32(SlotOffset(i) + GameLink.SlotPidOffset) == 0)
                _slot = i;
        }
        int slot = SlotOffset(_slot);
        _view.Write(slot + GameLink.SlotPidOffset, pid);
        _view.Write(slot + GameLink.SlotFlagsOffset, exclusive ? GameLink.SlotExclusive : 0u);
        var bytes = new byte[GameLink.SlotNameLength];
        Encoding.UTF8.GetBytes(name, bytes);
        _view.WriteArray(slot + GameLink.SlotNameOffset, bytes, 0, bytes.Length);
        Heartbeat();
    }

    public void Heartbeat() => _view.Write(SlotOffset(_slot) + GameLink.SlotBeatOffset, Stopwatch.GetTimestamp());

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }

    private static int SlotOffset(int slot) => GameLink.SlotsOffset + slot * GameLink.SlotSize;
}

public class GameLinkTests
{
    private static string UniqueName() => $@"Local\RCN1Bridge.GameLink.Test.{Guid.NewGuid():N}";

    [Fact]
    public void WritesTheHeader()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new TestReader(name);
        Assert.Equal(GameLink.Magic, reader.Magic);
        Assert.Equal(GameLink.Major, reader.Major);
        Assert.Equal(GameLink.Minor, reader.Minor);
        Assert.Equal((uint)GameLink.Size, reader.Size);
        Assert.Equal(Environment.ProcessId, reader.WriterPid);
        Assert.Equal(GameLink.DeviceRcN1, reader.Device);
        Assert.InRange(Stopwatch.GetElapsedTime(reader.WriterBeat).TotalSeconds, 0, 1);
    }

    [Fact]
    public void PublishesTheDocumentedLayout()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new TestReader(name);

        var raw = new RawSticks(RightH: 1684, RightV: 364, LeftH: 1354, LeftV: 694, Dial: 1500, HasDial: true);
        var tuned = new ProcessedInput(0.5f, -0.5f, 1f, -1f, 0.7f);
        var calibrated = new ProcessedInput(0.6f, -0.6f, 1f, -1f, 0.7f);
        var buttons = ButtonDecoder.Decode(0x2000 | 0x0002 | 0x0080);
        link.Publish(GameLink.FlagLive, raw, tuned, calibrated, buttons);

        var s = reader.Read();
        Assert.Equal(GameLink.FlagLive | GameLink.FlagButtons | GameLink.FlagDial, s.Flags);
        Assert.InRange(Stopwatch.GetElapsedTime(s.Time).TotalSeconds, 0, 5);
        Assert.Equal(1u, s.Sample);
        Assert.Equal(1, s.Switch);
        Assert.Equal(GameLink.ButtonFn | GameLink.ButtonReturnHome, s.Held);
        Assert.Equal([0.5f, -0.5f, 1f, -1f, 0.7f, 0f], s.Axes);
        Assert.Equal([0.6f, -0.6f, 1f, -1f, 0.7f, 0f], s.Calibrated);
        Assert.Equal([(ushort)1354, (ushort)694, (ushort)1684, (ushort)364, (ushort)1500], s.Raw);
        Assert.Equal(0x2082, s.Word);
    }

    [Fact]
    public void StateFlagsSayWhyItIsNotLive()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new TestReader(name);

        link.Publish(GameLink.FlagPaused, default, default, default, null);
        Assert.Equal(GameLink.FlagPaused, reader.Read().Flags);
        link.Publish(GameLink.FlagOff, default, default, default, null);
        Assert.Equal(GameLink.FlagOff, reader.Read().Flags);
        link.Publish(0, default, default, default, null);
        var s = reader.Read();
        Assert.Equal(0u, s.Flags);
        Assert.Equal(0, s.Switch);
        Assert.Equal(0, s.Held);
        Assert.Equal(3u, s.Sample);
    }

    [Fact]
    public void CountsEachPressOnce()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new TestReader(name);
        var fn = ButtonDecoder.Decode(0x1000 | 0x0002);
        var none = ButtonDecoder.Decode(0x1000);
        var rth = ButtonDecoder.Decode(0x1000 | 0x0080);

        foreach (var b in new[] { fn, fn, none, fn, none, rth })
            link.Publish(GameLink.FlagLive, default, default, default, b);
        // A stall between frames releases everything; the next hold is a fresh press
        link.Publish(0, default, default, default, null);
        link.Publish(GameLink.FlagLive, default, default, default, rth);

        var presses = reader.Read().Presses;
        Assert.Equal(2, presses[0]);
        Assert.Equal(0, presses[1]);
        Assert.Equal(2, presses[2]);
        Assert.Equal(0, presses[3]);
    }

    [Fact]
    public void ListsReadersAndTheirExclusiveFlag()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new TestReader(name);
        Assert.Empty(link.Readers);

        reader.Claim("Cyberpunk 2077", exclusive: true);
        Wait(() => link.ExclusiveReaderAttached, "exclusive picked up by the heartbeat");
        Assert.Equal([new GameLinkReader(4242, "Cyberpunk 2077", true)], link.Readers);

        reader.Claim("Cyberpunk 2077", exclusive: false);
        Wait(() => !link.ExclusiveReaderAttached, "exclusive released");
    }

    [Fact]
    public void ReadersSurviveABridgeRestart()
    {
        string name = UniqueName();
        var first = GameLink.TryCreate(name)!;
        using var reader = new TestReader(name);
        reader.Claim("Game", exclusive: false);
        // The reader's open handle keeps the block alive while the bridge restarts
        first.Dispose();
        Assert.Equal(0, reader.WriterPid);
        Assert.Equal(0, reader.WriterBeat);

        using var second = GameLink.TryCreate(name)!;
        Assert.Equal(Environment.ProcessId, reader.WriterPid);
        Wait(() => second.Readers.Count == 1, "reader kept its slot");
        Assert.Equal("Game", second.Readers[0].Name);
    }

    [Fact]
    public void EnginePublishesLiveSticksAndClearsOnUnplug()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new TestReader(name);
        var port = new PortInfo("COM5", "DJI USB VCOM For Protocol (COM5)", @"USB\VID_2CA3&PID_001F");
        FakeRc? rc = null;
        bool present = true;
        using var engine = new BridgeEngine(new RecordingOutput(), () => present ? [port] : [], _ => rc = new FakeRc()) { GameLink = link };
        engine.Processor.Tuning = new TuningProfile { Left = new StickShaping { Expo = 1f } };
        engine.Start();

        Wait(() => (reader.Read().Flags & GameLink.FlagLive) != 0 && rc is not null, "live");
        rc!.LeftH = 1354;
        Wait(() => reader.Read().Calibrated[0] > 0.45f, "left stick in the block");
        var s = reader.Read();
        Assert.True(s.Axes[0] < s.Calibrated[0] * 0.5f, "expo only in the tuned axes");
        Wait(() => (reader.Read().Flags & GameLink.FlagButtons) != 0 && reader.Read().Switch == 2, "switch on N");

        engine.GameLinkEnabled = false;
        Wait(() => (reader.Read().Flags & (GameLink.FlagLive | GameLink.FlagPaused | GameLink.FlagOff)) == GameLink.FlagOff, "off flag when disabled");
        engine.GameLinkEnabled = true;
        engine.OutputEnabled = false;
        Wait(() => (reader.Read().Flags & GameLink.FlagPaused) != 0, "paused flag");
        engine.OutputEnabled = true;
        Wait(() => (reader.Read().Flags & GameLink.FlagLive) != 0, "live again");

        present = false;
        rc.FailReads = true;
        Wait(() => reader.Read().Flags == 0 && reader.Read().Axes[0] == 0f, "cleared on unplug");
    }

    [Fact]
    public void ExclusiveReaderHoldsThePadAtCentre()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new TestReader(name);
        var output = new RecordingOutput();
        var port = new PortInfo("COM5", "DJI USB VCOM For Protocol (COM5)", @"USB\VID_2CA3&PID_001F");
        FakeRc? rc = null;
        using var engine = new BridgeEngine(output, () => [port], _ => rc = new FakeRc()) { GameLink = link };
        engine.Start();

        Wait(() => rc is not null && engine.Status.State == LinkState.Live, "live");
        rc!.LeftH = 1684;
        Wait(() => output.Last.LeftX > 30000, "pad follows the stick");

        reader.Claim("Cyberpunk 2077", exclusive: true);
        Wait(() => output.Last == PadReport.Neutral, "pad held while the reader has the RC");
        Assert.True(engine.PadHeldForReader);
        Assert.True(reader.Read().Axes[0] > 0.99f, "the reader still gets the stick");

        engine.GameLinkEnabled = false;
        Wait(() => output.Last.LeftX > 30000, "Xbox-only ignores the reader");
    }

    private static void Wait(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 3000)
                Assert.Fail($"Timed out waiting for: {what}");
            Thread.Sleep(5);
        }
    }
}
