using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

    private static readonly string[] Hex = Enumerable.Range(0, 256).Select(i => i.ToString("X2")).ToArray();

    private readonly Dictionary<string, FrameRow> _rows = [];
    private readonly ChannelSlot[] _slots = new ChannelSlot[StickDecoder.SlotCount];
    private readonly StringBuilder _slotText = new();
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
        _ = new LiveTimer(this, TimeSpan.FromMilliseconds(100), Refresh);
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
        ReplyText.Text = engine.ReplyTime.Median() is double reply ? $"{reply:0.0} ms" : "n/a";
        LostText.Text = engine.LostPollCount.ToString("N0");

        foreach (var f in engine.Frames.Snapshot())
        {
            string id = $"{f.CommandSet:X2}{f.CommandId:X2}{f.Sender:X2}{f.Receiver:X2}{f.Length}";
            if (seconds > 0 && _previousCounts.TryGetValue(id, out long before))
                _rates[id] = $"{(f.Count - before) / seconds:0}/s";
            _previousCounts[id] = f.Count;
        }
    }

    private sealed class FrameRow
    {
        public required Grid Root;
        public required TextBlock Count;
        public required TextBlock Rate;
        public required Run[] Bytes;
        public required TextBlock Slots;
    }

    private void RefreshFrames()
    {
        var stats = App.Engine.Frames.Snapshot();
        NoFramesText.Visibility = stats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var hot = TryFindResource("AccentFillColorDefaultBrush") as Brush;
        var onHot = TryFindResource("TextOnAccentFillColorPrimaryBrush") as Brush;
        long now = Stopwatch.GetTimestamp();

        bool reorder = stats.Count != FrameRows.Children.Count;
        for (int r = 0; r < stats.Count; r++)
        {
            var f = stats[r];
            string id = $"{f.CommandSet:X2}{f.CommandId:X2}{f.Sender:X2}{f.Receiver:X2}{f.Length}";
            if (!_rows.TryGetValue(id, out var row))
            {
                _rows[id] = row = BuildRow(f);
                reorder = true;
            }
            else if (!reorder && FrameRows.Children[r] != row.Root)
                reorder = true;

            row.Count.Text = f.Count.ToString("N0");
            row.Rate.Text = _rates.GetValueOrDefault(id, "");
            for (int i = 0; i < f.LastFrame.Length; i++)
            {
                var run = row.Bytes[i];
                string text = Hex[f.LastFrame[i]];
                if (run.Text != text)
                    run.Text = text;
                if (FrameStats.IsNoiseByte(i, f.Length))
                    continue;
                bool lit = f.ChangedAt[i] != 0 && Stopwatch.GetElapsedTime(f.ChangedAt[i], now) < HighlightFor;
                if (lit != (run.Background is not null))
                {
                    run.Background = lit ? hot : null;
                    if (lit)
                        run.Foreground = onHot;
                    else
                        run.ClearValue(TextElement.ForegroundProperty);
                }
            }

            int count = StickDecoder.DecodeSlots(f.LastFrame, _slots);
            if (count > 0)
            {
                _slotText.Clear().Append("Slots ");
                for (int i = 0; i < count; i++)
                    _slotText.Append("  ").Append(i).Append(':').Append(_slots[i].Value);
                row.Slots.Text = _slotText.ToString();
            }
            row.Slots.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (reorder)
        {
            FrameRows.Children.Clear();
            foreach (var f in stats)
                FrameRows.Children.Add(_rows[$"{f.CommandSet:X2}{f.CommandId:X2}{f.Sender:X2}{f.Receiver:X2}{f.Length}"].Root);
        }
    }

    private FrameRow BuildRow(FrameStat f)
    {
        var mono = (FontFamily)FindResource("MonoFont");
        var noise = TryFindResource("TextFillColorTertiaryBrush") as Brush;
        var root = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        foreach (double width in new[] { 110.0, 60, 90, 60 })
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var count = new TextBlock();
        var rate = new TextBlock();
        AddCell(root, 0, new TextBlock { Text = $"{f.CommandSet:X2}/{f.CommandId:X2} {f.Sender:X2}>{f.Receiver:X2}", FontFamily = mono });
        AddCell(root, 1, new TextBlock { Text = $"{f.Length} B" });
        AddCell(root, 2, count);
        AddCell(root, 3, rate);

        var hex = new System.Windows.Controls.TextBlock { FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var bytes = new Run[f.LastFrame.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = new Run();
            if (FrameStats.IsNoiseByte(i, f.Length))
                bytes[i].Foreground = noise;
            hex.Inlines.Add(bytes[i]);
            hex.Inlines.Add(new Run(" "));
        }

        var slots = new TextBlock
        {
            FontFamily = mono,
            FontTypography = FontTypography.Caption,
            Appearance = TextColor.Secondary,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var detail = new StackPanel();
        detail.Children.Add(hex);
        detail.Children.Add(slots);
        AddCell(root, 4, detail);

        return new FrameRow { Root = root, Count = count, Rate = rate, Bytes = bytes, Slots = slots };
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
            if (!App.Capture.Has(name))
            {
                button.Icon = null;
                button.ClearValue(BorderBrushProperty);
                button.ClearValue(BorderThicknessProperty);
                continue;
            }
            var check = new SymbolIcon { Symbol = SymbolRegular.Checkmark24 };
            check.SetResourceReference(ForegroundProperty, "SystemFillColorSuccessBrush");
            button.Icon = check;
            button.SetResourceReference(BorderBrushProperty, "SystemFillColorSuccessBrush");
            button.BorderThickness = new Thickness(1.5);
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
