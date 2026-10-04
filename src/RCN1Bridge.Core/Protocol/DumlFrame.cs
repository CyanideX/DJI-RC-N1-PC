using System.Buffers.Binary;

namespace RCN1Bridge.Core.Protocol;

// Points into the parser's buffer; only valid inside the FrameHandler call
public readonly ref struct DumlFrame
{
    public DumlFrame(ReadOnlySpan<byte> raw) => Raw = raw;

    public ReadOnlySpan<byte> Raw { get; }

    public int Length => Raw.Length;
    public byte Sender => Raw[4];
    public byte Receiver => Raw[5];
    public ushort Sequence => BinaryPrimitives.ReadUInt16LittleEndian(Raw[6..]);
    public byte CommandType => Raw[8];
    public byte CommandSet => Raw[9];
    public byte CommandId => Raw[10];
    public ReadOnlySpan<byte> Payload => Raw[DumlPacket.HeaderLength..^2];
}

public delegate void FrameHandler(DumlFrame frame);
