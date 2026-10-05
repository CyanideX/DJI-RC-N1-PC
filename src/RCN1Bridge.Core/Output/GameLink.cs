using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Output;

public readonly record struct GameLinkReader(int ProcessId, string Name, bool Exclusive);

// Shared memory that game mods read. Public contract, see docs/GameLink.md: new fields bump Minor, anything
// that moves bumps Major and the block name. Only the reader thread calls Publish; the seqlock is single-writer.
public sealed unsafe class GameLink : IDisposable
{
    public const string DefaultName = @"Local\RCN1Bridge.GameLink.2";
    public const int Size = 256;
    public const uint Magic = 0x314E4352; // "RCN1"
    public const ushort Major = 2;
    public const ushort Minor = 0;
    public const uint DeviceRcN1 = 1;

    public const int MajorOffset = 4;
    public const int MinorOffset = 6;
    public const int SizeOffset = 8;
    public const int WriterPidOffset = 12;
    public const int WriterBeatOffset = 16;
    public const int DeviceOffset = 24;
    public const int SequenceOffset = 32;
    public const int FlagsOffset = 36;
    public const int TimestampOffset = 40;
    public const int SampleOffset = 48;
    public const int SwitchOffset = 52;
    public const int HeldOffset = 54;
    public const int AxesOffset = 56;
    public const int CalibratedOffset = 80;
    public const int PressOffset = 104;
    public const int RawOffset = 112;
    public const int ButtonWordOffset = 124;
    public const int SlotsOffset = 128;

    public const int SlotSize = 32;
    public const int SlotCount = 4;
    public const int SlotPidOffset = 0;
    public const int SlotFlagsOffset = 4;
    public const int SlotBeatOffset = 8;
    public const int SlotNameOffset = 16;
    public const int SlotNameLength = 16;

    public const uint FlagLive = 1, FlagButtons = 2, FlagDial = 4, FlagPaused = 8, FlagOff = 16;
    public const ushort ButtonFn = 1, ButtonPhotoVideo = 2, ButtonReturnHome = 4, ButtonCapture = 8;
    public const uint SlotExclusive = 1;

    public static readonly TimeSpan ReaderTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BeatInterval = TimeSpan.FromMilliseconds(250);

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _block;
    private readonly Timer _beat;
    private readonly Lock _beatGate = new();
    private readonly byte[] _presses = new byte[8];
    private bool _disposed;
    private uint _sequence;
    private uint _sample;
    private ushort _lastHeld;
    private volatile bool _exclusive;
    private GameLinkReader[] _readers = [];
    // pid+flags and the two name words per slot, so the snapshot is only rebuilt when a slot changes
    private readonly long[] _seen = new long[SlotCount * 3];

    private GameLink(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _file = file;
        _view = view;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _block = pointer + view.PointerOffset;

        // Slots survive a bridge restart, so a game that's already reading keeps its place
        new Span<byte>(_block, SlotsOffset).Clear();
        *(ushort*)(_block + MajorOffset) = Major;
        *(ushort*)(_block + MinorOffset) = Minor;
        *(uint*)(_block + SizeOffset) = Size;
        *(int*)(_block + WriterPidOffset) = Environment.ProcessId;
        *(uint*)(_block + DeviceOffset) = DeviceRcN1;
        *(long*)(_block + WriterBeatOffset) = Stopwatch.GetTimestamp();
        Volatile.Write(ref *(uint*)_block, Magic);

        _beat = new Timer(Beat, null, TimeSpan.Zero, BeatInterval);
    }

    public static GameLink? TryCreate(string name = DefaultName)
    {
        try
        {
            var file = MemoryMappedFile.CreateOrOpen(name, Size, MemoryMappedFileAccess.ReadWrite);
            return new GameLink(file, file.CreateViewAccessor(0, Size));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Game link unavailable: {ex.Message}");
            return null;
        }
    }

    // Both refreshed with the writer heartbeat, so the per-frame check is a field read
    public bool ExclusiveReaderAttached => _exclusive;

    public IReadOnlyList<GameLinkReader> Readers => Volatile.Read(ref _readers);

