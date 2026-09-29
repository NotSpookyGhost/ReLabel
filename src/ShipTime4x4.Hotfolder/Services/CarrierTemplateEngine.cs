using System.Text.RegularExpressions;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Services;

public sealed record TemplateMatchResult(TemplateMatch Match, CarrierTemplate? Template,
    AppliedTemplateSnapshot? Snapshot);

public sealed class CarrierTemplateEngine
{
    private readonly BarcodeInspector _barcodes = new();

    public static bool IsFourBySix(GrayImage page)
    {
        var portrait = page.Width <= page.Height ? page : page.RotateClockwise(90);
        return Math.Abs(portrait.Width / (double)portrait.Height - (4d / 6d)) <= .035;
    }

    public static GrayImage NormalizePortrait(GrayImage page) =>
        page.Width <= page.Height ? page : page.RotateClockwise(90);

    public CarrierTemplate BuildTemplate(Guid id, string carrier, string layoutName, string sourcePdf,
        PdfInspection inspection, IReadOnlyList<TemplateZone>? zones = null)
    {
        if (inspection.PageCount != 1)
            throw new InvalidOperationException("Carrier template samples must contain exactly one 4 x 6 label page.");
        var page = NormalizePortrait(inspection.SourcePage);
        if (!IsFourBySix(page)) throw new InvalidOperationException("Carrier templates require a genuine 4 x 6 PDF page.");
        zones ??= SuggestZones(page, inspection.RecognizedTsv);
        var features = ExtractFeatures(page, inspection.RecognizedTsv, zones);
        return new CarrierTemplate { Id = id, Carrier = carrier.Trim(), LayoutName = layoutName.Trim(),
            SourcePdfPath = sourcePdf, Features = features, Zones = zones, LastModified = DateTimeOffset.Now };
    }

    public TemplateMatchResult Match(PdfInspection inspection, IReadOnlyList<CarrierTemplate> templates,
        Guid? specificTemplate = null)
    {
        var page = NormalizePortrait(inspection.SourcePage);
        if (!IsFourBySix(page)) return None();
        var candidates = templates.Where(x => x.Enabled && (!specificTemplate.HasValue || x.Id == specificTemplate)).ToArray();
        if (candidates.Length == 0) return None();
        var detected = _barcodes.Detect(page);
        var scored = candidates.Select(template => (Template: template,
                Score: Score(template, page, inspection.RecognizedTsv, detected)))
            .OrderByDescending(x => x.Score).ToArray();
        var best = scored[0];
        var second = scored.Length > 1 ? scored[1].Score : 0;
        var disposition = specificTemplate.HasValue
            ? TemplateMatchDisposition.Manual
            : best.Score >= .80 && best.Score - second >= .08
                ? TemplateMatchDisposition.Confident
                : best.Score >= .65 ? TemplateMatchDisposition.Ambiguous : TemplateMatchDisposition.None;
        if (disposition == TemplateMatchDisposition.None) return None(best.Score);
        var registration = EstimateRegistration(best.Template.Features.BarcodeLocations,
            detected.Select(x => NormalizedRect.FromPixels(x.Bounds, page.Width, page.Height)).ToArray());
        var match = new TemplateMatch(best.Template.Id, best.Template.Revision, best.Score, disposition,
            registration.ScaleX, registration.ScaleY, registration.OffsetX, registration.OffsetY);
        var transformedZones = best.Template.Zones.Select(zone =>
        {
            if (zone.IsGenerated) return zone with { };
            var transformed = zone with { Source = Transform(zone.Source, registration) };
            if (!IsRequired(zone)) return transformed;
            var originalHasInk = HasInk(page, zone.Source);
            var transformedHasInk = HasInk(page, transformed.Source);
            return originalHasInk && !transformedHasInk ? zone with { } : transformed;
        }).ToArray();
        var snapshot = new AppliedTemplateSnapshot { TemplateId = best.Template.Id, Revision = best.Template.Revision,
            Carrier = best.Template.Carrier, LayoutName = best.Template.LayoutName, MatchScore = best.Score,
            MatchDisposition = disposition, Zones = transformedZones };
        return new TemplateMatchResult(match, best.Template, snapshot);
    }

