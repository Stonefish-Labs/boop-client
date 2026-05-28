using System.Drawing;
using System.Windows.Forms;

namespace Boop.Windows.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconService(Action show, Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Boop", null, (_, _) => show());
        menu.Items.Add("Quit", null, (_, _) => quit());
        _notifyIcon = new NotifyIcon
        {
            Text = "Boop",
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => show();
    }

    public void SetPendingCount(int count)
    {
        _notifyIcon.Text = count > 0 ? $"Boop ({count} pending)" : "Boop";
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}

