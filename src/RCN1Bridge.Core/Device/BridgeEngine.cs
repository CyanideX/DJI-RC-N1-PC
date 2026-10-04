using System.Diagnostics;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Mapping;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Device;

public enum LinkState
{
    Searching,
    WaitingForData,
    Live,
    Stalled,
    Stopped,
}

public sealed record LinkStatus(
    LinkState State,
    PortInfo? Port,
    IReadOnlyList<PortInfo> Ports,
    string? Problem = null,
    bool SuggestReplug = false)
{
    public static LinkStatus Initial { get; } = new(LinkState.Searching, null, []);
}

// Buttons is null until the RC answers a 06/27 poll
public readonly record struct InputSnapshot(RawSticks Raw, ProcessedInput Processed, RcButtons? Buttons, long Sequence);

public interface ISerialLink : IDisposable
{
    int Read(Span<byte> buffer);
    void Write(ReadOnlySpan<byte> buffer);
}

public sealed class BridgeEngine : IDisposable
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SimEnableInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ReplugHintAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollReplyTimeout = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    // The RC answers one request at a time; two in flight hides the turnaround.
    // Measured on an RC-N1: ~129 stick and ~27 button updates/s.
    public int PollsInFlight { get; set; } = 2;
    public TimeSpan ButtonPollInterval { get; set; } = TimeSpan.FromMilliseconds(33);

    private readonly IGamepadOutput _output;
    private readonly Func<IReadOnlyList<PortInfo>> _scan;
    private readonly Func<PortInfo, ISerialLink> _open;
    private readonly StickProcessor _processor = new();
    private readonly AutoResetEvent _scanNow = new(false);
    private readonly AutoResetEvent _replyArrived = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _statusGate = new();
    private readonly Lock _inputGate = new();
    private Thread? _thread;

    private LinkStatus _status = LinkStatus.Initial;
    private InputSnapshot _input;
    private long _lastStickTimestamp;
    private long _lastExtendedTimestamp; // reader thread only
    private long _sessionStartTimestamp;
    private long _readTimestamp;
    private long _stickFrames;
    private long _replies;
    private long _badFrames;
    private long _skippedBytes;
    private volatile bool _outputEnabled = true;
    private InputMapper? _mapper;
    private ProcessedInput _lastProcessed; // reader thread only
    private RcButtons? _lastButtons; // reader thread only

    public BridgeEngine(IGamepadOutput output)
        : this(output, PortScanner.Scan, port => new SerialLinkAdapter(SerialDevice.Open(port.PortName)))
    {
    }

    public BridgeEngine(IGamepadOutput output, Func<IReadOnlyList<PortInfo>> scan, Func<PortInfo, ISerialLink> open)
    {
        _output = output;
        _scan = scan;
        _open = open;
    }

    // Raised on engine threads
    public event Action<LinkStatus>? StatusChanged;

    public LinkStatus Status => Volatile.Read(ref _status);
    public FrameStats Frames { get; } = new();
    public LatencyTracker Latency { get; } = new();
    public long StickFrameCount => Interlocked.Read(ref _stickFrames);
    public long BadFrameCount => Interlocked.Read(ref _badFrames);
    public long SkippedByteCount => Interlocked.Read(ref _skippedBytes);
    public StickProcessor Processor => _processor;

    // Null sends the sticks straight through, which is all the default layout needs
    public InputMapper? Mapper
    {
        get => Volatile.Read(ref _mapper);
        set => Volatile.Write(ref _mapper, value);
    }

    public InputSnapshot Input
    {
        get
        {
            lock (_inputGate)
                return _input;
        }
    }

    public bool OutputEnabled
    {
        get => _outputEnabled;
        set
        {
            _outputEnabled = value;
            if (!value)
                _output.SubmitNeutral();
        }
    }

    public void Start()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(Run) { IsBackground = true, Name = "RC reader" };
        _thread.Start();
    }

    public void RequestScan() => _scanNow.Set();

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _output.SubmitNeutral();
        _scanNow.Dispose();
        _replyArrived.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        var token = _stop.Token;
        Log.Info("Engine started");
        while (!token.IsCancellationRequested)
        {
            var ports = _scan();
            var port = ports.FirstOrDefault(p => p.IsDjiProtocol);
            if (port is null)
            {
                SetStatus(new LinkStatus(LinkState.Searching, null, ports));
                Wait(RescanInterval, token);
                continue;
            }

            string? problem = RunSession(port, ports, token);
            if (!token.IsCancellationRequested)
            {
                SetStatus(new LinkStatus(LinkState.Searching, null, ports, problem));
                Wait(problem is null ? TimeSpan.FromMilliseconds(500) : RescanInterval, token);
            }
        }
        UpdateStatus(s => s with { State = LinkState.Stopped });
        Log.Info("Engine stopped");
    }

    private string? RunSession(PortInfo port, IReadOnlyList<PortInfo> ports, CancellationToken stopToken)
    {
        ISerialLink link;
        try
        {
            link = _open(port);
        }
        catch (IOException ex)
        {
            Log.Warn(ex.Message);
            return ex.Message;
        }

        Log.Info($"Opened {port.PortName} ({port.Name})");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        var token = session.Token;
        string? problem = null;

        _processor.Reset();
        Mapper?.Reset();
        _lastProcessed = default;
        _lastButtons = null;
        Latency.Clear();
        Interlocked.Exchange(ref _replies, 0);
        Volatile.Write(ref _sessionStartTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _lastStickTimestamp, 0);
        _lastExtendedTimestamp = 0;
        SetStatus(new LinkStatus(LinkState.WaitingForData, port, ports));

        var poller = new Thread(() => PollLoop(link, port, ports, session)) { IsBackground = true, Name = "RC poller" };
        poller.Start();

        var parser = new DumlParser(OnFrame);
        var buffer = new byte[2048];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = link.Read(buffer);
                if (read <= 0)
                    continue;
                Volatile.Write(ref _readTimestamp, Stopwatch.GetTimestamp());
                parser.Feed(buffer.AsSpan(0, read));
                Interlocked.Exchange(ref _badFrames, parser.BadFrameCount);
                Interlocked.Exchange(ref _skippedBytes, parser.SkippedByteCount);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            problem = token.IsCancellationRequested ? null : "Lost the controller connection.";
            Log.Warn($"Reader stopped: {ex.Message}");
        }
        finally
        {
            session.Cancel();
            _replyArrived.Set();
            poller.Join(TimeSpan.FromSeconds(1));
            link.Dispose();
            _output.SubmitNeutral();
            PublishInput(default, ProcessedInput.Neutral);
            PublishButtons(null);
            Log.Info($"Closed {port.PortName}");
        }
        return problem;
    }

    private void OnFrame(DumlFrame frame)
    {
        Frames.Record(frame);
        if (ButtonDecoder.TryDecode(frame, out var buttons))
        {
            PublishButtons(buttons);
            if (_lastButtons != buttons)
            {
                _lastButtons = buttons;
                if (Mapper is { UsesButtons: true } && Status.State == LinkState.Live)
                    Submit(Stopwatch.GetTimestamp());
            }
            return;
        }
        if (!StickDecoder.TryDecode(frame, out var raw))
            return;

        long now = Stopwatch.GetTimestamp();
        if (raw.HasDial)
            _lastExtendedTimestamp = now;
        else if (_lastExtendedTimestamp != 0 && Stopwatch.GetElapsedTime(_lastExtendedTimestamp, now) < StallTimeout)
            return; // RC-N1 sends both; the AETR push has no dial and would yank it back to centre

        long previous = Volatile.Read(ref _lastStickTimestamp);
        float elapsed = previous == 0 ? 0f : (float)Math.Min(Stopwatch.GetElapsedTime(previous, now).TotalSeconds, 0.1);
        Volatile.Write(ref _lastStickTimestamp, now);

        var processed = _processor.Process(raw, elapsed);
        _lastProcessed = processed;
        Submit(now);
        Latency.Record(Volatile.Read(ref _readTimestamp));

        PublishInput(raw, processed);
        Interlocked.Increment(ref _stickFrames);
        if (frame.CommandId == RcCommands.GetChannels)
        {
            Interlocked.Increment(ref _replies);
            _replyArrived.Set();
        }

        var state = Status.State;
        if (state is LinkState.WaitingForData or LinkState.Stalled)
        {
            Log.Info(state == LinkState.Stalled ? "Stick data resumed" : $"Stick data flowing ({frame.Length} B frames)");
            UpdateStatus(s => s.State is LinkState.WaitingForData or LinkState.Stalled
                ? s with { State = LinkState.Live, SuggestReplug = false, Problem = null }
                : s);
        }
    }

    private void Submit(long timestamp)
    {
        if (!_outputEnabled)
            return;
        var mapper = Mapper;
        var report = mapper is null
            ? PadReport.FromSticks(_lastProcessed)
            : mapper.Map(_lastProcessed, _lastButtons, timestamp);
        _output.Submit(report);
    }

    private void PollLoop(ISerialLink link, PortInfo port, IReadOnlyList<PortInfo> ports, CancellationTokenSource session)
    {
        var token = session.Token;
        Span<byte> poll = stackalloc byte[DumlPacket.MinLength];
        ushort sequence = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        long sessionStart = Volatile.Read(ref _sessionStartTimestamp);
        long lastSimEnable = 0;
        long lastPoll = 0;
        long lastButtonPoll = 0;
        long sent = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                long lastStick = Volatile.Read(ref _lastStickTimestamp);

                if (lastStick == 0)
                {
                    if (lastSimEnable == 0 || Stopwatch.GetElapsedTime(lastSimEnable, now) >= SimEnableInterval)
                    {
                        link.Write(RcCommands.BuildSimulatorEnable(sequence++));
                        lastSimEnable = now;
                    }
                    if (!Status.SuggestReplug && Stopwatch.GetElapsedTime(sessionStart, now) >= ReplugHintAfter)
                    {
                        Log.Warn("No stick data after 10 s");
                        UpdateStatus(s => s.State == LinkState.WaitingForData ? s with { SuggestReplug = true } : s);
                    }
                }
                else if (Status.State == LinkState.Live && Stopwatch.GetElapsedTime(lastStick, now) >= StallTimeout)
                {
                    Log.Warn("Stick data stopped, centring");
                    _output.SubmitNeutral();
                    UpdateStatus(s => s.State == LinkState.Live ? s with { State = LinkState.Stalled } : s);
                }

                long inFlight = sent - Interlocked.Read(ref _replies);
                if (inFlight > 0 && Stopwatch.GetElapsedTime(lastPoll, now) >= PollReplyTimeout)
                {
                    sent = Interlocked.Read(ref _replies);
                    inFlight = 0;
                }

                if (lastStick != 0 && Stopwatch.GetElapsedTime(lastButtonPoll, now) >= ButtonPollInterval)
                {
                    DumlPacket.Write(poll, RcCommands.PcAddress, RcCommands.RcAddress, sequence++,
                        RcCommands.RequestType, RcCommands.RcCommandSet, RcCommands.GetButtons, []);
                    link.Write(poll);
                    lastButtonPoll = now;
                }

                if (inFlight < PollsInFlight)
                {
                    DumlPacket.Write(poll, RcCommands.PcAddress, RcCommands.RcAddress, sequence++,
                        RcCommands.RequestType, RcCommands.RcCommandSet, RcCommands.GetChannels, []);
                    link.Write(poll);
                    sent++;
                    lastPoll = now;
                }

                _replyArrived.WaitOne(5);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested)
                Log.Warn($"Poller stopped: {ex.Message}");
            session.Cancel();
        }
    }

    private void PublishInput(RawSticks raw, ProcessedInput processed)
    {
        lock (_inputGate)
            _input = _input with { Raw = raw, Processed = processed, Sequence = _input.Sequence + 1 };
    }

    private void PublishButtons(RcButtons? buttons)
    {
        bool first;
        lock (_inputGate)
        {
            first = _input.Buttons is null && buttons is not null;
            if (_input.Buttons == buttons)
                return;
            _input = _input with { Buttons = buttons, Sequence = _input.Sequence + 1 };
        }
        if (first)
            Log.Info($"Buttons reported, mode {buttons!.Value.Mode}");
    }

    private void SetStatus(LinkStatus status) => UpdateStatus(_ => status);

    // Listeners should read Status rather than trust event order across threads
    private void UpdateStatus(Func<LinkStatus, LinkStatus> change)
    {
        LinkStatus next;
        lock (_statusGate)
        {
            next = change(_status);
            if (next == _status)
                return;
            Volatile.Write(ref _status, next);
        }
        StatusChanged?.Invoke(next);
    }

    private void Wait(TimeSpan duration, CancellationToken token) =>
        WaitHandle.WaitAny([_scanNow, token.WaitHandle], duration);

    private sealed class SerialLinkAdapter(SerialDevice device) : ISerialLink
    {
        public int Read(Span<byte> buffer) => device.Read(buffer);
        public void Write(ReadOnlySpan<byte> buffer) => device.Write(buffer);
        public void Dispose() => device.Dispose();
    }
}
