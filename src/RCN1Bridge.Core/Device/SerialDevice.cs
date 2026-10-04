using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RCN1Bridge.Core.Device;

// Raw Win32 instead of System.IO.Ports, whose background event thread can take the whole
// process down when a USB serial device is yanked. Overlapped so a blocked read never stalls a write.
public sealed unsafe class SerialDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ManualResetEvent _readDone = new(false);
    private readonly ManualResetEvent _writeDone = new(false);

    private SerialDevice(string portName, SafeFileHandle handle)
    {
        PortName = portName;
        _handle = handle;
    }

    public string PortName { get; }

    public static SerialDevice Open(string portName, int baudRate = 115200, int readTimeoutMs = 50)
    {
        var handle = CreateFileW(@"\\.\" + portName, GenericRead | GenericWrite, 0, 0, OpenExisting, FileFlagOverlapped, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new SerialOpenException(portName, error);
        }

        try
        {
            var dcb = new Dcb { Length = (uint)sizeof(Dcb) };
            Check(GetCommState(handle, &dcb), "GetCommState");
            dcb.BaudRate = (uint)baudRate;
            // fBinary, DTR on, RTS on. pyserial asserts both by default and v1 worked with that;
            // some CDC devices stay silent without DTR.
            dcb.Flags = 1u | (1u << 4) | (1u << 12);
            dcb.ByteSize = 8;
            dcb.Parity = 0;
            dcb.StopBits = 0;
            Check(SetCommState(handle, &dcb), "SetCommState");

            // MAXDWORD interval + multiplier: return as soon as anything arrives, or after the constant
            var timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = uint.MaxValue,
                ReadTotalTimeoutMultiplier = uint.MaxValue,
                ReadTotalTimeoutConstant = (uint)readTimeoutMs,
                WriteTotalTimeoutConstant = 500,
            };
            Check(SetCommTimeouts(handle, &timeouts), "SetCommTimeouts");
            PurgeComm(handle, PurgeRxClear | PurgeTxClear);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new SerialDevice(portName, handle);
    }

    // Returns 0 when nothing arrived within the read timeout
    public int Read(Span<byte> buffer)
    {
        var overlapped = new NativeOverlapped { EventHandle = _readDone.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* data = buffer)
        {
            if (!ReadFile(_handle, data, (uint)buffer.Length, null, &overlapped))
                ThrowUnlessPending("read");
            return (int)WaitFor(&overlapped, "read");
        }
    }

    public void Write(ReadOnlySpan<byte> buffer)
    {
        var overlapped = new NativeOverlapped { EventHandle = _writeDone.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* data = buffer)
        {
            if (!WriteFile(_handle, data, (uint)buffer.Length, null, &overlapped))
                ThrowUnlessPending("write");
            if (WaitFor(&overlapped, "write") != buffer.Length)
                throw new IOException($"Write to {PortName} timed out.");
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        _readDone.Dispose();
        _writeDone.Dispose();
    }

    private uint WaitFor(NativeOverlapped* overlapped, string operation)
    {
        if (!GetOverlappedResult(_handle, overlapped, out uint transferred, true))
            throw Failure(operation, Marshal.GetLastPInvokeError());
        return transferred;
    }

    private void ThrowUnlessPending(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        if (error != ErrorIoPending)
            throw Failure(operation, error);
    }

    private IOException Failure(string operation, int error) =>
        new($"Serial {operation} on {PortName} failed: {new Win32Exception(error).Message} ({error})", error);

    private static void Check(bool ok, string call)
    {
        if (!ok)
            throw new IOException($"{call} failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PurgeTxClear = 0x0004;
    private const uint PurgeRxClear = 0x0008;
    private const int ErrorIoPending = 997;

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length;
        public uint BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle handle, Dcb* dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle handle, Dcb* dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle handle, CommTimeouts* timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PurgeComm(SafeFileHandle handle, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte* buffer, uint count, uint* read, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte* buffer, uint count, uint* written, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* overlapped, out uint transferred, bool wait);
}

public sealed class SerialOpenException(string portName, int error)
    : IOException(Describe(portName, error), error)
{
    public bool InUse => HResult is 5 or 32;

    private static string Describe(string portName, int error) => error switch
    {
        5 or 32 => $"{portName} is in use by another program. Close DJI Assistant, the old Python bridge, or anything else using it.",
        2 => $"{portName} disappeared before it could be opened.",
        _ => $"Couldn't open {portName}: {new Win32Exception(error).Message} ({error})",
    };
}
