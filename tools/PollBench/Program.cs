// Measures stick and button update rates against a real RC.
// Needs the RC plugged in and RC-N1 Bridge closed (it holds the COM port).
//   dotnet run --project tools/PollBench -c Release                  rates for each polling setup
//   dotnet run --project tools/PollBench -c Release -- stability 20   half-second rates over 20 s, like Home shows
//   dotnet run --project tools/PollBench -c Release -- seq            whether replies echo the request's sequence number
using System.Diagnostics;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

switch (args.FirstOrDefault())
{
    case "load":
        // A game pegging every core at normal priority
        for (int i = 0; i < Environment.ProcessorCount; i++)
            new Thread(() => { while (true) Thread.SpinWait(1000); }) { IsBackground = true }.Start();
        Stability(args.Length > 1 ? int.Parse(args[1]) : 20, args.Length > 2 ? int.Parse(args[2]) : 3);
        break;
    case "stability":
        Stability(args.Length > 1 ? int.Parse(args[1]) : 20, args.Length > 2 ? int.Parse(args[2]) : 3);
        break;
    case "gamelink":
        // Stands in for the game plugin: reads the running bridge's block and beats the heartbeat
        GameLinkProbe(args.Length > 1 ? int.Parse(args[1]) : 5);
        break;
    case "seq":
        SequenceProbe();
        break;
    default:
        Rates();
        break;
}

static void Rates()
{
    (int InFlight, int ButtonMs)[] configs =
    [
        (1, 0), (1, 33), (2, 0), (2, 33), (3, 0), (3, 33),
    ];

    foreach (var (inFlight, buttonMs) in configs)
    {
        string name = $"{inFlight} in flight, " + (buttonMs == 0 ? "no buttons" : $"buttons every {buttonMs} ms");
        using var engine = new BridgeEngine(NullGamepadOutput.Instance)
        {
            PollsInFlight = inFlight,
            ButtonPollInterval = buttonMs == 0 ? TimeSpan.FromHours(1) : TimeSpan.FromMilliseconds(buttonMs),
        };
        if (!StartLive(engine, name))
            continue;

        Thread.Sleep(1000);
        long sticks = engine.StickFrameCount, buttons = ButtonFrames(engine), bad = engine.BadFrameCount;
        var timer = Stopwatch.StartNew();
        Thread.Sleep(4000);
        double seconds = timer.Elapsed.TotalSeconds;
        Console.WriteLine($"{name,-32} sticks {(engine.StickFrameCount - sticks) / seconds,6:0.0}/s  " +
            $"buttons {(ButtonFrames(engine) - buttons) / seconds,5:0.0}/s  bad {engine.BadFrameCount - bad}");
    }
}

static void Stability(int seconds, int inFlight)
{
    using var engine = new BridgeEngine(NullGamepadOutput.Instance) { PollsInFlight = inFlight };
    if (!StartLive(engine, "stability"))
        return;
    Thread.Sleep(1000);

    var windows = new List<double>();
    long last = engine.StickFrameCount;
    var clock = Stopwatch.StartNew();
    double lastTime = 0;
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(500);
        double now = clock.Elapsed.TotalSeconds;
        long count = engine.StickFrameCount;
        windows.Add((count - last) / (now - lastTime));
        (last, lastTime) = (count, now);
    }

    double mean = windows.Average();
    double sd = Math.Sqrt(windows.Average(w => (w - mean) * (w - mean)));
    Console.WriteLine(string.Join(" ", windows.Select(w => w.ToString("0"))));
    Console.WriteLine($"{inFlight} in flight, half-second windows: min {windows.Min():0}  max {windows.Max():0}  mean {mean:0.0}  sd {sd:0.0}  unanswered {engine.LostPollCount}");
    Console.WriteLine($"reply time: median {engine.ReplyTime.Median():0.0} ms  p95 {engine.ReplyTime.Percentile(0.95):0.0} ms");
    foreach (var f in engine.Frames.Snapshot())
        Console.WriteLine($"  {f.CommandSet:X2}/{f.CommandId:X2} {f.Sender:X2}>{f.Receiver:X2} {f.Length,3} B  {f.Count / (clock.Elapsed.TotalSeconds + 1),6:0.0}/s");
}