    public IReadOnlyList<TemplateZone> SuggestZones(GrayImage sourcePage, string? tsv)
    {
        var page = NormalizePortrait(sourcePage);
        var zones = new List<TemplateZone>();
        var addresses = ShippingAddressRegionLocator.Locate(tsv ?? string.Empty, page.Width, page.Height,
            PdfLabelProcessor.SourceRenderDpi).OrderBy(x => x.X).ToArray();
        if (addresses.Length > 0) zones.Add(NewZone(TemplateZoneType.FromAddress, addresses[0], page, true));
        if (addresses.Length > 1) zones.Add(NewZone(TemplateZoneType.ToAddress, addresses[^1], page, true));

        var detected = _barcodes.Detect(page)
            .Where(barcode => !addresses.Any(address => Coverage(address, barcode.Bounds) >= .35))
            .OrderBy(x => x.Bounds.Y).ToArray();
        for (var i = 0; i < detected.Length; i++)
        {
            var type = i == detected.Length - 1 && detected.Length > 1
                ? TemplateZoneType.ShippingBarcode : TemplateZoneType.CarrierBarcode;
            zones.Add(NewZone(type, detected[i].Bounds.Inflate(Math.Max(3, page.Width / 150),
                Math.Max(3, page.Height / 200), page.Width, page.Height), page, true));
        }

        var topHeight = Math.Max(1, page.Height / 7);
        var topBounds = ImageAnalysis.FindContentBounds(
            page.Crop(new PixelRect(0, 0, page.Width, topHeight)), padding: 2);
        if (!topBounds.IsEmpty)
        {
            var logo = new PixelRect(topBounds.X, topBounds.Y, Math.Min(topBounds.Width, page.Width / 2), topBounds.Height);
            if (!zones.Any(x => Intersects(x.Source.ToPixels(page.Width, page.Height), logo)))
                zones.Add(NewZone(TemplateZoneType.CarrierLogo, logo, page, true));
        }

        // Other sections are deliberately user-created; automatic suggestions are limited
        // to the named, semantically identifiable zones.
        return zones.Select(zone => zone with { Placed4x4 = false }).ToArray();
    }

    public static IReadOnlyList<PixelRect> FindUncoveredBarcodes(
        IEnumerable<PixelRect> detectedBounds,
        IReadOnlyList<TemplateZone> zones,
        int pageWidth,
        int pageHeight)
    {
        var addressZones = zones
            .Where(zone => zone.HasSourceRegion &&
                           zone.Type is TemplateZoneType.FromAddress or TemplateZoneType.ToAddress)
            .Select(zone => zone.Source.ToPixels(pageWidth, pageHeight))
            .ToArray();
        var barcodeZones = zones
            .Where(zone => zone.HasSourceRegion &&
                           zone.Type is TemplateZoneType.CarrierBarcode or TemplateZoneType.ShippingBarcode)
            .ToArray();

        return detectedBounds
            // Dense address text can resemble a short 1D barcode. Suggestions already
            // reject these detections; save validation must use the same rule.
            .Where(bounds => !addressZones.Any(address => Coverage(address, bounds) >= .35))
            .Where(bounds =>
            {
                var normalized = NormalizedRect.FromPixels(bounds, pageWidth, pageHeight);
                return !barcodeZones.Any(zone =>
                    ContainsWithTolerance(zone.Source, normalized, .01) ||
                    Coverage(zone.Source.ToPixels(pageWidth, pageHeight), bounds) >= .85);
            })
            .ToArray();
    }

    public static IReadOnlyList<TemplateZone> AutoArrange(IReadOnlyList<TemplateZone> zones)
    {
        const double margin = .03125;
        var source = zones.Where(zone => zone.HasSourceRegion).ToArray();
        if (source.Length == 0) return zones.Select(zone => zone with { Placed4x4 = true }).ToArray();

        // Preserve the original carrier design: left/right address columns remain columns,
        // and each barcode/section stays in its original vertical order. We normalize the
        // visible source-zone union into the safe 4 x 4 square instead of re-stacking zones.
        var left = source.Min(zone => zone.Source.X);
        var top = source.Min(zone => zone.Source.Y);
        var right = source.Max(zone => zone.Source.Right);
        var bottom = source.Max(zone => zone.Source.Bottom);
        var spanX = Math.Max(.08, right - left);
        var spanY = Math.Max(.08, bottom - top);
        var usable = 1 - margin * 2;
        var output = zones.Select(zone =>
        {
            if (!zone.HasSourceRegion)
            {
                var generated = zone.Type == TemplateZoneType.BuiltInAsset
                    ? TemplateGeneratedContent.DefaultDestination(zone.BuiltInAsset)
                    : zone.Destination4x4;
                return zone with { Destination4x4 = ClampToSafe(generated), Placed4x4 = true, Suggested = false };
            }
            var rect = zone.Source;
            var mapped = new NormalizedRect(
                margin + (rect.X - left) / spanX * usable,
                margin + (rect.Y - top) / spanY * usable,
                rect.Width / spanX * usable,
                rect.Height / spanY * usable);
            return zone with { Destination4x4 = ClampToSafe(mapped), Placed4x4 = true, Suggested = false };
        }).ToArray();
        return output;
    }

