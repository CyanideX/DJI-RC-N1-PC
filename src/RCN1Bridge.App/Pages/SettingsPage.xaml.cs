using System.Windows;
using System.Windows.Controls;
using RCN1Bridge.App.Services;

namespace RCN1Bridge.App.Pages;

public partial class SettingsPage : UserControl
{
    private static readonly (OutputTarget Target, string Label, string Hint)[] Outputs =
    [
        (OutputTarget.XboxController, "Virtual Xbox controller", "Works with any game that supports an Xbox controller. Needs ViGEmBus."),
        (OutputTarget.DroneMod, "Drone mod only", "No virtual controller. The Drone mod reads the RC directly, so keyboard, mouse and a real controller are left alone."),
        (OutputTarget.Both, "Both", "The Drone mod reads the RC, and other games still see a virtual Xbox controller."),
    ];

    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        OutputBox.ItemsSource = Outputs.Select(o => o.Label).ToArray();
        ThemeBox.ItemsSource = Enum.GetValues<AppTheme>();
        _ = new LiveTimer(this, TimeSpan.FromSeconds(1), RenderDroneMod);
        AboutText.Text = $"RC-N1 Bridge {AppInfo.Version}";
        FoldersText.Text = $"Settings: {AppSettings.Folder}{Environment.NewLine}Logs: {FileLog.Folder}";
        Loaded += (_, _) => Render();
    }

    private void Render()
    {
        _loading = true;
        KeepInTrayToggle.IsChecked = App.Settings.KeepInTray;
        StartWithWindowsToggle.IsChecked = App.Settings.StartWithWindows;
        ThemeBox.SelectedItem = App.Settings.Theme;
        int output = Array.FindIndex(Outputs, o => o.Target == App.Settings.Output);
        OutputBox.SelectedIndex = output;
        OutputHint.Text = Outputs[output].Hint;
        DriverText.Text = !App.Pad.Enabled ? "Not used while the controller goes to the Drone mod only."
            : App.Pad.IsConnected ? "ViGEmBus is installed. Games see the RC as a virtual Xbox 360 controller."
            : App.Pad.Problem ?? "Not installed.";
        _loading = false;
        RenderDroneMod();
    }

    private void RenderDroneMod() =>
        DroneModText.Text = !App.Settings.Output.UsesGameLink() ? "Off"
            : App.GameLink is null ? "Unavailable, see the log"
            : App.GameLink.ReaderConnected ? "Connected"
            : "Not running. Start the game with the Drone mod installed.";

    private void OnOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputBox.SelectedIndex < 0)
            return;
        App.Settings.Output = Outputs[OutputBox.SelectedIndex].Target;
        App.Settings.Save();
        App.ApplyOutput();
        Render();
    }

    private void OnKeepInTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Settings.KeepInTray = KeepInTrayToggle.IsChecked == true;
        App.Settings.Save();
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Settings.StartWithWindows = StartWithWindowsToggle.IsChecked == true;
        StartupRegistration.Apply(App.Settings.StartWithWindows);
        App.Settings.Save();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not AppTheme theme)
            return;
        App.Settings.Theme = theme;
        App.Settings.Save();
        App.SetTheme(theme);
    }

    private void OnDriverDownloadClicked(object sender, RoutedEventArgs e) => Shell.OpenUrl(PadHost.DriverDownloadUrl);

    private void OnDriverCheckClicked(object sender, RoutedEventArgs e)
    {
        App.Pad.TryConnect();
        Render();
    }

    private void OnOpenLogsClicked(object sender, RoutedEventArgs e) => Shell.OpenFolder(FileLog.Folder);

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e) => Shell.OpenFolder(AppSettings.Folder);
}
