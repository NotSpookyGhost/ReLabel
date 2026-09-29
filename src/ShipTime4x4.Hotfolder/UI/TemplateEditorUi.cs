using System.Drawing.Drawing2D;
using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.UI;

internal enum EditorToolIcon
{
    Select, From, To, CarrierBarcode, ShippingBarcode, Logo, Other, Line,
    AutoLayout, Clear, Preview, Test, ZoomIn, ZoomOut, Fit,
    Lock, Unlock, Duplicate, Delete, Stage, StretchEdges, Above, Below,
    Rename, Rotate, AlignLeft, AlignRight, AlignTop, AlignBottom,
    AlignHorizontalCenter, AlignVerticalCenter, MarginLeft, MarginRight,
    MarginTop, MarginBottom, CanvasHorizontalCenter, CanvasVerticalCenter
}

internal static class TemplateEditorPalette
{
    public static readonly Color Ink = Color.FromArgb(38, 45, 62);
    public static readonly Color Palette = Color.FromArgb(43, 49, 65);
    public static readonly Color PaletteHover = Color.FromArgb(65, 75, 101);
    public static readonly Color Accent = Color.FromArgb(67, 92, 245);

    public static Color ZoneColor(TemplateZoneType type) => type switch
    {
        TemplateZoneType.FromAddress => Color.FromArgb(222, 92, 155),
        TemplateZoneType.ToAddress => Color.FromArgb(78, 174, 74),
        TemplateZoneType.CarrierBarcode => Color.FromArgb(44, 115, 230),
        TemplateZoneType.ShippingBarcode => Color.FromArgb(36, 90, 186),
        TemplateZoneType.CarrierLogo => Color.FromArgb(142, 87, 210),
        TemplateZoneType.BuiltInAsset => Color.FromArgb(56, 134, 112),
        TemplateZoneType.DividerLine => Color.FromArgb(45, 48, 55),
        _ => Color.FromArgb(224, 145, 32)
    };
}

internal sealed class EditorToolButton : Button
{
    private readonly EditorToolIcon _icon;
    private readonly Color? _zoneColor;

    public EditorToolButton(EditorToolIcon icon, Color? zoneColor = null)
    {
        _icon = icon; _zoneColor = zoneColor;
        Size = new Size(34, 34); Margin = new Padding(3); Padding = Padding.Empty;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = TemplateEditorPalette.Palette; ForeColor = Color.White;
        FlatAppearance.MouseOverBackColor = TemplateEditorPalette.PaletteHover;
        FlatAppearance.MouseDownBackColor = TemplateEditorPalette.Accent;
        Cursor = Cursors.Hand; TabStop = true; UseVisualStyleBackColor = false;
    }

    public bool Active
    {
        get => BackColor == TemplateEditorPalette.Accent;
        set => BackColor = value ? TemplateEditorPalette.Accent : TemplateEditorPalette.Palette;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);
        var g = e.Graphics; var c = ClientRectangle;
        using var white = new Pen(Color.White, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(Color.White);
        using var accent = new SolidBrush(_zoneColor ?? Color.White);
        var cx = c.Width / 2f; var cy = c.Height / 2f;
        switch (_icon)
        {
            case EditorToolIcon.Select:
                g.FillPolygon(fill, [new PointF(10, 7), new PointF(25, 19), new PointF(18, 20), new PointF(22, 28), new PointF(18, 30), new PointF(14, 22), new PointF(9, 27)]); break;
            case EditorToolIcon.From: DrawBadge(g, accent, "F"); break;
            case EditorToolIcon.To: DrawBadge(g, accent, "T"); break;
            case EditorToolIcon.CarrierBarcode: DrawBarcode(g, white, 8, 25, false); break;
            case EditorToolIcon.ShippingBarcode: DrawBarcode(g, white, 6, 27, true); break;
            case EditorToolIcon.Logo: g.DrawEllipse(white, 8, 8, 18, 18); g.DrawLine(white, 10, 23, 16, 15); g.DrawLine(white, 16, 15, 20, 20); g.DrawLine(white, 20, 20, 25, 12); break;
            case EditorToolIcon.Other: DrawBadge(g, accent, "+"); break;
            case EditorToolIcon.Line: g.DrawLine(white, 7, 25, 27, 9); break;
            case EditorToolIcon.AutoLayout: g.DrawRectangle(white, 7, 7, 20, 20); g.DrawRectangle(white, 10, 10, 7, 6); g.DrawRectangle(white, 19, 10, 5, 13); g.DrawRectangle(white, 10, 18, 7, 5); break;
            case EditorToolIcon.Clear: g.DrawRectangle(white, 8, 8, 18, 18); g.DrawLine(white, 9, 25, 25, 9); break;
            case EditorToolIcon.Preview: g.DrawEllipse(white, 7, 11, 20, 12); g.FillEllipse(fill, 14, 14, 6, 6); break;
            case EditorToolIcon.Test: g.DrawLine(white, 9, 25, 25, 9); g.DrawLine(white, 19, 8, 26, 15); g.DrawLine(white, 8, 19, 15, 26); break;
            case EditorToolIcon.ZoomIn: DrawMagnifier(g, white, true); break;
            case EditorToolIcon.ZoomOut: DrawMagnifier(g, white, false); break;
            case EditorToolIcon.Fit: g.DrawRectangle(white, 8, 8, 18, 18); g.DrawLine(white, 8, 13, 8, 8); g.DrawLine(white, 8, 8, 13, 8); g.DrawLine(white, 21, 26, 26, 26); g.DrawLine(white, 26, 21, 26, 26); break;
            case EditorToolIcon.Lock: DrawLock(g, white, true); break;
            case EditorToolIcon.Unlock: DrawLock(g, white, false); break;
            case EditorToolIcon.Duplicate: g.DrawRectangle(white, 7, 7, 13, 13); g.DrawRectangle(white, 14, 14, 13, 13); break;
            case EditorToolIcon.Delete: g.DrawRectangle(white, 10, 11, 14, 16); g.DrawLine(white, 8, 9, 26, 9); g.DrawLine(white, 14, 6, 20, 6); break;
            case EditorToolIcon.Stage: g.DrawLine(white, 7, 17, 25, 17); g.DrawLine(white, 20, 12, 25, 17); g.DrawLine(white, 20, 22, 25, 17); break;
            case EditorToolIcon.StretchEdges: g.DrawLine(white, 5, 17, 29, 17); g.DrawLine(white, 5, 10, 5, 24); g.DrawLine(white, 29, 10, 29, 24); g.DrawLine(white, 9, 13, 5, 17); g.DrawLine(white, 9, 21, 5, 17); g.DrawLine(white, 25, 13, 29, 17); g.DrawLine(white, 25, 21, 29, 17); break;
            case EditorToolIcon.Above: DrawLayer(g, white, true); break;
            case EditorToolIcon.Below: DrawLayer(g, white, false); break;
            case EditorToolIcon.Rename: g.DrawLine(white, 8, 26, 25, 9); g.DrawLine(white, 20, 8, 26, 14); g.DrawLine(white, 7, 27, 13, 25); break;
            case EditorToolIcon.Rotate: g.DrawArc(white, 7, 7, 20, 20, 35, 285); g.FillPolygon(fill, [new PointF(25, 5), new PointF(28, 13), new PointF(20, 10)]); break;
            default: DrawAlignment(g, white, _icon); break;
        }
        if (_zoneColor.HasValue) using (var p = new Pen(_zoneColor.Value, 2)) g.DrawRectangle(p, 2, 2, Width - 5, Height - 5);
    }

