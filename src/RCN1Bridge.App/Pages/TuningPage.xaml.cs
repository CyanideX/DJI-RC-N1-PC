using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RCN1Bridge.App.Controls;
using RCN1Bridge.App.Services;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Input;

namespace RCN1Bridge.App.Pages;

public partial class TuningPage : UserControl
{
    private enum Side { Left, Right }

    private enum CalibrationStep { Idle, Centre, Range }

    // Factory half-range is 660; much less than half of that means the stick wasn't pushed to the edge
    private const int MinHalfRange = 300;
    private const int AxisCount = 5;
    private static readonly string[] AxisNames = ["Left X", "Left Y", "Right X", "Right Y", "Dial"];

    private readonly Dictionary<(Side, string), (Slider Slider, TextBlock Value)> _stickSliders = [];
    private (Slider Slider, TextBlock Value) _dialDeadzone;
    private readonly TextBlock[] _calibrationCells = new TextBlock[AxisCount];
    private bool _loading;

    private CalibrationStep _step;
    private readonly Queue<ushort[]> _centreSamples = new();
    private readonly ushort[] _min = new ushort[AxisCount];
    private readonly ushort[] _max = new ushort[AxisCount];
    private readonly ushort[] _centre = new ushort[AxisCount];
    private bool _dialSeen;
    private long _lastSequence = -1;

    public TuningPage()
    {
        InitializeComponent();

        foreach (var (side, panel) in new[] { (Side.Left, LeftSliders), (Side.Right, RightSliders) })
        {
            AddStickSlider(panel, side, "Deadzone", 0, 0.2, 0.01, v => $"{v:0%}");
            AddStickSlider(panel, side, "Expo", 0, 1, 0.05, v => $"{v:0.00}");
            AddStickSlider(panel, side, "Rate", 0.5, 1, 0.05, v => $"{v:0%}");
            AddStickSlider(panel, side, "Smoothing", 0, 50, 1, v => v == 0 ? "Off" : $"{v:0} ms");
        }
        _dialDeadzone = SliderRow(DialSliders, "Deadzone", 0, 0.2, 0.01, v => $"{v:0%}", _ => OnDialChanged());

        for (int i = 0; i < AxisCount; i++)
        {
            var cell = new TextBlock { Style = (Style)FindResource("Mono"), Margin = new Thickness(0, 0, 8, 0) };
            _calibrationCells[i] = cell;
            CalibrationCells.Children.Add(cell);
        }

        _ = new LiveTimer(this, TimeSpan.FromMilliseconds(16), OnTick, DispatcherPriority.Render);
        Loaded += (_, _) => Render();
    }

    private void Render()
    {
        _loading = true;
        var settings = App.Settings;
        EnabledToggle.IsChecked = settings.TuningEnabled;
        OffBar.IsOpen = !settings.TuningEnabled;
        OffBar.Visibility = settings.TuningEnabled ? Visibility.Collapsed : Visibility.Visible;

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var shaping = Shaping(side);
            SetSlider(side, "Deadzone", shaping.Deadzone);
            SetSlider(side, "Expo", shaping.Expo);
            SetSlider(side, "Rate", shaping.Rate);
            SetSlider(side, "Smoothing", shaping.SmoothingMs);
            (side == Side.Left ? LeftCurve : RightCurve).SetShaping(shaping);
        }
        _dialDeadzone.Slider.Value = settings.Tuning.DialDeadzone;
        _loading = false;

