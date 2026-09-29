using System.Text.Json;

namespace ShipTime4x4.Hotfolder.UI;

public sealed class AuditLogForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly string _logFolder;

    public AuditLogForm(string currentLogPath)
    {
        _logFolder = Path.GetDirectoryName(currentLogPath)!;
        Text = "ReLabel audit log";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 610);
        MinimumSize = new Size(760, 460);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(245, 247, 252);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.White, Padding = new Padding(22) };
        header.Controls.Add(new Label { Text = "Audit log", Font = new Font("Segoe UI Semibold", 17f),
            ForeColor = Color.FromArgb(33, 43, 67), AutoSize = true });
        var refresh = new Button { Text = "Refresh", Size = new Size(90, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.FromArgb(239, 243, 252), FlatStyle = FlatStyle.Flat };
        refresh.FlatAppearance.BorderSize = 0;
        refresh.Click += (_, _) => LoadEntries();
        header.Controls.Add(refresh);
        header.Resize += (_, _) => refresh.Location = new Point(header.ClientSize.Width - refresh.Width - 22, 22);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", DataPropertyName = "Time", Width = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Event", DataPropertyName = "Action", Width = 115 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File", DataPropertyName = "FileName", Width = 230 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", DataPropertyName = "Outcome", Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Details", DataPropertyName = "Detail",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        Controls.Add(_grid);
        Controls.Add(header);
        Shown += (_, _) => LoadEntries();
    }

    private void LoadEntries()
    {
        Directory.CreateDirectory(_logFolder);
        var entries = new List<AuditEntry>();
        foreach (var path in Directory.EnumerateFiles(_logFolder, "audit-*.jsonl").OrderByDescending(path => path))
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var timestamp = root.GetProperty("TimestampUtc").GetDateTimeOffset().ToLocalTime();
                var action = Read(root, "Action");
                entries.Add(new AuditEntry(timestamp, FriendlyAction(action), Read(root, "FileName"),
                    Read(root, "Outcome"), FriendlyDetail(action, Read(root, "Detail"))));
            }
            catch (JsonException) { }
        }
        _grid.DataSource = entries.OrderByDescending(entry => entry.Timestamp).ToArray();
    }

    private static string Read(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;

    private static string FriendlyAction(string action) => action switch
    {
        "LabelSettings" => "Label settings",
        "ImportMigration" => "Import migration",
        "ScanVerification" => "Scan verification",
        "Performance" => "Performance",
        "TemplateImport" => "Template import",
        "TemplateExport" => "Template export",
        _ => action
    };

    private static string FriendlyDetail(string action, string detail)
    {
        if (action != "LabelSettings") return detail;
        return detail.Replace("Fit=Proportional", "Fitting: Proportional", StringComparison.Ordinal)
            .Replace("Fit=SquishToFill", "Fitting: Squish to fill", StringComparison.Ordinal)
            .Replace("; Size=", "; size: ", StringComparison.Ordinal)
            .Replace("; Rotation=", "; rotation: ", StringComparison.Ordinal) +
            (detail.Contains("Rotation=", StringComparison.Ordinal) ? "°" : string.Empty);
    }

    private sealed record AuditEntry(DateTimeOffset Timestamp, string Action, string FileName, string Outcome, string Detail)
    {
        public string Time => Timestamp.ToString("MMM d, yyyy  h:mm:ss tt");
    };
}
