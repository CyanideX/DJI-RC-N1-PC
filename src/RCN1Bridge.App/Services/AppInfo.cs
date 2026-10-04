using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Diagnostics;

namespace RCN1Bridge.App.Services;

public static class AppInfo
{
    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static string BuildDiagnostics(BridgeEngine engine, PadHost pad, ButtonCapture capture)
    {
        var status = engine.Status;
        var input = engine.Input;
        var sb = new StringBuilder();
        sb.AppendLine($"RC-N1 Bridge {Version}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine();
        sb.AppendLine($"Link: {status.State}{(status.SuggestReplug ? " (replug suggested)" : "")}");
        sb.AppendLine($"Port: {status.Port?.Name ?? "none"}");
        if (status.Problem is not null)
            sb.AppendLine($"Problem: {status.Problem}");
        sb.AppendLine($"Virtual pad: {(pad.IsConnected ? $"virtual Xbox 360, slot {pad.PlayerNumber?.ToString() ?? "?"}" : pad.Problem)}");
        sb.AppendLine($"Output: {(engine.OutputEnabled ? "on" : "paused")}");
        sb.AppendLine($"Stick frames: {engine.StickFrameCount}, bad frames: {engine.BadFrameCount}, skipped bytes: {engine.SkippedByteCount}");
        sb.AppendLine($"Median input to pad: {engine.Latency.Median()?.ToString("0.000") ?? "n/a"} ms");
        var raw = input.Raw;
        sb.AppendLine($"Raw sticks: LH {raw.LeftH} LV {raw.LeftV} RH {raw.RightH} RV {raw.RightV} dial {(raw.HasDial ? raw.Dial : "n/a")}");
        sb.AppendLine(input.Buttons is { } b
            ? $"Buttons: {b.Bits:X4}, mode {b.Mode}, Fn {b.Fn}, capture {b.Capture}, photo/video {b.PhotoVideo}, RTH {b.ReturnHome}"
            : "Buttons: not reported");
        sb.AppendLine();
        sb.AppendLine("COM ports:");
        foreach (var port in status.Ports)
            sb.AppendLine($"  {port.PortName}  {port.Name}  {port.PnpDeviceId}");
        if (status.Ports.Count == 0)
            sb.AppendLine("  none");
        sb.AppendLine();
        sb.AppendLine("Messages (set/id from>to size count: frame):");
        foreach (var f in engine.Frames.Snapshot())
            sb.AppendLine($"  {f.CommandSet:X2}/{f.CommandId:X2} {f.Sender:X2}>{f.Receiver:X2} {f.Length,4} B {f.Count,8}: {Convert.ToHexString(f.LastFrame)}");
        if (capture.Count > 0)
        {
            sb.AppendLine();
            sb.Append(capture.Report());
        }
        sb.AppendLine();
        sb.AppendLine("Log:");
        foreach (var line in Log.Tail(100))
            sb.AppendLine($"  {line}");
        return sb.ToString();
    }
}
