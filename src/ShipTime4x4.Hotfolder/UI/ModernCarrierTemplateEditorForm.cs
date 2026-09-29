using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Hotfolder.UI;

public sealed class ModernCarrierTemplateEditorForm : Form
{
    private readonly CarrierTemplateEngine _engine;
    private readonly PdfInspection _inspection;
    private readonly TemplateZoneCanvas _source = new(false) { Dock = DockStyle.Fill };
    private readonly TemplateZoneCanvas _destination = new(true) { Dock = DockStyle.Fill };
    private readonly TextBox _carrier = new() { Width = 180 };
    private readonly TextBox _layout = new() { Width = 220 };
    private readonly FlowLayoutPanel _staging = new() { Dock = DockStyle.Fill, AutoScroll = true,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(7) };
    private readonly FlowLayoutPanel _contextTools = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true, MaximumSize = new Size(500, 82), Padding = new Padding(3), BackColor = TemplateEditorPalette.Palette };
    private readonly ToolTip _tips = new();
    private readonly Bitmap _original;
    private readonly CarrierTemplate _originalTemplate;
    private readonly Panel _sourceHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _destinationHost = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<EditorToolButton, TemplateZoneType?> _sourceModes = [];
    private List<TemplateZone> _zones;
    private readonly HashSet<Guid> _selected = [];
    private Guid? _primary;
    private TemplateZoneCanvas? _activeCanvas;
    private double _builtInScale;
    private bool _refreshing;

    public ModernCarrierTemplateEditorForm(CarrierTemplate template, PdfInspection inspection, CarrierTemplateEngine engine)
    {
        _originalTemplate = template; _inspection = inspection; _engine = engine;
        _zones = template.Zones.Select(x => x with { }).ToList();
        _builtInScale = template.BuiltInAssetScale is > .2 and < 5 ? template.BuiltInAssetScale : InferBuiltInScale(_zones);
        _original = GrayImageBitmapConverter.ToBitmap(CarrierTemplateEngine.NormalizePortrait(inspection.SourcePage));
        Text = "Carrier template editor"; Font = new Font("Segoe UI", 9.5f); BackColor = Color.FromArgb(245, 247, 251);
        StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(1080, 680);
        var working = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(1540, Math.Max(1080, working.Width - 40)), Math.Min(920, Math.Max(660, working.Height - 50)));
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        _carrier.Text = template.Carrier; _layout.Text = template.LayoutName;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(template), 0, 0); root.Controls.Add(BuildWorkspace(), 0, 1); Controls.Add(root);

        _source.SelectionChangedDetailed += CanvasSelectionChanged; _destination.SelectionChangedDetailed += CanvasSelectionChanged;
        _source.RectanglesChangedDetailed += CanvasRectanglesChanged; _destination.RectanglesChangedDetailed += CanvasRectanglesChanged;
        _source.ZoneCreated += SourceZoneCreated;
        _source.ManipulationStarted += (_, _) => _contextTools.Visible = false;
        _destination.ManipulationStarted += (_, _) => _contextTools.Visible = false;
        _source.ManipulationCompleted += (_, _) => FinishManipulation();
        _destination.ManipulationCompleted += (_, _) => FinishManipulation();
        Resize += (_, _) => { RefreshCanvases(); PositionContextTools(); };
        Shown += (_, _) => { RefreshCanvases(); _source.FitToWindow(); _destination.FitToWindow(); };
        RefreshCanvases();
    }

    public CarrierTemplate? Result { get; private set; }
    public event EventHandler<TemplateTestEventArgs>? TemplateTested;

    private Control BuildHeader(CarrierTemplate template)
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Padding = new Padding(18, 12, 18, 10), BackColor = Color.White };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var identity = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        identity.Controls.AddRange([Caption("Carrier"), _carrier, Caption("Layout name"), _layout,
            new Label { Text = $"Revision {template.Revision}", AutoSize = true, Margin = new Padding(18, 7, 0, 0), ForeColor = Color.FromArgb(92, 104, 119) }]);
        var cancel = TextButton("Cancel", 88, false); var save = TextButton("Save template", 126, true);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); }; save.Click += (_, _) => SaveTemplate();
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.AddRange([cancel, save]);
        header.Controls.Add(identity, 0, 0); header.Controls.Add(new Panel(), 1, 0); header.Controls.Add(actions, 2, 0);
        return header;
    }

    private Control BuildWorkspace()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 610, BackColor = Color.FromArgb(219, 224, 235), SplitterWidth = 6 };
        split.Panel1.Padding = new Padding(12, 10, 6, 10); split.Panel2.Padding = new Padding(6, 10, 12, 10);
        BuildCanvasPanel(_sourceHost, _source, BuildSourcePalette());
        split.Panel1.Controls.Add(CanvasHost("Original 4 x 6 - draw and refine source zones", _sourceHost));

        var destinationArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        destinationArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); destinationArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
        BuildCanvasPanel(_destinationHost, _destination, BuildDestinationPalette());
        _destinationHost.Controls.Add(_contextTools); _contextTools.Visible = false; _contextTools.BringToFront();
        destinationArea.Controls.Add(_destinationHost, 0, 0); destinationArea.Controls.Add(BuildStagingArea(), 1, 0);
        split.Panel2.Controls.Add(CanvasHost("4 x 4 destination - red line is the 0.125-inch safe margin", destinationArea));
        split.Resize += (_, _) => { if (split.Width > 900) split.SplitterDistance = (split.Width - split.SplitterWidth) / 2; };
        return split;
    }

    private static void BuildCanvasPanel(Panel host, Control canvas, Control palette)
    { host.Controls.Add(canvas); host.Controls.Add(palette); palette.Location = new Point(10, 10); palette.BringToFront(); }

    private Control BuildSourcePalette()
    {
        var palette = FloatingPalette();
        AddSourceMode(palette, EditorToolIcon.Select, null, "Select zones. Ctrl-click selects more than one zone.");
        AddSourceMode(palette, EditorToolIcon.From, TemplateZoneType.FromAddress, "Draw a From Address zone.");
        AddSourceMode(palette, EditorToolIcon.To, TemplateZoneType.ToAddress, "Draw a To Address zone.");
        AddSourceMode(palette, EditorToolIcon.CarrierBarcode, TemplateZoneType.CarrierBarcode, "Draw a Carrier Barcode zone.");
        AddSourceMode(palette, EditorToolIcon.ShippingBarcode, TemplateZoneType.ShippingBarcode, "Draw a Shipping Barcode zone.");
        AddSourceMode(palette, EditorToolIcon.Logo, TemplateZoneType.CarrierLogo, "Draw a Carrier Logo zone.");
        AddSourceMode(palette, EditorToolIcon.Other, TemplateZoneType.OtherSection, "Draw an Other Section zone.");
        AddPaletteButton(palette, EditorToolIcon.ZoomIn, "Zoom in", (_, _) => _source.ZoomIn());
        AddPaletteButton(palette, EditorToolIcon.ZoomOut, "Zoom out", (_, _) => _source.ZoomOut());
        AddPaletteButton(palette, EditorToolIcon.Fit, "Fit the original label to the window", (_, _) => _source.FitToWindow());
        _sourceModes.Keys.First().Active = true; return palette;
    }

    private Control BuildDestinationPalette()
    {
        var palette = FloatingPalette();
        AddPaletteButton(palette, EditorToolIcon.Select, "Select zones. Ctrl-click selects multiple zones.", (_, _) => _destination.CreationType = null, true);
        AddPaletteButton(palette, EditorToolIcon.Line, "Add a straight black divider line", (_, _) => AddDividerLine(), false, TemplateEditorPalette.ZoneColor(TemplateZoneType.DividerLine));
        AddPaletteButton(palette, EditorToolIcon.AutoLayout, "Automatically arrange staged zones using the original label's layout", (_, _) => AutoPlace());
        AddPaletteButton(palette, EditorToolIcon.Clear, "Move all destination zones back to staging", (_, _) => ClearDestination());
        AddPaletteButton(palette, EditorToolIcon.Preview, "Preview without editing boxes", (_, _) => PreviewLayout());
        AddPaletteButton(palette, EditorToolIcon.Test, "Test this template with another 4 x 6 PDF", (_, _) => TestTemplate());
        AddPaletteButton(palette, EditorToolIcon.ZoomIn, "Zoom in", (_, _) => _destination.ZoomIn());
        AddPaletteButton(palette, EditorToolIcon.ZoomOut, "Zoom out", (_, _) => _destination.ZoomOut());
        AddPaletteButton(palette, EditorToolIcon.Fit, "Fit the 4 x 4 label to the window", (_, _) => _destination.FitToWindow());
        return palette;
    }

    private Control BuildStagingArea()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(240, 243, 249), Padding = new Padding(8) };
        var title = new Label { Text = "STAGING", Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = TemplateEditorPalette.Accent };
        var hint = new Label { Text = "Double-click a colored tag to place it.", Dock = DockStyle.Top, Height = 38,
            ForeColor = Color.FromArgb(100, 110, 130) };
        host.Controls.Add(_staging); host.Controls.Add(hint); host.Controls.Add(title); return host;
    }

    private void AddSourceMode(FlowLayoutPanel palette, EditorToolIcon icon, TemplateZoneType? type, string tooltip)
    {
        Color? color = type.HasValue ? TemplateEditorPalette.ZoneColor(type.Value) : null;
        var button = new EditorToolButton(icon, color) { Active = type is null };
        button.Click += (_, _) =>
        {
            _source.CreationType = type; foreach (var pair in _sourceModes) pair.Key.Active = pair.Key == button;
        };
        _tips.SetToolTip(button, tooltip); palette.Controls.Add(button);
        _sourceModes[button] = type;
    }

    private EditorToolButton AddPaletteButton(FlowLayoutPanel palette, EditorToolIcon icon, string tooltip,
        EventHandler click, bool active = false, Color? zoneColor = null)
    {
        var button = new EditorToolButton(icon, zoneColor) { Active = active }; button.Click += click;
        _tips.SetToolTip(button, tooltip); palette.Controls.Add(button); return button;
    }

    private static FlowLayoutPanel FloatingPalette() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MaximumSize = new Size(44, 700), FlowDirection = FlowDirection.TopDown, WrapContents = false,
        Padding = new Padding(2), BackColor = TemplateEditorPalette.Palette };

    private void CanvasSelectionChanged(object? sender, CanvasSelection selection)
    {
        if (_refreshing) return; _activeCanvas = sender as TemplateZoneCanvas;
        _selected.Clear(); foreach (var id in selection.Ids) _selected.Add(id); _primary = selection.PrimaryId;
        _source.SetSelection(_selected, _primary); _destination.SetSelection(_selected, _primary);
        BuildContextTools(selection.ContextRequested); PositionContextTools();
    }

    private void CanvasRectanglesChanged(object? sender, CanvasRectangles changes)
    {
        _contextTools.Visible = false;
        var destination = ReferenceEquals(sender, _destination);
        foreach (var (id, rect) in changes.Rectangles)
        {
            var index = _zones.FindIndex(z => z.Id == id); if (index < 0 || _zones[index].Locked) continue;
            var zone = _zones[index];
            if (!destination) _zones[index] = zone with { Source = rect, Suggested = false };
            else if (zone.Type == TemplateZoneType.BuiltInAsset) ResizeBuiltInPair(id, rect);
            else _zones[index] = zone with { Destination4x4 = rect, Placed4x4 = true, Suggested = false };
        }
        _source.SetZones(_zones); _destination.SetZones(_zones);
    }

    private void FinishManipulation()
    {
        RefreshCanvases();
        BuildContextTools(false);
        PositionContextTools();
    }

    private void SourceZoneCreated(object? sender, CanvasZoneCreated created)
    {
        var number = _zones.Count(z => z.Type == TemplateZoneType.OtherSection) + 1;
        var zone = new TemplateZone { Type = created.Type, Source = created.Rectangle,
            Destination4x4 = new NormalizedRect(.08, .08, created.Rectangle.Width, Math.Min(.5, created.Rectangle.Height * 1.5)),
            OtherName = created.Type == TemplateZoneType.OtherSection ? $"Other Section {number}" : string.Empty,
            OtherPriority = TemplateOtherPriority.Required, Placed4x4 = false };
        _zones.Add(zone); SetSelection([zone.Id], zone.Id, _source); RefreshCanvases();
        if (zone.Type == TemplateZoneType.OtherSection) RenameOther(zone.Id, true);
    }

    private void SetSelection(IEnumerable<Guid> ids, Guid? primary, TemplateZoneCanvas? active = null)
    {
        _selected.Clear(); foreach (var id in ids) _selected.Add(id); _primary = primary; if (active is not null) _activeCanvas = active;
        _source.SetSelection(_selected, _primary); _destination.SetSelection(_selected, _primary); BuildContextTools(false); PositionContextTools();
    }

    private void BuildContextTools(bool fromRightClick)
    {
        _contextTools.SuspendLayout(); _contextTools.Controls.Clear();
        var selected = SelectedZones().ToArray(); if (selected.Length == 0) { _contextTools.Visible = false; _contextTools.ResumeLayout(); return; }
        var allLocked = selected.All(z => z.Locked); var anyLocked = selected.Any(z => z.Locked);
        AddContext(allLocked ? EditorToolIcon.Unlock : EditorToolIcon.Lock, allLocked ? "Unlock zone" : "Lock zone", (_, _) => ToggleLock(), true);
        if (!anyLocked)
        {
            var primary = PrimaryZone();
            if (primary?.Type == TemplateZoneType.OtherSection) AddContext(EditorToolIcon.Rename, "Rename this Other Section", (_, _) => RenameOther(primary.Id));
            if (primary?.Type == TemplateZoneType.OtherSection) AddContext(EditorToolIcon.Other, "Change Required / Flexible / Optional priority", (_, _) => ChoosePriority(primary.Id));
            AddContext(EditorToolIcon.Duplicate, "Duplicate selected zone", (_, _) => DuplicateZones());
            AddContext(EditorToolIcon.Delete, "Delete selected zone", (_, _) => DeleteZones());
            if (ReferenceEquals(_activeCanvas, _destination) && selected.All(z => z.IsPlaced4x4))
            {
                AddContext(EditorToolIcon.Stage, "Move selected zones to staging", (_, _) => StageSelected());
                AddContext(EditorToolIcon.StretchEdges, "Stretch selected zones to both safe-margin edges", (_, _) => StretchSelectedToEdges());
                if (selected.Length == 1)
                {
                    AddContext(EditorToolIcon.Above, "Bring above an overlapping zone", (_, _) => MoveSelectedLayer(true));
                    AddContext(EditorToolIcon.Below, "Send below an overlapping zone", (_, _) => MoveSelectedLayer(false));
                    if (primary?.Type == TemplateZoneType.DividerLine) AddContext(EditorToolIcon.Rotate, "Rotate divider line", (_, _) => RotateSelectedLine());
                }
                if (selected.Length > 1)
                {
                    AddContext(EditorToolIcon.AlignLeft, "Align left edges to the primary zone", (_, _) => AlignSelected(EditorToolIcon.AlignLeft));
                    AddContext(EditorToolIcon.AlignRight, "Align right edges to the primary zone", (_, _) => AlignSelected(EditorToolIcon.AlignRight));
                    AddContext(EditorToolIcon.AlignTop, "Align top edges to the primary zone", (_, _) => AlignSelected(EditorToolIcon.AlignTop));
                    AddContext(EditorToolIcon.AlignBottom, "Align bottom edges to the primary zone", (_, _) => AlignSelected(EditorToolIcon.AlignBottom));
                    AddContext(EditorToolIcon.AlignHorizontalCenter, "Align horizontal centers", (_, _) => AlignSelected(EditorToolIcon.AlignHorizontalCenter));
                    AddContext(EditorToolIcon.AlignVerticalCenter, "Align vertical centers", (_, _) => AlignSelected(EditorToolIcon.AlignVerticalCenter));
                }
                AddContext(EditorToolIcon.MarginLeft, "Align to the left safe margin", (_, _) => AlignSelected(EditorToolIcon.MarginLeft));
                AddContext(EditorToolIcon.MarginRight, "Align to the right safe margin", (_, _) => AlignSelected(EditorToolIcon.MarginRight));
                AddContext(EditorToolIcon.MarginTop, "Align to the top safe margin", (_, _) => AlignSelected(EditorToolIcon.MarginTop));
                AddContext(EditorToolIcon.MarginBottom, "Align to the bottom safe margin", (_, _) => AlignSelected(EditorToolIcon.MarginBottom));
                AddContext(EditorToolIcon.CanvasHorizontalCenter, "Center horizontally on the label", (_, _) => AlignSelected(EditorToolIcon.CanvasHorizontalCenter));
                AddContext(EditorToolIcon.CanvasVerticalCenter, "Center vertically on the label", (_, _) => AlignSelected(EditorToolIcon.CanvasVerticalCenter));
            }
        }
        _contextTools.Visible = true; _contextTools.ResumeLayout(); _contextTools.PerformLayout();
    }

    private void AddContext(EditorToolIcon icon, string tooltip, EventHandler click, bool enabled = true)
    { var button = new EditorToolButton(icon) { Enabled = enabled }; button.Click += click; _tips.SetToolTip(button, tooltip); _contextTools.Controls.Add(button); }

    private void PositionContextTools()
    {
        if (!_contextTools.Visible || !_primary.HasValue || _activeCanvas is null) return;
        var host = ReferenceEquals(_activeCanvas, _source) ? _sourceHost : _destinationHost;
        if (_contextTools.Parent != host) { _contextTools.Parent?.Controls.Remove(_contextTools); host.Controls.Add(_contextTools); _contextTools.BringToFront(); }
        var zoneBounds = _activeCanvas.GetZoneScreenBounds(_primary.Value); if (zoneBounds.IsEmpty) return;
        var topLeft = host.PointToClient(_activeCanvas.PointToScreen(zoneBounds.Location));
        var x = Math.Clamp(topLeft.X, 48, Math.Max(48, host.ClientSize.Width - _contextTools.Width - 8));
        var y = topLeft.Y - _contextTools.Height - 8; if (y < 4) y = topLeft.Y + zoneBounds.Height + 8;
        y = Math.Clamp(y, 4, Math.Max(4, host.ClientSize.Height - _contextTools.Height - 4)); _contextTools.Location = new Point(x, y); _contextTools.BringToFront();
    }

    private IEnumerable<TemplateZone> SelectedZones() => _zones.Where(z => _selected.Contains(z.Id));
    private TemplateZone? PrimaryZone() => _primary.HasValue ? _zones.FirstOrDefault(z => z.Id == _primary.Value) : null;

    private void ToggleLock()
    {
        var selected = SelectedZones().ToArray(); var unlock = selected.All(z => z.Locked);
        _zones = _zones.Select(z => _selected.Contains(z.Id) ? z with { Locked = !unlock } : z).ToList();
        if (!unlock) SetSelection([], null); RefreshCanvases();
    }

    private void DuplicateZones()
    {
        var copies = SelectedZones().Where(z => !z.Locked).Select(source => source with { Id = Guid.NewGuid(),
            Source = Offset(source.Source), Destination4x4 = Offset(source.Destination4x4), Placed4x4 = false,
            OtherName = source.Type == TemplateZoneType.OtherSection ? source.OtherName + " copy" : source.OtherName,
            Locked = false, Suggested = false }).ToArray();
        _zones.AddRange(copies); SetSelection(copies.Select(z => z.Id), copies.LastOrDefault()?.Id, _destination); RefreshCanvases();
    }

    private void DeleteZones()
    { var ids = SelectedZones().Where(z => !z.Locked).Select(z => z.Id).ToHashSet(); _zones.RemoveAll(z => ids.Contains(z.Id)); SetSelection([], null); RefreshCanvases(); }

    private void StageSelected()
    { _zones = _zones.Select(z => _selected.Contains(z.Id) && !z.Locked ? z with { Placed4x4 = false, Suggested = false } : z).ToList(); SetSelection([], null); RefreshCanvases(); }

    private void StretchSelectedToEdges()
    {
        foreach (var selected in SelectedZones().Where(z => !z.Locked).ToArray())
        {
            if (selected.Type != TemplateZoneType.BuiltInAsset)
            {
                var index = _zones.FindIndex(z => z.Id == selected.Id);
                _zones[index] = CarrierTemplateEngine.StretchToHorizontalEdges(selected);
                continue;
            }
            var baseRect = TemplateGeneratedContent.DefaultDestination(selected.BuiltInAsset);
            var scale = (1 - .03125 * 2) / Math.Max(.001, baseRect.Width);
            ResizeBuiltInPair(selected.Id, new NormalizedRect(.03125, selected.Destination4x4.Y,
                baseRect.Width * scale, baseRect.Height * scale));
        }
        RefreshCanvases();
    }

    private void MoveSelectedLayer(bool above)
    { if (!_primary.HasValue) return; _zones = CarrierTemplateEngine.MoveLayer(_zones, _primary.Value, above).ToList(); RefreshCanvases(); }
    private void RotateSelectedLine()
    { if (!_primary.HasValue) return; Replace(_primary.Value, CarrierTemplateEngine.RotateDividerLine); }

    private void AlignSelected(EditorToolIcon action)
    {
        const double margin = .03125; var movable = SelectedZones().Where(z => !z.Locked && z.IsPlaced4x4).ToArray();
        var primary = PrimaryZone(); if (movable.Length == 0 || primary is null) return; var reference = primary.Destination4x4;
        _zones = _zones.Select(zone =>
        {
            if (!movable.Any(z => z.Id == zone.Id)) return zone; var r = zone.Destination4x4;
            var next = action switch
            {
                EditorToolIcon.AlignLeft => r with { X = reference.X },
                EditorToolIcon.AlignRight => r with { X = reference.Right - r.Width },
                EditorToolIcon.AlignTop => r with { Y = reference.Y },
                EditorToolIcon.AlignBottom => r with { Y = reference.Bottom - r.Height },
                EditorToolIcon.AlignHorizontalCenter => r with { X = reference.X + (reference.Width - r.Width) / 2 },
                EditorToolIcon.AlignVerticalCenter => r with { Y = reference.Y + (reference.Height - r.Height) / 2 },
                EditorToolIcon.MarginLeft => r with { X = margin },
                EditorToolIcon.MarginRight => r with { X = 1 - margin - r.Width },
                EditorToolIcon.MarginTop => r with { Y = margin },
                EditorToolIcon.MarginBottom => r with { Y = 1 - margin - r.Height },
                EditorToolIcon.CanvasHorizontalCenter => r with { X = .5 - r.Width / 2 },
                EditorToolIcon.CanvasVerticalCenter => r with { Y = .5 - r.Height / 2 },
                _ => r
            };
            return zone with { Destination4x4 = ClampSafe(next), Suggested = false };
        }).ToList(); RefreshCanvases();
    }

    private void RenameOther(Guid id, bool newZone = false)
    {
        var zone = _zones.FirstOrDefault(z => z.Id == id); if (zone is null || zone.Type != TemplateZoneType.OtherSection) return;
        var value = PromptText("Rename Other Section", "Name", zone.OtherName); if (value is null && newZone) return;
        if (!string.IsNullOrWhiteSpace(value)) Replace(id, z => z with { OtherName = value.Trim(), Suggested = false });
    }

    private void ChoosePriority(Guid id)
    {
        var zone = _zones.FirstOrDefault(z => z.Id == id); if (zone is null) return;
        var menu = new ContextMenuStrip(); foreach (var priority in Enum.GetValues<TemplateOtherPriority>())
        { var item = new ToolStripMenuItem(priority.ToString()) { Checked = zone.OtherPriority == priority }; item.Click += (_, _) => Replace(id, z => z with { OtherPriority = priority }); menu.Items.Add(item); }
        menu.Show(Cursor.Position);
    }

    private void AddDividerLine()
    {
        var line = new TemplateZone { Type = TemplateZoneType.DividerLine, Source = new NormalizedRect(0, 0, .1, .01),
            Destination4x4 = new NormalizedRect(.08, .48, .84, .003), Placed4x4 = true, OtherName = "Divider Line" };
        _zones.Add(line); SetSelection([line.Id], line.Id, _destination); RefreshCanvases();
    }

    private void AddBuiltInAsset(TemplateBuiltInAssetKind asset)
    {
        var existingIndex = _zones.FindLastIndex(zone =>
            zone.Type == TemplateZoneType.BuiltInAsset && zone.BuiltInAsset == asset);
        if (existingIndex >= 0)
        {
            var existing = _zones[existingIndex];
            if (!existing.IsPlaced4x4)
                _zones[existingIndex] = existing with { Destination4x4 = ScaledBuiltIn(asset),
                    Placed4x4 = true, Suggested = false };
            SetSelection([existing.Id], existing.Id, _destination);
            RefreshCanvases();
            return;
        }
        var rect = ScaledBuiltIn(asset); var zone = new TemplateZone { Type = TemplateZoneType.BuiltInAsset,
            BuiltInAsset = asset, Source = new NormalizedRect(0, 0, .1, .03), Destination4x4 = rect, Placed4x4 = true };
        _zones.Add(zone); SetSelection([zone.Id], zone.Id, _destination); RefreshCanvases();
    }

    private void ResizeBuiltInPair(Guid changedId, NormalizedRect requested)
    {
        var changed = _zones.First(z => z.Id == changedId); var baseRect = TemplateGeneratedContent.DefaultDestination(changed.BuiltInAsset);
        var scaleX = requested.Width / Math.Max(.001, baseRect.Width); var scaleY = requested.Height / Math.Max(.001, baseRect.Height);
        _builtInScale = Math.Clamp(Math.Sqrt(Math.Max(.01, scaleX * scaleY)), .35, 4);
        for (var i = 0; i < _zones.Count; i++)
        {
            if (_zones[i].Type != TemplateZoneType.BuiltInAsset) continue; var d = ScaledBuiltIn(_zones[i].BuiltInAsset);
            var current = _zones[i].Destination4x4; var x = _zones[i].Id == changedId ? requested.X : current.X;
            var y = _zones[i].Id == changedId ? requested.Y : current.Y;
            _zones[i] = _zones[i] with { Destination4x4 = ClampSafe(d with { X = x, Y = y }), Placed4x4 = true, Suggested = false };
        }
    }

    private NormalizedRect ScaledBuiltIn(TemplateBuiltInAssetKind asset)
    { var value = TemplateGeneratedContent.DefaultDestination(asset); return ClampSafe(value with { Width = value.Width * _builtInScale, Height = value.Height * _builtInScale }); }

    private void AutoPlace()
    {
        if (_zones.Count == 0) return; _zones = CarrierTemplateEngine.AutoArrange(_zones).ToList();
        // Keep generated headers immediately above their matching address while preserving their shared scale.
        PlaceBuiltInNearAddress(TemplateBuiltInAssetKind.FromDe, TemplateZoneType.FromAddress);
        PlaceBuiltInNearAddress(TemplateBuiltInAssetKind.ToA, TemplateZoneType.ToAddress);
        RefreshCanvases();
    }

    private void PlaceBuiltInNearAddress(TemplateBuiltInAssetKind asset, TemplateZoneType addressType)
    {
        var address = _zones.FirstOrDefault(z => z.Type == addressType && z.IsPlaced4x4); if (address is null) return;
        var index = _zones.FindIndex(z => z.Type == TemplateZoneType.BuiltInAsset && z.BuiltInAsset == asset);
        if (index < 0) return; var d = ScaledBuiltIn(asset);
        _zones[index] = _zones[index] with { Destination4x4 = ClampSafe(d with { X = address.Destination4x4.X,
            Y = Math.Max(.03125, address.Destination4x4.Y - d.Height) }), Placed4x4 = true };
    }

    private void ClearDestination()
    {
        if (!_zones.Any(z => z.IsPlaced4x4)) return;
        if (MessageBox.Show("Move every destination zone back to staging?", "Clear 4 x 4 layout", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _zones = _zones.Select(z => z.Locked ? z : z with { Placed4x4 = false }).ToList(); SetSelection([], null); RefreshCanvases();
    }

    private void ActivateStaging(Guid? id, TemplateBuiltInAssetKind asset)
    {
        if (asset != TemplateBuiltInAssetKind.None) { AddBuiltInAsset(asset); return; }
        if (!id.HasValue) return; var index = _zones.FindIndex(z => z.Id == id.Value); if (index < 0 || _zones[index].Locked) return;
        _zones[index] = CarrierTemplateEngine.PlaceAtReferenceSize(_zones[index]); SetSelection([id.Value], id, _destination); RefreshCanvases();
    }

    private void RefreshCanvases()
    {
        if (_refreshing) return; _refreshing = true;
        try
        {
            using var packed = RenderDestination(CarrierTemplateEngine.NormalizePortrait(_inspection.SourcePage));
            _source.SetContent(_original, _zones, _selected, _primary); _destination.SetContent(packed, _zones, _selected, _primary);
            RefreshStaging(); BuildContextTools(false); PositionContextTools();
        }
        finally { _refreshing = false; }
    }

    private void RefreshStaging()
    {
        _staging.SuspendLayout(); _staging.Controls.Clear();
        AddStagingTag(null, "FROM / DE", TemplateZoneType.BuiltInAsset, TemplateBuiltInAssetKind.FromDe);
        AddStagingTag(null, "TO / A", TemplateZoneType.BuiltInAsset, TemplateBuiltInAssetKind.ToA);
        foreach (var zone in _zones.Where(z => !z.IsPlaced4x4 && z.Type != TemplateZoneType.BuiltInAsset))
            AddStagingTag(zone.Id, zone.DisplayName, zone.Type);
        _staging.ResumeLayout();
    }

    private void AddStagingTag(Guid? id, string text, TemplateZoneType type, TemplateBuiltInAssetKind asset = TemplateBuiltInAssetKind.None)
    {
        var color = TemplateEditorPalette.ZoneColor(type); var chip = new StagingTagButton { Text = text, AutoSize = false, Width = 136,
            Height = 32, Margin = new Padding(0, 0, 0, 7), FlatStyle = FlatStyle.Flat, BackColor = color,
            ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand, Tag = (id, asset) };
        chip.FlatAppearance.BorderSize = 0;
        chip.DoubleClick += (_, _) => { if (asset == TemplateBuiltInAssetKind.None) ActivateStaging(id, asset); };
        chip.Click += (_, _) =>
        {
            if (asset != TemplateBuiltInAssetKind.None) ActivateStaging(id, asset);
            else if (id.HasValue) SetSelection([id.Value], id, _source);
        };
        _tips.SetToolTip(chip, asset == TemplateBuiltInAssetKind.None
            ? "Double-click to place on the 4 x 4 label"
            : "Click to place on the 4 x 4 label");
        _staging.Controls.Add(chip);
    }

    private void Replace(Guid id, Func<TemplateZone, TemplateZone> update)
    { var i = _zones.FindIndex(z => z.Id == id); if (i < 0 || _zones[i].Locked) return; _zones[i] = update(_zones[i]); RefreshCanvases(); }

    private void PreviewLayout()
    {
        using var image = RenderDestination(CarrierTemplateEngine.NormalizePortrait(_inspection.SourcePage));
        using var dialog = new Form { Text = "4 x 4 template preview", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(650, 690), BackColor = Color.FromArgb(241, 244, 250), Font = Font };
        var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Padding = new Padding(24), Image = new Bitmap(image) };
        var close = TextButton("Close", 90, true); close.Dock = DockStyle.Right; close.Click += (_, _) => dialog.Close();
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(0, 8, 18, 8) }; footer.Controls.Add(close);
        dialog.Controls.Add(picture); dialog.Controls.Add(footer); dialog.ShowDialog(this);
    }

    private void TestTemplate()
    {
        if (_zones.Any(x => !x.IsPlaced4x4)) { MessageBox.Show("Place or delete all staged zones before testing the template.", "Zones still staged", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var picker = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Title = "Choose another 4 x 6 label" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var processor = new TemporaryTemplateTester(picker.FileName); var inspected = processor.Inspection; var draft = BuildResult();
            var match = _engine.Match(inspected, [draft], draft.Id); var snapshot = match.Snapshot ?? new AppliedTemplateSnapshot
            { TemplateId = draft.Id, Revision = draft.Revision, Carrier = draft.Carrier, LayoutName = draft.LayoutName,
                MatchScore = match.Match.Score, MatchDisposition = TemplateMatchDisposition.Manual, Zones = draft.Zones };
            var prepared = processor.Prepare(snapshot); var barcodeVerified = prepared.WarningCode is not ("template-barcode-verification-failed" or "template-final-barcode-verification-failed");
            TemplateTested?.Invoke(this, new TemplateTestEventArgs(draft.Id, match.Match.Score, match.Match.Disposition, barcodeVerified));
            using var preview = GrayImageBitmapConverter.ToBitmap(prepared.Image); using var dialog = new Form { Text = "Template test", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(540, 650), BackColor = Color.White, Font = Font };
            dialog.Controls.Add(new PictureBox { Image = new Bitmap(preview), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Padding = new Padding(18) });
            dialog.Controls.Add(new Label { Dock = DockStyle.Top, Height = 70, Padding = new Padding(18, 14, 10, 8), Text = $"Match score: {match.Match.Score:P1}   Result: {match.Match.Disposition}\r\nTransformed zones: {_zones.Count}; barcode verification: {(barcodeVerified ? "passed" : "FAILED - fallback shown")}" }); dialog.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Template test failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void SaveTemplate()
    {
        try
        {
            var result = BuildResult(); CarrierTemplateValidator.Validate(result); EnsureRequiredZonesContainContent(); EnsureBarcodesCovered();
            if (UnmarkedInkRatio() > .10 && MessageBox.Show("More than 10% of visible source content is unmarked. Save anyway?", "Unmarked content", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Result = result; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Template needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private CarrierTemplate BuildResult() => _originalTemplate with { Carrier = _carrier.Text.Trim(), LayoutName = _layout.Text.Trim(),
        BuiltInAssetScale = _builtInScale, Zones = _zones.Select(z => z with { }).ToArray(),
        Features = _engine.ExtractFeatures(_inspection.SourcePage, _inspection.RecognizedTsv, _zones) };

    private void EnsureBarcodesCovered()
    {
        var page = CarrierTemplateEngine.NormalizePortrait(_inspection.SourcePage);
        var detected = new BarcodeInspector().Detect(page).Select(barcode => barcode.Bounds);
        var uncovered = CarrierTemplateEngine.FindUncoveredBarcodes(
            detected, _zones, page.Width, page.Height);
        if (uncovered.Count > 0)
            throw new InvalidOperationException(
                "Every detected barcode must be covered by a Carrier Barcode or Shipping Barcode zone.");
    }

    private void EnsureRequiredZonesContainContent()
    {
        var page = CarrierTemplateEngine.NormalizePortrait(_inspection.SourcePage);
        foreach (var zone in _zones.Where(zone => zone.HasSourceRegion && (zone.Type != TemplateZoneType.OtherSection || zone.OtherPriority == TemplateOtherPriority.Required)))
        {
            var crop = page.Crop(zone.Source.ToPixels(page.Width, page.Height));
            if (ImageAnalysis.FindContentBounds(crop, padding: 0).IsEmpty)
                throw new InvalidOperationException($"'{zone.DisplayName}' is marked Required but its source box contains no visible content. Move or resize the source box, change an Other zone's priority, or delete the zone.");
        }
    }

    private double UnmarkedInkRatio()
    {
        var page = CarrierTemplateEngine.NormalizePortrait(_inspection.SourcePage); long ink = 0, unmarked = 0;
        for (var y = 0; y < page.Height; y += 3) for (var x = 0; x < page.Width; x += 3)
        { if (page[x, y] >= 210) continue; ink++; var nx = x / (double)page.Width; var ny = y / (double)page.Height; if (!_zones.Any(z => z.HasSourceRegion && z.Source.Contains(nx, ny))) unmarked++; }
        return ink == 0 ? 0 : unmarked / (double)ink;
    }

    private Bitmap RenderDestination(GrayImage source)
    {
        using var sourceBitmap = GrayImageBitmapConverter.ToBitmap(source); var output = new Bitmap(800, 800);
        using var g = Graphics.FromImage(output); g.Clear(Color.White); g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        foreach (var zone in _zones.Where(x => x.IsPlaced4x4))
        {
            var d = zone.Destination4x4; var destination = new Rectangle((int)(d.X * 800), (int)(d.Y * 800), Math.Max(1, (int)(d.Width * 800)), Math.Max(1, (int)(d.Height * 800)));
            if (zone.Type == TemplateZoneType.DividerLine) { g.FillRectangle(Brushes.Black, destination); continue; }
            if (zone.Type == TemplateZoneType.BuiltInAsset) { var generated = TemplateGeneratedContent.Render(zone, 300); using var generatedBitmap = GrayImageBitmapConverter.ToBitmap(generated); g.DrawImage(generatedBitmap, destination); continue; }
            var s = zone.Source.ToPixels(source.Width, source.Height); g.DrawImage(sourceBitmap, destination, s.X, s.Y, s.Width, s.Height, GraphicsUnit.Pixel);
        }
        return output;
    }

    private static NormalizedRect ClampSafe(NormalizedRect value)
    { const double m = .03125; var width = Math.Clamp(value.Width, .003, 1 - m * 2); var height = Math.Clamp(value.Height, .003, 1 - m * 2); return new NormalizedRect(Math.Clamp(value.X, m, 1 - m - width), Math.Clamp(value.Y, m, 1 - m - height), width, height); }
    private static double InferBuiltInScale(IEnumerable<TemplateZone> zones)
    { var zone = zones.FirstOrDefault(z => z.Type == TemplateZoneType.BuiltInAsset); if (zone is null) return 1; var baseRect = TemplateGeneratedContent.DefaultDestination(zone.BuiltInAsset); return Math.Clamp(zone.Destination4x4.Height / Math.Max(.001, baseRect.Height), .35, 4); }
    private static NormalizedRect Offset(NormalizedRect r) => r with { X = Math.Min(1 - r.Width, r.X + .025), Y = Math.Min(1 - r.Height, r.Y + .025) };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 7, 5, 0) };
    private static Label CanvasTitle(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 32, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Color.FromArgb(33, 43, 67) };
    private static Control CanvasHost(string title, Control content) { var host = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 }; host.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); host.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); host.Controls.Add(CanvasTitle(title), 0, 0); host.Controls.Add(content, 0, 1); return host; }
    private static Button TextButton(string text, int width, bool accent) { var button = new Button { Text = text, Width = width, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = accent ? TemplateEditorPalette.Accent : Color.FromArgb(239, 243, 252), ForeColor = accent ? Color.White : TemplateEditorPalette.Ink, Margin = new Padding(7, 1, 0, 0) }; button.FlatAppearance.BorderSize = 0; return button; }

    private string? PromptText(string title, string label, string value)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(410, 142), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = Font };
        var input = new TextBox { Text = value, Location = new Point(24, 45), Width = 360 }; var caption = new Label { Text = label, Location = new Point(24, 18), AutoSize = true };
        var cancel = TextButton("Cancel", 82, false); var okay = TextButton("Save", 82, true); cancel.Location = new Point(212, 92); okay.Location = new Point(302, 92); cancel.DialogResult = DialogResult.Cancel; okay.DialogResult = DialogResult.OK;
        dialog.Controls.AddRange([caption, input, cancel, okay]); dialog.AcceptButton = okay; dialog.CancelButton = cancel; return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }

    protected override void Dispose(bool disposing) { if (disposing) { _original.Dispose(); _tips.Dispose(); } base.Dispose(disposing); }

    private sealed class TemporaryTemplateTester : IDisposable
    {
        private readonly PdfLabelProcessor _processor = new(AppContext.BaseDirectory); private readonly string _path;
        public TemporaryTemplateTester(string path) { _path = path; Inspection = _processor.Inspect(path); }
        public PdfInspection Inspection { get; }
        public PreparedLabel Prepare(AppliedTemplateSnapshot snapshot) => _processor.Prepare(_path, 203, .125, 4, 4, LabelFitMode.Proportional, LabelRotation.None, PrintQualityPreset.Standard, 10, 4, RotatedBarcodeCompensation.Off, TextEnhancement.Off, 100, 100, snapshot);
        public void Dispose() { }
    }
}

public sealed record TemplateTestEventArgs(Guid TemplateId, double Score,
    TemplateMatchDisposition Disposition, bool BarcodeVerified);
