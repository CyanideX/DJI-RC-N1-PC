using RCN1Bridge.Core.Input;

namespace RCN1Bridge.Core.Output;

// Bit values match XUSB, so the whole set goes to the driver in one call
[Flags]
public enum PadButton : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

public readonly record struct PadReport(
    short LeftX, short LeftY, short RightX, short RightY, byte LeftTrigger, byte RightTrigger, PadButton Buttons)
{
    public static PadReport Neutral { get; } = default;

    public static PadReport FromSticks(in ProcessedInput input) => new(
        StickProcessor.ToAxis(input.LeftX),
        StickProcessor.ToAxis(input.LeftY),
        StickProcessor.ToAxis(input.RightX),
        StickProcessor.ToAxis(input.RightY),
        0, 0, PadButton.None);
}
