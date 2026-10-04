using System.Collections.Concurrent;
using System.Diagnostics;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Mapping;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Tests;

internal sealed class FakeRc : ISerialLink
{
    private readonly ConcurrentQueue<byte> _outgoing = new();
    private readonly DumlParser _requests;
    private bool _simEnabled;

    public FakeRc() => _requests = new DumlParser(OnRequest);

    public volatile bool Responding = true;
    public volatile bool SendExtended = true;
    public volatile bool SendAetr;
    public volatile bool FailReads;
    public volatile bool Disposed;
    public ushort LeftH = 1024;
    public ushort AetrLeftH = 1024;
    public volatile ushort ButtonBits = 0x1000;

    public int Read(Span<byte> buffer)
    {
        if (FailReads)
            throw new IOException("Device unplugged");
        if (_outgoing.IsEmpty)
        {
            Thread.Sleep(2);
            return 0;
        }
        int n = 0;
        while (n < buffer.Length && _outgoing.TryDequeue(out byte b))
            buffer[n++] = b;
        return n;
    }

    public void Write(ReadOnlySpan<byte> buffer)
    {
        if (FailReads)
            throw new IOException("Device unplugged");
        lock (_requests)
            _requests.Feed(buffer);
    }

    public void Dispose() => Disposed = true;

    public readonly ConcurrentDictionary<(byte, byte, byte), int> Requests = new();

    private void OnRequest(DumlFrame frame)
    {
        Requests.AddOrUpdate((frame.Sender, frame.Receiver, frame.CommandId), 1, (_, n) => n + 1);
        if (frame.CommandId == RcCommands.SimulatorMode)
            _simEnabled = true;
        else if (frame.CommandId == RcCommands.GetButtons && Responding)
            foreach (byte b in TestFrames.Buttons(ButtonBits))
                _outgoing.Enqueue(b);
        else if (frame.CommandId == RcCommands.GetChannels && _simEnabled && Responding)
        {
            if (SendExtended)
                foreach (byte b in TestFrames.Extended(1024, 1024, 1024, LeftH, 1024))
                    _outgoing.Enqueue(b);
            if (SendAetr)
                foreach (byte b in TestFrames.Compact(1024, 1024, 1024, AetrLeftH))
                    _outgoing.Enqueue(b);
        }
    }
}

internal sealed class RecordingOutput : IGamepadOutput
{
    private readonly Lock _gate = new();
    private PadReport _last;
    public int Submits;
    public int Neutrals;

    public PadReport Last { get { lock (_gate) return _last; } }

    public void Submit(in PadReport input)
    {
        lock (_gate)
        {
            _last = input;
            Submits++;
        }
    }

    public void SubmitNeutral()
    {
        lock (_gate)
        {
            _last = PadReport.Neutral;
            Neutrals++;
        }
    }
}

public class EngineTests
{
    private static readonly PortInfo DjiPort = new("COM5", "DJI USB VCOM For Protocol (COM5)", @"USB\VID_2CA3&PID_001F");
    private static readonly PortInfo OtherPort = new("COM3", "Intel(R) Active Management Technology - SOL (COM3)", null);

    private sealed class Rig : IDisposable
    {
        public readonly RecordingOutput Output = new();
        public readonly List<FakeRc> Links = [];
        public volatile bool PortPresent = true;
        public readonly BridgeEngine Engine;

        public Rig()
        {
            Engine = new BridgeEngine(
                Output,
                () => PortPresent ? [OtherPort, DjiPort] : [OtherPort],
                _ =>
                {
                    var rc = new FakeRc();
                    lock (Links)
                        Links.Add(rc);
                    return rc;
                });
        }

        public FakeRc Current { get { lock (Links) return Links[^1]; } }

