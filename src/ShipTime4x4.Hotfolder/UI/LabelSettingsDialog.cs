using ShipTime4x4.Core.Processing;
using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.UI;

public sealed class LabelSettingsDialog : Form
{
    private readonly ComboBox _fit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _rotation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _barcodeCompensation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _textEnhancement = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown _fromAddressScale = new()
    {
        Minimum = 100, Maximum = 450, Increment = 5, Width = 104, TextAlign = HorizontalAlignment.Right
    };
    private readonly NumericUpDown _toAddressScale = new()
    {
        Minimum = 100, Maximum = 250, Increment = 5, Width = 104, TextAlign = HorizontalAlignment.Right
    };
    private readonly Label _fromAddressScaleSuffix = new() { Text = "%", AutoSize = true };
    private readonly Label _toAddressScaleSuffix = new() { Text = "%", AutoSize = true };
    private readonly ComboBox _template = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    public LabelSettingsDialog(LabelRecord record, IReadOnlyList<CarrierTemplate>? templates = null)
        : this(record.FitMode, record.OutputWidthInches, record.OutputHeightInches, record.Rotation,
            record.BarcodeCompensation, record.TextEnhancement, record.EffectiveFromAddressScalePercent,
            record.EffectiveToAddressScalePercent,
            "Label settings", record.ShipToName, "These choices apply only to this label and its reprints.", "Apply")
    {
        ConfigureTemplateChoices(record, templates ?? []);
    }

    public LabelSettingsDialog(LabelFitMode fitMode, int outputWidth, int outputHeight, LabelRotation rotation,
        RotatedBarcodeCompensation barcodeCompensation = RotatedBarcodeCompensation.Off,
        TextEnhancement textEnhancement = TextEnhancement.Off, int fromAddressScalePercent = 158,
        int toAddressScalePercent = 158)
        : this(fitMode, outputWidth, outputHeight, rotation, barcodeCompensation, textEnhancement,
            fromAddressScalePercent, toAddressScalePercent,
            "Label defaults", "Defaults for new labels",
            "These choices apply to labels imported after you save Settings.", "Use defaults")
    {
    }

    private LabelSettingsDialog(LabelFitMode fitMode, int outputWidth, int outputHeight, LabelRotation rotation,
        RotatedBarcodeCompensation barcodeCompensation, TextEnhancement textEnhancement,
        int fromAddressScalePercent, int toAddressScalePercent,
        string windowTitle, string heading, string introText, string applyText)
    {
        Text = windowTitle;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 665);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        _fit.Items.AddRange([
            new FitItem(LabelFitMode.Proportional, "Proportional - compact blank space and preserve shape"),
            new FitItem(LabelFitMode.SquishToFill, "Squish to fill - stretch content to the selected size")
        ]);
        _size.Items.AddRange([new SizeItem(4, 4), new SizeItem(4, 6), new SizeItem(4, 8)]);
        _rotation.Items.AddRange([
            new RotationItem(LabelRotation.None, "0° - no rotation"),
            new RotationItem(LabelRotation.Clockwise90, "90° clockwise"),
            new RotationItem(LabelRotation.UpsideDown180, "180°"),
            new RotationItem(LabelRotation.Clockwise270, "270° clockwise")
        ]);
        _barcodeCompensation.Items.AddRange([
            new BarcodeCompensationItem(RotatedBarcodeCompensation.Off, "Off"),
            new BarcodeCompensationItem(RotatedBarcodeCompensation.OneDot, "1 dot - recommended starting point"),
            new BarcodeCompensationItem(RotatedBarcodeCompensation.TwoDots, "2 dots - stronger correction"),
            new BarcodeCompensationItem(RotatedBarcodeCompensation.ThreeDots, "3 dots - aggressive"),
            new BarcodeCompensationItem(RotatedBarcodeCompensation.FourDots, "4 dots - very aggressive"),
            new BarcodeCompensationItem(RotatedBarcodeCompensation.FiveDots, "5 dots - maximum")
        ]);
        _textEnhancement.Items.AddRange([
            new TextEnhancementItem(TextEnhancement.Off, "Off - preserve source address size"),
            new TextEnhancementItem(TextEnhancement.Larger, "Larger - enlarge FROM and TO addresses"),
            new TextEnhancementItem(TextEnhancement.ExtraLarge, "Extra large - automatic maximum"),
            new TextEnhancementItem(TextEnhancement.Custom, "Custom - use separate percentages below")
        ]);
        _fit.SelectedItem = _fit.Items.Cast<FitItem>().First(item => item.Mode == fitMode);
        _size.SelectedItem = _size.Items.Cast<SizeItem>().FirstOrDefault(item =>
            item.Width == outputWidth && item.Height == outputHeight) ?? _size.Items[0];
        _rotation.SelectedItem = _rotation.Items.Cast<RotationItem>().First(item => item.Rotation == rotation);
        _barcodeCompensation.SelectedItem = _barcodeCompensation.Items.Cast<BarcodeCompensationItem>()
            .First(item => item.Value == barcodeCompensation);
        _textEnhancement.SelectedItem = _textEnhancement.Items.Cast<TextEnhancementItem>()
            .First(item => item.Value == textEnhancement);
        _fromAddressScale.Value = Math.Clamp(fromAddressScalePercent, 100, 450);
        _toAddressScale.Value = Math.Clamp(toAddressScalePercent, 100, 250);
        _textEnhancement.SelectedIndexChanged += (_, _) => RefreshAddressScaleEnabled();

