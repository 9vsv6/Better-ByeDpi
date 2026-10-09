using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ByeDpiPc;

/// <summary>Notification-area icon: a dot that is green while connected.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggle;
    private Icon? _current;

    public TrayIcon(Action open, Action toggle, Action exit)
    {
        _toggle = new ToolStripMenuItem("Connect", null, (_, _) => toggle());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open ByeDPI", null, (_, _) => open()) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
        Update(connected: false, busy: false, "ByeDPI — disconnected");
    }

    public static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new Pen(color, 3);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 10, 10, 12, 12);
        }
        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Update(bool connected, bool busy, string tooltip)
    {
        var color = busy ? Color.FromArgb(245, 158, 11) : connected ? Color.FromArgb(34, 197, 94) : Color.FromArgb(148, 163, 184);
        var old = _current;
        _current = MakeIcon(color);
        _icon.Icon = _current;
        old?.Dispose();
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _toggle.Text = connected ? "Disconnect" : "Connect";
        _toggle.Enabled = !busy;
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
