using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace RCN1Bridge.App.Services;

// WinForms NotifyIcon: WPF-UI's tray icon can't show a balloon, and Windows 11 turns balloons into normal notifications
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _sendToGame;

    public event Action? OpenRequested;
    public event Action<bool>? SendToGameChanged;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "RC-N1 Bridge",
            Visible = true,
        };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _sendToGame = new Forms.ToolStripMenuItem("Send to game") { Checked = true };
        _sendToGame.Click += (_, _) => SendToGameChanged?.Invoke(!_sendToGame.Checked);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open RC-N1 Bridge", null, (_, _) => OpenRequested?.Invoke())
        {
            Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold),
        });
        menu.Items.Add(_sendToGame);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit", null, (_, _) => QuitRequested?.Invoke()));
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public void SetSendToGame(bool on) => _sendToGame.Checked = on;

    // NotifyIcon.Text is capped at 63 characters
    public void SetStatus(string status)
    {
        string tip = $"RC-N1 Bridge: {status}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    public void Notify(string title, string body) => _icon.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.None);

    // The taskbar has its own light/dark setting, separate from the app theme
    private static System.Drawing.Icon LoadIcon()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool lightTaskbar = key?.GetValue("SystemUsesLightTheme") is 1;
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{(lightTaskbar ? "icon.ico" : "icon-light.ico")}"));
        if (resource is null)
            return System.Drawing.SystemIcons.Application;
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
            return;
        var old = _icon.Icon;
        _icon.Icon = LoadIcon();
        old?.Dispose();
    }

    public void Dispose()
    {
        // Static event; leaving it attached keeps this object alive
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