    private static NormalizedRect ClampToSafe(NormalizedRect value)
    {
        const double margin = .03125;
        var width = Math.Clamp(value.Width, .003, 1 - margin * 2);
        var height = Math.Clamp(value.Height, .003, 1 - margin * 2);
        return new NormalizedRect(Math.Clamp(value.X, margin, 1 - margin - width),
            Math.Clamp(value.Y, margin, 1 - margin - height), width, height);
    }

    public static TemplateZone PlaceAtReferenceSize(TemplateZone zone)
    {
        const double margin = .03125;
        var usable = 1 - margin * 2;
        var requested = zone.Type == TemplateZoneType.BuiltInAsset
            ? TemplateGeneratedContent.DefaultDestination(zone.BuiltInAsset)
            : zone.Type == TemplateZoneType.DividerLine
                ? zone.Destination4x4
                : new NormalizedRect(zone.Source.X, zone.Source.Y * 1.5,
                    zone.Source.Width, zone.Source.Height * 1.5);
        var width = Math.Min(usable, Math.Max(.015, requested.Width));
        var height = Math.Min(usable, Math.Max(.006, requested.Height));
        var x = Math.Clamp(requested.X, margin, 1 - margin - width);
        var y = Math.Clamp(requested.Y, margin, 1 - margin - height);
        return zone with { Destination4x4 = new NormalizedRect(x, y, width, height),
            Placed4x4 = true, Suggested = false };
    }

    public static TemplateZone StretchToHorizontalEdges(TemplateZone zone)
    {
        const double margin = .03125;
        return zone with { Destination4x4 = zone.Destination4x4 with
            { X = margin, Width = 1 - margin * 2 }, Placed4x4 = true, Suggested = false };
    }

    public static TemplateZone RotateDividerLine(TemplateZone zone)
    {
        if (zone.Type != TemplateZoneType.DividerLine) return zone;
        const double margin = .03125;
        var current = zone.Destination4x4;
        var width = current.Height;
        var height = current.Width;
        var centerX = current.X + current.Width / 2;
        var centerY = current.Y + current.Height / 2;
        var x = Math.Clamp(centerX - width / 2, margin, 1 - margin - width);
        var y = Math.Clamp(centerY - height / 2, margin, 1 - margin - height);
        return zone with { Destination4x4 = new NormalizedRect(x, y, width, height),
            Placed4x4 = true, Suggested = false };
    }

    public static IReadOnlyList<TemplateZone> MoveLayer(
        IReadOnlyList<TemplateZone> zones, Guid selectedId, bool above)
    {
        var output = zones.Select(zone => zone with { }).ToList();
        var index = output.FindIndex(zone => zone.Id == selectedId);
        if (index < 0 || !output[index].IsPlaced4x4) return output;
        var selected = output[index];
        var overlapping = output.Where((zone, candidate) => candidate != index && zone.IsPlaced4x4 &&
                Overlap(zone.Destination4x4, selected.Destination4x4) > 0)
            .Select(zone => zone.Id).ToArray();
        if (overlapping.Length == 0) return output;
        output.RemoveAt(index);
        var targets = overlapping.Select(id => output.FindIndex(zone => zone.Id == id)).Where(i => i >= 0).ToArray();
        var insertion = above ? targets.Max() + 1 : targets.Min();
        output.Insert(Math.Clamp(insertion, 0, output.Count), selected);
        return output;
    }

    public static bool HasDestinationOverlap(IReadOnlyList<TemplateZone> zones, Guid selectedId)
    {
        var selected = zones.FirstOrDefault(zone => zone.Id == selectedId);
        return selected is not null && selected.IsPlaced4x4 && zones.Any(zone => zone.Id != selectedId &&
            zone.IsPlaced4x4 && Overlap(zone.Destination4x4, selected.Destination4x4) > 0);
    }

