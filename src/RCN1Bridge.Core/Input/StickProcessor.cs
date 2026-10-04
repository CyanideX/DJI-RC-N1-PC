using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Input;

public readonly record struct ProcessedInput(float LeftX, float LeftY, float RightX, float RightY, float Dial)
{
    public static ProcessedInput Neutral { get; } = default;
}

public sealed record StickCalibration
{
    public AxisCalibration LeftH { get; init; } = AxisCalibration.Factory;
    public AxisCalibration LeftV { get; init; } = AxisCalibration.Factory;
    public AxisCalibration RightH { get; init; } = AxisCalibration.Factory;
    public AxisCalibration RightV { get; init; } = AxisCalibration.Factory;
    public AxisCalibration Dial { get; init; } = AxisCalibration.Factory;
}

public sealed record TuningProfile
{
    public static TuningProfile Default { get; } = new();

    public StickShaping Left { get; init; } = StickShaping.Default;
    public StickShaping Right { get; init; } = StickShaping.Default;
    public float DialDeadzone { get; init; } = 0.03f;
    public StickCalibration Calibration { get; init; } = new();
}

// Reader-thread only. Swap Tuning as a whole; it's read once per frame.
public sealed class StickProcessor
{
    private AxisSmoother _lx, _ly, _rx, _ry;
    private TuningProfile _tuning = TuningProfile.Default;

    public TuningProfile Tuning
    {
        get => Volatile.Read(ref _tuning);
        set => Volatile.Write(ref _tuning, value);
    }

    public ProcessedInput Process(in RawSticks raw, float elapsedSeconds)
    {
        var tuning = Tuning;
        var cal = tuning.Calibration;
        var left = tuning.Left;
        var right = tuning.Right;

        var (lx, ly) = left.Apply(cal.LeftH.Normalize(raw.LeftH), cal.LeftV.Normalize(raw.LeftV));
        var (rx, ry) = right.Apply(cal.RightH.Normalize(raw.RightH), cal.RightV.Normalize(raw.RightV));

        lx = _lx.Next(lx, left.SmoothingMs, elapsedSeconds);
        ly = _ly.Next(ly, left.SmoothingMs, elapsedSeconds);
        rx = _rx.Next(rx, right.SmoothingMs, elapsedSeconds);
        ry = _ry.Next(ry, right.SmoothingMs, elapsedSeconds);

        float dial = raw.HasDial ? StickShaping.ApplyDeadzone(cal.Dial.Normalize(raw.Dial), tuning.DialDeadzone) : 0f;
        return new ProcessedInput(lx, ly, rx, ry, dial);
    }

    public void Reset()
    {
        _lx.Reset(); _ly.Reset(); _rx.Reset(); _ry.Reset();
    }

    public static short ToAxis(float value) =>
        (short)Math.Clamp(MathF.Round(value * short.MaxValue), short.MinValue, short.MaxValue);

    public static byte ToTrigger(float value) =>
        (byte)Math.Clamp(MathF.Round(value * byte.MaxValue), 0, byte.MaxValue);
}
