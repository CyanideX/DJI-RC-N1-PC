// Measures stick and button update rates against a real RC for each polling setup.
// Needs the RC plugged in and RC-N1 Bridge closed (it holds the COM port).
//   dotnet run --project tools/PollBench -c Release
using System.Diagnostics;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

(int InFlight, int ButtonMs)[] configs =
[
    (1, 0), (1, 20), (1, 33), (1, 50),
    (2, 0), (2, 20), (2, 33), (2, 50),
];

foreach (var (inFlight, buttonMs) in configs)
{
    string name = $"{inFlight} in flight, " + (buttonMs == 0 ? "no buttons" : $"buttons every {buttonMs} ms");
    using var engine = new BridgeEngine(NullGamepadOutput.Instance)
    {
        PollsInFlight = inFlight,
        ButtonPollInterval = buttonMs == 0 ? TimeSpan.FromHours(1) : TimeSpan.FromMilliseconds(buttonMs),
    };
    engine.Start();

    var wait = Stopwatch.StartNew();
    while (engine.Status.State != LinkState.Live && wait.Elapsed < TimeSpan.FromSeconds(8))
        Thread.Sleep(20);
    if (engine.Status.State != LinkState.Live)
    {
        Console.WriteLine($"{name}: never went live ({engine.Status.Problem ?? "no controller found"})");
        continue;
    }

    Thread.Sleep(1000);
    long sticks = engine.StickFrameCount, buttons = ButtonFrames(engine), bad = engine.BadFrameCount;
    var timer = Stopwatch.StartNew();
    Thread.Sleep(4000);
    double seconds = timer.Elapsed.TotalSeconds;
    Console.WriteLine($"{name,-32} sticks {(engine.StickFrameCount - sticks) / seconds,6:0.0}/s  " +
        $"buttons {(ButtonFrames(engine) - buttons) / seconds,5:0.0}/s  bad {engine.BadFrameCount - bad}");
}

static long ButtonFrames(BridgeEngine engine) =>
    engine.Frames.Snapshot().Where(f => f.CommandId == RcCommands.GetButtons).Sum(f => f.Count);