    public TemplateFeatures ExtractFeatures(GrayImage sourcePage, string? tsv, IReadOnlyList<TemplateZone> zones)
    {
        var page = NormalizePortrait(sourcePage);
        var barcodes = _barcodes.Detect(page);
        return new TemplateFeatures { Width = page.Width, Height = page.Height,
            StaticSignature = Signature(page, zones),
            BarcodeLocations = barcodes.Select(x => NormalizedRect.FromPixels(x.Bounds, page.Width, page.Height)).ToArray(),
            BarcodeFormats = barcodes.Select(x => x.Format.ToString()).OrderBy(x => x).ToArray(),
            StaticOcrAnchors = StaticAnchors(tsv, zones, page.Width, page.Height) };
    }

    private double Score(CarrierTemplate template, GrayImage page, string? tsv,
        IReadOnlyList<BarcodeRegion> detected)
    {
        var currentSignature = Signature(page, template.Zones);
        var structure = SignatureSimilarity(template.Features.StaticSignature, currentSignature);
        var expectedFormats = template.Features.BarcodeFormats.OrderBy(x => x).ToArray();
        var actualFormats = detected.Select(x => x.Format.ToString()).OrderBy(x => x).ToArray();
        var format = expectedFormats.Length == 0 ? .5 : SetSimilarity(expectedFormats, actualFormats);
        var position = PositionSimilarity(template.Features.BarcodeLocations,
            detected.Select(x => NormalizedRect.FromPixels(x.Bounds, page.Width, page.Height)).ToArray());
        var anchors = SetSimilarity(template.Features.StaticOcrAnchors,
            StaticAnchors(tsv, template.Zones, page.Width, page.Height));
        var hasLogo = template.Zones.Any(x => x.Type == TemplateZoneType.CarrierLogo);
        var structureWeight = hasLogo ? .48 : .55;
        var anchorWeight = hasLogo ? .22 : .15;
        return Math.Clamp(structure * structureWeight + format * .15 + position * .15 + anchors * anchorWeight, 0, 1);
    }

    private static string Signature(GrayImage image, IReadOnlyList<TemplateZone> zones)
    {
        const int columns = 32, rows = 48;
        var variable = zones.Where(x => x.HasSourceRegion && x.Type != TemplateZoneType.CarrierLogo)
            .Select(x => x.Source).ToArray();
        var chars = new char[columns * rows];
        for (var row = 0; row < rows; row++) for (var column = 0; column < columns; column++)
        {
            var nx = (column + .5) / columns; var ny = (row + .5) / rows;
            if (variable.Any(rect => rect.Contains(nx, ny))) { chars[row * columns + column] = '?'; continue; }
            var x = Math.Clamp((int)(nx * image.Width), 0, image.Width - 1);
            var y = Math.Clamp((int)(ny * image.Height), 0, image.Height - 1);
            chars[row * columns + column] = image[x, y] < 210 ? '1' : '0';
        }
        return new string(chars);
    }

    private static double SignatureSimilarity(string first, string second)
    {
        if (first.Length == 0 || first.Length != second.Length) return 0;
        var same = 0; var compared = 0;
        for (var i = 0; i < first.Length; i++)
        {
            if (first[i] == '?' || second[i] == '?') continue;
            compared++; if (first[i] == second[i]) same++;
        }
        return compared == 0 ? 0 : same / (double)compared;
    }