        if (_step == CalibrationStep.Idle)
            RenderCalibration(settings.Tuning.Calibration);
    }

    private static StickShaping Shaping(Side side) => side == Side.Left ? App.Settings.Tuning.Left : App.Settings.Tuning.Right;

    private void SetSlider(Side side, string name, double value) => _stickSliders[(side, name)].Slider.Value = value;

    private void AddStickSlider(Panel panel, Side side, string name, double min, double max, double step, Func<double, string> format) =>
        _stickSliders[(side, name)] = SliderRow(panel, name, min, max, step, format, _ => OnStickChanged(side));

    private (Slider, TextBlock) SliderRow(Panel panel, string name, double min, double max, double step, Func<double, string> format, Action<double> changed)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            SmallChange = step,
            LargeChange = step * 5,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBlock { Style = (Style)FindResource("Mono"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.Text = format(slider.Value);
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            if (!_loading)
                changed(e.NewValue);
        };

        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        panel.Children.Add(grid);
        return (slider, value);
    }

    private void OnStickChanged(Side side)
    {
        float Value(string name) => (float)Math.Round(_stickSliders[(side, name)].Slider.Value, 3);
        var shaping = new StickShaping
        {
            Deadzone = Value("Deadzone"),
            Expo = Value("Expo"),
            Rate = Value("Rate"),
            SmoothingMs = Value("Smoothing"),
        };
        var tuning = App.Settings.Tuning;
        Update(side == Side.Left ? tuning with { Left = shaping } : tuning with { Right = shaping });
        (side == Side.Left ? LeftCurve : RightCurve).SetShaping(shaping);
    }

    private void OnDialChanged() =>
        Update(App.Settings.Tuning with { DialDeadzone = (float)Math.Round(_dialDeadzone.Slider.Value, 3) });

    private static void Update(TuningProfile tuning, bool saveNow = false)
    {
        App.Settings.Tuning = tuning;
        if (saveNow)
            App.Settings.Save();
        else
            App.Settings.SaveSoon();
        if (App.Settings.TuningEnabled)
            App.ApplyTuning();
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Settings.TuningEnabled = EnabledToggle.IsChecked == true;
        App.Settings.Save();
        App.ApplyTuning();
        Render();
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        Update(TuningProfile.Default with { Calibration = App.Settings.Tuning.Calibration }, saveNow: true);
        Render();
    }

    private void OnTick()
    {
        var input = App.Engine.Input;
        bool live = App.Engine.Status.State == LinkState.Live;
        var cal = App.Settings.Tuning.Calibration;
        var raw = input.Raw;

        PlotStick(LeftCurve, Shaping(Side.Left), live, cal.LeftH.Normalize(raw.LeftH), cal.LeftV.Normalize(raw.LeftV));
        PlotStick(RightCurve, Shaping(Side.Right), live, cal.RightH.Normalize(raw.RightH), cal.RightV.Normalize(raw.RightV));

        if (_step == CalibrationStep.Idle || !live || input.Sequence == _lastSequence)
            return;
        _lastSequence = input.Sequence;
        ushort[] sample = [raw.LeftH, raw.LeftV, raw.RightH, raw.RightV, raw.Dial];

        if (_step == CalibrationStep.Centre)
        {
            _centreSamples.Enqueue(sample);
            if (_centreSamples.Count > 20)
                _centreSamples.Dequeue();
            return;
        }

        int axes = raw.HasDial ? AxisCount : AxisCount - 1;
        _dialSeen |= raw.HasDial;
        for (int i = 0; i < axes; i++)
        {
            _min[i] = Math.Min(_min[i], sample[i]);
            _max[i] = Math.Max(_max[i], sample[i]);
        }
        for (int i = 0; i < AxisCount; i++)
            _calibrationCells[i].Text = $"{AxisNames[i]}\n{_min[i]} · {_centre[i]} · {_max[i]}";
    }

    private static void PlotStick(CurveView curve, StickShaping shaping, bool live, float x, float y)
    {
        if (!live)
        {
            curve.SetPosition(-1, 0);
            return;
        }
        double input = Math.Min(1, Math.Sqrt(x * x + y * y));
        float dz = Math.Clamp(shaping.Deadzone, 0f, 0.95f);
        float scaled = input <= dz ? 0f : (float)(input - dz) / (1f - dz);
        curve.SetPosition(Math.Round(input, 3), Math.Round(shaping.Curve(scaled), 3));
    }

    private void RenderCalibration(StickCalibration cal)
    {
        AxisCalibration[] axes = [cal.LeftH, cal.LeftV, cal.RightH, cal.RightV, cal.Dial];
        for (int i = 0; i < AxisCount; i++)
            _calibrationCells[i].Text = $"{AxisNames[i]}\n{axes[i].Min} · {axes[i].Center} · {axes[i].Max}";
    }

    private void ShowStep(CalibrationStep step, string? status)
    {
        _step = step;
        CalibrationStatus.Text = status ?? "";
        CalibrationStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        CalibrateButton.Visibility = step == CalibrationStep.Idle ? Visibility.Visible : Visibility.Collapsed;
        FactoryButton.Visibility = CalibrateButton.Visibility;
        NextButton.Visibility = step == CalibrationStep.Centre ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = step == CalibrationStep.Range ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = step == CalibrationStep.Idle ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCalibrateClicked(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Status.State != LinkState.Live)
        {
            ShowStep(CalibrationStep.Idle, "Connect the controller first. The Home page shows when it's ready.");
            return;
        }
        _centreSamples.Clear();
        ShowStep(CalibrationStep.Centre, "Step 1 of 2. Let go of both sticks and leave the dial centred, then click Next.");
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (_centreSamples.Count < 5)
        {
            CalibrationStatus.Text = "Waiting for stick data. Check the controller is still connected, then click Next again.";
            return;
        }

        for (int i = 0; i < AxisCount; i++)
            _centre[i] = (ushort)Math.Round(_centreSamples.Average(s => s[i]));
        _centre.CopyTo(_min, 0);
        _centre.CopyTo(_max, 0);
        _dialSeen = false;
        ShowStep(CalibrationStep.Range,
            "Step 2 of 2. Push both sticks all the way round a few times and roll the dial fully both ways. Click Finish when the numbers stop changing.");
    }

    private void OnFinishClicked(object sender, RoutedEventArgs e)
    {
        var tooShort = new List<string>();
        for (int i = 0; i < AxisCount - 1; i++)
        {
            if (_centre[i] - _min[i] < MinHalfRange || _max[i] - _centre[i] < MinHalfRange)
                tooShort.Add(AxisNames[i]);
        }
        if (tooShort.Count > 0)
        {
            CalibrationStatus.Text = $"{string.Join(", ", tooShort)} didn't move far enough. Keep circling the sticks right to the edge, then click Finish.";
            return;
        }

        var old = App.Settings.Tuning.Calibration;
        bool dialOk = _dialSeen && _centre[4] - _min[4] >= MinHalfRange && _max[4] - _centre[4] >= MinHalfRange;
        var cal = new StickCalibration
        {
            LeftH = Axis(0),
            LeftV = Axis(1),
            RightH = Axis(2),
            RightV = Axis(3),
            Dial = dialOk ? Axis(4) : old.Dial,
        };
        Update(App.Settings.Tuning with { Calibration = cal }, saveNow: true);
        RenderCalibration(cal);

        string message = dialOk ? "Calibrated." : "Sticks calibrated. The dial kept its old values because it wasn't rolled to both ends.";
        if (!App.Settings.TuningEnabled)
            message += " Turn on custom tuning to use it.";
        ShowStep(CalibrationStep.Idle, message);

        AxisCalibration Axis(int i) => new(_min[i], _centre[i], _max[i]);
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        ShowStep(CalibrationStep.Idle, null);
        RenderCalibration(App.Settings.Tuning.Calibration);
    }

    private void OnFactoryClicked(object sender, RoutedEventArgs e)
    {
        Update(App.Settings.Tuning with { Calibration = new StickCalibration() }, saveNow: true);
        RenderCalibration(App.Settings.Tuning.Calibration);
        ShowStep(CalibrationStep.Idle, "Back to the factory values.");
    }
}
