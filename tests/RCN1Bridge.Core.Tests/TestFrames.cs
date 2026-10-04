using System.Buffers.Binary;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Tests;

internal static class TestFrames
{
    public static byte[] Compact(ushort rh, ushort rv, ushort lv, ushort lh)
    {
        var payload = new byte[8];
        Put(payload, 0, rh);
        Put(payload, 2, rv);
        Put(payload, 4, lv);
        Put(payload, 6, lh);
        return Reply(payload, cmdId: RcCommands.AetrPush);
    }

    public static byte[] Extended(ushort rh, ushort rv, ushort lv, ushort lh, ushort dial)
    {
        var payload = new byte[25];
        Put(payload, 2, rh);
        Put(payload, 5, rv);
        Put(payload, 8, lv);
        Put(payload, 11, lh);
        Put(payload, 14, dial);
        return Reply(payload);
    }

    public static byte[] Buttons(ushort bits)
    {
        var payload = new byte[ButtonDecoder.FrameLength - DumlPacket.MinLength];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(28 - DumlPacket.HeaderLength), bits);
        return DumlPacket.Build(RcCommands.RcAddress, RcCommands.PcAddress, 1, 0x80, RcCommands.RcCommandSet, RcCommands.GetButtons, payload);
    }

    public static byte[] Reply(byte[] payload, byte cmdId = RcCommands.GetChannels, ushort seq = 0x1234) =>
        DumlPacket.Build(RcCommands.RcAddress, RcCommands.PcAddress, seq, 0x80, RcCommands.RcCommandSet, cmdId, payload);

    public static bool HasValidCrcs(ReadOnlySpan<byte> frame) =>
        frame.Length >= DumlPacket.MinLength
        && frame[0] == DumlPacket.StartByte
        && Crc.Crc8(frame[..3]) == frame[3]
        && (BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]) & DumlPacket.MaxLength) == frame.Length
        && Crc.Crc16(frame[..^2]) == BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);

    private static void Put(byte[] buffer, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value);
}
