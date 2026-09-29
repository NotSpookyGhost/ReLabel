using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Hotfolder.Models;

// Append-only: numeric values are persisted in existing template catalogs.
public enum TemplateZoneType
{
    FromAddress, ToAddress, CarrierBarcode, ShippingBarcode, CarrierLogo, OtherSection,
    BuiltInAsset, DividerLine
}
public enum TemplateBuiltInAssetKind { None, FromDe, ToA }
public enum TemplateOtherPriority { Required, Flexible, Optional }
public enum TemplateMatchDisposition { None, Ambiguous, Confident, Manual }
public enum TemplateSelectionMode { Auto, Specific, None }

public sealed record NormalizedRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public PixelRect ToPixels(int width, int height)
    {
        var left = Math.Clamp((int)Math.Round(X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Round(Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Round(Right * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Round(Bottom * height), top + 1, height);
        return new PixelRect(left, top, right - left, bottom - top);
    }
    public static NormalizedRect FromPixels(PixelRect value, int width, int height) => new(
        value.X / (double)width, value.Y / (double)height,
        value.Width / (double)width, value.Height / (double)height);
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

public sealed record TemplateZone
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TemplateZoneType Type { get; init; }
    public NormalizedRect Source { get; init; } = new(0, 0, .25, .15);
    public NormalizedRect Destination4x4 { get; init; } = new(.03125, .03125, .9375, .15);
    public TemplateOtherPriority OtherPriority { get; init; } = TemplateOtherPriority.Required;
    public string OtherName { get; init; } = string.Empty;
    public TemplateBuiltInAssetKind BuiltInAsset { get; init; }
    // Null keeps catalogs created by 2.1.0 backward-compatible: old zones were all placed.
    public bool? Placed4x4 { get; init; }
    [JsonIgnore] public bool IsPlaced4x4 => Placed4x4 ?? true;
    [JsonIgnore] public bool IsGenerated => Type is TemplateZoneType.BuiltInAsset or TemplateZoneType.DividerLine;
    [JsonIgnore] public bool HasSourceRegion => !IsGenerated;
    [JsonIgnore] public string DisplayName => Type == TemplateZoneType.OtherSection &&
        !string.IsNullOrWhiteSpace(OtherName) ? OtherName : Type switch
        {
            TemplateZoneType.FromAddress => "From Address",
            TemplateZoneType.ToAddress => "To Address",
            TemplateZoneType.CarrierBarcode => "Carrier Barcode",
            TemplateZoneType.ShippingBarcode => "Shipping Barcode",
            TemplateZoneType.CarrierLogo => "Carrier Logo",
            TemplateZoneType.BuiltInAsset when BuiltInAsset == TemplateBuiltInAssetKind.FromDe => "FROM / DE",
            TemplateZoneType.BuiltInAsset when BuiltInAsset == TemplateBuiltInAssetKind.ToA => "TO / A",
            TemplateZoneType.DividerLine => "Divider Line",
            _ => "Other Section"
        };
    public bool Suggested { get; init; }
    // Editor-only protection. Locked zones still render and print normally, but cannot be
    // selected with a left click, dragged, resized, aligned, staged, or deleted until unlocked.
    public bool Locked { get; init; }
}

public sealed record TemplateFeatures
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string StaticSignature { get; init; } = string.Empty;
    public IReadOnlyList<NormalizedRect> BarcodeLocations { get; init; } = [];
    public IReadOnlyList<string> BarcodeFormats { get; init; } = [];
    public IReadOnlyList<string> StaticOcrAnchors { get; init; } = [];
}

public sealed record CarrierTemplate
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Revision { get; init; } = 1;
    public string Carrier { get; init; } = string.Empty;
    public string LayoutName { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string SourcePdfPath { get; init; } = string.Empty;
    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.Now;
    public TemplateFeatures Features { get; init; } = new();
    public IReadOnlyList<TemplateZone> Zones { get; init; } = [];
    // FROM / DE and TO / A are a linked physical-size pair in the editor. Keeping the
    // shared scale on the template also applies it when one asset remains staged.
    public double BuiltInAssetScale { get; init; } = 1;
    [JsonIgnore] public string DisplayName => $"{Carrier} - {LayoutName}";
}

public sealed record TemplateMatch(Guid? TemplateId, int Revision, double Score,
    TemplateMatchDisposition Disposition, double ScaleX = 1, double ScaleY = 1,
    double OffsetX = 0, double OffsetY = 0);

public sealed record AppliedTemplateSnapshot
{
    public Guid TemplateId { get; init; }
    public int Revision { get; init; }
    public string Carrier { get; init; } = string.Empty;
    public string LayoutName { get; init; } = string.Empty;
    public double MatchScore { get; init; }
    public TemplateMatchDisposition MatchDisposition { get; init; }
    public IReadOnlyList<TemplateZone> Zones { get; init; } = [];
    [JsonIgnore] public string Hash => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
}