        public void Dispose() => Engine.Dispose();
    }

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"Timed out waiting for: {what}");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void GoesLiveAndDrivesThePad()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Status.State == LinkState.Live, "Live");

        rig.Current.LeftH = 1684;
        WaitFor(() => rig.Output.Last.LeftX > 32000, "left stick to reach the pad");

        Assert.Equal(DjiPort, rig.Engine.Status.Port);
        Assert.True(rig.Engine.StickFrameCount > 0);
    }

    [Fact]
    public void CentresWhenTheRcGoesQuiet()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Status.State == LinkState.Live, "Live");
        rig.Current.LeftH = 1684;
        WaitFor(() => rig.Output.Last.LeftX > 32000, "stick deflected");

        rig.Current.Responding = false;
        WaitFor(() => rig.Engine.Status.State == LinkState.Stalled, "Stalled");
        Assert.Equal(0, rig.Output.Last.LeftX);

        rig.Current.Responding = true;
        WaitFor(() => rig.Engine.Status.State == LinkState.Live, "Live again");
    }

    [Fact]
    public void UnplugCentresAndReconnects()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Status.State == LinkState.Live, "Live");
        rig.Current.LeftH = 1684;
        WaitFor(() => rig.Output.Last.LeftX > 32000, "stick deflected");

        var first = rig.Current;
        rig.PortPresent = false;
        first.FailReads = true;
        WaitFor(() => rig.Engine.Status.State == LinkState.Searching, "Searching");
        Assert.Equal(0, rig.Output.Last.LeftX);
        Assert.True(first.Disposed);

        rig.PortPresent = true;
        rig.Engine.RequestScan();
        WaitFor(() => rig.Engine.Status.State == LinkState.Live && rig.Current != first, "reconnected");
    }

    [Fact]
    public void PrefersFullFrameWhenBothArrive()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Status.State == LinkState.Live, "Live");
        rig.Current.LeftH = 1684;
        rig.Current.AetrLeftH = 364;
        rig.Current.SendAetr = true;

        WaitFor(() => rig.Engine.Frames.Snapshot().Any(f => f.CommandId == RcCommands.AetrPush), "AETR frames arriving");
        for (int i = 0; i < 20; i++)
        {
            Assert.True(rig.Output.Last.LeftX >= 0, "AETR push overrode the full frame");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void FallsBackToAetrPush()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Status.State == LinkState.Live, "Live");
        rig.Current.SendExtended = false;
        rig.Current.SendAetr = true;
        rig.Current.AetrLeftH = 364;

        WaitFor(() => rig.Output.Last.LeftX < -32000, "AETR push used once full frames stop");
    }

    [Fact]
    public void ReportsButtonsAndClearsThemOnUnplug()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Input.Buttons?.Mode == FlightMode.Normal, "buttons reported");

        rig.Current.ButtonBits = 0x2000 | 0x0002;
        WaitFor(() => rig.Engine.Input.Buttons is { Mode: FlightMode.Cine, Fn: true }, "switch to C with Fn held");

        rig.PortPresent = false;
        rig.Current.FailReads = true;
        WaitFor(() => rig.Engine.Input.Buttons is null, "buttons cleared");
    }

    [Fact]
    public void ListsPortsWhileSearching()
    {
        using var rig = new Rig { PortPresent = false };
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Status.Ports.Count == 1, "port list");
        Assert.Equal(LinkState.Searching, rig.Engine.Status.State);
        Assert.Null(rig.Engine.Status.Port);
    }

    [Fact]
    public void PausedOutputStaysNeutral()
    {
        using var rig = new Rig();
        rig.Engine.OutputEnabled = false;
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Status.State == LinkState.Live, "Live");
        rig.Current.LeftH = 1684;
        WaitFor(() => rig.Engine.Input.Processed.LeftX > 0.99f, "UI still sees the stick");

        Assert.Equal(0, rig.Output.Submits);
    }

    [Fact]
    public void ReportsPortInUse()
    {
        using var engine = new BridgeEngine(new RecordingOutput(), () => [DjiPort], _ => throw new SerialOpenException("COM5", 5));
        engine.Start();
        WaitFor(() => engine.Status.Problem is not null, "problem reported");
        Assert.Contains("in use", engine.Status.Problem);
        Assert.Equal(LinkState.Searching, engine.Status.State);
    }

    [Fact]
    public void DisposeLeavesPadNeutral()
    {
        var rig = new Rig();
        rig.Engine.Start();
        WaitFor(() => rig.Engine.Status.State == LinkState.Live, "Live");
        rig.Current.LeftH = 1684;
        WaitFor(() => rig.Output.Last.LeftX > 32000, "stick deflected");

        rig.Dispose();

        Assert.Equal(0, rig.Output.Last.LeftX);
        Assert.True(rig.Current.Disposed);
    }
}

public class EngineMappingTests
{
    private static readonly PortInfo DjiPort = new("COM5", "DJI USB VCOM For Protocol (COM5)", @"USB\VID_2CA3&PID_001F");

    private static (BridgeEngine Engine, RecordingOutput Output, Func<FakeRc> Rc) Start(MappingProfile profile)
    {
        var output = new RecordingOutput();
        FakeRc? rc = null;
        var engine = new BridgeEngine(output, () => [DjiPort], _ => rc = new FakeRc()) { Mapper = new InputMapper(profile) };
        engine.Start();
        return (engine, output, () => rc!);
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 3000)
                Assert.Fail($"Timed out waiting for: {what}");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void MappedButtonReachesThePad()
    {
        var (engine, output, rc) = Start(MappingProfile.Default with { Fn = PadButton.A });
        using var _ = engine;
        WaitFor(() => engine.Input.Buttons is not null && engine.Status.State == LinkState.Live, "Live with buttons");

        rc().ButtonBits = 0x1000 | 0x0002;
        WaitFor(() => output.Last.Buttons == PadButton.A, "A pressed");
        rc().ButtonBits = 0x1000;
        WaitFor(() => output.Last.Buttons == PadButton.None, "A released");
    }

    [Fact]
    public void SwitchMovePulsesItsButton()
    {
        var (engine, output, rc) = Start(MappingProfile.Default with { ModeC = PadButton.DPadLeft });
        using var _ = engine;
        WaitFor(() => engine.Input.Buttons?.Mode == FlightMode.Normal, "buttons reported");

        rc().ButtonBits = 0x2000;
        WaitFor(() => output.Last.Buttons == PadButton.DPadLeft, "pulse starts");
        WaitFor(() => output.Last.Buttons == PadButton.None, "pulse ends");
    }
}
