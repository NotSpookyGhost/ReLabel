using System.Drawing.Drawing2D;

namespace ShipTime4x4.Hotfolder.UI;

internal sealed class EmptyLabelState : Panel
{
    private readonly ImportButton _import = new();
    private readonly Label _message = new()
    {
        Text = "Drag and drop PDF files here\nor add them to the hotfolder.",
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(112, 122, 145),
        Font = new Font("Segoe UI", 11f)
    };

    public event EventHandler? ImportRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ImportEnabled { get => _import.Enabled; set => _import.Enabled = value; }

    public EmptyLabelState()
    {
        DoubleBuffered = true;
        _import.Click += (_, _) => ImportRequested?.Invoke(this, EventArgs.Empty);
        Controls.AddRange([_import, _message]);
        AccessibleName = "Import labels";
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var scale = DeviceDpi / 96f;
        var buttonSize = (int)(64 * scale);
        var gap = (int)(16 * scale);
        var textWidth = Math.Max(1, Math.Min((int)(420 * scale), ClientSize.Width - (int)(24 * scale)));
        var textHeight = TextRenderer.MeasureText(_message.Text, _message.Font,
            new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + (int)(8 * scale);
        var top = Math.Max(0, (ClientSize.Height - buttonSize - gap - textHeight) / 2);
        _import.SetBounds((ClientSize.Width - buttonSize) / 2, top, buttonSize, buttonSize);
        _message.SetBounds((ClientSize.Width - textWidth) / 2, top + buttonSize + gap, textWidth, textHeight);
    }

    private sealed class ImportButton : Button
    {
        private bool _hovered;

        public ImportButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            AccessibleName = "Import PDF files";
            AccessibleDescription = "Choose one or more PDF files to import.";
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var scale = Math.Min(Width, Height) / 64f;
            if (scale <= 0) return;
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var inset = 2 * scale;
            var diameter = 22 * scale;
            var bounds = new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset);
            using var outline = new GraphicsPath();
            outline.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            outline.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            outline.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            outline.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            outline.CloseFigure();
            var active = Enabled && (_hovered || Focused);
            using var fill = new SolidBrush(active ? Color.FromArgb(235, 240, 255) : BackColor);
            using var border = new Pen(active ? Color.FromArgb(67, 92, 245) : Color.FromArgb(193, 203, 224), 1.6f * scale);
            e.Graphics.FillPath(fill, outline);
            e.Graphics.DrawPath(border, outline);
            using var plus = new Pen(Enabled ? Color.FromArgb(112, 122, 145) : SystemColors.GrayText, 2.5f * scale)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round
            };
            var x = Width / 2f;
            var y = Height / 2f;
            e.Graphics.DrawLine(plus, x - 12 * scale, y, x + 12 * scale, y);
            e.Graphics.DrawLine(plus, x, y - 12 * scale, x, y + 12 * scale);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -(int)(7 * scale), -(int)(7 * scale)));
        }
    }
}
