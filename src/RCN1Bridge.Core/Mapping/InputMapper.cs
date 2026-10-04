using System.Diagnostics;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Mapping;

// Reader thread only. Bindings are fixed at construction; build a new one to change them.
public sealed class InputMapper
{
    public static readonly TimeSpan PulseLength = TimeSpan.FromMilliseconds(60);
    private static readonly long PulseTicks = (long)(PulseLength.TotalSeconds * Stopwatch.Frequency);
    private const int AnalogCount = 5;

    private readonly AxisBinding[] _axes = new AxisBinding[AnalogCount];
    private readonly ThresholdButton[] _positive = new ThresholdButton[AnalogCount];
    private readonly ThresholdButton[] _negative = new ThresholdButton[AnalogCount];
    private readonly float _threshold;
    private readonly PadButton _fn, _photoVideo, _returnHome, _capture, _modeC, _modeN, _modeS;
    private FlightMode _mode;
    private PadButton _pulse;
    private long _pulseEnds;

    // Pass the mapper being replaced so editing a binding doesn't re-pulse the current switch position
    public InputMapper(MappingProfile profile, InputMapper? previous = null)
    {
        _mode = previous?._mode ?? FlightMode.Unknown;
        for (int i = 0; i < AnalogCount; i++)
            _axes[i] = profile.Get((AnalogInput)i);
        _threshold = profile.ButtonThreshold;
        (_fn, _photoVideo, _returnHome, _capture) = (profile.Fn, profile.PhotoVideo, profile.ReturnHome, profile.Capture);
        (_modeC, _modeN, _modeS) = (profile.ModeC, profile.ModeN, profile.ModeS);
        UsesButtons = (_fn | _photoVideo | _returnHome | _capture | _modeC | _modeN | _modeS) != PadButton.None;
    }

    // Button frames only need a resubmit when something is bound to them
    public bool UsesButtons { get; }

    // New connection: the next switch position counts as a change and pulses
    public void Reset()
    {
        _mode = FlightMode.Unknown;
        _pulse = PadButton.None;
        Array.Clear(_positive);
        Array.Clear(_negative);
    }

    public PadReport Map(in ProcessedInput input, RcButtons? buttons, long timestamp)
    {
        float lx = 0, ly = 0, rx = 0, ry = 0, lt = 0, rt = 0;
        var pressed = PadButton.None;

        for (int i = 0; i < AnalogCount; i++)
        {
            var binding = _axes[i];
            if (binding.Target == AxisTarget.None)
                continue;

            float v = i switch
            {
                0 => input.LeftX,
                1 => input.LeftY,
                2 => input.RightX,
                3 => input.RightY,
                _ => input.Dial,
            };
            if (binding.Invert)
                v = -v;

            switch (binding.Target)
            {
                case AxisTarget.LeftX: lx += v; break;
                case AxisTarget.LeftY: ly += v; break;
                case AxisTarget.RightX: rx += v; break;
                case AxisTarget.RightY: ry += v; break;
                case AxisTarget.LeftTrigger: lt += MathF.Max(v, 0f); break;
                case AxisTarget.RightTrigger: rt += MathF.Max(v, 0f); break;
                case AxisTarget.SplitTriggers:
                    if (v < 0) lt -= v;
                    else rt += v;
                    break;
                case AxisTarget.Buttons:
                    if (_positive[i].Update(v, _threshold)) pressed |= binding.Positive;
                    if (_negative[i].Update(-v, _threshold)) pressed |= binding.Negative;
                    break;
            }
        }

        if (buttons is { } b)
        {
            if (b.Fn) pressed |= _fn;
            if (b.PhotoVideo) pressed |= _photoVideo;
            if (b.ReturnHome) pressed |= _returnHome;
            if (b.Capture) pressed |= _capture;

            if (b.Mode != _mode)
            {
                var pulse = b.Mode switch
                {
                    FlightMode.Cine => _modeC,
                    FlightMode.Normal => _modeN,
                    FlightMode.Sport => _modeS,
                    _ => PadButton.None,
                };
                if (pulse != PadButton.None)
                {
                    _pulse = pulse;
                    _pulseEnds = timestamp + PulseTicks;
                }
                _mode = b.Mode;
            }
        }

        if (_pulse != PadButton.None)
        {
            if (timestamp < _pulseEnds)
                pressed |= _pulse;
            else
                _pulse = PadButton.None;
        }

        return new PadReport(
            StickProcessor.ToAxis(lx), StickProcessor.ToAxis(ly),
            StickProcessor.ToAxis(rx), StickProcessor.ToAxis(ry),
            StickProcessor.ToTrigger(lt), StickProcessor.ToTrigger(rt),
            pressed);
    }
}
