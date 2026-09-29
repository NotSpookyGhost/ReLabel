using System.Drawing.Printing;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Hotfolder.UI;

public sealed class SettingsDialog : Form
{
    private readonly string _auditLogPath;
    private readonly Action<HotfolderConfiguration> _printCalibration;
    private readonly TextBox _incoming = new() { Dock = DockStyle.Fill };
    private readonly TextBox _archive = new() { Dock = DockStyle.Fill };
    private readonly TextBox _ready = new() { Dock = DockStyle.Fill };
    private readonly TextBox _printed = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _printer = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _resolution = new() { Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ComboBox _quality = new() { Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly NumericUpDown _darkness = new()
    {
        Dock = DockStyle.Left, Width = 190, Minimum = 0, Maximum = 30, DecimalPlaces = 1,
        Increment = 0.5m
    };
    private readonly ComboBox _speed = new() { Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly CheckBox _watch = new() { Text = "Watch for new PDF files", AutoSize = true };
    private readonly CheckBox _autoPrint = new() { Text = "Print automatically after import", AutoSize = true };
    private readonly CheckBox _scannerVerification = new()
    {
        Text = "Ask for a USB barcode scan after printing", AutoSize = true
    };
    private readonly ComboBox _defaultFit = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _defaultSize = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _defaultRotationChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _defaultBarcode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _defaultText = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _defaultFromScale = new()
    {
        Minimum = 100, Maximum = 450, Increment = 5, Width = 120, TextAlign = HorizontalAlignment.Right
    };
    private readonly NumericUpDown _defaultToScale = new()
    {
        Minimum = 100, Maximum = 250, Increment = 5, Width = 120, TextAlign = HorizontalAlignment.Right
    };
    private readonly ToolTip _toolTips = new();
    private readonly HotfolderCoordinator? _coordinator;

    public SettingsDialog(HotfolderConfiguration configuration, string auditLogPath,
        Action<HotfolderConfiguration> printCalibration, HotfolderCoordinator? coordinator = null)
    {
        _coordinator = coordinator;
        _auditLogPath = auditLogPath;
        _printCalibration = printCalibration;
        Text = "ReLabel settings";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterParent;
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(800, 650);

        foreach (string name in PrinterSettings.InstalledPrinters)
            _printer.Items.Add(name);
        if (!string.IsNullOrWhiteSpace(configuration.PhysicalPrinterQueue) &&
            !_printer.Items.Contains(configuration.PhysicalPrinterQueue))
            _printer.Items.Add(configuration.PhysicalPrinterQueue);

        _resolution.Items.Add(new ResolutionItem(203, "200"));
        _resolution.Items.Add(new ResolutionItem(300, "300"));
        _resolution.Items.Add(new ResolutionItem(600, "600"));
        _quality.Items.Add(new QualityItem(PrintQualityPreset.Fast, "Fast"));
        _quality.Items.Add(new QualityItem(PrintQualityPreset.Standard, "Standard"));
        _quality.Items.Add(new QualityItem(PrintQualityPreset.HighQuality, "High Quality"));
        _incoming.Text = configuration.IncomingFolder;
        _archive.Text = configuration.ArchiveFolder;
        _ready.Text = configuration.ReadyFolder;
        _printed.Text = configuration.PrintedFolder;
        _printer.SelectedItem = configuration.PhysicalPrinterQueue;
        _resolution.SelectedItem = _resolution.Items.Cast<ResolutionItem>()
            .First(x => x.Value == configuration.PrintResolutionDpi);
        _quality.SelectedItem = _quality.Items.Cast<QualityItem>()
            .First(x => x.Value == configuration.PrintQuality);
        _darkness.Value = (decimal)configuration.PrintDarkness;
        _toolTips.SetToolTip(_darkness, "The printer's physical Zebra darkness (ZPL ~SD) for the entire label.");
        RefreshPrintSpeeds(configuration.PrintSpeedIps);
        _resolution.SelectedIndexChanged += (_, _) => RefreshPrintSpeeds(null);
        _watch.Checked = configuration.WatchEnabled;
        _autoPrint.Checked = configuration.AutoPrint;
        _scannerVerification.Checked = configuration.ScannerVerificationEnabled;
        _toolTips.SetToolTip(_scannerVerification,
            "When enabled, ReLabel asks the operator to scan one printed barcode and compares it locally. Barcode values are never logged.");
        ConfigureDefaultEditors(configuration);

        var title = new Label
        {
            Text = "Settings",
            Font = new Font("Segoe UI Semibold", 16f),
            ForeColor = Color.FromArgb(24, 39, 58),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        var intro = new Label
        {
            Text = "Configure printer output, label defaults, application behavior, and folder paths.",
            ForeColor = Color.FromArgb(92, 104, 119),
            AutoSize = true
        };
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(24, 16, 24, 0), FlowDirection = FlowDirection.TopDown };
        header.Controls.Add(title);
        header.Controls.Add(intro);
        Controls.Add(header);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 10f),
            Padding = new Point(18, 7), ItemSize = new Size(145, 34), SizeMode = TabSizeMode.Fixed
        };
        var foldersPage = SettingsPage("Folder Paths");
        var foldersGrid = SettingsGrid(4);
        AddRow(foldersGrid, 0, "Incoming folder", _incoming, BrowseButton(_incoming));
        AddRow(foldersGrid, 1, "Ready folder", _ready, BrowseButton(_ready));
        AddRow(foldersGrid, 2, "Archive folder", _archive, BrowseButton(_archive));
        AddRow(foldersGrid, 3, "Printed folder", _printed, BrowseButton(_printed));
        foldersPage.Controls.Add(foldersGrid);

        var printerPage = SettingsPage("Printer Settings");
        var printerGrid = SettingsGrid(5);
        AddRow(printerGrid, 0, "Zebra printer", _printer, null);
        AddRow(printerGrid, 1, "Print resolution (DPI)", _resolution, null);
        AddRow(printerGrid, 2, "Print quality", _quality, null);
        AddRow(printerGrid, 3, "Label darkness (0-30)", _darkness, null);
        AddRow(printerGrid, 4, "Print speed (in/sec)", _speed, null);
        printerPage.Controls.Add(printerGrid);

        var automationPage = SettingsPage("App Settings");
        var automation = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(30, 28, 30, 20),
            FlowDirection = FlowDirection.TopDown, WrapContents = false
        };
        foreach (var option in new[] { _watch, _autoPrint, _scannerVerification })
        {
            option.Font = new Font("Segoe UI", 10f);
            option.Margin = new Padding(0, 0, 0, 22);
            automation.Controls.Add(option);
        }
        automation.Controls.Add(new Label
        {
            Text = "Scanner verification compares the scan locally. Barcode values are never written to the audit log.",
            ForeColor = Color.FromArgb(92, 104, 119), AutoSize = false, Size = new Size(630, 48),
            Margin = new Padding(24, 0, 0, 0)
        });
        automationPage.Controls.Add(automation);

        var defaultsPage = SettingsPage("Label Defaults");
        var defaultsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        defaultsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        defaultsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        defaultsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        var defaultsGrid = SettingsGrid(7, 49);
        AddRow(defaultsGrid, 0, "Fitting", _defaultFit, null);
        AddRow(defaultsGrid, 1, "Output label size", _defaultSize, null);
        AddRow(defaultsGrid, 2, "Print rotation", _defaultRotationChoice, null);
        AddRow(defaultsGrid, 3, "Rotated barcode", _defaultBarcode, null);
        AddRow(defaultsGrid, 4, "FROM/TO addresses", _defaultText, null);
        AddRow(defaultsGrid, 5, "Custom FROM scale (%)", _defaultFromScale, null);
        AddRow(defaultsGrid, 6, "Custom TO scale (%)", _defaultToScale, null);
        defaultsLayout.Controls.Add(defaultsGrid, 0, 0);
        var qualityHost = new Panel { Dock = DockStyle.Fill };
        var qualityTest = new Button
        {
            Text = "Print Quality Test", Width = 144, Height = 36, Location = new Point(28, 12),
            BackColor = Color.FromArgb(239, 243, 252), ForeColor = Color.FromArgb(33, 43, 67),
            FlatStyle = FlatStyle.Flat
        };
        qualityTest.FlatAppearance.BorderSize = 0;
        qualityTest.Click += QualityTestClicked;
        qualityHost.Controls.Add(qualityTest);
        defaultsLayout.Controls.Add(qualityHost, 0, 1);
        defaultsPage.Controls.Add(defaultsLayout);

        var templatePage = SettingsPage("Carrier Templates");
        if (_coordinator is not null) templatePage.Controls.Add(new CarrierTemplateLibraryControl(_coordinator));
        tabs.TabPages.AddRange([printerPage, defaultsPage, templatePage, automationPage, foldersPage]);
        Controls.Add(tabs);
        tabs.BringToFront();

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 96, Height = 34 };
        var save = new Button { Text = "Save settings", Width = 124, Height = 36, BackColor = Color.FromArgb(67, 92, 245), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        save.FlatAppearance.BorderSize = 0;
        save.Click += SaveClicked;
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 62 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 280,
            Padding = new Padding(0, 12, 20, 0),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        var audit = new Button { Text = "View audit log", Width = 120, Height = 36,
            BackColor = Color.FromArgb(239, 243, 252), ForeColor = Color.FromArgb(33, 43, 67), FlatStyle = FlatStyle.Flat };
        audit.FlatAppearance.BorderSize = 0;
        audit.Click += (_, _) => { using var viewer = new AuditLogForm(_auditLogPath); viewer.ShowDialog(this); };
        audit.Location = new Point(24, 12);
        audit.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        footer.Controls.Add(buttons);
        footer.Controls.Add(audit);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public HotfolderConfiguration? Result { get; private set; }

    private void SaveClicked(object? sender, EventArgs arguments)
    {
        try
        {
            Result = BuildConfiguration();
            Result.Validate();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Settings need attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private HotfolderConfiguration BuildConfiguration() => new()
    {
        IncomingFolder = _incoming.Text.Trim(),
        ArchiveFolder = _archive.Text.Trim(),
        ReadyFolder = _ready.Text.Trim(),
        PrintedFolder = _printed.Text.Trim(),
        PhysicalPrinterQueue = _printer.SelectedItem?.ToString() ?? string.Empty,
        PrintResolutionDpi = (_resolution.SelectedItem as ResolutionItem)?.Value ?? 203,
        PrintQuality = (_quality.SelectedItem as QualityItem)?.Value ?? PrintQualityPreset.Standard,
        PrintDarkness = (double)_darkness.Value,
        PrintSpeedIps = (_speed.SelectedItem as SpeedItem)?.Value ?? 4,
        MarginInches = 0.125,
        DefaultFitMode = (_defaultFit.SelectedItem as FitItem)?.Mode ?? LabelFitMode.Proportional,
        DefaultLabelWidthInches = (_defaultSize.SelectedItem as SizeItem)?.Width ?? 4,
        DefaultLabelHeightInches = (_defaultSize.SelectedItem as SizeItem)?.Height ?? 4,
        DefaultRotation = (_defaultRotationChoice.SelectedItem as RotationItem)?.Value ?? LabelRotation.None,
        DefaultBarcodeCompensation = (_defaultBarcode.SelectedItem as BarcodeItem)?.Value ?? RotatedBarcodeCompensation.Off,
        DefaultTextEnhancement = (_defaultText.SelectedItem as TextItem)?.Value ?? TextEnhancement.Off,
        DefaultFromAddressScalePercent = (int)_defaultFromScale.Value,
        DefaultToAddressScalePercent = (int)_defaultToScale.Value,
        WatchEnabled = _watch.Checked,
        AutoPrint = _autoPrint.Checked,
        ScannerVerificationEnabled = _scannerVerification.Checked
    };

    private void QualityTestClicked(object? sender, EventArgs arguments)
    {
        try
        {
            var settings = BuildConfiguration();
            settings.Validate();
            _printCalibration(settings);
            MessageBox.Show("The 4 x 4 print-quality test was sent to the selected printer.",
                "Quality test sent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Quality test could not print", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void RefreshPrintSpeeds(double? preferred)
    {
        var dpi = (_resolution.SelectedItem as ResolutionItem)?.Value ?? 203;
        var current = preferred ?? (_speed.SelectedItem as SpeedItem)?.Value;
        _speed.Items.Clear();
        foreach (var value in HotfolderConfiguration.AllowedPrintSpeeds(dpi))
            _speed.Items.Add(new SpeedItem(value));
        _speed.SelectedItem = _speed.Items.Cast<SpeedItem>()
            .OrderBy(item => current.HasValue ? Math.Abs(item.Value - current.Value) : Math.Abs(item.Value - 4))
            .First();
    }

    private void ConfigureDefaultEditors(HotfolderConfiguration configuration)
    {
        _defaultFit.Items.AddRange([
            new FitItem(LabelFitMode.Proportional, "Proportional - compact blank space and preserve shape"),
            new FitItem(LabelFitMode.SquishToFill, "Squish to fill - stretch content to selected size")
        ]);
        _defaultSize.Items.AddRange([new SizeItem(4, 4), new SizeItem(4, 6), new SizeItem(4, 8)]);
        _defaultRotationChoice.Items.AddRange([
            new RotationItem(LabelRotation.None, "0° - no rotation"),
            new RotationItem(LabelRotation.Clockwise90, "90° clockwise"),
            new RotationItem(LabelRotation.UpsideDown180, "180°"),
            new RotationItem(LabelRotation.Clockwise270, "270° clockwise")
        ]);
        _defaultBarcode.Items.AddRange([
            new BarcodeItem(RotatedBarcodeCompensation.Off, "Off"),
            new BarcodeItem(RotatedBarcodeCompensation.OneDot, "1 dot - recommended starting point"),
            new BarcodeItem(RotatedBarcodeCompensation.TwoDots, "2 dots - stronger correction"),
            new BarcodeItem(RotatedBarcodeCompensation.ThreeDots, "3 dots - aggressive"),
            new BarcodeItem(RotatedBarcodeCompensation.FourDots, "4 dots - very aggressive"),
            new BarcodeItem(RotatedBarcodeCompensation.FiveDots, "5 dots - maximum")
        ]);
        _defaultText.Items.AddRange([
            new TextItem(TextEnhancement.Off, "Off - preserve source address size"),
            new TextItem(TextEnhancement.Larger, "Larger - enlarge FROM and TO addresses"),
            new TextItem(TextEnhancement.ExtraLarge, "Extra large - automatic maximum"),
            new TextItem(TextEnhancement.Custom, "Custom - use separate percentages below")
        ]);
        _defaultFit.SelectedItem = _defaultFit.Items.Cast<FitItem>().First(item => item.Mode == configuration.DefaultFitMode);
        _defaultSize.SelectedItem = _defaultSize.Items.Cast<SizeItem>().First(item =>
            item.Width == configuration.DefaultLabelWidthInches && item.Height == configuration.DefaultLabelHeightInches);
        _defaultRotationChoice.SelectedItem = _defaultRotationChoice.Items.Cast<RotationItem>()
            .First(item => item.Value == configuration.DefaultRotation);
        _defaultBarcode.SelectedItem = _defaultBarcode.Items.Cast<BarcodeItem>()
            .First(item => item.Value == configuration.DefaultBarcodeCompensation);
        _defaultText.SelectedItem = _defaultText.Items.Cast<TextItem>()
            .First(item => item.Value == configuration.DefaultTextEnhancement);
        _defaultFromScale.Value = configuration.EffectiveDefaultFromAddressScalePercent;
        _defaultToScale.Value = configuration.EffectiveDefaultToAddressScalePercent;
        _defaultText.SelectedIndexChanged += (_, _) => RefreshDefaultScaleEnabled();
        RefreshDefaultScaleEnabled();
    }

    private void RefreshDefaultScaleEnabled()
    {
        var enabled = (_defaultText.SelectedItem as TextItem)?.Value == TextEnhancement.Custom;
        _defaultFromScale.Enabled = enabled;
        _defaultToScale.Enabled = enabled;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control editor, Control? action)
    {
        grid.Controls.Add(new Label
        {
            Text = label, AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 68, 83),
            Margin = new Padding(0, 0, 12, 0)
        }, 0, row);
        editor.Dock = DockStyle.None;
        editor.Anchor = editor is NumericUpDown ? AnchorStyles.Left : AnchorStyles.Left | AnchorStyles.Right;
        editor.Margin = new Padding(0, 4, 10, 4);
        grid.Controls.Add(editor, 1, row);
        if (action is not null)
        {
            action.Anchor = AnchorStyles.Left;
            grid.Controls.Add(action, 2, row);
        }
    }

    private static TabPage SettingsPage(string title) => new(title)
    {
        BackColor = Color.White, Padding = new Padding(4)
    };

    private static TableLayoutPanel SettingsGrid(int rows, int rowHeight = 58)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, rowHeight < 58 ? 10 : 20, 24, 8),
            ColumnCount = 3, RowCount = rows + 1
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        for (var index = 0; index < rows; index++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return grid;
    }

    private Button BrowseButton(TextBox target)
    {
        var button = new Button { Text = "Browse", Width = 72, Height = 30, Anchor = AnchorStyles.Left };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { InitialDirectory = target.Text, ShowNewFolderButton = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        };
        return button;
    }

    private sealed record ResolutionItem(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record QualityItem(PrintQualityPreset Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SpeedItem(double Value)
    {
        public override string ToString() => Value.ToString("0.0");
    }

    private sealed record FitItem(LabelFitMode Mode, string Label) { public override string ToString() => Label; }
    private sealed record SizeItem(int Width, int Height) { public override string ToString() => $"{Width} x {Height} inches"; }
    private sealed record RotationItem(LabelRotation Value, string Label) { public override string ToString() => Label; }
    private sealed record BarcodeItem(RotatedBarcodeCompensation Value, string Label) { public override string ToString() => Label; }
    private sealed record TextItem(TextEnhancement Value, string Label) { public override string ToString() => Label; }
}