    private static void DrawBadge(Graphics g, Brush brush, string text)
    {
        g.FillRectangle(brush, 7, 7, 20, 20);
        using var f = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        var size = g.MeasureString(text, f); g.DrawString(text, f, Brushes.White, 17 - size.Width / 2, 17 - size.Height / 2);
    }
    private static void DrawBarcode(Graphics g, Pen p, int left, int right, bool wide)
    { for (var x = left; x <= right; x += wide ? 3 : 2) { p.Width = x % 4 == 0 ? 2 : 1; g.DrawLine(p, x, 8, x, 26); } p.Width = 1.7f; }
    private static void DrawMagnifier(Graphics g, Pen p, bool plus)
    { g.DrawEllipse(p, 7, 7, 15, 15); g.DrawLine(p, 20, 20, 28, 28); g.DrawLine(p, 11, 14, 18, 14); if (plus) g.DrawLine(p, 14.5f, 10.5f, 14.5f, 17.5f); }
    private static void DrawLock(Graphics g, Pen p, bool closed)
    { g.DrawRectangle(p, 9, 15, 16, 13); g.DrawArc(p, closed ? 11 : 15, 7, 12, 14, 180, closed ? 180 : 115); }
    private static void DrawLayer(Graphics g, Pen p, bool above)
    { g.DrawRectangle(p, 8, above ? 7 : 16, 16, 10); g.DrawRectangle(p, 11, above ? 18 : 7, 16, 10); }
    private static void DrawAlignment(Graphics g, Pen p, EditorToolIcon icon)
    {
        var vertical = icon is EditorToolIcon.AlignLeft or EditorToolIcon.AlignRight or EditorToolIcon.AlignHorizontalCenter or EditorToolIcon.MarginLeft or EditorToolIcon.MarginRight or EditorToolIcon.CanvasHorizontalCenter;
        if (vertical)
        {
            var x = icon is EditorToolIcon.AlignRight or EditorToolIcon.MarginRight ? 25 : icon is EditorToolIcon.AlignLeft or EditorToolIcon.MarginLeft ? 9 : 17;
            g.DrawLine(p, x, 6, x, 28); g.DrawLine(p, icon is EditorToolIcon.AlignRight ? 11 : x, 11, icon is EditorToolIcon.AlignLeft ? 24 : x, 11); g.DrawLine(p, icon is EditorToolIcon.AlignRight ? 7 : x, 21, icon is EditorToolIcon.AlignLeft ? 28 : x, 21);
        }
        else
        {
            var y = icon is EditorToolIcon.AlignBottom or EditorToolIcon.MarginBottom ? 25 : icon is EditorToolIcon.AlignTop or EditorToolIcon.MarginTop ? 9 : 17;
            g.DrawLine(p, 6, y, 28, y); g.DrawLine(p, 11, icon is EditorToolIcon.AlignBottom ? 11 : y, 11, icon is EditorToolIcon.AlignTop ? 24 : y); g.DrawLine(p, 21, icon is EditorToolIcon.AlignBottom ? 7 : y, 21, icon is EditorToolIcon.AlignTop ? 28 : y);
        }
    }
}

internal sealed class StagingTagButton : Button
{
    public StagingTagButton() => SetStyle(ControlStyles.StandardClick |
        ControlStyles.StandardDoubleClick, true);
}
