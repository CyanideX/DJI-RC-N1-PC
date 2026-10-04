using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Output;

namespace RCN1Bridge.App.Services;

// Lives for the whole app run so the game never sees the controller vanish and come back
public sealed class PadHost : IGamepadOutput, IDisposable
{
    public const string DriverDownloadUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";

    private readonly Lock _gate = new();
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private short _lx, _ly, _rx, _ry;
    private bool _y, _b;

    public bool IsConnected
    {
        get { lock (_gate) return _pad is not null; }
    }

    public string? Problem { get; private set; }

    public bool TryConnect()
    {
        lock (_gate)
        {
            if (_pad is not null)
                return true;
            try
            {
                _client = new ViGEmClient();
                var pad = _client.CreateXbox360Controller();
                pad.AutoSubmitReport = false;
                pad.Connect();
                _pad = pad;
                Problem = null;
                Log.Info("Virtual Xbox 360 controller connected");
                return true;
            }
            catch (VigemBusNotFoundException)
            {
                Problem = "The ViGEmBus driver isn't installed, so games can't see the controller.";
            }
            catch (VigemBusVersionMismatchException)
            {
                Problem = "The ViGEmBus driver is too old. Install the latest version.";
            }
            catch (Exception ex)
            {
                Problem = $"Couldn't create the virtual controller: {ex.Message}";
            }
            Log.Warn(Problem);
            _client?.Dispose();
            _client = null;
            return false;
        }
    }

    // 1 to 4 once Windows has assigned a slot
    public int? PlayerNumber
    {
        get
        {
            lock (_gate)
            {
                if (_pad is null)
                    return null;
                try
                {
                    return _pad.UserIndex + 1;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }

    public void Submit(in ProcessedInput input)
    {
        short lx = StickProcessor.ToAxis(input.LeftX);
        short ly = StickProcessor.ToAxis(input.LeftY);
        short rx = StickProcessor.ToAxis(input.RightX);
        short ry = StickProcessor.ToAxis(input.RightY);
        lock (_gate)
            Send(lx, ly, rx, ry, input.DialUp, input.DialDown);
    }

    public void SubmitNeutral()
    {
        lock (_gate)
            Send(0, 0, 0, 0, false, false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_pad is not null)
            {
                try
                {
                    _pad.ResetReport();
                    _pad.SubmitReport();
                    _pad.Disconnect();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Virtual controller disconnect failed: {ex.Message}");
                }
            }
            _client?.Dispose();
            _pad = null;
            _client = null;
        }
    }

    private void Send(short lx, short ly, short rx, short ry, bool y, bool b)
    {
        if (_pad is null || (lx == _lx && ly == _ly && rx == _rx && ry == _ry && y == _y && b == _b))
            return;
        try
        {
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
            _pad.SetAxisValue(Xbox360Axis.RightThumbX, rx);
            _pad.SetAxisValue(Xbox360Axis.RightThumbY, ry);
            _pad.SetButtonState(Xbox360Button.Y, y);
            _pad.SetButtonState(Xbox360Button.B, b);
            _pad.SubmitReport();
            (_lx, _ly, _rx, _ry, _y, _b) = (lx, ly, rx, ry, y, b);
        }
        catch (Exception ex)
        {
            Log.Error("Virtual controller report failed", ex);
        }
    }
}
