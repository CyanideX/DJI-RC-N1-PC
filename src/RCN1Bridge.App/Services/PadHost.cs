using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Output;

namespace RCN1Bridge.App.Services;

// Lives for the whole app run so the game never sees the controller vanish and come back
public sealed class PadHost : IGamepadOutput, IDisposable
{
    public const string DriverDownloadUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";

    private readonly Lock _gate = new();
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private PadReport _sent;
    private PadHold _hold;

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
            if (!Enabled)
                return false;
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

    public void Submit(in PadReport report)
    {
        lock (_gate)
            Send(report);
    }

    public void SubmitNeutral()
    {
        lock (_gate)
            Send(PadReport.Neutral);
    }

    public void SetHold(PadHold hold, bool on)
    {
        lock (_gate)
        {
            _hold = on ? _hold | hold : _hold & ~hold;
            if (_hold != PadHold.None)
                Send(PadReport.Neutral);
        }
    }

    // Off removes the controller from Windows entirely, so games and their prompts never see it
    public bool Enabled { get; private set; } = true;

    public void SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
            return;
        Enabled = enabled;
        if (enabled)
            TryConnect();
        else
        {
            Dispose();
            Problem = null;
            Log.Info("Virtual Xbox 360 controller turned off");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _sent = default;
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

    private void Send(PadReport report)
    {
        if (_hold != PadHold.None)
            report = PadReport.Neutral;
        if (_pad is null || report == _sent)
            return;
        try
        {
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, report.LeftX);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, report.LeftY);
            _pad.SetAxisValue(Xbox360Axis.RightThumbX, report.RightX);
            _pad.SetAxisValue(Xbox360Axis.RightThumbY, report.RightY);
            _pad.SetSliderValue(Xbox360Slider.LeftTrigger, report.LeftTrigger);
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, report.RightTrigger);
            _pad.SetButtonsFull((ushort)report.Buttons);
            _pad.SubmitReport();
            _sent = report;
        }
        catch (Exception ex)
        {
            Log.Error("Virtual controller report failed", ex);
        }
    }
}
