using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SMI_Widget.Services;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly Icon _icon;
    private bool _balloonShown;

    public TrayIconHost()
    {
        _icon = CreateIcon();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _notify = new NotifyIcon
        {
            Icon = _icon,
            Text = "SMI Widget",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notify.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public void SetText(string text)
    {
        var trimmed = string.IsNullOrWhiteSpace(text) ? "SMI Widget" : text.Trim();
        _notify.Text = trimmed.Length <= 63 ? trimmed : trimmed[..63];
    }

    public void ShowBalloonOnce()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _notify.BalloonTipTitle = "SMI Widget";
        _notify.BalloonTipText = "Still running in the tray. Right-click the green S icon to exit.";
        _notify.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new SolidBrush(System.Drawing.Color.FromArgb(118, 185, 0));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var fg = new SolidBrush(System.Drawing.Color.FromArgb(16, 20, 10));
            using var font = new Font("Segoe UI", 15, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("S", font, fg, new RectangleF(0, 1, 32, 32), format);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
