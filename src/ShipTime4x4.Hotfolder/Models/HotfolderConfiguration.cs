using System.Text.Json;
using System.Text.Json.Serialization;
using ShipTime4x4.Core.Processing;

namespace ShipTime4x4.Hotfolder.Models;

public sealed record HotfolderConfiguration
{
    public string IncomingFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReLabel", "Incoming");
    public string ArchiveFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReLabel", "Archive");
    public string ReadyFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReLabel", "Ready");
    public string PrintedFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReLabel", "Printed");
    public string PhysicalPrinterQueue { get; init; } = string.Empty;
    public int PrintResolutionDpi { get; init; } = 203;
    public PrintQualityPreset PrintQuality { get; init; } = PrintQualityPreset.Standard;
    public double PrintDarkness { get; init; } = 10;
    public double PrintSpeedIps { get; init; } = 4;
    public double MarginInches { get; init; } = 0.125;
    public LabelFitMode DefaultFitMode { get; init; } = LabelFitMode.Proportional;
    public int DefaultLabelWidthInches { get; init; } = 4;
    public int DefaultLabelHeightInches { get; init; } = 4;
    public LabelRotation DefaultRotation { get; init; } = LabelRotation.None;
    public RotatedBarcodeCompensation DefaultBarcodeCompensation { get; init; } = RotatedBarcodeCompensation.Off;
    public TextEnhancement DefaultTextEnhancement { get; init; } = TextEnhancement.Off;
    // Retained so settings created by ReLabel 1.8.0 migrate both new defaults automatically.
    public int DefaultAddressScalePercent { get; init; } = 158;
    public int DefaultFromAddressScalePercent { get; init; }
    public int DefaultToAddressScalePercent { get; init; }
    [JsonIgnore]
    public int EffectiveDefaultFromAddressScalePercent => DefaultFromAddressScalePercent == 0
        ? DefaultAddressScalePercent : DefaultFromAddressScalePercent;
    [JsonIgnore]
    public int EffectiveDefaultToAddressScalePercent => DefaultToAddressScalePercent == 0
        ? DefaultAddressScalePercent : DefaultToAddressScalePercent;
    public bool AutoPrint { get; init; }
    public bool WatchEnabled { get; init; } = true;
    public bool ScannerVerificationEnabled { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(IncomingFolder))
            throw new InvalidOperationException("The incoming folder is required.");
        if (string.IsNullOrWhiteSpace(ArchiveFolder))
            throw new InvalidOperationException("The archive folder is required.");
        if (string.IsNullOrWhiteSpace(ReadyFolder))
            throw new InvalidOperationException("The ready folder is required.");
        if (string.IsNullOrWhiteSpace(PrintedFolder))
            throw new InvalidOperationException("The printed folder is required.");
        if (PrintResolutionDpi is not (203 or 300 or 600))
            throw new InvalidOperationException("Resolution must be 203, 300, or 600 dpi.");
        if (!Enum.IsDefined(PrintQuality))
            throw new InvalidOperationException("Select Fast, Standard, or High Quality.");
        if (PrintDarkness is < 0 or > 30)
            throw new InvalidOperationException("Label darkness must be between 0 and 30.");
        if (!AllowedPrintSpeeds(PrintResolutionDpi).Any(speed => Math.Abs(speed - PrintSpeedIps) < 0.01))
            throw new InvalidOperationException($"Select a print speed supported at {PrintResolutionDpi} dpi.");
        if (MarginInches is < 0 or > 0.25)
            throw new InvalidOperationException("Margin must be between 0 and 0.25 inches.");
        if (!Enum.IsDefined(DefaultFitMode))
            throw new InvalidOperationException("Select a supported default fitting type.");
        if ((DefaultLabelWidthInches, DefaultLabelHeightInches) is not ((4, 4) or (4, 6) or (4, 8)))
            throw new InvalidOperationException("Default label size must be 4 x 4, 4 x 6, or 4 x 8 inches.");
        if (!Enum.IsDefined(DefaultRotation))
            throw new InvalidOperationException("Default rotation must be 0, 90, 180, or 270 degrees.");
        if (!Enum.IsDefined(DefaultBarcodeCompensation))
            throw new InvalidOperationException("Select a supported default barcode compensation.");
        if (!Enum.IsDefined(DefaultTextEnhancement))
            throw new InvalidOperationException("Select a supported default text enhancement.");
        if (DefaultAddressScalePercent is < 100 or > 250)
            throw new InvalidOperationException("Default custom address scale must be between 100% and 250%.");
        if (DefaultFromAddressScalePercent != 0 && DefaultFromAddressScalePercent is < 100 or > 450)
            throw new InvalidOperationException("Default FROM address scale must be between 100% and 450%.");
        if (DefaultToAddressScalePercent != 0 && DefaultToAddressScalePercent is < 100 or > 250)
            throw new InvalidOperationException("Default TO address scale must be between 100% and 250%.");
        var folders = new[] { IncomingFolder, ReadyFolder, PrintedFolder, ArchiveFolder };
        for (var first = 0; first < folders.Length; first++)
        for (var second = first + 1; second < folders.Length; second++)
            if (PathsOverlap(folders[first], folders[second]) || PathsOverlap(folders[second], folders[first]))
                throw new InvalidOperationException("Incoming, Ready, Printed, and Archive folders cannot be nested inside one another.");
        if (SamePath(IncomingFolder, PrintedFolder))
            throw new InvalidOperationException("The incoming and printed folders must be different.");
        if (SamePath(IncomingFolder, ReadyFolder) || SamePath(ReadyFolder, PrintedFolder) ||
            SamePath(ReadyFolder, ArchiveFolder))
            throw new InvalidOperationException("Incoming, Ready, Printed, and Archive must use different folders.");
        if (AutoPrint && string.IsNullOrWhiteSpace(PhysicalPrinterQueue))
            throw new InvalidOperationException("Select a physical Zebra queue before enabling automatic printing.");
    }

    private static bool PathsOverlap(string incoming, string archive)
    {
        var incomingPath = Path.GetFullPath(incoming).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var archivePath = Path.GetFullPath(archive).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return archivePath.StartsWith(incomingPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<double> AllowedPrintSpeeds(int dpi) => dpi switch
    {
        203 => [2.4, 3, 4, 5, 6, 7, 8, 9, 10],
        300 => [2.4, 3, 4, 5, 6, 7, 8],
        600 => [1.5, 2, 3, 4],
        _ => throw new ArgumentOutOfRangeException(nameof(dpi))
    };
}

public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ConfigurationStore(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        Path = System.IO.Path.Combine(dataFolder, "settings.json");
    }

    public string Path { get; }

    public HotfolderConfiguration Load()
    {
        if (!File.Exists(Path))
            return new HotfolderConfiguration();
        var json = File.ReadAllText(Path);
        var value = JsonSerializer.Deserialize<HotfolderConfiguration>(json, Options)
            ?? new HotfolderConfiguration();
        if (!JsonContainsProperty(json, nameof(HotfolderConfiguration.PrintedFolder)))
            value = value with { PrintedFolder = new HotfolderConfiguration().PrintedFolder };
        if (!JsonContainsProperty(json, nameof(HotfolderConfiguration.ReadyFolder)))
            value = value with { ReadyFolder = new HotfolderConfiguration().ReadyFolder };
        if (!JsonContainsProperty(json, nameof(HotfolderConfiguration.MarginInches)) || value.MarginInches < 0.125)
            value = value with { MarginInches = 0.125 };
        value.Validate();
        return value;
    }

    private static bool JsonContainsProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .Any(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(HotfolderConfiguration configuration)
    {
        configuration.Validate();
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(configuration, Options));
        File.Move(temporary, Path, true);
    }
}
