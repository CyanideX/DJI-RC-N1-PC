using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Input;

public readonly record struct ProcessedInput(
    float LeftX, float LeftY, float RightX, float RightY, float Dial, bool DialUp, bool DialDown)
{
    public static ProcessedInput Neutral { get; } = default;
}

public sealed class StickCalibration
{
    public AxisCalibration LeftH { get; init; } = AxisCalibration.Factory;
    public AxisCalibration LeftV { get; init; } = AxisCalibration.Factory;
    public AxisCalibration RightH { get; init; } = AxisCalibration.Factory;
    public AxisCalibration RightV { get; init; } = AxisCalibration.Factory;
    public AxisCalibration Dial { get; init; } = AxisCalibration.Factory;
}

// Reader-thread only. Swap settings by assigning the properties; they're read once per frame.
public sealed class StickProcessor
{
    private AxisSmoother _lx, _ly, _rx, _ry;
    private ThresholdButton _dialUp, _dialDown;

    public StickCalibration Calibration { get; set; } = new();
    public StickShaping Left { get; set; } = StickShaping.Default;
    public StickShaping Right { get; set; } = StickShaping.Default;
    public float DialDeadzone { get; set; } = 0.03f;
    public float DialButtonThreshold { get; set; } = ThresholdButton.DefaultPress;

    public ProcessedInput Process(in RawSticks raw, float elapsedSeconds)
    {
        var cal = Calibration;
        var left = Left;
        var right = Right;

        var (lx, ly) = left.Apply(cal.LeftH.Normalize(raw.LeftH), cal.LeftV.Normalize(raw.LeftV));
        var (rx, ry) = right.Apply(cal.RightH.Normalize(raw.RightH), cal.RightV.Normalize(raw.RightV));

        lx = _lx.Next(lx, left.SmoothingMs, elapsedSeconds);
        ly = _ly.Next(ly, left.SmoothingMs, elapsedSeconds);
        rx = _rx.Next(rx, right.SmoothingMs, elapsedSeconds);
        ry = _ry.Next(ry, right.SmoothingMs, elapsedSeconds);

        float dial = raw.HasDial ? StickShaping.ApplyDeadzone(cal.Dial.Normalize(raw.Dial), DialDeadzone) : 0f;
        bool up = _dialUp.Update(dial, DialButtonThreshold);
        bool down = _dialDown.Update(-dial, DialButtonThreshold);

        return new ProcessedInput(lx, ly, rx, ry, dial, up, down);
    }

    public void Reset()
    {
        _lx.Reset(); _ly.Reset(); _rx.Reset(); _ry.Reset();
        _dialUp.Reset(); _dialDown.Reset();
    }

    public static short ToAxis(float value) =>
        (short)Math.Clamp(MathF.Round(value * short.MaxValue), short.MinValue, short.MaxValue);

    public static byte ToTrigger(float value) =>
        (byte)Math.Clamp(MathF.Round(value * byte.MaxValue), 0, byte.MaxValue);
}
