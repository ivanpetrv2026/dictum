using System.Drawing;
using System.Windows.Forms;

namespace Dictum.Tray;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public event EventHandler? SettingsRequested;

    public event EventHandler? SetupRequested;

    public event EventHandler? AboutRequested;

    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Настройки…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Мастер настройки…", null, (_, _) => SetupRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("О программе", null, (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);

        _icon = new NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "Dictum — локальная диктовка",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowInfo(string title, string message)
        => _icon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);

    public void ShowError(string title, string message)
        => _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
