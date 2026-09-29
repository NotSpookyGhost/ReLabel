using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Hotfolder.UI;

internal sealed class CarrierTemplateLibraryControl : UserControl
{
    private readonly HotfolderCoordinator _coordinator;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, FullRowSelect = true,
        View = View.Details, HideSelection = false, BorderStyle = BorderStyle.None };

    public CarrierTemplateLibraryControl(HotfolderCoordinator coordinator)
    {
        _coordinator = coordinator; Dock = DockStyle.Fill; BackColor = Color.White;
        _list.Columns.Add("Carrier", 145); _list.Columns.Add("Layout", 195); _list.Columns.Add("Revision", 75);
        _list.Columns.Add("Enabled", 75); _list.Columns.Add("Match status", 105);
        _list.Columns.Add("Last modified", 145);
        var add = MakeButton("Add Template", true); var import = MakeButton("Import");
        var export = MakeButton("Export"); var edit = MakeButton("Edit");
        var duplicate = MakeButton("Duplicate..."); var toggle = MakeButton("Enable / Disable");
        var test = MakeButton("Test"); var delete = MakeButton("Delete");
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 55, Padding = new Padding(14, 10, 10, 8) };
        buttons.Controls.AddRange([add, import, export, edit, duplicate, toggle, test, delete]);
        var intro = new Label { Dock = DockStyle.Top, Height = 54, Padding = new Padding(18, 10, 10, 0),
            Text = "Teach ReLabel carrier layouts with marked source zones and an editable 4 × 4 arrangement. All PDFs remain local.",
            ForeColor = Color.FromArgb(92, 104, 119) };
        intro.Text = "Teach ReLabel carrier layouts with marked source zones and an editable 4 x 4 arrangement. Import or export portable packages; all PDFs remain local.";
        Controls.Add(_list); Controls.Add(buttons); Controls.Add(intro);
        add.Click += (_, _) => AddTemplate(); import.Click += (_, _) => ImportTemplates();
        export.Click += (_, _) => ExportSelected(); edit.Click += (_, _) => EditSelected();
        duplicate.Click += (_, _) => DuplicateSelected(); toggle.Click += (_, _) => ToggleSelected();
        test.Click += (_, _) => TestSelected(); delete.Click += (_, _) => DeleteSelected();
        _list.DoubleClick += (_, _) => EditSelected();
        Reload();
    }

    private CarrierTemplate? Selected => _list.SelectedItems.Count == 0 ? null :
        _list.SelectedItems[0].Tag is Guid id
            ? _coordinator.Templates.FirstOrDefault(x => x.Id == id) : null;

    private void Reload(Guid? select = null)
    {
        _list.BeginUpdate(); _list.Items.Clear();
        _list.Groups.Clear();
        foreach (var group in _coordinator.Templates.GroupBy(x => x.Carrier))
        {
        var listGroup = new ListViewGroup(group.Key, HorizontalAlignment.Left);
        _list.Groups.Add(listGroup);
        foreach (var template in group)
        {
            var item = new ListViewItem(template.Carrier, listGroup) { Tag = template.Id };
            item.SubItems.Add(template.LayoutName); item.SubItems.Add(template.Revision.ToString());
            item.SubItems.Add(template.Enabled ? "Enabled" : "Disabled");
            item.SubItems.Add(template.Zones.Any(x => x.Suggested) ? "Review" : "Ready");
            item.SubItems.Add(template.LastModified.LocalDateTime.ToString("g"));
            if (!template.Enabled) item.ForeColor = Color.FromArgb(130, 137, 149);
            _list.Items.Add(item);
            if (template.Id == select) item.Selected = true;
        }
        }
        _list.EndUpdate();
    }
    private void AddTemplate()
    {
        using var picker = new OpenFileDialog { Filter = "4 × 6 PDF (*.pdf)|*.pdf", Title = "Choose a 4 × 6 carrier label" };
        picker.Filter = "4 x 6 PDF (*.pdf)|*.pdf";
        picker.Title = "Choose a 4 x 6 carrier label";
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        using var identity = new TemplateIdentityDialog(Path.GetFileNameWithoutExtension(picker.FileName));
        if (identity.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var template = _coordinator.CreateTemplate(picker.FileName, identity.Carrier, identity.LayoutName);
            Edit(template, isNew: true);
        }
        catch (Exception ex) { Error(ex, "Template was not created"); }
    }
    private void EditSelected() { if (Selected is { } template) Edit(template, false); }
    private void ImportTemplates()
    {
        using var picker = new OpenFileDialog
        {
            Filter = "ReLabel template package (*.relabel-template)|*.relabel-template",
            Title = "Import carrier templates",
            Multiselect = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        Guid? last = null;
        var imported = 0;
        foreach (var path in picker.FileNames)
        {
            try
            {
                last = _coordinator.ImportTemplate(path).Id;
                imported++;
            }
            catch (Exception ex)
            {
                Error(ex, $"Template was not imported: {Path.GetFileName(path)}");
            }
        }
        Reload(last);
        if (imported > 0)
            MessageBox.Show($"{imported} carrier template{(imported == 1 ? "" : "s")} imported.",
                "Template import complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private void ExportSelected()
    {
        if (Selected is not { } selected) return;
        if (MessageBox.Show(
                "The exported package includes the original sample-label PDF and may contain shipping information.\r\n\r\nExport this template?",
                "Export carrier template", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        using var picker = new SaveFileDialog
        {
            Filter = "ReLabel template package (*.relabel-template)|*.relabel-template",
            Title = "Export carrier template",
            FileName = SafePackageName(selected),
            DefaultExt = "relabel-template",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _coordinator.ExportTemplate(selected.Id, picker.FileName);
            MessageBox.Show($"{selected.DisplayName} was exported successfully.",
                "Template export complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Error(ex, "Template was not exported"); }
    }
    private void Edit(CarrierTemplate template, bool isNew)
    {
        try
        {
            using var editor = new ModernCarrierTemplateEditorForm(template,
                _coordinator.InspectPdf(template.SourcePdfPath), _coordinator.TemplateEngine);
            editor.TemplateTested += (_, result) => _coordinator.AuditTemplateTest(result.TemplateId,
                result.Score, result.Disposition, result.BarcodeVerified);
            if (editor.ShowDialog(this) != DialogResult.OK || editor.Result is null)
            {
                if (isNew) { _coordinator.DeleteTemplate(template.Id); Reload(); }
                return;
            }
            var reapply = false;
            if (!isNew)
            {
                var decision = MessageBox.Show("Apply this revision to currently Ready labels that use this template?\r\n\r\n" +
                    "Yes = reapply now    No = future imports only    Cancel = do not save",
                    "Save template revision", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (decision == DialogResult.Cancel) return;
                reapply = decision == DialogResult.Yes;
            }
            var saved = _coordinator.SaveTemplate(editor.Result, reapply, incrementRevision: !isNew);
            Reload(saved.Id);
        }
        catch (Exception ex) { Error(ex, "Template was not saved"); }
    }
    private void DuplicateSelected()
    {
        if (Selected is not { } selected) return;
        var choice = MessageBox.Show(
            "Would you like to use a different original sample label for the duplicate?\r\n\r\n" +
            "Yes = choose a new 4 x 6 PDF\r\n" +
            "No = keep the current sample PDF\r\n" +
            "Cancel = do not duplicate",
            "Duplicate carrier template", MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
        if (choice == DialogResult.Cancel) return;
        string? replacement = null;
        if (choice == DialogResult.Yes)
        {
            using var picker = new OpenFileDialog
            {
                Filter = "4 x 6 PDF (*.pdf)|*.pdf",
                Title = "Choose the duplicate's sample label"
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            replacement = picker.FileName;
        }
        try
        {
            var duplicate = _coordinator.DuplicateTemplate(selected.Id, replacement);
            Edit(duplicate, isNew: true);
        }
        catch (Exception ex) { Error(ex, "Template was not duplicated"); }
    }
    private void ToggleSelected()
    {
        if (Selected is not { } selected) return;
        try { _coordinator.SetTemplateEnabled(selected.Id, !selected.Enabled); Reload(selected.Id); }
        catch (Exception ex) { Error(ex, "Template status was not changed"); }
    }
    private void TestSelected()
    {
        if (Selected is not { } selected) return;
        try
        {
            using var editor = new ModernCarrierTemplateEditorForm(selected,
                _coordinator.InspectPdf(selected.SourcePdfPath), _coordinator.TemplateEngine);
            editor.TemplateTested += (_, result) => _coordinator.AuditTemplateTest(result.TemplateId,
                result.Score, result.Disposition, result.BarcodeVerified);
            MessageBox.Show("Use Test Template at the bottom of the editor to choose a second PDF. Close without saving when finished.",
                "Test template", MessageBoxButtons.OK, MessageBoxIcon.Information);
            editor.ShowDialog(this);
        }
        catch (Exception ex) { Error(ex, "Template test could not open"); }
    }
    private void DeleteSelected()
    {
        if (Selected is not { } selected) return;
        if (MessageBox.Show($"Permanently delete {selected.DisplayName} and its stored source PDF?",
                "Delete carrier template", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try { _coordinator.DeleteTemplate(selected.Id); Reload(); }
        catch (Exception ex) { Error(ex, "Template was not deleted"); }
    }
    private void Error(Exception ex, string title) => MessageBox.Show(ex.Message, title,
        MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private static Button MakeButton(string text, bool accent = false)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 33, FlatStyle = FlatStyle.Flat,
            BackColor = accent ? Color.FromArgb(67, 92, 245) : Color.FromArgb(239, 243, 252),
            ForeColor = accent ? Color.White : Color.FromArgb(33, 43, 67), Margin = new Padding(4) };
        button.FlatAppearance.BorderSize = 0; return button;
    }
    private static string SafePackageName(CarrierTemplate template)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string($"{template.Carrier}-{template.LayoutName}"
            .Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return (string.IsNullOrWhiteSpace(name) ? "ReLabel-template" : name) + ".relabel-template";
    }

    private sealed class TemplateIdentityDialog : Form
    {
        private readonly TextBox _carrier = new() { Width = 260 };
        private readonly TextBox _layout = new() { Width = 260 };
        public TemplateIdentityDialog(string suggested)
        {
            Text = "New carrier template"; StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; ClientSize = new Size(440, 220);
            Font = new Font("Segoe UI", 9.5f); BackColor = Color.White; MinimizeBox = MaximizeBox = false;
            _layout.Text = suggested;
            Controls.AddRange([new Label { Text = "Carrier name", Location = new Point(28, 32), AutoSize = true },
                _carrier, new Label { Text = "Layout name", Location = new Point(28, 89), AutoSize = true }, _layout]);
            _carrier.Location = new Point(145, 28); _layout.Location = new Point(145, 85);
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(225, 158), Size = new Size(88, 34) };
            var save = MakeButton("Continue", true); save.Location = new Point(322, 158); save.Size = new Size(92, 34);
            save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(_carrier.Text) || string.IsNullOrWhiteSpace(_layout.Text))
                MessageBox.Show("Enter both a carrier and layout name.", "Template name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else { DialogResult = DialogResult.OK; Close(); } };
            Controls.AddRange([cancel, save]); AcceptButton = save; CancelButton = cancel;
        }
        public string Carrier => _carrier.Text.Trim();
        public string LayoutName => _layout.Text.Trim();
    }
}
