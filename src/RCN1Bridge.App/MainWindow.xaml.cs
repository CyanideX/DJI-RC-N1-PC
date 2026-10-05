using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RCN1Bridge.App.Pages;
using RCN1Bridge.Core.Device;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RCN1Bridge.App;

public partial class MainWindow : FluentWindow
{
    private const int WmDeviceChange = 0x0219;

    private readonly HomePage _home = new();
    private readonly MappingPage _mapping = new();
    private readonly TuningPage _tuning = new();
    private readonly DiagnosticsPage _diagnostics = new();
    private readonly SettingsPage _settings = new();
    private bool _reallyClose;

    public event Action? QuitRequested;
    public event Action? HiddenToTray;

    public MainWindow()
    {
        InitializeComponent();
        // SizeToContent fits the window to Home; this keeps it on screen when Home is tall
        MaxHeight = SystemParameters.WorkArea.Height;
        Navigate(_home);
        RenderState();
        // Taskbar button; grey reads on light and dark taskbars alike, so only the title icon follows the theme
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-grey.ico"));
        ApplyIconTheme();
        ApplicationThemeManager.Changed += (_, _) => Dispatcher.BeginInvoke(ApplyIconTheme);
        App.Engine.StatusChanged += _ => Dispatcher.BeginInvoke(RenderState);
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    public void CloseForReal()
    {
        if (_reallyClose)
            return;
        _reallyClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose && App.Settings.KeepInTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        _reallyClose = true;
        base.OnClosing(e);
    }

    // Quitting from inside Closing would call Close() on a window that's already closing, which WPF throws on
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        QuitRequested?.Invoke();
    }

    private void OnNavClicked(object sender, RoutedEventArgs e)
    {
        UserControl page = sender == HomeNav ? _home
            : sender == MappingNav ? _mapping
            : sender == TuningNav ? _tuning
            : sender == DiagnosticsNav ? _diagnostics
            : _settings;
        Navigate(page);
    }

    private void Navigate(UserControl page)
    {
        // Only Home sizes the window; the other pages scroll inside whatever height it has
        if (page != _home && SizeToContent != SizeToContent.Manual)
            SizeToContent = SizeToContent.Manual;

        PageHost.Content = page;
        PageScroll.ScrollToTop();
        HomeNav.Tag = page == _home;
        MappingNav.Tag = page == _mapping;
        TuningNav.Tag = page == _tuning;
        DiagnosticsNav.Tag = page == _diagnostics;
        SettingsNav.Tag = page == _settings;
    }

    // The icon is a dark silhouette, invisible on a dark title bar or taskbar without the white copy
    private void ApplyIconTheme()
    {
        string suffix = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? "-light" : "";
        TitleIcon.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/icon-256{suffix}.png"));
    }

    private void RenderState()
    {
        var s = App.Engine.Status;
        StateText.Text = s.State switch
        {
            LinkState.Live => $"Connected · {s.Port?.PortName}",
            LinkState.WaitingForData => $"Waiting · {s.Port?.PortName}",
            LinkState.Stalled => "Signal lost",
            LinkState.Searching => "Not connected",
            _ => "Stopped",
        };
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmDeviceChange)
            App.Engine.RequestScan();
        return 0;
    }
}
