using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Output;

// Shared-memory block for in-game readers such as the Drone mod's RED4ext plugin. The layout is a
// public contract documented in docs/GameLink.md: bump Version if any offset moves. Only the engine's
// reader thread calls Publish, which keeps the seqlock single-writer.
public sealed unsafe class GameLink : IDisposable
{
    public const string DefaultName = @"Local\RCN1Bridge.GameLink";
    public const int Size = 128;
    public const uint Magic = 0x314E4352; // "RCN1"
    public const uint Version = 1;

    public const int SequenceOffset = 8;
    public const int FlagsOffset = 12;
    public const int TimestampOffset = 16;
    public const int AxesOffset = 24;
    public const int ButtonWordOffset = 44;
    public const int ModeOffset = 46;
    public const int ButtonsOffset = 47;
    public const int RawOffset = 48;
    public const int HeartbeatOffset = 64;

    public const uint FlagLive = 1, FlagButtons = 2, FlagDial = 4;
    public const byte ButtonFn = 1, ButtonPhotoVideo = 2, ButtonReturnHome = 4, ButtonCapture = 8;

    public static readonly TimeSpan ReaderTimeout = TimeSpan.FromSeconds(2);

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _block;
    private uint _sequence;

    private GameLink(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _file = file;
        _view = view;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _block = pointer + view.PointerOffset;
        new Span<byte>(_block, Size).Clear();
        *(uint*)(_block + 4) = Version;
        Volatile.Write(ref *(uint*)_block, Magic);
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

    public bool ReaderConnected
    {
        get
        {
            long beat = Volatile.Read(ref *(long*)(_block + HeartbeatOffset));
            return beat != 0 && Stopwatch.GetElapsedTime(beat) < ReaderTimeout;
        }
    }

    public void Publish(bool live, in RawSticks raw, in ProcessedInput input, RcButtons? buttons)
    {
        uint* sequence = (uint*)(_block + SequenceOffset);
        Volatile.Write(ref *sequence, ++_sequence);
        Interlocked.MemoryBarrier();

        uint flags = (live ? FlagLive : 0) | (buttons is null ? 0 : FlagButtons) | (raw.HasDial ? FlagDial : 0);
        *(uint*)(_block + FlagsOffset) = flags;
        *(long*)(_block + TimestampOffset) = Stopwatch.GetTimestamp();

        float* axes = (float*)(_block + AxesOffset);
        axes[0] = input.LeftX;
        axes[1] = input.LeftY;
        axes[2] = input.RightX;
        axes[3] = input.RightY;
        axes[4] = input.Dial;

        var b = buttons ?? default;
        *(ushort*)(_block + ButtonWordOffset) = b.Bits;
        _block[ModeOffset] = (byte)(b.Mode switch
        {
            FlightMode.Cine => 1,
            FlightMode.Normal => 2,
            FlightMode.Sport => 3,
            _ => 0,
        });
        _block[ButtonsOffset] = (byte)((b.Fn ? ButtonFn : 0) | (b.PhotoVideo ? ButtonPhotoVideo : 0)
            | (b.ReturnHome ? ButtonReturnHome : 0) | (b.Capture ? ButtonCapture : 0));

        ushort* rawAxes = (ushort*)(_block + RawOffset);
        rawAxes[0] = raw.LeftH;
        rawAxes[1] = raw.LeftV;
        rawAxes[2] = raw.RightH;
        rawAxes[3] = raw.RightV;
        rawAxes[4] = raw.Dial;

        Volatile.Write(ref *sequence, ++_sequence);
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
