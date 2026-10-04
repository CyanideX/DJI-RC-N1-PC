using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using RCN1Bridge.App.Services;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Mapping;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.App.Pages;

public partial class MappingPage : UserControl
{
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly Choice<AxisTarget>[] AxisChoices =
    [
        new(AxisTarget.None, "Nothing"),
        new(AxisTarget.LeftX, "Left stick X"),
        new(AxisTarget.LeftY, "Left stick Y"),
        new(AxisTarget.RightX, "Right stick X"),
        new(AxisTarget.RightY, "Right stick Y"),
        new(AxisTarget.LeftTrigger, "Left trigger (analog, + side)"),
        new(AxisTarget.RightTrigger, "Right trigger (analog, + side)"),
        new(AxisTarget.SplitTriggers, "Both triggers, one per direction (analog)"),
        new(AxisTarget.Buttons, "Buttons, held while turned"),
    ];

    private static readonly Choice<PadButton>[] ButtonChoices =
    [
        new(PadButton.None, "Nothing"),
        new(PadButton.A, "A"),
        new(PadButton.B, "B"),
        new(PadButton.X, "X"),
        new(PadButton.Y, "Y"),
        new(PadButton.LeftShoulder, "LB"),
        new(PadButton.RightShoulder, "RB"),
        new(PadButton.Back, "Back"),
        new(PadButton.Start, "Start"),
        new(PadButton.LeftThumb, "Left stick click"),
        new(PadButton.RightThumb, "Right stick click"),
        new(PadButton.DPadUp, "D-pad up"),
        new(PadButton.DPadDown, "D-pad down"),
        new(PadButton.DPadLeft, "D-pad left"),
        new(PadButton.DPadRight, "D-pad right"),
    ];

    private sealed record AxisRow(ComboBox Target, CheckBox Invert, FrameworkElement Ends, ComboBox Positive, ComboBox Negative, Slider PressAt);

    private readonly Dictionary<AnalogInput, AxisRow> _axisRows = [];
    private readonly Dictionary<DigitalInput, ComboBox> _buttonRows = [];
    private readonly List<(Ellipse Dot, Func<InputSnapshot, bool> Active)> _dots = [];
    private bool _loading;

    public MappingPage()
    {
        InitializeComponent();

        AddAxis(StickRows, AnalogInput.LeftX, "Left stick, left and right", "Yaw", s => Math.Abs(s.Processed.LeftX) > 0.15f);
        AddAxis(StickRows, AnalogInput.LeftY, "Left stick, up and down", "Throttle", s => Math.Abs(s.Processed.LeftY) > 0.15f);
        AddAxis(StickRows, AnalogInput.RightX, "Right stick, left and right", "Roll", s => Math.Abs(s.Processed.RightX) > 0.15f);
        AddAxis(StickRows, AnalogInput.RightY, "Right stick, up and down", "Pitch", s => Math.Abs(s.Processed.RightY) > 0.15f);
        AddAxis(DialRows, AnalogInput.Dial, "Gimbal dial", "Top left of the remote", s => Math.Abs(s.Processed.Dial) > 0.15f);

        AddButton(ButtonRows, DigitalInput.Fn, "Fn", "Customisable button", s => s.Buttons?.Fn == true);
        AddButton(ButtonRows, DigitalInput.PhotoVideo, "Photo/Video", "Switches between photo and video", s => s.Buttons?.PhotoVideo == true);
        AddButton(ButtonRows, DigitalInput.ReturnHome, "RTH", "Pause / return to home", s => s.Buttons?.ReturnHome == true);
        AddButton(ButtonRows, DigitalInput.Capture, "Capture", "Photo/record, top right", s => s.Buttons?.Capture == true);

        AddButton(ModeRows, DigitalInput.ModeC, "C", "Cine", s => s.Buttons?.Mode == FlightMode.Cine);
        AddButton(ModeRows, DigitalInput.ModeN, "N", "Normal", s => s.Buttons?.Mode == FlightMode.Normal);
        AddButton(ModeRows, DigitalInput.ModeS, "S", "Sport", s => s.Buttons?.Mode == FlightMode.Sport);

        _ = new LiveTimer(this, TimeSpan.FromMilliseconds(50), RenderActivity);
        Loaded += (_, _) => Render();
    }

    private void Render()
    {
        _loading = true;
        var settings = App.Settings;
        EnabledToggle.IsChecked = settings.MappingEnabled;
        OffBar.IsOpen = !settings.MappingEnabled;
        OffBar.Visibility = settings.MappingEnabled ? Visibility.Collapsed : Visibility.Visible;

        foreach (var (input, row) in _axisRows)
        {
            var binding = settings.Mapping.Get(input);
            Select(row.Target, AxisChoices, binding.Target);
            row.Invert.IsChecked = binding.Invert;
            Select(row.Positive, ButtonChoices, binding.Positive);
            Select(row.Negative, ButtonChoices, binding.Negative);
            row.PressAt.Value = binding.PressAt;
            row.Ends.Visibility = binding.Target == AxisTarget.Buttons ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var (input, box) in _buttonRows)
            Select(box, ButtonChoices, settings.Mapping.Get(input));
        _loading = false;
    }

