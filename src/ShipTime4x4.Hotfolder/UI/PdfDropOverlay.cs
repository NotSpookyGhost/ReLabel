using System.Drawing.Drawing2D;

namespace ShipTime4x4.Hotfolder.UI;

internal sealed class PdfDropOverlay : Control
{
    private int _pdfCount;
    private int _ignoredCount;
    internal bool IsActive { get; private set; }

    public PdfDropOverlay()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(242, 245, 255);
        ForeColor = Color.FromArgb(67, 92, 245);
        AccessibleName = "PDF import drop area";
        TabStop = false;
        Visible = false;
    }

    internal void ShowFiles(int pdfCount, int ignoredCount)
    {
        if (IsActive && _pdfCount == pdfCount && _ignoredCount == ignoredCount) return;
        _pdfCount = pdfCount;
        _ignoredCount = ignoredCount;
        IsActive = true;
        AccessibleDescription = $"Drop {pdfCount} PDF file(s) to import. Original files stay untouched.";
        Visible = true;
        BringToFront();
        Invalidate();
    }

    internal void HideOverlay()
    {
        IsActive = false;
        Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var inset = 22f * scale;
        var border = new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset);
        if (border.Width <= 0 || border.Height <= 0) return;
        using var pen = new Pen(ForeColor, 3f * scale) { DashStyle = DashStyle.Dash };
        graphics.DrawRectangle(pen, border.X, border.Y, border.Width, border.Height);

        var centerX = Width / 2f;
        var top = Height / 2f - 105f * scale;
        // A down arrow and tray: code-drawn so the icon stays sharp at all DPI settings.
        using var iconPen = new Pen(ForeColor, 4f * scale)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        graphics.DrawLine(iconPen, centerX, top, centerX, top + 42f * scale);
        graphics.DrawLines(iconPen, [
            new PointF(centerX - 16f * scale, top + 26f * scale),
            new PointF(centerX, top + 42f * scale),
            new PointF(centerX + 16f * scale, top + 26f * scale)]);
        graphics.DrawLines(iconPen, [
            new PointF(centerX - 34f * scale, top + 42f * scale),
            new PointF(centerX - 34f * scale, top + 62f * scale),
            new PointF(centerX + 34f * scale, top + 62f * scale),
            new PointF(centerX + 34f * scale, top + 42f * scale)]);

        using var titleFont = new Font("Segoe UI Semibold", 22f);
        using var detailFont = new Font("Segoe UI", 11f);
        var title = _pdfCount == 1 ? "Drop PDF to import" : $"Drop {_pdfCount} PDFs to import";
        var details = "Release to add your labels to ReLabel. Original files stay untouched.";
        if (_ignoredCount > 0)
            details += $"\n{_ignoredCount} non-PDF item(s) will be ignored.";
        var textWidth = Math.Max(1, (int)(Width - 4 * inset));
        var textLeft = (Width - textWidth) / 2;
        TextRenderer.DrawText(graphics, title, titleFont,
            new Rectangle(textLeft, (int)(top + 85f * scale), textWidth, (int)(55f * scale)),
            ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        TextRenderer.DrawText(graphics, details, detailFont,
            new Rectangle(textLeft, (int)(top + 145f * scale), textWidth, (int)(85f * scale)),
            Color.FromArgb(78, 89, 118), TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
}
