using ShipTime4x4.Core.Processing;
using System.Text.Json.Serialization;

namespace ShipTime4x4.Hotfolder.Models;

public enum LabelStatus
{
    Ready,
    Printing,
    Printed,
    Warning,
    Failed
}

public enum ScanVerificationStatus
{
    NotRequired,
    Pending,
    Verified,
    Mismatch
}

public sealed record LabelRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ShipToName { get; init; } = "Unknown recipient";
    public string ShipToAddress { get; init; } = string.Empty;
    public string OriginalFileName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string ArchivePath { get; init; } = string.Empty;
    public string? PrintedPath { get; init; }
    public string SourceFingerprint { get; init; } = string.Empty;
    public int PageCount { get; init; } = 1;
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? PrintedAt { get; init; }
    public LabelStatus Status { get; init; } = LabelStatus.Ready;
    public string? StatusMessage { get; init; }
    public bool UsedFallback { get; init; }
    public ScanVerificationStatus ScanVerification { get; init; }
    public DateTimeOffset? ScanVerifiedAt { get; init; }
    public LabelFitMode FitMode { get; init; } = LabelFitMode.Proportional;
    public int OutputWidthInches { get; init; } = 4;
    public int OutputHeightInches { get; init; } = 4;
    public LabelRotation Rotation { get; init; } = LabelRotation.None;
    public RotatedBarcodeCompensation BarcodeCompensation { get; init; } = RotatedBarcodeCompensation.Off;
    public TextEnhancement TextEnhancement { get; init; } = TextEnhancement.Off;
    // Retained so catalogs created by ReLabel 1.8.0 migrate both new values automatically.
    public int AddressScalePercent { get; init; } = 158;
    public int FromAddressScalePercent { get; init; }
    public int ToAddressScalePercent { get; init; }
    public TemplateSelectionMode TemplateSelection { get; init; } = TemplateSelectionMode.Auto;
    public Guid? SelectedTemplateId { get; init; }
    public AppliedTemplateSnapshot? AppliedTemplate { get; init; }
    [JsonIgnore]
    public int EffectiveFromAddressScalePercent => FromAddressScalePercent == 0
        ? AddressScalePercent : FromAddressScalePercent;
    [JsonIgnore]
    public int EffectiveToAddressScalePercent => ToAddressScalePercent == 0
        ? AddressScalePercent : ToAddressScalePercent;
}
