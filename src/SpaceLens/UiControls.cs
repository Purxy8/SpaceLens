using System.Drawing.Drawing2D;

namespace DesktopOrganizer;

internal static class AppTheme
{
    internal static readonly Color Canvas = Color.FromArgb(243, 247, 252);
    internal static readonly Color Text = Color.FromArgb(24, 38, 58);
    internal static readonly Color Muted = Color.FromArgb(92, 109, 132);
    internal static readonly Color Border = Color.FromArgb(218, 227, 239);
    internal static readonly Color Primary = Color.FromArgb(22, 119, 210);
    internal static readonly Color Teal = Color.FromArgb(26, 166, 154);
    internal static readonly Color Green = Color.FromArgb(24, 151, 103);
    internal static readonly Color Amber = Color.FromArgb(224, 145, 36);
    internal static readonly Color Violet = Color.FromArgb(115, 92, 204);

    internal static Color Blend(Color first, Color second, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(first.R + (second.R - first.R) * amount),
            (int)(first.G + (second.G - first.G) * amount),
            (int)(first.B + (second.B - first.B) * amount));
    }
}

internal sealed class GradientHeaderPanel : Panel
{
    internal GradientHeaderPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0) return;
        using var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(18, 29, 49), Color.FromArgb(30, 70, 108), LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}

internal sealed class ActivityStrip : Control
{
    // A subtle ~11 FPS sweep is visually smooth at four pixels high while
    // leaving more UI-thread budget for virtual-grid scrolling and filtering.
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 90 };
    private float position;
    private bool active;

    internal ActivityStrip()
    {
        DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); Height = 4; TabStop = false;
        timer.Tick += (_, _) => { position += 0.075f; if (position > 1.2f) position = -0.25f; if (IsHandleCreated && Visible) Invalidate(); };
    }

    internal void SetActive(bool value)
    {
        if (active == value) return; active = value;
        if (active) { position = -0.25f; timer.Start(); } else timer.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        e.Graphics.Clear(Color.FromArgb(211, 226, 242));
        if (!active)
        {
            using var idle = new LinearGradientBrush(ClientRectangle, AppTheme.Primary, AppTheme.Teal, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(idle, ClientRectangle); return;
        }
        int width = Math.Max(130, ClientSize.Width / 6), x = (int)((ClientSize.Width + width) * position) - width;
        var rectangle = new Rectangle(x, 0, width, Math.Max(1, Height));
        using var moving = new LinearGradientBrush(rectangle, Color.FromArgb(55, AppTheme.Primary), AppTheme.Teal, LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(moving, rectangle);
    }

    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

internal sealed class MetricCardPanel : Panel
{
    private readonly Color accent;
    private bool hovered;

    internal MetricCardPanel(string captionText, Label value, Color accentColor)
    {
        accent = accentColor; Dock = DockStyle.Fill; Margin = new Padding(5); Padding = new Padding(14, 8, 10, 7); BackColor = Color.Transparent; DoubleBuffered = true;
        var caption = new Label { Text = captionText, Dock = DockStyle.Top, Height = 23, BackColor = Color.Transparent, ForeColor = AppTheme.Muted, Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold) };
        value.BackColor = Color.Transparent; value.Padding = new Padding(0, 0, 0, 1);
        Controls.Add(value); Controls.Add(caption);
        void enter(object? _, EventArgs __) { hovered = true; Invalidate(); }
        void leave(object? _, EventArgs __) { if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return; hovered = false; Invalidate(); }
        MouseEnter += enter; MouseLeave += leave; caption.MouseEnter += enter; caption.MouseLeave += leave; value.MouseEnter += enter; value.MouseLeave += leave;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle; bounds.Inflate(-1, -1); if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using GraphicsPath path = RoundedRectangle(bounds, 10);
        using var fill = new SolidBrush(hovered ? AppTheme.Blend(Color.White, accent, 0.035f) : Color.White);
        using var border = new Pen(hovered ? AppTheme.Blend(AppTheme.Border, accent, 0.32f) : AppTheme.Border);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
        using var accentPen = new Pen(accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(accentPen, bounds.Left + 12, bounds.Top + 3, Math.Min(bounds.Right - 12, bounds.Left + 58), bounds.Top + 3);
        base.OnPaint(e);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        int diameter = radius * 2; var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}

internal sealed class ModernButton : Button
{
    private Color normalBack;
    private Color hoverBack;

    internal ModernButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; Height = 34; Padding = new Padding(12, 0, 12, 0); UseVisualStyleBackColor = false;
    }

    internal void SetPalette(Color back, Color fore)
    {
        normalBack = back; hoverBack = back.GetBrightness() > 0.82f ? AppTheme.Blend(back, Color.Black, 0.07f) : AppTheme.Blend(back, Color.White, 0.12f); BackColor = back; ForeColor = fore;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Enabled) BackColor = hoverBack; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); BackColor = normalBack; }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); BackColor = normalBack; Cursor = Enabled ? Cursors.Hand : Cursors.Default; }
}

internal sealed class SmoothDataGridView : DataGridView
{
    internal SmoothDataGridView() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
}
