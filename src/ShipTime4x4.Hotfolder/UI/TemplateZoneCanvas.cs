using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.UI;

internal sealed record CanvasSelection(IReadOnlyList<Guid> Ids, Guid? PrimaryId,
    bool ContextRequested = false, Point ContextPoint = default);
internal sealed record CanvasRectangles(IReadOnlyDictionary<Guid, NormalizedRect> Rectangles);
internal sealed record CanvasZoneCreated(TemplateZoneType Type, NormalizedRect Rectangle);

internal sealed class TemplateZoneCanvas : Control
{
    private enum DragMode { None, Move, ResizeTopLeft, ResizeBottomRight, Create, Pan }
    private readonly bool _destination;
    private Bitmap? _image;
    private IReadOnlyList<TemplateZone> _zones = [];
    private readonly HashSet<Guid> _selected = [];
    private Guid? _primary;
    private Point _start;
    private readonly Dictionary<Guid, NormalizedRect> _startRects = [];
    private DragMode _dragMode;
    private NormalizedRect _creationRect = new(0, 0, 0, 0);
    private double _zoom = 1;
    private PointF _pan;

    public TemplateZoneCanvas(bool destination)
    {
        _destination = destination;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(236, 240, 247);
        Cursor = Cursors.Default;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }

    public event EventHandler<CanvasSelection>? SelectionChangedDetailed;
    public event EventHandler<CanvasRectangles>? RectanglesChangedDetailed;
    public event EventHandler<CanvasZoneCreated>? ZoneCreated;
    public event EventHandler? ManipulationStarted;
    public event EventHandler? ManipulationCompleted;
    public bool ShowZoneOverlays { get; set; } = true;
    public TemplateZoneType? CreationType { get; set; }
    public double Zoom => _zoom;

    public void SetContent(Bitmap image, IReadOnlyList<TemplateZone> zones, IEnumerable<Guid>? selected = null,
        Guid? primary = null)
    {
        _image?.Dispose(); _image = new Bitmap(image); _zones = zones;
        SetSelection(selected ?? [], primary, false); Invalidate();
    }

    public void SetZones(IReadOnlyList<TemplateZone> zones) { _zones = zones; Invalidate(); }

    public void SetSelection(IEnumerable<Guid> ids, Guid? primary = null, bool notify = false)
    {
        _selected.Clear(); foreach (var id in ids) _selected.Add(id);
        _primary = primary.HasValue && _selected.Contains(primary.Value) ? primary : _selected.LastOrDefault();
        if (_selected.Count == 0) _primary = null;
        if (notify) RaiseSelection(); Invalidate();
    }

