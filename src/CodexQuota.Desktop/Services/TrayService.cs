using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Desktop.Services;

/// <summary>Generates the widget icon at runtime; no binary asset is bundled.</summary>
internal static class AppIcon
{
    public static Icon Create(ProviderIconState state)
    {
        using Bitmap bitmap = RenderBitmap(state, 32);
        nint handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    internal static Bitmap RenderBitmap(ProviderIconState state, int size)
    {
        var bitmap = new Bitmap(size, size);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(size / 32f, size / 32f);

        var canvas = new RectangleF(0, 0, 32, 32);
        using Brush surfaceBrush = NativeBrush(state.SurfaceBrush, canvas);
        graphics.FillEllipse(surfaceBrush, 0, 0, 32, 32);
        var inner = new RectangleF(4.67f, 4.67f, 22.66f, 22.66f);
        using Brush innerRimBrush = NativeBrush(state.InnerRimBrush, inner);
        using var innerRim = new Pen(innerRimBrush, 0.49f);
        graphics.DrawEllipse(innerRim, inner);

        var ring = new RectangleF(2.25f, 2.25f, 27.5f, 27.5f);
        using Brush trackBrush = NativeBrush(state.TrackBrush, canvas);
        using var trackPen = new Pen(trackBrush, 4.5f);
        graphics.DrawEllipse(trackPen, ring);
        if (state.RemainingPercent is > 0)
        {
            using Brush arcBrush = NativeBrush(state.ArcBrush, canvas);
            using var arcPen = new Pen(arcBrush, 4.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (state.RemainingPercent >= 100) { graphics.DrawEllipse(arcPen, ring); }
            else { graphics.DrawArc(arcPen, ring, -90, 360f * state.RemainingPercent.Value / 100f); }
        }

        using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using Brush textBrush = NativeBrush(state.SymbolBrush, new RectangleF(8, 7, 16, 18));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(state.Appearance.Symbol, font, textBrush, new RectangleF(0, -0.5f, 32, 32), format);

        var badge = new RectangleF(21.333f, 21.333f, 10.667f, 10.667f);
        var disk = new RectangleF(23.111f, 23.111f, 7.111f, 7.111f);
        using Brush separatorBrush = NativeBrush(state.BadgeSeparatorBrush, badge);
        using Brush dotBrush = NativeBrush(state.BadgeBrush, disk);
        graphics.FillEllipse(separatorBrush, badge);
        graphics.FillEllipse(dotBrush, disk);
        var highlight = new RectangleF(24, 24, 2.667f, 1.333f);
        using Brush highlightBrush = NativeBrush(state.BadgeHighlightBrush, highlight);
        graphics.FillEllipse(highlightBrush, highlight);
        return bitmap;
    }

    private static Brush NativeBrush(System.Windows.Media.Brush brush, RectangleF bounds)
    {
        if (brush is System.Windows.Media.LinearGradientBrush gradient)
        {
            var start = new PointF(bounds.Left + (float)gradient.StartPoint.X * bounds.Width, bounds.Top + (float)gradient.StartPoint.Y * bounds.Height);
            var end = new PointF(bounds.Left + (float)gradient.EndPoint.X * bounds.Width, bounds.Top + (float)gradient.EndPoint.Y * bounds.Height);
            var native = new LinearGradientBrush(start, end, Color.Black, Color.White)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = gradient.GradientStops.Select(stop => DrawingColor(stop.Color)).ToArray(),
                    Positions = gradient.GradientStops.Select(stop => (float)stop.Offset).ToArray(),
                },
            };
            return native;
        }

        if (brush is System.Windows.Media.RadialGradientBrush radial)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(bounds);
            return new PathGradientBrush(path)
            {
                CenterColor = DrawingColor(radial.GradientStops[0].Color),
                SurroundColors = [DrawingColor(radial.GradientStops[^1].Color)],
            };
        }

        return new SolidBrush(brush is System.Windows.Media.SolidColorBrush solid ? DrawingColor(solid.Color) : Color.Gray);
    }
    private static Color DrawingColor(System.Windows.Media.Color color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}

/// <summary>Owns the notification-area icon and mirrors the ball/tray commands.</summary>
public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _toggleItem;
    private Icon? _icon;
    private bool _disposed;
    private ProviderIconState _state;
    private AppLanguage _language;
    private bool _ballVisible = true;
    internal System.Windows.Forms.ContextMenuStrip Menu => _notifyIcon.ContextMenuStrip!;

    public TrayService(Action showDetails, Action refresh, Action showSettings, Action toggleVisibility, Action exit, AppLanguage language = AppLanguage.SimplifiedChinese)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(CreateItem(AppText.Format(language, "Details"), showDetails));
        menu.Items.Add(CreateItem(AppText.Format(language, "RefreshNow"), refresh));
        menu.Items.Add(CreateItem(AppText.Format(language, "Settings"), showSettings));
        _toggleItem = CreateItem(AppText.Format(language, "HideBall"), toggleVisibility);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(CreateItem(AppText.Format(language, "Exit"), exit));

        _state = new ProviderIconState(ProviderIconAppearance.Codex, null, RefreshStatus.Loading, ProviderIconState.CurrentThemeIsDark);
        _icon = AppIcon.Create(_state);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Text = AppText.Format(language, "BallAccessible"),
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => showDetails();
        ApplyLanguage(language);
    }

    public void SetBallVisible(bool visible)
    {
        _ballVisible = visible;
        _toggleItem.Text = AppText.Format(_language, visible ? "HideBall" : "ShowBall");
    }

    public void ApplyLanguage(AppLanguage language)
    {
        _language = language;
        Menu.Items[0].Text = AppText.Format(language, "Details");
        Menu.Items[1].Text = AppText.Format(language, "RefreshNow");
        Menu.Items[2].Text = AppText.Format(language, "Settings");
        Menu.Items[5].Text = AppText.Format(language, "Exit");
        _notifyIcon.Text = AppText.Format(language, "BallAccessible");
        SetBallVisible(_ballVisible);
    }

    public void ShowLowQuotaNotification(int remaining, int threshold)
    {
        _notifyIcon.BalloonTipTitle = AppText.Format(_language, "QuotaAlertTitle");
        _notifyIcon.BalloonTipText = AppText.Format(_language, "QuotaAlertBody", remaining, threshold);
        _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(5000);
    }

    internal void UpdateIcon(ProviderIconState state)
    {
        if (_disposed || _state == state) { return; }
        Icon replacement = AppIcon.Create(state);
        _notifyIcon.Icon = replacement;
        _icon?.Dispose();
        _icon = replacement;
        _state = state;
    }

    private static System.Windows.Forms.ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon?.Dispose();
        _icon = null;
    }
}
