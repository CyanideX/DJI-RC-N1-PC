using System.Buffers.Binary;

namespace RCN1Bridge.Core.Protocol;

public enum FlightMode
{
    Unknown,
    Cine,
    Normal,
    Sport,
}

public readonly record struct RcButtons(ushort Bits, FlightMode Mode, bool Fn, bool PhotoVideo, bool Capture, bool ReturnHome);

public static class ButtonDecoder
{
    public const int FrameLength = 58;

    // Big-endian, unlike everything else in the frame
    private const int BitsOffset = 28;

    public static bool TryDecode(DumlFrame frame, out RcButtons buttons)
    {
        buttons = default;
        if (frame.CommandSet != RcCommands.RcCommandSet || frame.CommandId != RcCommands.GetButtons || frame.Length != FrameLength)
            return false;

        buttons = Decode(BinaryPrimitives.ReadUInt16BigEndian(frame.Raw[BitsOffset..]));
        return true;
    }

    public static RcButtons Decode(ushort bits) => new(
        bits,
        (bits & 0x3000) switch
        {
            0x0000 => FlightMode.Sport,
            0x1000 => FlightMode.Normal,
            0x2000 => FlightMode.Cine,
            _ => FlightMode.Unknown,
        },
        Fn: (bits & 0x0002) != 0,
        PhotoVideo: (bits & 0x0004) != 0,
        Capture: (bits & 0x0060) != 0,
        ReturnHome: (bits & 0x0080) != 0);
}
