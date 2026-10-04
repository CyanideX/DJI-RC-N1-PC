using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RCN1Bridge.App.Services;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Protocol;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace RCN1Bridge.App.Pages;

public partial class HomePage : UserControl
{
    private readonly DispatcherTimer _frameTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private long _lastSequence = -1;
    private long _lastFrames;
    private long _lastStatsTimestamp;
    private (LinkStatus Status, bool PadOk, int? Player)? _rendered;
    private RcButtons? _renderedButtons;

    public HomePage()
    {
        InitializeComponent();
        SendToggle.IsChecked = App.Engine.OutputEnabled;
        _frameTimer.Tick += (_, _) => RenderInput();
        _statsTimer.Tick += (_, _) =>
        {
            RenderStats();
            RenderStatus();
        };
        Loaded += (_, _) =>
        {
            App.Engine.StatusChanged += OnStatusChanged;
            RenderStatus();
            RenderStats();
            _frameTimer.Start();
            _statsTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            App.Engine.StatusChanged -= OnStatusChanged;
            _frameTimer.Stop();
            _statsTimer.Stop();
        };
    }

    private void OnStatusChanged(LinkStatus _) => Dispatcher.BeginInvoke(RenderStatus);

    private void RenderStatus()
    {
        var s = App.Engine.Status;
        bool padOk = App.Pad.IsConnected;
        int? player = s.State == LinkState.Live ? App.Pad.PlayerNumber : null;
        if (_rendered == (s, padOk, player))
            return;
        _rendered = (s, padOk, player);

        string port = s.Port?.PortName ?? "";
        (StatusBar.Severity, StatusBar.Title, StatusBar.Message) = s.State switch
        {
            LinkState.Searching when s.Problem is not null =>
                (InfoBarSeverity.Error, "Can't use the controller", s.Problem),
            LinkState.Searching =>
                (InfoBarSeverity.Informational, "Looking for the controller",
                    "Plug the cable into the bottom USB-C port on the RC, then power the controller on."),
            LinkState.WaitingForData when s.SuggestReplug =>
                (InfoBarSeverity.Warning, "Found the controller, but it isn't sending stick data",
                    "Unplug the RC, plug it back in, then power it on. If that doesn't help, copy the report on the Diagnostics page."),
            LinkState.WaitingForData =>
                (InfoBarSeverity.Warning, "Found the controller, waiting for stick data",
                    $"Connected on {port}. Asking the RC to start sending stick positions."),
            LinkState.Live =>
                (InfoBarSeverity.Success, "Connected",
                    padOk
                        ? $"{port}. Games see it as a virtual Xbox 360 controller{(player is null ? "" : $" in slot {player}")}."
                        : $"{port}. Games can't see it until the driver below is installed."),
            LinkState.Stalled =>
                (InfoBarSeverity.Error, "Stick data stopped",
                    "The sticks are centred until the controller responds again. Check the cable and that the RC is on."),
            _ => (InfoBarSeverity.Informational, "Stopped", ""),
        };

        bool searching = s.State == LinkState.Searching;
        StatusActions.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        LivePanel.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        Checklist.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;

        DriverBar.IsOpen = !padOk;
        DriverBar.Visibility = padOk ? Visibility.Collapsed : Visibility.Visible;
        DriverBar.Message = App.Pad.Problem ?? "";
        DriverActions.Visibility = DriverBar.Visibility;

        if (searching)
            BuildChecklist(s, padOk);
    }

    private void BuildChecklist(LinkStatus s, bool padOk)
    {
        ChecklistItems.Children.Clear();
        ChecklistItems.Children.Add(padOk
            ? Row(Check.Ok, "Virtual controller driver", "ViGEmBus is installed.")
            : Row(Check.Failed, "Virtual controller driver", "Not installed. Use the download button above, then click Check again."));

        string ports = s.Ports.Count == 0
            ? "No COM ports found."
            : string.Join(Environment.NewLine, s.Ports.Select(p => $"{p.PortName,-6} {p.Name}"));

        if (s.Problem is not null)
            ChecklistItems.Children.Add(Row(Check.Failed, "Controller connected", s.Problem, ports));
        else if (s.Ports.Any(p => p.IsDji))
            ChecklistItems.Children.Add(Row(Check.Failed, "Controller connected",
                "A DJI device is plugged in, but not its control port. Make sure the cable is in the bottom USB-C port on the RC.", ports));
        else
            ChecklistItems.Children.Add(Row(Check.Failed, "Controller connected",
                "Plug the cable into the bottom USB-C port on the RC, then power the controller on. The top port only charges a phone. "
                + "If it still isn't found, install DJI Assistant 2 once for the USB driver.", ports));

        ChecklistItems.Children.Add(Row(Check.Pending, "Stick data", "Checked once the controller is connected."));
    }

    private enum Check { Ok, Failed, Pending }

