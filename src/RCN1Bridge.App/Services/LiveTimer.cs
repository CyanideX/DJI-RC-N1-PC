using System.Windows;
using System.Windows.Threading;

namespace RCN1Bridge.App.Services;

// Ticks only while the page is on screen. Hide() and minimising don't unload a page,
// so a plain DispatcherTimer would keep redrawing for a window sitting in the tray.
public sealed class LiveTimer
{
    private readonly FrameworkElement _owner;
    private readonly DispatcherTimer _timer;
    private readonly Action _tick;
    private Window? _window;

    public LiveTimer(FrameworkElement owner, TimeSpan interval, Action tick, DispatcherPriority priority = DispatcherPriority.Background)
    {
        _owner = owner;
        _tick = tick;
        _timer = new DispatcherTimer(priority) { Interval = interval };
        _timer.Tick += (_, _) => tick();

        owner.Loaded += (_, _) =>
        {
            _window = Window.GetWindow(owner);
            if (_window is not null)
                _window.StateChanged += OnWindowStateChanged;
            Update();
        };
        owner.Unloaded += (_, _) =>
        {
            if (_window is not null)
                _window.StateChanged -= OnWindowStateChanged;
            _window = null;
            Update();
        };
        owner.IsVisibleChanged += (_, _) => Update();
    }

    public bool IsRunning => _timer.IsEnabled;

    private void OnWindowStateChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        bool run = _owner.IsLoaded && _owner.IsVisible && _window?.WindowState != WindowState.Minimized;
        if (run == _timer.IsEnabled)
            return;
        if (run)
        {
            _tick();
            _timer.Start();
        }
        else
            _timer.Stop();
    }
}
