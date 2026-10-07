using System.Diagnostics;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Printing;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Hotfolder.UI;

public sealed class MainForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(245, 247, 252);
    private static readonly Color Primary = Color.FromArgb(67, 92, 245);
    private static readonly Color Accent = Color.FromArgb(31, 160, 255);
    private static readonly Color Ink = Color.FromArgb(33, 43, 67);
    private static readonly Color Muted = Color.FromArgb(112, 122, 145);

    private readonly HotfolderCoordinator _coordinator;
    private readonly FlowLayoutPanel _cards = new();
    private readonly Button _readyTab = new();
    private readonly Button _printedTab = new();
    private readonly Button _dateSort = new();
    private readonly Label _readyCount = new();
    private readonly Label _activity = new();
    private readonly Panel _activityDot = new();
    private readonly Label _recipient = new();
    private readonly Label _details = new();
    private readonly Label _result = new();
    private readonly CheckBox _previewRotation = new();
    private readonly PictureBox _preview = new();
    private readonly Panel _previewHost = new();
    private readonly Panel _previewFrame = new();
    private readonly Button _print = new();
    private readonly Button _openPdf = new();
    private readonly Button _labelSettings = new();
    private readonly Label _empty = new();
    private readonly Label _printerStatus = new();
    private readonly ToolTip _toolTips = new();
    private CancellationTokenSource? _previewCancellation;
    private Guid? _selectedId;
    private bool _showPrinted;
    private bool _oldestFirst;
    private Guid? _printingId;
    private string? _printingMessage;

    public MainForm(HotfolderCoordinator coordinator)
    {
        _coordinator = coordinator;
        Text = "ReLabel";
        MinimumSize = new Size(1080, 700);
        Size = new Size(1320, 820);
        StartPosition = FormStartPosition.CenterScreen;
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null) Icon = appIcon;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Canvas;
        BuildLayout();

        _coordinator.CatalogChanged += (_, _) => SafeUi(RefreshCards);
        _coordinator.ActivityChanged += (_, message) => SafeUi(() => SetActivity(message));
        _coordinator.PrintProgressChanged += (_, progress) => SafeUi(() => ShowPrintProgress(progress));
        _coordinator.PrinterStatusChanged += (_, status) => SafeUi(() => ShowPrinterStatus(status));
        Shown += (_, _) =>
        {
            RefreshCards();
            SetActivity(string.IsNullOrWhiteSpace(_coordinator.Configuration.PhysicalPrinterQueue)
                ? "Select your Zebra printer in Settings"
                : "Checking for new labels...");
        };
    }

    private void BuildLayout()
    {
        Controls.Add(BuildWorkspace());
        Controls.Add(BuildFooter());
        Controls.Add(BuildHeader());
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Color.White };
        var brand = new Panel { Location = new Point(28, 10), Size = new Size(360, 86) };
        var icon = new PictureBox
        {
            Location = new Point(0, 11), Size = new Size(58, 58), SizeMode = PictureBoxSizeMode.Zoom,
            Image = LoadBrandImage(), BackColor = Color.Transparent
        };
        var version = typeof(MainForm).Assembly.GetName().Version;
        var versionText = version is null ? "Version 2.4.4" : $"Version {version.Major}.{version.Minor}.{version.Build}";
        var brandText = new BrandTextControl(versionText)
        {
            Location = new Point(72, 3), Size = new Size(260, 66), BackColor = Color.Transparent
        };
        brand.Controls.AddRange([icon, brandText]);

        var settings = new Button
        {
            Size = new Size(48, 48), FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent,
            Image = LoadSettingsImage(), ImageAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand, TabStop = true, UseVisualStyleBackColor = false
        };
        settings.FlatAppearance.BorderSize = 0;
        settings.FlatAppearance.MouseOverBackColor = Color.FromArgb(245, 247, 252);
        settings.FlatAppearance.MouseDownBackColor = Color.FromArgb(232, 237, 248);
        settings.AccessibleName = "Settings";
        settings.AccessibleDescription = "Open ReLabel settings";
        _toolTips.SetToolTip(settings, "Settings");
        settings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        settings.Click += SettingsClicked;

        var countCard = new Panel { Height = 56, Width = 215, BackColor = Color.FromArgb(238, 242, 255) };
        _readyCount.Text = "0 labels ready";
        _readyCount.Font = new Font("Segoe UI Semibold", 12f);
        _readyCount.ForeColor = Primary;
        _readyCount.AutoSize = false;
        _readyCount.Dock = DockStyle.Fill;
        _readyCount.TextAlign = ContentAlignment.MiddleCenter;
        countCard.Controls.Add(_readyCount);

        header.Controls.AddRange([brand, countCard, settings]);
        header.Resize += (_, _) =>
        {
            settings.Location = new Point(header.ClientSize.Width - settings.Width - 28, 28);
            countCard.Location = new Point(settings.Left - countCard.Width - 12, 24);
        };
        return header;
    }

    private Control BuildWorkspace()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Canvas, Padding = new Padding(28, 24, 28, 22),
            ColumnCount = 2, RowCount = 1
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(BuildListArea(), 0, 0);
        grid.Controls.Add(BuildPreviewArea(), 1, 0);
        return grid;
    }

    private Control BuildListArea()
    {
        var area = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 18, 0) };
        var tabs = new Panel { Dock = DockStyle.Top, Height = 52 };
        StyleTab(_readyTab, "Ready", true);
        StyleTab(_printedTab, "Printed", false);
        _readyTab.Location = new Point(0, 0);
        _printedTab.Location = new Point(132, 0);
        _readyTab.Click += (_, _) => ChangeTab(false);
        _printedTab.Click += (_, _) => ChangeTab(true);
        var add = FlatButton("+  Add PDF", Primary, Color.White, 118);
        add.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        add.Click += AddPdfClicked;
        StyleSecondary(_dateSort, 124);
        _dateSort.Text = "Newest first  ↓";
        _dateSort.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _dateSort.Click += (_, _) =>
        {
            _oldestFirst = !_oldestFirst;
            _dateSort.Text = _oldestFirst ? "Oldest first  ↑" : "Newest first  ↓";
            RefreshCards();
        };
        tabs.Controls.AddRange([_readyTab, _printedTab, _dateSort, add]);
        tabs.Resize += (_, _) =>
        {
            add.Location = new Point(tabs.ClientSize.Width - add.Width, 1);
            _dateSort.Location = new Point(add.Left - _dateSort.Width - 10, 1);
        };

        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
        _cards.Dock = DockStyle.Fill;
        _cards.AutoScroll = true;
        _cards.FlowDirection = FlowDirection.TopDown;
        _cards.WrapContents = false;
        _cards.BackColor = Canvas;
        _cards.Padding = new Padding(0, 2, 8, 10);
        _cards.Resize += (_, _) => ResizeCards();
        _empty.Text = "No labels here yet\n\nPDF labels placed in Incoming will appear automatically.";
        _empty.ForeColor = Muted;
        _empty.Font = new Font("Segoe UI", 11f);
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.Dock = DockStyle.Fill;
        _empty.Visible = false;
        listHost.Controls.Add(_empty);
        listHost.Controls.Add(_cards);

        area.Controls.Add(listHost);
        area.Controls.Add(tabs);
        return area;
    }

    private Control BuildPreviewArea()
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(26) };
        var titleHost = new Panel { Dock = DockStyle.Top, Height = 72 };
        _recipient.Text = "Select a label";
        _recipient.Font = new Font("Segoe UI Semibold", 16f);
        _recipient.ForeColor = Ink;
        _recipient.Dock = DockStyle.Top;
        _recipient.Height = 34;
        _recipient.AutoEllipsis = true;
        _details.Text = "Your composed 4 x 4 preview will appear here.";
        _details.ForeColor = Muted;
        _details.Dock = DockStyle.Top;
        _details.Height = 26;
        _details.AutoEllipsis = true;
        titleHost.Controls.Add(_details);
        titleHost.Controls.Add(_recipient);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 54, ColumnCount = 3, RowCount = 1,
            Padding = new Padding(0, 5, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _print.Text = "Print Label";
        StylePrimary(_print, 100);
        _print.Dock = DockStyle.Fill;
        _print.Margin = new Padding(0, 0, 4, 4);
        _print.Enabled = false;
        _print.Click += async (_, _) => await PrintSelectedAsync();
        _openPdf.Text = "Open PDF";
        StyleSecondary(_openPdf, 100);
        _openPdf.Dock = DockStyle.Fill;
        _openPdf.Margin = new Padding(4, 0, 4, 4);
        _openPdf.Enabled = false;
        _openPdf.Click += (_, _) => OpenSelectedPdf();
        _labelSettings.Text = "Edit Label";
        StyleSecondary(_labelSettings, 100);
        _labelSettings.Dock = DockStyle.Fill;
        _labelSettings.Margin = new Padding(4, 0, 0, 4);
        _labelSettings.Enabled = false;
        _labelSettings.Click += LabelSettingsClicked;
        actions.Controls.Add(_print, 0, 0);
        actions.Controls.Add(_openPdf, 1, 0);
        actions.Controls.Add(_labelSettings, 2, 0);

        var previewInfo = new Panel { Dock = DockStyle.Bottom, Height = 42 };
        _result.Dock = DockStyle.Fill;
        _result.ForeColor = Muted;
        _result.TextAlign = ContentAlignment.MiddleLeft;
        _result.AutoEllipsis = true;
        _previewRotation.Text = "Show rotation";
        _previewRotation.Checked = true;
        _previewRotation.AutoSize = false;
        _previewRotation.Width = 116;
        _previewRotation.Dock = DockStyle.Right;
        _previewRotation.TextAlign = ContentAlignment.MiddleRight;
        _previewRotation.ForeColor = Ink;
        _previewRotation.CheckedChanged += (_, _) =>
        {
            if (_previewRotation.Enabled) _ = LoadPreviewAsync();
        };
        previewInfo.Controls.Add(_result);
        previewInfo.Controls.Add(_previewRotation);

        _previewHost.Dock = DockStyle.Fill;
        _previewHost.Padding = new Padding(8, 10, 8, 16);
        _previewHost.BackColor = Color.White;
        _previewFrame.BackColor = Color.White;
        _previewFrame.BorderStyle = BorderStyle.FixedSingle;
        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.White;
        _previewFrame.Controls.Add(_preview);
        _previewHost.Controls.Add(_previewFrame);
        _previewHost.Resize += (_, _) => LayoutPreviewFrame();

        card.Controls.Add(_previewHost);
        card.Controls.Add(previewInfo);
        card.Controls.Add(actions);
        card.Controls.Add(titleHost);
        return card;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Color.White };
        _activityDot.Size = new Size(9, 9);
        _activityDot.Location = new Point(29, 17);
        _activityDot.BackColor = Accent;
        _activity.Text = "Checking for new labels...";
        _activity.Location = new Point(46, 10);
        _activity.Size = new Size(700, 24);
        _activity.ForeColor = Muted;
        _activity.AutoEllipsis = true;
        var printer = new Panel
        {
            Dock = DockStyle.Right, Width = 210, BackColor = Color.FromArgb(239, 243, 252),
            Padding = new Padding(12, 0, 12, 0)
        };
        _printerStatus.Dock = DockStyle.Fill;
        _printerStatus.Text = "Checking printer...";
        _printerStatus.TextAlign = ContentAlignment.MiddleRight;
        _printerStatus.Font = new Font("Segoe UI Semibold", 8.8f);
        _printerStatus.ForeColor = Muted;
        printer.Controls.Add(_printerStatus);
        footer.Controls.AddRange([_activityDot, _activity, printer]);
        return footer;
    }

    private void RefreshCards()
    {
        var labels = _coordinator.Labels;
        var ready = labels.Count(IsReady);
        var printed = labels.Count(x => x.PrintedAt.HasValue);
        _readyCount.Text = $"{ready} label{(ready == 1 ? string.Empty : "s")} ready";
        _readyTab.Text = $"Ready   {ready}";
        _printedTab.Text = $"Printed   {printed}";

        var matching = labels.Where(x => _showPrinted == x.PrintedAt.HasValue);
        var visible = (_oldestFirst
                ? matching.OrderBy(x => x.ImportedAt)
                : matching.OrderByDescending(x => x.ImportedAt))
            .ToArray();

        _cards.SuspendLayout();
        var wantedIds = visible.Select(record => record.Id).ToHashSet();
        foreach (var stale in _cards.Controls.OfType<LabelCard>()
                     .Where(card => !wantedIds.Contains(card.Record.Id)).ToArray())
        {
            _cards.Controls.Remove(stale);
            stale.Dispose();
        }
        for (var index = 0; index < visible.Length; index++)
        {
            var record = visible[index];
            var selected = record.Id == _selectedId;
            var labelCard = _cards.Controls.OfType<LabelCard>().FirstOrDefault(card => card.Record.Id == record.Id);
            if (labelCard is null || labelCard.Record != record || labelCard.IsSelected != selected)
            {
                if (labelCard is not null)
                {
                    _cards.Controls.Remove(labelCard);
                    labelCard.Dispose();
                }
                labelCard = CreateLabelCard(record, selected);
                _cards.Controls.Add(labelCard);
            }
            _cards.Controls.SetChildIndex(labelCard, index);
        }
        _cards.ResumeLayout();
        _empty.Visible = visible.Length == 0;
        _empty.BringToFront();
        ResizeCards();

        if (_selectedId.HasValue && labels.All(x => x.Id != _selectedId.Value))
            ClearSelection();
    }

    private LabelCard CreateLabelCard(LabelRecord record, bool selected)
    {
        var card = new LabelCard(record, selected);
        if (record.Id == _printingId) card.SetPrintProgress(_printingMessage);
        card.Selected += (_, _) => SelectLabel(card.Record.Id);
        card.PrintRequested += async (_, _) => await PrintRecordAsync(card.Record);
        card.DeleteRequested += (_, _) => DeletePrintedRecord(card.Record);
        card.VerifyRequested += async (_, _) => await PromptScanVerificationAsync(card.Record.Id);
        return card;
    }

    private void ResizeCards()
    {
        var width = Math.Max(420, _cards.ClientSize.Width - _cards.Padding.Horizontal - 22);
        foreach (Control control in _cards.Controls)
        {
            control.Width = width;
            control.Invalidate(true);
        }
        _cards.Invalidate(true);
    }

    private void ChangeTab(bool printed)
    {
        _showPrinted = printed;
        StyleTab(_readyTab, _readyTab.Text, !printed);
        StyleTab(_printedTab, _printedTab.Text, printed);
        RefreshCards();
    }

    private void SelectLabel(Guid id)
    {
        if (_selectedId == id)
        {
            _ = LoadPreviewAsync();
            return;
        }
        _selectedId = id;
        RefreshCards();
        _ = LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var record = SelectedLabel;
        _print.Enabled = record is not null;
        _openPdf.Enabled = record is not null && File.Exists(record.ArchivePath);
        _labelSettings.Enabled = record is not null;
        if (record is null)
        {
            ClearSelection();
            return;
        }

        _recipient.Text = record.ShipToName;
        _details.Text = string.IsNullOrWhiteSpace(record.ShipToAddress)
            ? $"{record.OriginalFileName}{PageCountText(record)}  |  {record.ImportedAt.LocalDateTime:g}"
            : $"{record.ShipToAddress}  |  {record.OriginalFileName}{PageCountText(record)}";
        _print.Text = record.PrintedAt.HasValue ? "Print Again" : "Print Label";
        _previewRotation.Enabled = record.Rotation != ShipTime4x4.Core.Processing.LabelRotation.None;
        LayoutPreviewFrame();
        _result.Text = $"Preparing {record.OutputWidthInches} x {record.OutputHeightInches} preview...";
        _result.ForeColor = Muted;
        try
        {
            if (!_previewRotation.Checked && record.Rotation != LabelRotation.None)
                _coordinator.WarmPrintCache(record);
            var prepared = await _coordinator.PreparePreviewAsync(record, _previewRotation.Checked,
                _previewCancellation.Token);
            if (_previewCancellation.IsCancellationRequested) return;
            ReplacePreview(GrayImageBitmapConverter.ToBitmap(prepared.Image));
            _result.Text = prepared.UsedFallback
                ? $"{record.OutputWidthInches} x {record.OutputHeightInches} fallback preview: {prepared.WarningCode}{RotationViewText(record)}{MultiPagePreviewText(record)}"
                : record.AppliedTemplate?.MatchDisposition == TemplateMatchDisposition.Ambiguous
                    ? $"Possible template: {record.AppliedTemplate.Carrier} / {record.AppliedTemplate.LayoutName} " +
                      $"r{record.AppliedTemplate.Revision} ({record.AppliedTemplate.MatchScore:P0}) — confirm when printing{MultiPagePreviewText(record)}"
                    : PreviewResultText(record, prepared.AppliedBarcodeCompensationDots);
            _result.ForeColor = prepared.UsedFallback ||
                record.AppliedTemplate?.MatchDisposition == TemplateMatchDisposition.Ambiguous
                ? Color.FromArgb(194, 117, 14) : Color.FromArgb(34, 145, 104);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ReplacePreview(null);
            _result.Text = $"Preview failed: {exception.Message}";
            _result.ForeColor = Color.FromArgb(196, 58, 72);
        }
    }

    private async Task PrintSelectedAsync()
    {
        var record = SelectedLabel;
        if (record is not null)
            await PrintRecordAsync(record);
    }

    private async Task PrintRecordAsync(LabelRecord record)
    {
        if (string.IsNullOrWhiteSpace(_coordinator.Configuration.PhysicalPrinterQueue))
        {
            MessageBox.Show("Open Settings and select the physical Zebra printer first.", "Printer not selected",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _selectedId = record.Id;
        _printingId = record.Id;
        _printingMessage = "Preparing...";
        UpdatePrintIndicators(record.Id, _printingMessage);
        _print.Enabled = false;
        _print.Text = "Preparing...";
        _result.Text = "Preparing print-ready label...";
        _result.ForeColor = Accent;
        SetActivity($"Preparing label {record.OriginalFileName}...");
        try
        {
            var ignoreTemplate = false;
            if (record.AppliedTemplate?.MatchDisposition == TemplateMatchDisposition.Ambiguous &&
                record.TemplateSelection == TemplateSelectionMode.Auto)
            {
                var choice = MessageBox.Show(
                    $"ReLabel found a possible template: {record.AppliedTemplate.Carrier} / {record.AppliedTemplate.LayoutName} " +
                    $"(score {record.AppliedTemplate.MatchScore:P0}).\r\n\r\nYes = use proposed template\r\nNo = use generalized layout\r\nCancel = do not print",
                    "Ambiguous carrier template", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (choice == DialogResult.Cancel) return;
                ignoreTemplate = choice == DialogResult.No;
            }
            await _coordinator.PrintAsync(record.Id, default, ignoreTemplate);
            if (_coordinator.Configuration.ScannerVerificationEnabled)
                await PromptScanVerificationAsync(record.Id);
            _printingId = null;
            _printingMessage = null;
            if (_showPrinted)
                await LoadPreviewAsync();
            else
                ClearSelection();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Label did not print", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _printingId = null;
            _printingMessage = null;
            _print.Text = SelectedLabel?.PrintedAt.HasValue == true ? "Print Again" : "Print Label";
            _print.Enabled = SelectedLabel is not null;
        }
    }

    private void AddPdfClicked(object? sender, EventArgs arguments)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf", Multiselect = true, Title = "Add shipping labels"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        Directory.CreateDirectory(_coordinator.Configuration.IncomingFolder);
        foreach (var source in dialog.FileNames)
        {
            var destination = UniqueIncomingPath(Path.GetFileName(source));
            File.Copy(source, destination);
            _coordinator.QueueFile(destination);
        }
    }

    private void LabelSettingsClicked(object? sender, EventArgs arguments)
    {
        var record = SelectedLabel;
        if (record is null) return;
        using var dialog = new LabelSettingsDialog(record, _coordinator.Templates);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (dialog.TemplateSelection == TemplateSelectionMode.Specific)
            {
                var chosen = _coordinator.Templates.FirstOrDefault(x => x.Id == dialog.SelectedTemplateId);
                if (chosen is not null)
                {
                    var score = _coordinator.TemplateEngine.Match(
                        _coordinator.InspectPdf(record.ArchivePath), [chosen], chosen.Id).Match.Score;
                    if (score < .65 && MessageBox.Show(
                            $"This PDF scores only {score:P0} against {chosen.DisplayName}. Apply it anyway?",
                            "Low template score", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }
            }
            _coordinator.UpdateLabelSettings(record.Id, dialog.FitMode, dialog.OutputWidth, dialog.OutputHeight,
                dialog.Rotation, dialog.BarcodeCompensation, dialog.TextEnhancement,
                dialog.FromAddressScalePercent, dialog.ToAddressScalePercent);
            _coordinator.UpdateTemplateSelection(record.Id, dialog.TemplateSelection, dialog.SelectedTemplateId);
            RefreshCards();
            _ = LoadPreviewAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Label settings were not saved", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void DeletePrintedRecord(LabelRecord record)
    {
        var message = File.Exists(record.PrintedPath)
            ? $"Delete {record.OriginalFileName} from ReLabel and permanently delete its PDF from the Printed folder?"
            : $"Delete {record.OriginalFileName} from the Printed list? The PDF is no longer present.";
        if (MessageBox.Show(message, "Delete printed label", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            _coordinator.DeletePrinted(record.Id);
            if (_selectedId == record.Id) ClearSelection();
            RefreshCards();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Printed label was not deleted", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private string UniqueIncomingPath(string fileName)
    {
        return ManagedFileNames.NextAvailable(_coordinator.Configuration.IncomingFolder, fileName);
    }

    private void SettingsClicked(object? sender, EventArgs arguments)
    {
        using var dialog = new SettingsDialog(_coordinator.Configuration, _coordinator.AuditLogPath,
            _coordinator.PrintCalibrationLabel, _coordinator);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        try
        {
            _coordinator.ApplyConfiguration(dialog.Result);
            RefreshCards();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Settings were not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private LabelRecord? SelectedLabel => _selectedId is null
        ? null
        : _coordinator.Labels.FirstOrDefault(x => x.Id == _selectedId.Value);

    private void ClearSelection()
    {
        _selectedId = null;
        ReplacePreview(null);
        _recipient.Text = "Select a label";
        _details.Text = "Your composed 4 x 4 preview will appear here.";
        _result.Text = string.Empty;
        _print.Enabled = false;
        _openPdf.Enabled = false;
        _labelSettings.Enabled = false;
        _previewRotation.Enabled = false;
    }

    private void OpenSelectedPdf()
    {
        var record = SelectedLabel;
        if (record is not null) OpenPath(record.ArchivePath);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        OpenPath(path);
    }

    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private void ShowPrinterStatus(PrinterStatusSnapshot status)
    {
        _printerStatus.Text = status.Message;
        _printerStatus.ForeColor = status.Availability switch
        {
            PrinterAvailability.Ready => Color.FromArgb(26, 130, 95),
            PrinterAvailability.Busy => Color.FromArgb(24, 119, 180),
            PrinterAvailability.Warning => Color.FromArgb(176, 104, 6),
            PrinterAvailability.Error => Color.FromArgb(196, 58, 72),
            _ => Muted
        };
        _printerStatus.Parent!.BackColor = status.Availability switch
        {
            PrinterAvailability.Ready => Color.FromArgb(234, 250, 247),
            PrinterAvailability.Error => Color.FromArgb(255, 234, 237),
            PrinterAvailability.Warning => Color.FromArgb(255, 245, 221),
            _ => Color.FromArgb(239, 243, 252)
        };
    }

    private async Task PromptScanVerificationAsync(Guid id)
    {
        while (true)
        {
            var record = _coordinator.Labels.FirstOrDefault(item => item.Id == id);
            if (record is null || !record.PrintedAt.HasValue) return;
            using var dialog = new ScannerVerificationDialog(record.OriginalFileName);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (await _coordinator.VerifyPrintedBarcodeAsync(id, dialog.ScannedValue))
                {
                    SetActivity($"Scan verified for {record.OriginalFileName}");
                    return;
                }
                if (MessageBox.Show("That scan did not match a barcode decoded from the printed label. Try scanning again?",
                        "Barcode did not match", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                    return;
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "Scan could not be verified", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }
    }

    private void SetActivity(string message)
    {
        _activity.Text = message;
        _activityDot.BackColor = message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(220, 70, 85)
            : message.StartsWith("Importing", StringComparison.OrdinalIgnoreCase) ||
              message.StartsWith("Checking", StringComparison.OrdinalIgnoreCase) ||
              message.StartsWith("Printing", StringComparison.OrdinalIgnoreCase)
                ? Accent
                : Color.FromArgb(41, 184, 137);
    }

    private void ShowPrintProgress(PrintProgressEventArgs progress)
    {
        _printingId = progress.LabelId;
        _printingMessage = progress.Message.Replace(" label", string.Empty, StringComparison.OrdinalIgnoreCase);
        UpdatePrintIndicators(progress.LabelId, _printingMessage);
        if (_selectedId == progress.LabelId)
        {
            _print.Text = progress.Message.StartsWith("Sending", StringComparison.OrdinalIgnoreCase)
                ? "Sending..." : "Preparing...";
            _result.Text = progress.Message;
            _result.ForeColor = Accent;
        }
    }

    private void UpdatePrintIndicators(Guid id, string? message)
    {
        foreach (var card in _cards.Controls.OfType<LabelCard>())
            if (card.Record.Id == id)
                card.SetPrintProgress(message);
    }

    private static bool IsReady(LabelRecord record) => !record.PrintedAt.HasValue && record.Status != LabelStatus.Failed;
    private static string FitLabel(ShipTime4x4.Core.Processing.LabelFitMode mode) =>
        mode == ShipTime4x4.Core.Processing.LabelFitMode.SquishToFill ? "Squish to fill" : "Proportional";

    private string PreviewResultText(LabelRecord record, int appliedBarcodeDots)
    {
        return $"{record.OutputWidthInches} x {record.OutputHeightInches} - {FitLabel(record.FitMode)}" +
               $"{RotationViewText(record)}{EnhancementViewText(record, appliedBarcodeDots)}" +
               MultiPagePreviewText(record);
    }

    private static string MultiPagePreviewText(LabelRecord record) =>
        record.PageCount > 1 ? $" - showing page 1 of {record.PageCount}; all pages will print" : string.Empty;

    private static string PageCountText(LabelRecord record) =>
        record.PageCount > 1 ? $"  |  {record.PageCount} labels" : string.Empty;

    private string EnhancementViewText(LabelRecord record, int appliedBarcodeDots)
    {
        var values = new List<string>();
        if (record.BarcodeCompensation != RotatedBarcodeCompensation.Off &&
            record.Rotation is LabelRotation.Clockwise90 or LabelRotation.Clockwise270)
        {
            var requestedDots = (int)record.BarcodeCompensation;
            values.Add(!_previewRotation.Checked
                ? $"barcode correction hidden (prints up to {requestedDots} dots)"
                : appliedBarcodeDots == requestedDots
                    ? $"barcode correction {appliedBarcodeDots} {DotWord(appliedBarcodeDots)} applied"
                    : $"barcode correction {appliedBarcodeDots} {DotWord(appliedBarcodeDots)} applied, {requestedDots} requested");
        }
        if (record.TextEnhancement != TextEnhancement.Off)
        {
            values.Add(record.TextEnhancement switch
            {
                TextEnhancement.Larger => "larger FROM/TO",
                TextEnhancement.ExtraLarge => "extra-large FROM/TO",
                TextEnhancement.Custom => $"FROM {record.EffectiveFromAddressScalePercent}% / " +
                                          $"TO {record.EffectiveToAddressScalePercent}%",
                _ => "FROM/TO enhanced"
            });
        }
        return values.Count == 0 ? string.Empty : " - " + string.Join(", ", values);
    }

    private static string DotWord(int dots) => dots == 1 ? "dot" : "dots";

    private string RotationViewText(LabelRecord record)
    {
        var rotation = (int)record.Rotation;
        return rotation == 0 ? string.Empty
            : _previewRotation.Checked ? $" - rotated {rotation}°" : $" - unrotated preview (prints {rotation}°)";
    }

    private void LayoutPreviewFrame()
    {
        var record = SelectedLabel;
        var ratio = record is null ? 1d : record.OutputWidthInches / (double)record.OutputHeightInches;
        var maxWidth = Math.Max(120, _previewHost.ClientSize.Width - _previewHost.Padding.Horizontal);
        var maxHeight = Math.Max(120, _previewHost.ClientSize.Height - _previewHost.Padding.Vertical);
        var width = maxWidth;
        var height = (int)Math.Round(width / ratio);
        if (height > maxHeight)
        {
            height = maxHeight;
            width = (int)Math.Round(height * ratio);
        }
        _previewFrame.Size = new Size(Math.Max(1, width), Math.Max(1, height));
        _previewFrame.Location = new Point(
            Math.Max(0, (_previewHost.ClientSize.Width - _previewFrame.Width) / 2),
            Math.Max(0, (_previewHost.ClientSize.Height - _previewFrame.Height) / 2));
    }

    private static void StyleTab(Button button, string text, bool active)
    {
        button.Text = text;
        button.Size = new Size(122, 42);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = active ? Primary : Canvas;
        button.ForeColor = active ? Color.White : Muted;
        button.Font = new Font("Segoe UI Semibold", 10f);
        button.Cursor = Cursors.Hand;
    }

    private static Button FlatButton(string text, Color background, Color foreground, int width)
    {
        var button = new Button { Text = text };
        button.Size = new Size(width, 40);
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI Semibold", 9.5f);
        button.Cursor = Cursors.Hand;
        return button;
    }

    private static void StylePrimary(Button button, int width)
    {
        button.Size = new Size(width, 40);
        button.BackColor = Primary;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI Semibold", 9.5f);
        button.Cursor = Cursors.Hand;
    }

    private static void StyleSecondary(Button button, int width)
    {
        button.Size = new Size(width, 40);
        button.BackColor = Color.FromArgb(239, 243, 252);
        button.ForeColor = Ink;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI Semibold", 9.5f);
        button.Cursor = Cursors.Hand;
    }

    private static Image? LoadBrandImage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "printer.png");
        if (File.Exists(path))
        {
            using var source = Image.FromFile(path);
            return new Bitmap(source);
        }
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap();
    }

    private static Image? LoadSettingsImage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "settings.png");
        if (!File.Exists(path)) return null;
        using var source = Image.FromFile(path);
        return new Bitmap(source, new Size(32, 32));
    }

    private sealed class BrandTextControl : Control
    {
        private readonly string _versionText;
        private readonly Font _titleFont = new("Segoe UI Semibold", 22f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _versionFont = new("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly StringFormat _format = new(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap
        };

        public BrandTextControl(string versionText)
        {
            _versionText = versionText;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var titleBrush = new SolidBrush(Ink);
            using var versionBrush = new SolidBrush(Muted);
            e.Graphics.DrawString("ReLabel", _titleFont, titleBrush, 0f, -2f, _format);
            var titleHeight = e.Graphics.MeasureString("ReLabel", _titleFont, int.MaxValue, _format).Height;
            using var titleOutline = new System.Drawing.Drawing2D.GraphicsPath();
            using var versionOutline = new System.Drawing.Drawing2D.GraphicsPath();
            titleOutline.AddString("ReLabel", _titleFont.FontFamily, (int)_titleFont.Style,
                e.Graphics.DpiY * _titleFont.SizeInPoints / 72f, PointF.Empty, _format);
            versionOutline.AddString(_versionText, _versionFont.FontFamily, (int)_versionFont.Style,
                e.Graphics.DpiY * _versionFont.SizeInPoints / 72f, PointF.Empty, _format);
            var opticalLeftOffset = titleOutline.GetBounds().Left - versionOutline.GetBounds().Left;
            e.Graphics.DrawString(_versionText, _versionFont, versionBrush, opticalLeftOffset,
                Math.Max(0f, titleHeight - 5f), _format);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _versionFont.Dispose();
                _format.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private void ReplacePreview(Image? image)
    {
        var old = _preview.Image;
        _preview.Image = image;
        old?.Dispose();
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            _preview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
