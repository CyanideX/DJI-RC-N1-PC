using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RCN1Bridge.App.Services;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Protocol;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace RCN1Bridge.App.Pages;

public partial class DiagnosticsPage : UserControl
{
    private static readonly (string Name, string Hint)[] Steps =
    [
        (ButtonCapture.Rest, "Hands off, sticks centred, switch on N"),
        ("Mode C", "Flight mode switch on C"),
        ("Mode N", "Flight mode switch on N"),
        ("Mode S", "Flight mode switch on S"),
        ("Fn", "Hold the Fn (customisable) button"),
        ("Photo/Video", "Hold the photo/video toggle"),
        ("RTH", "Hold the pause/return-to-home button"),
        ("Capture", "Hold the photo/record button, top right"),
    ];

    private static readonly TimeSpan CaptureLength = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan HighlightFor = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, long> _previousCounts = [];
    private readonly Dictionary<string, string> _rates = [];
    private readonly Dictionary<string, Button> _stepButtons = [];
    private int _tick;
    private long _previousTimestamp;
    private long _previousFrames;
    private string? _lastLogLine;
    private bool _capturing;

    public DiagnosticsPage()
    {
        InitializeComponent();
        foreach (var (name, hint) in Steps)
        {
            var button = new Button { Content = name, ToolTip = hint, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += async (_, _) => await CaptureStep(name);
            _stepButtons[name] = button;
            StepButtons.Children.Add(button);
        }
        RefreshStepButtons();

        LogBox.PreviewMouseWheel += PassWheelAtEdges;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            _tick = 0;
            Refresh();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        bool slow = _tick++ % 5 == 0;
        if (slow)
            RefreshCounters();
        RefreshFrames();
        if (slow)
            RefreshLog();
    }

    private void RefreshCounters()
    {
        var engine = App.Engine;
        long now = Stopwatch.GetTimestamp();
        double seconds = _previousTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalSeconds;
        _previousTimestamp = now;

        long frames = engine.StickFrameCount;
        FramesText.Text = frames.ToString("N0");
        RateText.Text = seconds > 0 ? $"{Math.Max(0, frames - _previousFrames) / seconds:0}" : "0";
        _previousFrames = frames;
        LatencyText.Text = engine.Latency.Median() is double ms ? $"{ms:0.00} ms" : "n/a";
        BadText.Text = engine.BadFrameCount.ToString("N0");
        SkippedText.Text = engine.SkippedByteCount.ToString("N0");

        foreach (var f in engine.Frames.Snapshot())
        {
            string id = $"{f.CommandSet:X2}{f.CommandId:X2}{f.Sender:X2}{f.Receiver:X2}{f.Length}";
            if (seconds > 0 && _previousCounts.TryGetValue(id, out long before))
                _rates[id] = $"{(f.Count - before) / seconds:0}/s";
            _previousCounts[id] = f.Count;
        }
    }

    private void RefreshFrames()
    {
        var stats = App.Engine.Frames.Snapshot();
        NoFramesText.Visibility = stats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var hot = TryFindResource("AccentFillColorDefaultBrush") as Brush;
        var onHot = TryFindResource("TextOnAccentFillColorPrimaryBrush") as Brush;
        var noise = TryFindResource("TextFillColorTertiaryBrush") as Brush;
        var mono = (FontFamily)FindResource("MonoFont");
        long now = Stopwatch.GetTimestamp();

        FrameRows.Children.Clear();
        foreach (var f in stats)
        {
            string id = $"{f.CommandSet:X2}{f.CommandId:X2}{f.Sender:X2}{f.Receiver:X2}{f.Length}";
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            foreach (double width in new[] { 110.0, 60, 90, 60 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddCell(row, 0, new TextBlock { Text = $"{f.CommandSet:X2}/{f.CommandId:X2} {f.Sender:X2}>{f.Receiver:X2}", FontFamily = mono });
            AddCell(row, 1, new TextBlock { Text = $"{f.Length} B" });
            AddCell(row, 2, new TextBlock { Text = f.Count.ToString("N0") });
            AddCell(row, 3, new TextBlock { Text = _rates.GetValueOrDefault(id, "") });

            var hex = new System.Windows.Controls.TextBlock { FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            for (int i = 0; i < f.LastFrame.Length; i++)
            {
                var run = new Run(f.LastFrame[i].ToString("X2"));
                if (FrameStats.IsNoiseByte(i, f.Length))
                    run.Foreground = noise;
                else if (f.ChangedAt[i] != 0 && Stopwatch.GetElapsedTime(f.ChangedAt[i], now) < HighlightFor)
                {
                    run.Background = hot;
                    run.Foreground = onHot;
                }
                hex.Inlines.Add(run);
                hex.Inlines.Add(new Run(" "));
            }

            var detail = new StackPanel();
            detail.Children.Add(hex);
            var slots = new ChannelSlot[StickDecoder.SlotCount];
            int count = StickDecoder.DecodeSlots(f.LastFrame, slots);
            if (count > 0)
            {
                detail.Children.Add(new TextBlock
                {
                    Text = "Slots  " + string.Join("   ", slots.Take(count).Select((s, i) => $"{i}:{s.Value}")),
                    FontFamily = mono,
                    FontTypography = FontTypography.Caption,
                    Appearance = TextColor.Secondary,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            AddCell(row, 4, detail);
            FrameRows.Children.Add(row);
        }
    }

    // A TextBox eats the wheel even when it can't scroll further, which strands the page scroll
    private static void PassWheelAtEdges(object sender, MouseWheelEventArgs e)
    {
        var box = (System.Windows.Controls.TextBox)sender;
        bool atTop = box.VerticalOffset <= 0;
        bool atBottom = box.VerticalOffset >= box.ExtentHeight - box.ViewportHeight - 0.5;
        if ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom))
            return;

        e.Handled = true;
        if (VisualTreeHelper.GetParent(box) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = box });
    }

    private static void AddCell(Grid row, int column, UIElement element)
    {
        Grid.SetColumn(element, column);
        row.Children.Add(element);
    }

    private void RefreshLog()
    {
        var tail = Log.Tail(200);
        if (tail.Count > 0 && tail[^1] != _lastLogLine)
        {
            _lastLogLine = tail[^1];
            LogBox.Text = string.Join(Environment.NewLine, tail);
            LogBox.ScrollToEnd();
        }
    }

    private async Task CaptureStep(string step)
    {
        if (_capturing)
            return;
        if (step != ButtonCapture.Rest && !App.Capture.Has(ButtonCapture.Rest))
        {
            CaptureStatus.Text = "Capture Rest first, so there's something to compare against.";
            return;
        }

        _capturing = true;
        RefreshStepButtons();
        CaptureStatus.Text = $"Capturing {step}. Keep holding...";
        App.Engine.Frames.BeginWindow();
        await Task.Delay(CaptureLength);
        var windows = App.Engine.Frames.EndWindow();
        _capturing = false;

        if (windows.Count == 0)
            CaptureStatus.Text = "Nothing arrived from the controller. Check it's connected on the Home page.";
        else
        {
            App.Capture.Store(step, windows);
            Log.Info($"Captured {step}");
            CaptureStatus.Text = step == ButtonCapture.Rest
                ? "Rest captured. Now do each switch position and button."
                : $"{step} captured. {App.Capture.Count} of {Steps.Length} done.";
        }
        RefreshStepButtons();
    }

    private void RefreshStepButtons()
    {
        foreach (var (name, button) in _stepButtons)
        {
            button.IsEnabled = !_capturing;
            button.Icon = App.Capture.Has(name) ? new SymbolIcon { Symbol = SymbolRegular.Checkmark24 } : null;
        }
    }

    private void OnResetCaptureClicked(object sender, RoutedEventArgs e)
    {
        App.Capture.Clear();
        CaptureStatus.Text = "";
        RefreshStepButtons();
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        object label = button.Content;
        string report = AppInfo.BuildDiagnostics(App.Engine, App.Pad, App.Capture);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(report);
                button.Content = "Copied";
                await Task.Delay(2000);
                button.Content = label;
                return;
            }
            catch (ExternalException)
            {
                // Another app has the clipboard open; it usually lets go within a few ms
                await Task.Delay(50);
            }
        }
        button.Content = "Clipboard busy, try again";
    }

    private void OnOpenLogsClicked(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FileLog.Folder}\"") { UseShellExecute = true });
}
