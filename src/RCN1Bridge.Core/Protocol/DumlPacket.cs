using System.Buffers.Binary;

namespace RCN1Bridge.Core.Protocol;

public static class DumlPacket
{
    public const byte StartByte = 0x55;
    public const int HeaderLength = 11;
    public const int MinLength = HeaderLength + 2;
    public const int MaxLength = 0x3FF;

    // Upper 6 bits of the length field; version 1 puts 0x04 in byte 2
    private const int Version = 1;

    public static int GetLength(int payloadLength) => MinLength + payloadLength;

    public static int Write(
        Span<byte> destination, byte sender, byte receiver, ushort sequence,
        byte commandType, byte commandSet, byte commandId, ReadOnlySpan<byte> payload)
    {
        int length = GetLength(payload.Length);
        if (length > MaxLength)
            throw new ArgumentException($"Payload too long ({payload.Length} bytes).", nameof(payload));
        if (destination.Length < length)
            throw new ArgumentException("Destination too small.", nameof(destination));

        var packet = destination[..length];
        packet[0] = StartByte;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[1..], (ushort)(length | (Version << 10)));
        packet[3] = Crc.Crc8(packet[..3]);
        packet[4] = sender;
        packet[5] = receiver;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..], sequence);
        packet[8] = commandType;
        packet[9] = commandSet;
        packet[10] = commandId;
        payload.CopyTo(packet[HeaderLength..]);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[^2..], Crc.Crc16(packet[..^2]));
        return length;
    }

    public static byte[] Build(
        byte sender, byte receiver, ushort sequence,
        byte commandType, byte commandSet, byte commandId, ReadOnlySpan<byte> payload = default)
    {
        var packet = new byte[GetLength(payload.Length)];
        Write(packet, sender, receiver, sequence, commandType, commandSet, commandId, payload);
        return packet;
    }
}
