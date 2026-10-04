using System.Buffers.Binary;

namespace RCN1Bridge.Core.Protocol;

// Not thread-safe. One parser per connection, fed from the reader thread.
public sealed class DumlParser
{
    private readonly byte[] _buffer = new byte[4096];
    private readonly FrameHandler _onFrame;
    private int _start;
    private int _end;

    public DumlParser(FrameHandler onFrame) => _onFrame = onFrame;

    public long FrameCount { get; private set; }

    // Header CRC passed but the body didn't
    public long BadFrameCount { get; private set; }

    public long SkippedByteCount { get; private set; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            Compact();
            int count = Math.Min(data.Length, _buffer.Length - _end);
            data[..count].CopyTo(_buffer.AsSpan(_end));
            _end += count;
            data = data[count..];
            Drain();
        }
    }

    public void Reset()
    {
        _start = _end = 0;
    }

    private void Drain()
    {
        while (true)
        {
            var pending = _buffer.AsSpan(_start, _end - _start);
            int sync = pending.IndexOf(DumlPacket.StartByte);
            if (sync < 0)
            {
                SkippedByteCount += pending.Length;
                _start = _end;
                return;
            }

            SkippedByteCount += sync;
            _start += sync;
            pending = pending[sync..];

            if (pending.Length < 4)
                return;

            int length = BinaryPrimitives.ReadUInt16LittleEndian(pending[1..]) & DumlPacket.MaxLength;
            if (length < DumlPacket.MinLength || Crc.Crc8(pending[..3]) != pending[3])
            {
                SkipOne();
                continue;
            }

            if (pending.Length < length)
                return;

            var frame = pending[..length];
            if (Crc.Crc16(frame[..^2]) != BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]))
            {
                BadFrameCount++;
                SkipOne();
                continue;
            }

            _start += length;
            FrameCount++;
            _onFrame(new DumlFrame(frame));
        }
    }

    private void SkipOne()
    {
        _start++;
        SkippedByteCount++;
    }

    private void Compact()
    {
        if (_start == 0)
            return;
        int remaining = _end - _start;
        _buffer.AsSpan(_start, remaining).CopyTo(_buffer);
        _start = 0;
        _end = remaining;
    }
}
