using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Output;

namespace RCN1Bridge.Core.Mapping;

public enum AnalogInput { LeftX, LeftY, RightX, RightY, Dial }

public enum DigitalInput { Fn, PhotoVideo, ReturnHome, Capture, ModeC, ModeN, ModeS }

public enum AxisTarget
{
    None,
    LeftX,
    LeftY,
    RightX,
    RightY,
    // One-way: the positive half drives the trigger, so Invert picks the other half
    LeftTrigger,
    RightTrigger,
    // Negative half to LT, positive half to RT
    SplitTriggers,
    // Positive held past +PressAt, Negative past -PressAt
    Buttons,
}

public sealed record AxisBinding
{
    public AxisTarget Target { get; init; }
    public bool Invert { get; init; }
    public PadButton Positive { get; init; }
    public PadButton Negative { get; init; }
    public float PressAt { get; init; } = ThresholdButton.DefaultPress;
}

public sealed record MappingProfile
{
    public static MappingProfile Default { get; } = new();

    public AxisBinding LeftX { get; init; } = new() { Target = AxisTarget.LeftX };
    public AxisBinding LeftY { get; init; } = new() { Target = AxisTarget.LeftY };
    public AxisBinding RightX { get; init; } = new() { Target = AxisTarget.RightX };
    public AxisBinding RightY { get; init; } = new() { Target = AxisTarget.RightY };
    public AxisBinding Dial { get; init; } = new();

    public PadButton Fn { get; init; }
    public PadButton PhotoVideo { get; init; }
    public PadButton ReturnHome { get; init; }
    public PadButton Capture { get; init; }

    // Pulsed when the switch moves and once on connect, never held
    public PadButton ModeC { get; init; }
    public PadButton ModeN { get; init; }
    public PadButton ModeS { get; init; }

    public AxisBinding Get(AnalogInput input) => input switch
    {
        AnalogInput.LeftX => LeftX,
        AnalogInput.LeftY => LeftY,
        AnalogInput.RightX => RightX,
        AnalogInput.RightY => RightY,
        _ => Dial,
    };

    public MappingProfile With(AnalogInput input, AxisBinding binding) => input switch
    {
        AnalogInput.LeftX => this with { LeftX = binding },
        AnalogInput.LeftY => this with { LeftY = binding },
        AnalogInput.RightX => this with { RightX = binding },
        AnalogInput.RightY => this with { RightY = binding },
        _ => this with { Dial = binding },
    };

    public PadButton Get(DigitalInput input) => input switch
    {
        DigitalInput.Fn => Fn,
        DigitalInput.PhotoVideo => PhotoVideo,
        DigitalInput.ReturnHome => ReturnHome,
        DigitalInput.Capture => Capture,
        DigitalInput.ModeC => ModeC,
        DigitalInput.ModeN => ModeN,
        _ => ModeS,
    };

    public MappingProfile With(DigitalInput input, PadButton button) => input switch
    {
        DigitalInput.Fn => this with { Fn = button },
        DigitalInput.PhotoVideo => this with { PhotoVideo = button },
        DigitalInput.ReturnHome => this with { ReturnHome = button },
        DigitalInput.Capture => this with { Capture = button },
        DigitalInput.ModeC => this with { ModeC = button },
        DigitalInput.ModeN => this with { ModeN = button },
        _ => this with { ModeS = button },
    };
}