    private static FrameworkElement Row(Check state, string title, string detail, string? mono = null)
    {
        var icon = new SymbolIcon
        {
            Symbol = state switch
            {
                Check.Ok => SymbolRegular.CheckmarkCircle24,
                Check.Failed => SymbolRegular.DismissCircle24,
                _ => SymbolRegular.Clock24,
            },
            Filled = true,
            FontSize = 20,
            Margin = new Thickness(0, 1, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(ForegroundProperty, state switch
        {
            Check.Ok => "SystemFillColorSuccessBrush",
            Check.Failed => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorCautionBrush",
        });

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontTypography = FontTypography.BodyStrong });
        text.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Appearance = TextColor.Secondary, Margin = new Thickness(0, 2, 0, 0) });
        if (mono is not null)
        {
            var list = new TextBlock { Text = mono, FontTypography = FontTypography.Caption, Appearance = TextColor.Tertiary, Margin = new Thickness(0, 8, 0, 0) };
            list.SetResourceReference(FontFamilyProperty, "MonoFont");
            text.Children.Add(list);
        }

        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 12) };
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    private void RenderInput()
    {
        var input = App.Engine.Input;
        if (input.Sequence == _lastSequence)
            return;
        _lastSequence = input.Sequence;

        var p = input.Processed;
        var r = input.Raw;
        var processor = App.Engine.Processor;
        LeftStick.Update(p.LeftX, p.LeftY, processor.Left.Deadzone);
        RightStick.Update(p.RightX, p.RightY, processor.Right.Deadzone);
        LeftReadout.Text = $"X {Axis(p.LeftX)}  Y {Axis(p.LeftY)}\nraw {r.LeftH} / {r.LeftV}";
        RightReadout.Text = $"X {Axis(p.RightX)}  Y {Axis(p.RightY)}\nraw {r.RightH} / {r.RightV}";

        RenderButtons(input.Buttons);
        Dial.Update(p.Dial);
        DialReadout.Text = !r.HasDial
            ? "This controller hasn't reported the dial yet."
            : $"{Axis(p.Dial)}  raw {r.Dial}";
    }

    private void RenderButtons(RcButtons? buttons)
    {
        if (_renderedButtons == buttons && ModeSwitch.Children.Count > 0)
            return;
        _renderedButtons = buttons;

        if (ModeSwitch.Children.Count == 0)
        {
            foreach (var label in new[] { "C", "N", "S" })
                ModeSwitch.Children.Add(Chip(label));
            foreach (var label in new[] { "Fn", "Photo/Video", "RTH", "Capture" })
                ButtonChips.Children.Add(Chip(label));
        }

        ButtonsMissing.Visibility = buttons is null ? Visibility.Visible : Visibility.Collapsed;
        var b = buttons ?? default;
        bool[] modes = [b.Mode == FlightMode.Cine, b.Mode == FlightMode.Normal, b.Mode == FlightMode.Sport];
        bool[] pressed = [b.Fn, b.PhotoVideo, b.ReturnHome, b.Capture];
        for (int i = 0; i < modes.Length; i++)
            SetChip((Border)ModeSwitch.Children[i], buttons is not null && modes[i]);
        for (int i = 0; i < pressed.Length; i++)
            SetChip((Border)ButtonChips.Children[i], buttons is not null && pressed[i]);
    }

    private static Border Chip(string text)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new System.Windows.Controls.TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center },
        };
        SetChip(chip, false);
        return chip;
    }

    private static void SetChip(Border chip, bool on)
    {
        var text = (System.Windows.Controls.TextBlock)chip.Child;
        chip.SetResourceReference(Border.BackgroundProperty, on ? "AccentFillColorDefaultBrush" : "SubtleFillColorTransparentBrush");
        chip.SetResourceReference(Border.BorderBrushProperty, on ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, on ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static string Axis(float value) => $"{StickProcessor.ToAxis(value),6:+0;-0;0}";

    private void RenderStats()
    {
        if (SendToggle.IsChecked != App.Engine.OutputEnabled)
            SendToggle.IsChecked = App.Engine.OutputEnabled;

        long now = Stopwatch.GetTimestamp();
        long frames = App.Engine.StickFrameCount;
        if (_lastStatsTimestamp != 0)
        {
            double seconds = Stopwatch.GetElapsedTime(_lastStatsTimestamp, now).TotalSeconds;
            double rate = seconds > 0 ? Math.Max(0, frames - _lastFrames) / seconds : 0;
            RateText.Text = $"{rate:0} Hz";
        }
        _lastFrames = frames;
        _lastStatsTimestamp = now;

        double? median = App.Engine.Latency.Median();
        LatencyText.Text = median is null ? "n/a" : $"{median:0.00} ms";
        BadText.Text = App.Engine.BadFrameCount.ToString("N0");
        PadText.Text = !App.Pad.IsConnected ? "Not available"
            : App.Pad.PlayerNumber is int n ? $"Slot {n}" : "Connected";
    }

    private void OnSendToggled(object sender, RoutedEventArgs e)
    {
        bool on = SendToggle.IsChecked == true;
        if (App.Engine.OutputEnabled == on)
            return;
        App.Engine.OutputEnabled = on;
        Log.Info(on ? "Output resumed" : "Output paused");
        App.SyncSendToGame(on);
    }

    private void OnScanClicked(object sender, RoutedEventArgs e) => App.Engine.RequestScan();

    private void OnDriverDownloadClicked(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(PadHost.DriverDownloadUrl) { UseShellExecute = true });

    private void OnDriverRetryClicked(object sender, RoutedEventArgs e)
    {
        App.Pad.TryConnect();
        _rendered = null;
        RenderStatus();
    }
}