    public void FitToWindow() { _zoom = 1; _pan = PointF.Empty; Invalidate(); ManipulationCompleted?.Invoke(this, EventArgs.Empty); }
    public void ZoomIn() => SetZoom(_zoom * 1.2, new Point(Width / 2, Height / 2));
    public void ZoomOut() => SetZoom(_zoom / 1.2, new Point(Width / 2, Height / 2));
    public Rectangle GetZoneScreenBounds(Guid id)
    {
        var zone = _zones.FirstOrDefault(x => x.Id == id);
        return zone is null ? Rectangle.Empty : ToClient(_destination ? zone.Destination4x4 : zone.Source);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(BackColor); if (_image is null) return;
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        var canvas = ImageBounds(); e.Graphics.DrawImage(_image, canvas);
        using (var edge = new Pen(Color.FromArgb(122, 132, 148), 1)) e.Graphics.DrawRectangle(edge, canvas);
        if (_destination)
        {
            var safe = ToClient(new NormalizedRect(.03125, .03125, .9375, .9375));
            using var margin = new Pen(Color.FromArgb(228, 54, 68), 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            e.Graphics.DrawRectangle(margin, safe);
        }
        if (!ShowZoneOverlays) return;
        foreach (var zone in VisibleZones()) DrawZone(e.Graphics, zone);
        if (_dragMode == DragMode.Create && _creationRect.Width > 0 && _creationRect.Height > 0)
        {
            using var pen = new Pen(TemplateEditorPalette.ZoneColor(CreationType ?? TemplateZoneType.OtherSection), 2)
                { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, ToClient(_creationRect));
        }
    }

    private void DrawZone(Graphics graphics, TemplateZone zone)
    {
        var rect = ToClient(_destination ? zone.Destination4x4 : zone.Source);
        var color = TemplateEditorPalette.ZoneColor(zone.Type); var selected = _selected.Contains(zone.Id);
        using var pen = new Pen(color, selected ? 3 : zone.Suggested ? 2 : 1)
            { DashStyle = zone.Suggested ? System.Drawing.Drawing2D.DashStyle.Dash : System.Drawing.Drawing2D.DashStyle.Solid };
        graphics.DrawRectangle(pen, rect);
        using var fill = new SolidBrush(Color.FromArgb(selected ? 48 : 22, color)); graphics.FillRectangle(fill, rect);
        var label = zone.Locked ? $"{zone.DisplayName}  LOCKED" : zone.DisplayName;
        var size = graphics.MeasureString(label, Font); using var labelFill = new SolidBrush(color);
        var tag = new RectangleF(rect.X, rect.Y, Math.Min(Math.Max(22, rect.Width), size.Width + 8),
            Math.Min(Math.Max(12, rect.Height), size.Height + 3));
        graphics.FillRectangle(labelFill, tag); graphics.DrawString(label, Font, Brushes.White, tag.X + 4, tag.Y + 1);
        if (selected && !zone.Locked)
        {
            graphics.FillRectangle(labelFill, rect.X - 5, rect.Y - 5, 10, 10);
            graphics.FillRectangle(labelFill, rect.Right - 5, rect.Bottom - 5, 10, 10);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); _start = e.Location;
        if (e.Button == MouseButtons.Middle)
        {
            _dragMode = DragMode.Pan; Capture = true;
            ManipulationStarted?.Invoke(this, EventArgs.Empty); return;
        }
        var hit = HitZone(e.Location, includeLocked: e.Button == MouseButtons.Right);
        if (e.Button == MouseButtons.Right)
        {
            if (hit is null) return;
            if (!_selected.Contains(hit.Id)) { _selected.Clear(); _selected.Add(hit.Id); _primary = hit.Id; }
            RaiseSelection(true, PointToScreen(e.Location)); Invalidate(); return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (hit is null)
        {
            if (!_destination && CreationType.HasValue && PointInsideImage(e.Location))
            {
                _dragMode = DragMode.Create; _creationRect = PointRect(e.Location); Capture = true;
                ManipulationStarted?.Invoke(this, EventArgs.Empty); return;
            }
            _selected.Clear(); _primary = null; RaiseSelection(); Invalidate(); return;
        }
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            if (!_selected.Add(hit.Id)) _selected.Remove(hit.Id);
            _primary = _selected.Contains(hit.Id) ? hit.Id : _selected.LastOrDefault();
            RaiseSelection(); Invalidate(); return;
        }
        if (!_selected.Contains(hit.Id)) { _selected.Clear(); _selected.Add(hit.Id); }
        _primary = hit.Id; _startRects.Clear();
        foreach (var zone in VisibleZones().Where(z => _selected.Contains(z.Id) && !z.Locked))
            _startRects[zone.Id] = _destination ? zone.Destination4x4 : zone.Source;
        var client = ToClient(_destination ? hit.Destination4x4 : hit.Source);
        _dragMode = TopLeftHandle(client).Contains(e.Location) ? DragMode.ResizeTopLeft :
            BottomRightHandle(client).Contains(e.Location) ? DragMode.ResizeBottomRight : DragMode.Move;
        Capture = true; RaiseSelection(); ManipulationStarted?.Invoke(this, EventArgs.Empty); Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragMode == DragMode.Pan)
        {
            _pan = new PointF(_pan.X + e.X - _start.X, _pan.Y + e.Y - _start.Y); _start = e.Location; Invalidate(); return;
        }
        if (_dragMode == DragMode.Create)
        {
            var first = ToNormalized(_start); var current = ToNormalized(e.Location);
            _creationRect = Clamp(new NormalizedRect(Math.Min(first.X, current.X), Math.Min(first.Y, current.Y),
                Math.Abs(first.X - current.X), Math.Abs(first.Y - current.Y)), .006); Invalidate(); return;
        }
        if (_dragMode != DragMode.None && _startRects.Count > 0)
        {
            var bounds = ImageBounds(); var dx = (e.X - _start.X) / Math.Max(1d, bounds.Width);
            var dy = (e.Y - _start.Y) / Math.Max(1d, bounds.Height); var changed = new Dictionary<Guid, NormalizedRect>();
            foreach (var (id, start) in _startRects)
            {
                var zone = _zones.First(z => z.Id == id); var minimum = zone.Type == TemplateZoneType.DividerLine ? .003 : .012;
                var rect = _dragMode switch
                {
                    DragMode.ResizeBottomRight => start with { Width = Math.Clamp(start.Width + dx, minimum, 1 - start.X), Height = Math.Clamp(start.Height + dy, minimum, 1 - start.Y) },
                    DragMode.ResizeTopLeft => ResizeTopLeft(start, dx, dy, minimum),
                    _ => start with { X = Math.Clamp(start.X + dx, 0, 1 - start.Width), Y = Math.Clamp(start.Y + dy, 0, 1 - start.Height) }
                };
                changed[id] = rect;
            }
            RectanglesChangedDetailed?.Invoke(this, new CanvasRectangles(changed));
            return;
        }
        UpdateCursor(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var created = _dragMode == DragMode.Create && CreationType.HasValue && _creationRect.Width >= .008 && _creationRect.Height >= .008;
        var changed = _dragMode is DragMode.Move or DragMode.ResizeTopLeft or DragMode.ResizeBottomRight;
        var panned = _dragMode == DragMode.Pan;
        _dragMode = DragMode.None; Capture = false;
        if (created) ZoneCreated?.Invoke(this, new CanvasZoneCreated(CreationType!.Value, _creationRect));
        _creationRect = new NormalizedRect(0, 0, 0, 0);
        if (created || changed || panned) ManipulationCompleted?.Invoke(this, EventArgs.Empty);
        UpdateCursor(e.Location); Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e); SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    { base.OnMouseLeave(e); if (_dragMode == DragMode.None) Cursor = Cursors.Default; }

    private void SetZoom(double value, Point focus)
    {
        if (_image is null) return; var old = ImageBounds(); var nx = (focus.X - old.X) / Math.Max(1d, old.Width);
        var ny = (focus.Y - old.Y) / Math.Max(1d, old.Height); _zoom = Math.Clamp(value, .75, 5);
        var next = ImageBounds(); _pan = new PointF(_pan.X + focus.X - (next.X + (float)(nx * next.Width)),
            _pan.Y + focus.Y - (next.Y + (float)(ny * next.Height))); Invalidate();
        ManipulationCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCursor(Point point)
    {
        var hit = HitZone(point, false); if (hit is null) { Cursor = !_destination && CreationType.HasValue ? Cursors.Cross : Cursors.Default; return; }
        var rect = ToClient(_destination ? hit.Destination4x4 : hit.Source);
        Cursor = TopLeftHandle(rect).Contains(point) || BottomRightHandle(rect).Contains(point) ? Cursors.SizeNWSE : Cursors.SizeAll;
    }

    private void RaiseSelection(bool context = false, Point contextPoint = default)
    {
        SelectionChangedDetailed?.Invoke(this, new CanvasSelection(_selected.ToArray(), _primary, context, contextPoint));
    }
    private IEnumerable<TemplateZone> VisibleZones() => _destination ? _zones.Where(z => z.IsPlaced4x4) : _zones.Where(z => z.HasSourceRegion);
    private TemplateZone? HitZone(Point point, bool includeLocked) => !ShowZoneOverlays ? null :
        VisibleZones().Reverse().FirstOrDefault(zone => (includeLocked || !zone.Locked) && ToClient(_destination ? zone.Destination4x4 : zone.Source).Contains(point));
    private bool PointInsideImage(Point point) => ImageBounds().Contains(point);
    private static Rectangle TopLeftHandle(Rectangle rect) => new(rect.X - 9, rect.Y - 9, 18, 18);
    private static Rectangle BottomRightHandle(Rectangle rect) => new(rect.Right - 9, rect.Bottom - 9, 18, 18);

    private Rectangle ImageBounds()
    {
        if (_image is null) return ClientRectangle;
        var scale = Math.Min(Math.Max(1, Width - 18) / (double)_image.Width, Math.Max(1, Height - 18) / (double)_image.Height) * _zoom;
        var width = Math.Max(1, (int)Math.Round(_image.Width * scale)); var height = Math.Max(1, (int)Math.Round(_image.Height * scale));
        return new Rectangle((int)Math.Round((Width - width) / 2d + _pan.X), (int)Math.Round((Height - height) / 2d + _pan.Y), width, height);
    }
    private Rectangle ToClient(NormalizedRect rect)
    { var b = ImageBounds(); return new Rectangle(b.X + (int)Math.Round(rect.X * b.Width), b.Y + (int)Math.Round(rect.Y * b.Height), Math.Max(1, (int)Math.Round(rect.Width * b.Width)), Math.Max(1, (int)Math.Round(rect.Height * b.Height))); }
    private NormalizedRect PointRect(Point point) { var n = ToNormalized(point); return new NormalizedRect(n.X, n.Y, 0, 0); }
    private PointF ToNormalized(Point point)
    { var b = ImageBounds(); return new PointF((float)Math.Clamp((point.X - b.X) / Math.Max(1d, b.Width), 0, 1), (float)Math.Clamp((point.Y - b.Y) / Math.Max(1d, b.Height), 0, 1)); }
    private static NormalizedRect ResizeTopLeft(NormalizedRect start, double dx, double dy, double minimum)
    { var x = Math.Clamp(start.X + dx, 0, start.Right - minimum); var y = Math.Clamp(start.Y + dy, 0, start.Bottom - minimum); return new NormalizedRect(x, y, start.Right - x, start.Bottom - y); }
    private static NormalizedRect Clamp(NormalizedRect value, double minimum)
    { var x = Math.Clamp(value.X, 0, 1 - minimum); var y = Math.Clamp(value.Y, 0, 1 - minimum); return new NormalizedRect(x, y, Math.Clamp(value.Width, minimum, 1 - x), Math.Clamp(value.Height, minimum, 1 - y)); }

    protected override void Dispose(bool disposing) { if (disposing) _image?.Dispose(); base.Dispose(disposing); }
}
