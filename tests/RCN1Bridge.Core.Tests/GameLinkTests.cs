using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Tests;

// Reads the block the way the game plugin does, so the layout in docs/GameLink.md stays honest
internal sealed class GameLinkReader : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    public GameLinkReader(string name)
    {
        _file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        _view = _file.CreateViewAccessor(0, GameLink.Size);
    }

    public uint Magic => _view.ReadUInt32(0);
    public uint Version => _view.ReadUInt32(4);

    public (uint Flags, long Time, float[] Axes, ushort Word, byte Mode, byte Buttons, ushort[] Raw) Read()
    {
        while (true)
        {
            uint before = _view.ReadUInt32(GameLink.SequenceOffset);
            if ((before & 1) != 0)
                continue;
            var axes = new float[5];
            var raw = new ushort[5];
            for (int i = 0; i < 5; i++)
            {
                axes[i] = _view.ReadSingle(GameLink.AxesOffset + i * 4);
                raw[i] = _view.ReadUInt16(GameLink.RawOffset + i * 2);
            }
            var result = (_view.ReadUInt32(GameLink.FlagsOffset), _view.ReadInt64(GameLink.TimestampOffset), axes,
                _view.ReadUInt16(GameLink.ButtonWordOffset), _view.ReadByte(GameLink.ModeOffset), _view.ReadByte(GameLink.ButtonsOffset), raw);
            if (_view.ReadUInt32(GameLink.SequenceOffset) == before)
                return result;
        }
    }

    public void Heartbeat() => _view.Write(GameLink.HeartbeatOffset, Stopwatch.GetTimestamp());

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}

public class GameLinkTests
{
    private static string UniqueName() => $@"Local\RCN1Bridge.GameLink.Test.{Guid.NewGuid():N}";

    [Fact]
    public void PublishesTheDocumentedLayout()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new GameLinkReader(name);
        Assert.Equal(GameLink.Magic, reader.Magic);
        Assert.Equal(GameLink.Version, reader.Version);

        var raw = new RawSticks(RightH: 1684, RightV: 364, LeftH: 1354, LeftV: 694, Dial: 1500, HasDial: true);
        var input = new ProcessedInput(0.5f, -0.5f, 1f, -1f, 0.7f);
        var buttons = ButtonDecoder.Decode(0x2000 | 0x0002 | 0x0080);
        link.Publish(true, raw, input, buttons);

        var (flags, time, axes, word, mode, pressed, rawAxes) = reader.Read();
        Assert.Equal(GameLink.FlagLive | GameLink.FlagButtons | GameLink.FlagDial, flags);
        Assert.InRange(Stopwatch.GetElapsedTime(time).TotalSeconds, 0, 5);
        Assert.Equal([0.5f, -0.5f, 1f, -1f, 0.7f], axes);
        Assert.Equal(0x2082, word);
        Assert.Equal(1, mode);
        Assert.Equal(GameLink.ButtonFn | GameLink.ButtonReturnHome, pressed);
        Assert.Equal([(ushort)1354, (ushort)694, (ushort)1684, (ushort)364, (ushort)1500], rawAxes);
    }

    [Fact]
    public void NotLiveWithoutButtonsClearsFlags()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new GameLinkReader(name);
        link.Publish(false, default, default, null);
        var (flags, _, _, _, mode, pressed, _) = reader.Read();
        Assert.Equal(0u, flags);
        Assert.Equal(0, mode);
        Assert.Equal(0, pressed);
    }

    [Fact]
    public void HeartbeatShowsTheReader()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new GameLinkReader(name);
        Assert.False(link.ReaderConnected);
        reader.Heartbeat();
        Assert.True(link.ReaderConnected);
    }

    [Fact]
    public void EnginePublishesLiveSticksAndClearsOnUnplug()
    {
        string name = UniqueName();
        using var link = GameLink.TryCreate(name)!;
        using var reader = new GameLinkReader(name);
        var port = new PortInfo("COM5", "DJI USB VCOM For Protocol (COM5)", @"USB\VID_2CA3&PID_001F");
        FakeRc? rc = null;
        bool present = true;
        using var engine = new BridgeEngine(new RecordingOutput(), () => present ? [port] : [], _ => rc = new FakeRc()) { GameLink = link };
        engine.Start();

        Wait(() => (reader.Read().Flags & GameLink.FlagLive) != 0 && rc is not null, "live");
        rc!.LeftH = 1684;
        Wait(() => reader.Read().Axes[0] > 0.99f, "left stick in the block");
        Wait(() => (reader.Read().Flags & GameLink.FlagButtons) != 0 && reader.Read().Mode == 2, "switch on N");

        engine.GameLinkEnabled = false;
        Wait(() => (reader.Read().Flags & GameLink.FlagLive) == 0, "live cleared when disabled");
        engine.GameLinkEnabled = true;
        Wait(() => (reader.Read().Flags & GameLink.FlagLive) != 0, "live again");

        present = false;
        rc.FailReads = true;
        Wait(() => reader.Read().Flags == 0 && reader.Read().Axes[0] == 0f, "cleared on unplug");
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
