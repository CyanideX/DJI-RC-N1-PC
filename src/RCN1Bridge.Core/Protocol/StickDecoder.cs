using System.Buffers.Binary;

namespace RCN1Bridge.Core.Protocol;

public readonly record struct RawSticks(
    ushort RightH, ushort RightV, ushort LeftH, ushort LeftV, ushort Dial, bool HasDial);

public readonly record struct ChannelSlot(byte Tag, ushort Value);

public static class StickDecoder
{
    public const int CompactFrameLength = 21;
    public const int ExtendedFrameLength = 38;

    // Offsets from v1, which matched on size alone. The 21 B frame turned out to be the RC's
    // unsolicited 06/26 AETR push (roll, pitch, throttle, yaw), not a poll reply.
    public static bool TryDecode(DumlFrame frame, out RawSticks sticks)
    {
        sticks = default;
        if (frame.CommandSet != RcCommands.RcCommandSet)
            return false;

        var raw = frame.Raw;
        switch (raw.Length)
        {
            case CompactFrameLength when frame.CommandId == RcCommands.AetrPush:
                sticks = new RawSticks(
                    RightH: U16(raw, 11),
                    RightV: U16(raw, 13),
                    LeftH: U16(raw, 17),
                    LeftV: U16(raw, 15),
                    Dial: 1024,
                    HasDial: false);
                return true;

            case ExtendedFrameLength when frame.CommandId == RcCommands.GetChannels:
                sticks = new RawSticks(
                    RightH: U16(raw, 13),
                    RightV: U16(raw, 16),
                    LeftH: U16(raw, 22),
                    LeftV: U16(raw, 19),
                    Dial: U16(raw, 25),
                    HasDial: true);
                return true;

            default:
                return false;
        }
    }

    // Byte 11 then 8 slots of (tag, u16); confirmed on an RC-N1. v1 only read slots 0-4.
    public const int SlotCount = 8;

    public static int DecodeSlots(ReadOnlySpan<byte> raw, Span<ChannelSlot> slots)
    {
        if (raw.Length != ExtendedFrameLength || raw[9] != RcCommands.RcCommandSet || raw[10] != RcCommands.GetChannels)
            return 0;
        int count = Math.Min(SlotCount, slots.Length);
        for (int i = 0; i < count; i++)
            slots[i] = new ChannelSlot(raw[12 + i * 3], U16(raw, 13 + i * 3));
        return count;
    }

    private static ushort U16(ReadOnlySpan<byte> raw, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(raw[offset..]);
}