static void SequenceProbe()
{
    var port = PortScanner.Scan().FirstOrDefault(p => p.IsDjiProtocol);
    if (port is null)
    {
        Console.WriteLine("No controller found");
        return;
    }

    using var device = SerialDevice.Open(port.PortName);
    var sent = new Dictionary<ushort, long>();
    var replies = new List<string>();
    var parser = new DumlParser(frame =>
    {
        if (frame.CommandSet != RcCommands.RcCommandSet || frame.CommandId is not (RcCommands.GetChannels or RcCommands.GetButtons))
            return;
        string age = sent.TryGetValue(frame.Sequence, out long at)
            ? $"{Stopwatch.GetElapsedTime(at).TotalMilliseconds:0.0} ms after its request"
            : "no request with this sequence";
        replies.Add($"{frame.CommandSet:X2}/{frame.CommandId:X2} seq {frame.Sequence:X4}: {age}");
    });

    device.Write(RcCommands.BuildSimulatorEnable(0x0100));
    var buffer = new byte[2048];
    var clock = Stopwatch.StartNew();
    ushort seq = 0x1000;
    while (clock.ElapsedMilliseconds < 1500)
    {
        if (clock.ElapsedMilliseconds > 500 && replies.Count < 40)
        {
            byte id = seq % 4 == 0 ? RcCommands.GetButtons : RcCommands.GetChannels;
            sent[seq] = Stopwatch.GetTimestamp();
            device.Write(DumlPacket.Build(RcCommands.PcAddress, RcCommands.RcAddress, seq++, RcCommands.RequestType, RcCommands.RcCommandSet, id));
        }
        int n = device.Read(buffer);
        if (n > 0)
            parser.Feed(buffer.AsSpan(0, n));
    }
    foreach (var line in replies.Take(20))
        Console.WriteLine(line);
}

static void GameLinkProbe(int seconds)
{
    using var file = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(GameLink.DefaultName);
    using var view = file.CreateViewAccessor(0, GameLink.Size);
    Console.WriteLine($"magic {view.ReadUInt32(0):X8} version {view.ReadUInt16(GameLink.MajorOffset)}.{view.ReadUInt16(GameLink.MinorOffset)}, writer pid {view.ReadInt32(GameLink.WriterPidOffset)}");
    var clock = Stopwatch.StartNew();
    uint lastSequence = 0;
    int writes = 0;
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        uint sequence = view.ReadUInt32(GameLink.SequenceOffset);
        if (sequence != lastSequence && (sequence & 1) == 0)
        {
            writes++;
            lastSequence = sequence;
        }
        Thread.Sleep(1);
    }
    long age = (long)Stopwatch.GetElapsedTime(view.ReadInt64(GameLink.TimestampOffset)).TotalMilliseconds;
    Console.WriteLine($"{writes / (double)seconds:0}/s seen, flags {view.ReadUInt32(GameLink.FlagsOffset)}, switch {view.ReadByte(GameLink.SwitchOffset)}, " +
        $"held {view.ReadUInt16(GameLink.HeldOffset)}, left X {view.ReadSingle(GameLink.AxesOffset):0.00}, raw LH {view.ReadUInt16(GameLink.RawOffset)}, age {age} ms");
}

static bool StartLive(BridgeEngine engine, string name)
{
    engine.Start();
    var wait = Stopwatch.StartNew();
    while (engine.Status.State != LinkState.Live && wait.Elapsed < TimeSpan.FromSeconds(8))
        Thread.Sleep(20);
    if (engine.Status.State == LinkState.Live)
        return true;
    Console.WriteLine($"{name}: never went live ({engine.Status.Problem ?? "no controller found"})");
    return false;
}

static long ButtonFrames(BridgeEngine engine) =>
    engine.Frames.Snapshot().Where(f => f.CommandId == RcCommands.GetButtons).Sum(f => f.Count);
