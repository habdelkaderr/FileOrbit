using Forms = System.Windows.Forms;

namespace FileOrbit.App.Shell;

/// <summary>
/// The notification-area icon and Windows notifications. Notifications are shown through the tray
/// icon, which Windows 10/11 presents as standard toast notifications.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private Action? _balloonAction;
    private bool _persistent;

    public TrayIcon(Action openMain, Action convertFile, Action openSettings, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open FileOrbit", null, (_, _) => openMain());
        menu.Items.Add("Convert a file…", null, (_, _) => convertFile());
        menu.Items.Add("Settings", null, (_, _) => openSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/FileOrbit;component/Assets/FileOrbit.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "FileOrbit",
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                openMain();
            }
        };
        _icon.BalloonTipClicked += (_, _) => _balloonAction?.Invoke();
        _icon.BalloonTipClosed += (_, _) => HideIfTemporary();
    }

    /// <summary>Whether the icon stays in the notification area (the "Run in tray" setting).</summary>
    public bool Persistent
    {
        get => _persistent;
        set
        {
            _persistent = value;
            _icon.Visible = value;
        }
    }

    public void Notify(string title, string text, bool isError, Action? onClick)
    {
        _balloonAction = onClick;
        _icon.Visible = true;
        _icon.ShowBalloonTip(5000, title, text, isError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void HideIfTemporary()
    {
        if (!_persistent)
        {
            _icon.Visible = false;
        }
    }
}
