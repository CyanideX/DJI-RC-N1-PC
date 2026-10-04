using System.Windows;
using System.Windows.Controls;
using RCN1Bridge.App.Services;

namespace RCN1Bridge.App.Pages;

public partial class SettingsPage : UserControl
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeBox.ItemsSource = Enum.GetValues<AppTheme>();
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
        DriverText.Text = App.Pad.IsConnected
            ? "ViGEmBus is installed. Games see the RC as a virtual Xbox 360 controller."
            : App.Pad.Problem ?? "Not installed.";
        _loading = false;
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