    private void RenderActivity()
    {
        var input = App.Engine.Input;
        bool live = App.Engine.Status.State == LinkState.Live;
        foreach (var (dot, active) in _dots)
        {
            bool on = live && active(input);
            if (dot.Tag is bool was && was == on)
                continue;
            dot.Tag = on;
            dot.SetResourceReference(Shape.FillProperty, on ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
        }
    }

    private void AddAxis(Panel panel, AnalogInput input, string title, string hint, Func<InputSnapshot, bool> active)
    {
        var target = Combo(AxisChoices);
        var invert = new CheckBox { Content = "Invert", Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var positive = Combo(ButtonChoices, 170);
        var negative = Combo(ButtonChoices, 170);

        var ends = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        ends.Children.Add(Label("Turned to +"));
        ends.Children.Add(positive);
        ends.Children.Add(Label("Turned to -", 20));
        ends.Children.Add(negative);

        var pressAt = new Slider { Minimum = 0.1, Maximum = 0.98, TickFrequency = 0.01, IsSnapToTickEnabled = true, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        var pressAtText = new TextBlock { Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        pressAt.ValueChanged += (_, e) =>
        {
            pressAtText.Text = $"{e.NewValue:0%}";
            OnAxisChanged(input, saveNow: false);
        };
        ends.Children.Add(Label("Press at", 20));
        ends.Children.Add(pressAt);
        ends.Children.Add(pressAtText);

        _axisRows[input] = new AxisRow(target, invert, ends, positive, negative, pressAt);
        target.SelectionChanged += (_, _) => OnAxisChanged(input);
        positive.SelectionChanged += (_, _) => OnAxisChanged(input);
        negative.SelectionChanged += (_, _) => OnAxisChanged(input);
        invert.Checked += (_, _) => OnAxisChanged(input);
        invert.Unchecked += (_, _) => OnAxisChanged(input);

        panel.Children.Add(Row(title, hint, active, target, invert, ends));
    }

    private void AddButton(Panel panel, DigitalInput input, string title, string hint, Func<InputSnapshot, bool> active)
    {
        var box = Combo(ButtonChoices);
        _buttonRows[input] = box;
        box.SelectionChanged += (_, _) =>
        {
            if (!_loading && box.SelectedItem is Choice<PadButton> choice)
                Update(App.Settings.Mapping.With(input, choice.Value));
        };
        panel.Children.Add(Row(title, hint, active, box, null, null));
    }

    private void OnAxisChanged(AnalogInput input, bool saveNow = true)
    {
        if (_loading)
            return;
        var row = _axisRows[input];
        var binding = new AxisBinding
        {
            Target = Selected(row.Target, AxisTarget.None),
            Invert = row.Invert.IsChecked == true,
            Positive = Selected(row.Positive, PadButton.None),
            Negative = Selected(row.Negative, PadButton.None),
            PressAt = (float)Math.Round(row.PressAt.Value, 2),
        };
        row.Ends.Visibility = binding.Target == AxisTarget.Buttons ? Visibility.Visible : Visibility.Collapsed;
        Update(App.Settings.Mapping.With(input, binding), saveNow);
    }

    private static void Update(MappingProfile profile, bool saveNow = true)
    {
        App.Settings.Mapping = profile;
        if (saveNow)
            App.Settings.Save();
        else
            App.Settings.SaveSoon();
        if (App.Settings.MappingEnabled)
            App.ApplyMapping();
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Settings.MappingEnabled = EnabledToggle.IsChecked == true;
        App.Settings.Save();
        App.ApplyMapping();
        Render();
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        Update(MappingProfile.Default);
        Render();
    }

    private FrameworkElement Row(string title, string hint, Func<InputSnapshot, bool> active, ComboBox output, CheckBox? invert, FrameworkElement? extra)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "ControlStrokeColorDefaultBrush");
        _dots.Add((dot, active));
        Place(grid, dot, 0, 0);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title });
        text.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Muted") });
        Place(grid, text, 1, 0);

        output.VerticalAlignment = VerticalAlignment.Center;
        Place(grid, output, 2, 0);
        if (invert is not null)
            Place(grid, invert, 3, 0);
        if (extra is not null)
        {
            Place(grid, extra, 1, 1);
            Grid.SetColumnSpan(extra, 3);
        }

        return new Border { Style = (Style)FindResource("RowDivider"), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 12, 0, 0), Child = grid };
    }

    private static void Place(Grid grid, UIElement element, int column, int row)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private TextBlock Label(string text, double left = 0) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 8, 0),
        Style = (Style)FindResource("Muted"),
    };

    private static ComboBox Combo<T>(Choice<T>[] choices, double width = 280) =>
        new() { ItemsSource = choices, Width = width };

    private static void Select<T>(ComboBox box, Choice<T>[] choices, T value) =>
        box.SelectedItem = choices.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value)) ?? choices[0];

    private static T Selected<T>(ComboBox box, T fallback) => box.SelectedItem is Choice<T> choice ? choice.Value : fallback;
}