    private static IReadOnlyList<string> StaticAnchors(string? tsv, IReadOnlyList<TemplateZone> zones,
        int width, int height)
    {
        if (string.IsNullOrWhiteSpace(tsv)) return [];
        var variable = zones.Where(x => x.HasSourceRegion && x.Type != TemplateZoneType.CarrierLogo)
            .Select(x => x.Source).ToArray();
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var f = row.TrimEnd('\r').Split('\t');
            if (f.Length < 12 || f[0] != "5" || !int.TryParse(f[6], out var x) || !int.TryParse(f[7], out var y) ||
                !int.TryParse(f[8], out var w) || !int.TryParse(f[9], out var h)) continue;
            var nx = (x + w / 2d) / width; var ny = (y + h / 2d) / height;
            if (variable.Any(rect => rect.Contains(nx, ny))) continue;
            var text = Regex.Replace(f[11].ToUpperInvariant(), "[^A-Z]", string.Empty);
            if (text.Length >= 3 && text.Length <= 24) anchors.Add(text);
        }
        return anchors.OrderBy(x => x).Take(24).ToArray();
    }

    private static double SetSimilarity(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        if (expected.Count == 0) return .5;
        var a = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count() / (double)a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
    }
    private static double PositionSimilarity(IReadOnlyList<NormalizedRect> expected, IReadOnlyList<NormalizedRect> actual)
    {
        if (expected.Count == 0 || actual.Count == 0) return 0;
        var scores = expected.Select(e => actual.Max(a => Math.Max(0, 1 -
            Math.Sqrt(Math.Pow((e.X + e.Width / 2) - (a.X + a.Width / 2), 2) +
                      Math.Pow((e.Y + e.Height / 2) - (a.Y + a.Height / 2), 2)) / .25))).ToArray();
        return scores.Average() * Math.Min(expected.Count, actual.Count) / Math.Max(expected.Count, actual.Count);
    }
    private static (double ScaleX, double ScaleY, double OffsetX, double OffsetY) EstimateRegistration(
        IReadOnlyList<NormalizedRect> expected, IReadOnlyList<NormalizedRect> actual)
    {
        if (expected.Count == 0 || expected.Count != actual.Count) return (1, 1, 0, 0);
        var e = expected.OrderBy(x => x.Y).ThenBy(x => x.X).ToArray();
        var a = actual.OrderBy(x => x.Y).ThenBy(x => x.X).ToArray();
        var eLeft = e.Min(x => x.X); var eTop = e.Min(x => x.Y);
        var eRight = e.Max(x => x.Right); var eBottom = e.Max(x => x.Bottom);
        var aLeft = a.Min(x => x.X); var aTop = a.Min(x => x.Y);
        var aRight = a.Max(x => x.Right); var aBottom = a.Max(x => x.Bottom);
        var scaleX = Math.Clamp((aRight - aLeft) / Math.Max(.01, eRight - eLeft), .97, 1.03);
        var scaleY = Math.Clamp((aBottom - aTop) / Math.Max(.01, eBottom - eTop), .97, 1.03);
        return (scaleX, scaleY, Math.Clamp(aLeft - eLeft * scaleX, -.03, .03),
            Math.Clamp(aTop - eTop * scaleY, -.03, .03));
    }
    private static NormalizedRect Transform(NormalizedRect source,
        (double ScaleX, double ScaleY, double OffsetX, double OffsetY) t)
    {
        var x = Math.Clamp(source.X * t.ScaleX + t.OffsetX, 0, .999);
        var y = Math.Clamp(source.Y * t.ScaleY + t.OffsetY, 0, .999);
        return new NormalizedRect(x, y, Math.Min(source.Width * t.ScaleX, 1 - x),
            Math.Min(source.Height * t.ScaleY, 1 - y));
    }
    private static bool IsRequired(TemplateZone zone) =>
        zone.HasSourceRegion && (zone.Type != TemplateZoneType.OtherSection ||
                                 zone.OtherPriority == TemplateOtherPriority.Required);
    private static bool HasInk(GrayImage page, NormalizedRect source) =>
        !ImageAnalysis.FindContentBounds(page.Crop(source.ToPixels(page.Width, page.Height)), padding: 0).IsEmpty;
    private static TemplateZone NewZone(TemplateZoneType type, PixelRect rect, GrayImage page, bool suggested) => new()
    { Type = type, Source = NormalizedRect.FromPixels(rect, page.Width, page.Height), Suggested = suggested };
    private static void SetDestination(TemplateZone[] zones, Guid id, NormalizedRect destination)
    { var index = Array.FindIndex(zones, x => x.Id == id); if (index >= 0) zones[index] = zones[index] with { Destination4x4 = destination }; }
    private static bool Intersects(PixelRect a, PixelRect b) => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
    private static double Overlap(NormalizedRect a, NormalizedRect b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
    private static double Coverage(PixelRect a, PixelRect b)
    { var area = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y)); return area / (double)Math.Max(1, b.Area); }
    private static bool ContainsWithTolerance(NormalizedRect outer, NormalizedRect inner, double tolerance) =>
        outer.X <= inner.X + tolerance && outer.Y <= inner.Y + tolerance &&
        outer.Right >= inner.Right - tolerance && outer.Bottom >= inner.Bottom - tolerance;
    private static TemplateMatchResult None(double score = 0) => new(new TemplateMatch(null, 0, score, TemplateMatchDisposition.None), null, null);
}