    // state is FlagLive, FlagPaused, FlagOff or 0; the button and dial flags are added here
    public void Publish(uint state, in RawSticks raw, in ProcessedInput tuned, in ProcessedInput calibrated, RcButtons? buttons)
    {
        ushort held = buttons is { } pressed ? Held(pressed) : (ushort)0;
        for (int bit = 0, rising = held & ~_lastHeld; rising != 0; bit++, rising >>= 1)
        {
            if ((rising & 1) != 0)
                _presses[bit]++;
        }
        _lastHeld = held;

        uint* sequence = (uint*)(_block + SequenceOffset);
        Volatile.Write(ref *sequence, ++_sequence);
        Interlocked.MemoryBarrier();

        *(uint*)(_block + FlagsOffset) = state | (buttons is null ? 0 : FlagButtons) | (raw.HasDial ? FlagDial : 0);
        *(long*)(_block + TimestampOffset) = Stopwatch.GetTimestamp();
        *(uint*)(_block + SampleOffset) = ++_sample;
        _block[SwitchOffset] = (byte)((buttons?.Mode ?? FlightMode.Unknown) switch
        {
            FlightMode.Cine => 1,
            FlightMode.Normal => 2,
            FlightMode.Sport => 3,
            _ => 0,
        });
        *(ushort*)(_block + HeldOffset) = held;
        WriteAxes(AxesOffset, tuned);
        WriteAxes(CalibratedOffset, calibrated);
        for (int i = 0; i < _presses.Length; i++)
            _block[PressOffset + i] = _presses[i];

        ushort* rawAxes = (ushort*)(_block + RawOffset);
        rawAxes[0] = raw.LeftH;
        rawAxes[1] = raw.LeftV;
        rawAxes[2] = raw.RightH;
        rawAxes[3] = raw.RightV;
        rawAxes[4] = raw.Dial;
        *(ushort*)(_block + ButtonWordOffset) = buttons?.Bits ?? 0;

        Volatile.Write(ref *sequence, ++_sequence);
    }

    public void Dispose()
    {
        lock (_beatGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            // Readers see the bridge gone at once instead of waiting out the heartbeat
            *(int*)(_block + WriterPidOffset) = 0;
            Volatile.Write(ref *(long*)(_block + WriterBeatOffset), 0);
        }
        _beat.Dispose();
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }

    private static ushort Held(RcButtons b) => (ushort)((b.Fn ? ButtonFn : 0) | (b.PhotoVideo ? ButtonPhotoVideo : 0)
        | (b.ReturnHome ? ButtonReturnHome : 0) | (b.Capture ? ButtonCapture : 0));

    private void WriteAxes(int offset, in ProcessedInput input)
    {
        float* axes = (float*)(_block + offset);
        axes[0] = input.LeftX;
        axes[1] = input.LeftY;
        axes[2] = input.RightX;
        axes[3] = input.RightY;
        axes[4] = input.Dial;
        axes[5] = 0f;
    }

    private void Beat(object? state)
    {
        lock (_beatGate)
        {
            if (_disposed)
                return;
            Volatile.Write(ref *(long*)(_block + WriterBeatOffset), Stopwatch.GetTimestamp());
            bool exclusive = false, changed = false;
            for (int i = 0; i < SlotCount; i++)
            {
                byte* slot = _block + SlotsOffset + i * SlotSize;
                bool fresh = Fresh(slot);
                exclusive |= fresh && (Volatile.Read(ref *(uint*)(slot + SlotFlagsOffset)) & SlotExclusive) != 0;
                changed |= See(i * 3, fresh ? *(long*)(slot + SlotPidOffset) : 0);
                changed |= See(i * 3 + 1, fresh ? *(long*)(slot + SlotNameOffset) : 0);
                changed |= See(i * 3 + 2, fresh ? *(long*)(slot + SlotNameOffset + 8) : 0);
            }
            _exclusive = exclusive;
            if (changed)
                Volatile.Write(ref _readers, Snapshot());
        }
    }

    private bool See(int index, long value)
    {
        if (_seen[index] == value)
            return false;
        _seen[index] = value;
        return true;
    }

    private GameLinkReader[] Snapshot()
    {
        var readers = new List<GameLinkReader>(SlotCount);
        for (int i = 0; i < SlotCount; i++)
        {
            byte* slot = _block + SlotsOffset + i * SlotSize;
            if (!Fresh(slot))
                continue;
            var name = new ReadOnlySpan<byte>(slot + SlotNameOffset, SlotNameLength);
            int end = name.IndexOf((byte)0);
            readers.Add(new GameLinkReader(*(int*)(slot + SlotPidOffset), Encoding.UTF8.GetString(end < 0 ? name : name[..end]),
                (*(uint*)(slot + SlotFlagsOffset) & SlotExclusive) != 0));
        }
        return [.. readers];
    }

    private static bool Fresh(byte* slot)
    {
        int pid = Volatile.Read(ref *(int*)(slot + SlotPidOffset));
        long beat = Volatile.Read(ref *(long*)(slot + SlotBeatOffset));
        return pid != 0 && beat != 0 && Stopwatch.GetElapsedTime(beat) < ReaderTimeout;
    }
}
