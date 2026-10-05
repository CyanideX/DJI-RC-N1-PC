using System.Windows;
using System.Windows.Threading;
using RCN1Bridge.App.Services;
using RCN1Bridge.Core.Device;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Mapping;
using RCN1Bridge.Core.Output;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RCN1Bridge.App;

public partial class App : Application
{
    private const string InstanceName = @"Local\RCN1Bridge.Instance";
    private const string ShowEventName = @"Local\RCN1Bridge.Show";
    private const string QuitEventName = @"Local\RCN1Bridge.Quit";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _quitSignal;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _watchingTheme;
    private bool _toldAboutTray;
    private bool _quitting;

    public static PadHost Pad { get; } = new();
    public static BridgeEngine Engine { get; private set; } = null!;
    public static ButtonCapture Capture { get; } = new();
    public static AppSettings Settings { get; private set; } = null!;
    public static GameLink? GameLink { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            // A second copy would fight the first for the COM port, so bring the first one forward instead
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        _showSignal = ListenFor(ShowEventName, ShowMain, repeat: true);
        _quitSignal = ListenFor(QuitEventName, Quit, repeat: false);

        FileLog.Start();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        Log.Info($"RC-N1 Bridge {AppInfo.Version} starting, Windows {Environment.OSVersion.Version}");

        Settings = AppSettings.Load();
        GameLink = Core.Output.GameLink.TryCreate();
        Engine = new BridgeEngine(Pad) { GameLink = GameLink };
        ApplyOutput();
        Pad.TryConnect();
        ApplyMapping();
        ApplyTuning();
        Engine.Start();

        _window = new MainWindow();
        _window.QuitRequested += Quit;
        _window.HiddenToTray += OnHiddenToTray;
        ApplyTheme(Settings.Theme);

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowMain;
        _tray.QuitRequested += Quit;
        _tray.SendToGameChanged += on =>
        {
            Engine.OutputEnabled = on;
            _tray.SetSendToGame(on);
        };
        Engine.StatusChanged += _ => Dispatcher.BeginInvoke(() => _tray?.SetStatus(Engine.Status.State.ToString()));

        if (Settings.StartWithWindows)
            StartupRegistration.Apply(true);

        bool startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) && Settings.KeepInTray;
        if (!startHidden)
            _window.Show();
    }

    public static void SyncSendToGame(bool on) => (Current as App)?._tray?.SetSendToGame(on);

    public static void SetTheme(AppTheme theme) => (Current as App)?.ApplyTheme(theme);

    public static void ApplyMapping() =>
        Engine.Mapper = Settings.MappingEnabled ? new InputMapper(Settings.Mapping, Engine.Mapper) : null;

    public static void ApplyOutput()
    {
        Pad.SetEnabled(Settings.Output.UsesPad());
        Engine.GameLinkEnabled = Settings.Output.UsesGameLink();
    }

    public static void ApplyTuning() =>
        Engine.Processor.Tuning = Settings.TuningEnabled ? Settings.Tuning : TuningProfile.Default;

    private EventWaitHandle ListenFor(string name, Action action, bool repeat)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() =>
        {
            do
            {
                if (!signal.WaitOne())
                    return;
                Dispatcher.InvokeAsync(action);
            }
            while (repeat);
        }) { IsBackground = true, Name = name }.Start();
        return signal;
    }

    private void ShowMain()
    {
        if (_window is null)
            return;
        if (!_window.IsVisible)
            _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnHiddenToTray()
    {
        if (_toldAboutTray)
            return;
        _toldAboutTray = true;
        _tray?.Notify("RC-N1 Bridge is still running", "The controller keeps working. Right-click the tray icon to quit.");
    }

    private void ApplyTheme(AppTheme theme)
    {
        if (_window is not { } w)
            return;
        // SystemThemeWatcher throws if attached or detached before the window has loaded
        if (_watchingTheme && w.IsLoaded)
        {
            SystemThemeWatcher.UnWatch(w);
            _watchingTheme = false;
        }
        switch (theme)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica);
                break;
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                if (w.IsLoaded)
                    WatchTheme();
                else
                    w.Loaded += OnLoadedWatch;
                break;
        }

        void OnLoadedWatch(object? sender, RoutedEventArgs e)
        {
            w.Loaded -= OnLoadedWatch;
            if (Settings.Theme == AppTheme.System)
                WatchTheme();
        }

        void WatchTheme()
        {
            if (_watchingTheme)
                return;
            SystemThemeWatcher.Watch(w, WindowBackdropType.Mica);
            _watchingTheme = true;
        }
    }

    private void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        _tray?.Dispose();
        _tray = null;
        _window?.CloseForReal();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance is not null)
        {
            _tray?.Dispose();
            Settings?.Flush();
            Engine?.Dispose();
            // Unmapping under a reader that's still publishing is an access violation; exit frees it anyway
            if (Engine?.IsStopped != false)
                GameLink?.Dispose();
            Pad.Dispose();
            Log.Info("Exited");
            FileLog.Stop();
            _showSignal?.Dispose();
            _quitSignal?.Dispose();
            try { _instance.ReleaseMutex(); }
            catch (ApplicationException) { }
            _instance.Dispose();
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI error", e.Exception);
        // Keep running; the engine and virtual pad don't depend on the UI thread
        e.Handled = true;
    }
}