        var title = new Label { Text = heading, Font = new Font("Segoe UI Semibold", 16f),
            ForeColor = Color.FromArgb(33, 43, 67), AutoSize = true, Location = new Point(26, 20) };
        var intro = new Label { Text = introText,
            ForeColor = Color.FromArgb(112, 122, 145), AutoSize = true, Location = new Point(29, 55) };
        var fitLabel = FieldLabel("Fitting", 98);
        _fit.Location = new Point(185, 93);
        var sizeLabel = FieldLabel("Output label size", 151);
        _size.Location = new Point(185, 146);
        var rotationLabel = FieldLabel("Print rotation", 204);
        _rotation.Location = new Point(185, 199);
        var barcodeLabel = FieldLabel("Rotated barcode", 257);
        _barcodeCompensation.Location = new Point(185, 252);
        var textLabel = FieldLabel("FROM/TO addresses", 310);
        _textEnhancement.Location = new Point(185, 305);
        var fromScaleLabel = FieldLabel("Custom FROM scale", 355);
        _fromAddressScale.Location = new Point(185, 358);
        _fromAddressScaleSuffix.Location = new Point(294, 363);
        var toScaleLabel = FieldLabel("Custom TO scale", 408);
        _toAddressScale.Location = new Point(185, 411);
        _toAddressScaleSuffix.Location = new Point(294, 416);
        RefreshAddressScaleEnabled();
        var templateLabel = FieldLabel("Carrier template", 461);
        _template.Location = new Point(185, 464);
        var note = new Label
        {
            Text = "Barcode correction applies only at 90°/270°. Custom FROM and TO percentages replace the address-size preset independently; collision and line-preservation safeguards still apply.",
            ForeColor = Color.FromArgb(112, 122, 145), Location = new Point(29, 510), Size = new Size(500, 54)
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(92, 36),
            Location = new Point(326, 609) };
        var save = new Button { Text = applyText, Size = new Size(104, 36), Location = new Point(429, 609),
            BackColor = Color.FromArgb(67, 92, 245), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Controls.AddRange([title, intro, fitLabel, _fit, sizeLabel, _size, rotationLabel, _rotation,
            barcodeLabel, _barcodeCompensation, textLabel, _textEnhancement, fromScaleLabel, _fromAddressScale,
            _fromAddressScaleSuffix, toScaleLabel, _toAddressScale, _toAddressScaleSuffix,
            templateLabel, _template, note, cancel, save]);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public LabelFitMode FitMode => (_fit.SelectedItem as FitItem)?.Mode ?? LabelFitMode.Proportional;
    public int OutputWidth => (_size.SelectedItem as SizeItem)?.Width ?? 4;
    public int OutputHeight => (_size.SelectedItem as SizeItem)?.Height ?? 4;
    public LabelRotation Rotation => (_rotation.SelectedItem as RotationItem)?.Rotation ?? LabelRotation.None;
    public RotatedBarcodeCompensation BarcodeCompensation =>
        (_barcodeCompensation.SelectedItem as BarcodeCompensationItem)?.Value ?? RotatedBarcodeCompensation.Off;
    public TextEnhancement TextEnhancement =>
        (_textEnhancement.SelectedItem as TextEnhancementItem)?.Value ?? TextEnhancement.Off;
    public int FromAddressScalePercent => (int)_fromAddressScale.Value;
    public int ToAddressScalePercent => (int)_toAddressScale.Value;
    public TemplateSelectionMode TemplateSelection =>
        (_template.SelectedItem as TemplateItem)?.Mode ?? TemplateSelectionMode.Auto;
    public Guid? SelectedTemplateId => (_template.SelectedItem as TemplateItem)?.Id;

    private void ConfigureTemplateChoices(LabelRecord record, IReadOnlyList<CarrierTemplate> templates)
    {
        _template.Items.Add(new TemplateItem(TemplateSelectionMode.Auto, null,
            record.AppliedTemplate is null ? "Auto - no current match" :
                $"Auto - {record.AppliedTemplate.Carrier} / {record.AppliedTemplate.LayoutName} r{record.AppliedTemplate.Revision} " +
                $"({record.AppliedTemplate.MatchScore:P0}, {record.AppliedTemplate.MatchDisposition})"));
        _template.Items.Add(new TemplateItem(TemplateSelectionMode.None, null, "No template - generalized layout"));
        foreach (var template in templates.Where(x => x.Enabled))
            _template.Items.Add(new TemplateItem(TemplateSelectionMode.Specific, template.Id,
                $"{template.DisplayName} r{template.Revision}"));
        _template.SelectedItem = _template.Items.Cast<TemplateItem>().FirstOrDefault(x =>
            x.Mode == record.TemplateSelection &&
            (x.Mode != TemplateSelectionMode.Specific || x.Id == record.SelectedTemplateId)) ?? _template.Items[0];
    }

    private void RefreshAddressScaleEnabled()
    {
        var enabled = TextEnhancement == TextEnhancement.Custom;
        _fromAddressScale.Enabled = enabled;
        _toAddressScale.Enabled = enabled;
        _fromAddressScaleSuffix.Enabled = enabled;
        _toAddressScaleSuffix.Enabled = enabled;
    }

    private static Label FieldLabel(string text, int top) => new()
    {
        Text = text, Location = new Point(29, top), Size = new Size(145, 30), TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(55, 68, 83), Font = new Font("Segoe UI Semibold", 9.5f)
    };

    private sealed record FitItem(LabelFitMode Mode, string Label) { public override string ToString() => Label; }
    private sealed record SizeItem(int Width, int Height) { public override string ToString() => $"{Width} x {Height} inches"; }
    private sealed record RotationItem(LabelRotation Rotation, string Label) { public override string ToString() => Label; }
    private sealed record BarcodeCompensationItem(RotatedBarcodeCompensation Value, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record TextEnhancementItem(TextEnhancement Value, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record TemplateItem(TemplateSelectionMode Mode, Guid? Id, string Label)
    { public override string ToString() => Label; }
}
