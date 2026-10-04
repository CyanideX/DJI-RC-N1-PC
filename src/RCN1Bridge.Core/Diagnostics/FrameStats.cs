using System.Diagnostics;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Diagnostics;

public sealed record FrameStat(
    byte Sender, byte Receiver, byte CommandSet, byte CommandId, int Length, long Count, byte[] LastFrame, long[] ChangedAt);

// Per-byte min/max over a capture window, plus the last frame seen in it
public sealed record ByteWindow(
    byte Sender, byte Receiver, byte CommandSet, byte CommandId, int Length, byte[] Min, byte[] Max, byte[] Sample)
{
    public string Key => $"{CommandSet:X2}/{CommandId:X2} {Sender:X2}>{Receiver:X2} {Length} B";
}

// Written by the reader thread per frame, read by the UI a few times a second
public sealed class FrameStats
{
    private sealed class Entry(int length)
    {
        public readonly byte[] LastFrame = new byte[length];
        public readonly long[] ChangedAt = new long[length];
        public readonly byte[] WindowMin = new byte[length];
        public readonly byte[] WindowMax = new byte[length];
        public bool WindowSeen;
        public long Count;
    }

    private readonly record struct Id(byte Sender, byte Receiver, byte CommandSet, byte CommandId, int Length) : IComparable<Id>
    {
        private long Order => (long)CommandSet << 48 | (long)CommandId << 40 | (long)Sender << 32 | (long)Receiver << 24 | (uint)Length;
        public int CompareTo(Id other) => Order.CompareTo(other.Order);
    }

    private readonly Dictionary<Id, Entry> _entries = [];
    private readonly Lock _gate = new();
    private bool _windowOpen;

    // Sequence number and body CRC change every frame, so they say nothing about the controls
    public static bool IsNoiseByte(int index, int length) => index is 6 or 7 || index >= length - 2;

    public void Record(DumlFrame frame)
    {
        var id = new Id(frame.Sender, frame.Receiver, frame.CommandSet, frame.CommandId, frame.Length);
        var raw = frame.Raw;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                _entries[id] = entry = new Entry(frame.Length);
                raw.CopyTo(entry.LastFrame);
            }
            else
            {
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] != entry.LastFrame[i])
                    {
                        entry.LastFrame[i] = raw[i];
                        entry.ChangedAt[i] = now;
                    }
                }
            }
            entry.Count++;

            if (_windowOpen)
            {
                if (!entry.WindowSeen)
                {
                    raw.CopyTo(entry.WindowMin);
                    raw.CopyTo(entry.WindowMax);
                    entry.WindowSeen = true;
                }
                else
                {
                    for (int i = 0; i < raw.Length; i++)
                    {
                        if (raw[i] < entry.WindowMin[i]) entry.WindowMin[i] = raw[i];
                        if (raw[i] > entry.WindowMax[i]) entry.WindowMax[i] = raw[i];
                    }
                }
            }
        }
    }

    public void BeginWindow()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
                entry.WindowSeen = false;
            _windowOpen = true;
        }
    }

    // Messages slower than the window (06/1E is ~0.5/s) fall back to their last frame,
    // otherwise they'd look like they only exist in whichever capture caught one
    public IReadOnlyList<ByteWindow> EndWindow()
    {
        lock (_gate)
        {
            _windowOpen = false;
            if (!_entries.Values.Any(e => e.WindowSeen))
                return [];
            return _entries
                .OrderBy(e => e.Key)
                .Select(e => new ByteWindow(
                    e.Key.Sender, e.Key.Receiver, e.Key.CommandSet, e.Key.CommandId, e.Key.Length,
                    (byte[])(e.Value.WindowSeen ? e.Value.WindowMin : e.Value.LastFrame).Clone(),
                    (byte[])(e.Value.WindowSeen ? e.Value.WindowMax : e.Value.LastFrame).Clone(),
                    (byte[])e.Value.LastFrame.Clone()))
                .ToArray();
        }
    }

    public IReadOnlyList<FrameStat> Snapshot()
    {
        lock (_gate)
        {
            return _entries
                .OrderBy(e => e.Key)
                .Select(e => new FrameStat(
                    e.Key.Sender, e.Key.Receiver, e.Key.CommandSet, e.Key.CommandId, e.Key.Length,
                    e.Value.Count, (byte[])e.Value.LastFrame.Clone(), (long[])e.Value.ChangedAt.Clone()))
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }
}
