using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Services;
using ZXing;
using ZXing.Common;

namespace ShipTime4x4.Tests;

public sealed class CarrierTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReLabelTemplateTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void StorePersistsRevisionDuplicateStatusAndDeletion()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.pdf");
        File.WriteAllBytes(source, [1, 2, 3]);
        var store = new CarrierTemplateStore(_root);
        var id = Guid.NewGuid();
        var stored = store.StoreSource(id, source);
        var template = ValidTemplate(id, stored);
        store.Save(template);

        var reloaded = new CarrierTemplateStore(_root);
        Assert.Equal("Test Carrier", Assert.Single(reloaded.GetAll()).Carrier);
        var duplicate = reloaded.Duplicate(id);
        Assert.NotEqual(id, duplicate.Id);
        Assert.True(File.Exists(duplicate.SourcePdfPath));
        reloaded.Save(template with { Revision = 2, Enabled = false });
        Assert.False(new CarrierTemplateStore(_root).Find(id)!.Enabled);
        reloaded.Delete(id);
        Assert.Null(reloaded.Find(id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(stored)));
    }

    [Fact]
    public void DuplicateCanReplaceSampleWhileKeepingEveryZonePosition()
    {
        Directory.CreateDirectory(_root);
        var originalPdf = Path.Combine(_root, "original.pdf");
        var replacementPdf = Path.Combine(_root, "replacement.pdf");
        File.WriteAllText(originalPdf, "%PDF-1.4\noriginal");
        File.WriteAllText(replacementPdf, "%PDF-1.4\nreplacement");
        var store = new CarrierTemplateStore(Path.Combine(_root, "data"));
        var id = Guid.NewGuid();
        var stored = store.StoreSource(id, originalPdf);
        var original = ValidTemplate(id, stored) with
        {
            Zones =
            [
                Zone(TemplateZoneType.FromAddress, .04, .08, .34, .19) with
                    { Destination4x4 = new NormalizedRect(.05, .07, .38, .22), Locked = true },
                Zone(TemplateZoneType.ToAddress, .44, .08, .50, .22) with
                    { Destination4x4 = new NormalizedRect(.47, .07, .48, .25) },
                Zone(TemplateZoneType.ShippingBarcode, .06, .70, .88, .18) with
                    { Destination4x4 = new NormalizedRect(.04, .73, .92, .20) }
            ]
        };
        store.Save(original);

        var duplicate = store.Duplicate(id, replacementPdf);

        Assert.Equal("%PDF-1.4\nreplacement", File.ReadAllText(duplicate.SourcePdfPath));
        Assert.Equal(original.Zones.Select(zone => (zone.Type, zone.Source, zone.Destination4x4,
                zone.Locked)).ToArray(),
            duplicate.Zones.Select(zone => (zone.Type, zone.Source, zone.Destination4x4,
                zone.Locked)).ToArray());
        Assert.Empty(original.Zones.Select(zone => zone.Id).Intersect(
            duplicate.Zones.Select(zone => zone.Id)));
        Assert.Equal("%PDF-1.4\noriginal", File.ReadAllText(original.SourcePdfPath));
    }

    [Fact]
    public void PortablePackageRoundTripsTemplateAndPdfWithoutReusingIds()
    {
        var sourceData = Path.Combine(_root, "source-data");
        var destinationData = Path.Combine(_root, "destination-data");
        Directory.CreateDirectory(_root);
        var inputPdf = Path.Combine(_root, "input.pdf");
        var pdfBytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nReLabel template fixture");
        File.WriteAllBytes(inputPdf, pdfBytes);
        var sourceStore = new CarrierTemplateStore(sourceData);
        var originalId = Guid.NewGuid();
        var stored = sourceStore.StoreSource(originalId, inputPdf);
        var original = ValidTemplate(originalId, stored) with
        {
            Revision = 7,
            BuiltInAssetScale = 1.65,
            Enabled = false,
            Zones = ValidTemplate(originalId, stored).Zones
                .Select(zone => zone with { Locked = true }).ToArray()
        };
        sourceStore.Save(original);
        var packagePath = Path.Combine(_root, "standard.relabel-template");

        sourceStore.ExportPackage(original.Id, packagePath);
        var destinationStore = new CarrierTemplateStore(destinationData);
        var imported = destinationStore.ImportPackage(packagePath);

        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(original.Carrier, imported.Carrier);
        Assert.Equal(original.LayoutName, imported.LayoutName);
        Assert.Equal(7, imported.Revision);
        Assert.Equal(1.65, imported.BuiltInAssetScale, 3);
        Assert.False(imported.Enabled);
        Assert.All(imported.Zones, zone => Assert.True(zone.Locked));
        Assert.Empty(original.Zones.Select(zone => zone.Id).Intersect(imported.Zones.Select(zone => zone.Id)));
        Assert.Equal(pdfBytes, File.ReadAllBytes(imported.SourcePdfPath));
        Assert.Equal(imported.Id, Assert.Single(new CarrierTemplateStore(destinationData).GetAll()).Id);
    }

    [Fact]
    public void ReimportCreatesReadableUniqueLayoutNames()
    {
        Directory.CreateDirectory(_root);
        var inputPdf = Path.Combine(_root, "input.pdf");
        File.WriteAllText(inputPdf, "%PDF-1.4\nfixture");
        var sourceStore = new CarrierTemplateStore(Path.Combine(_root, "source"));
        var id = Guid.NewGuid();
        var template = ValidTemplate(id, sourceStore.StoreSource(id, inputPdf));
        sourceStore.Save(template);
        var packagePath = Path.Combine(_root, "standard.relabel-template");
        sourceStore.ExportPackage(id, packagePath);
        var destinationStore = new CarrierTemplateStore(Path.Combine(_root, "destination"));

        var first = destinationStore.ImportPackage(packagePath);
        var second = destinationStore.ImportPackage(packagePath);
        var third = destinationStore.ImportPackage(packagePath);

        Assert.Equal("Standard", first.LayoutName);
        Assert.Equal("Standard imported", second.LayoutName);
        Assert.Equal("Standard imported 2", third.LayoutName);
        Assert.Equal(3, destinationStore.GetAll().Count);
    }

    [Fact]
    public void DamagedPortablePackageIsRejectedWithoutAddingTemplate()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, "damaged.relabel-template");
        File.WriteAllText(packagePath, "not a template package");
        var store = new CarrierTemplateStore(Path.Combine(_root, "destination"));

        Assert.Throws<InvalidDataException>(() => store.ImportPackage(packagePath));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void SnapshotHashChangesWhenLayoutChanges()
    {
        var first = Snapshot(new NormalizedRect(.05, .05, .4, .2));
        var second = Snapshot(new NormalizedRect(.06, .05, .4, .2));
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Equal(first.Hash, (first with { }).Hash);
    }

    [Fact]
    public void ValidatorAllowsDestinationOverlap()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.pdf");
        File.WriteAllBytes(source, [1]);
        var invalid = ValidTemplate(Guid.NewGuid(), source) with
        {
            Zones =
            [
                new TemplateZone { Type = TemplateZoneType.ToAddress,
                    Source = new NormalizedRect(.1, .1, .2, .2),
                    Destination4x4 = new NormalizedRect(.1, .1, .5, .3) },
                new TemplateZone { Type = TemplateZoneType.CarrierBarcode,
                    Source = new NormalizedRect(.1, .5, .7, .2),
                    Destination4x4 = new NormalizedRect(.3, .2, .5, .3) }
            ]
        };
        CarrierTemplateValidator.Validate(invalid);
    }

    [Fact]
    public void DefinedCompositionFallsBackWhenRequiredZoneIsBlank()
    {
        var page = GrayImage.White(800, 1200);
        var regions = new[]
        {
            new DefinedLayoutRegion(new PixelRect(20, 20, 200, 100),
                new NormalizedLayoutRect(.05, .05, .4, .2), BlockPriority.Required, false, true)
        };
        var result = new LabelPipeline().PrepareDefined(page, 203, 203, .125, 4, 4,
            LabelFitMode.Proportional, LabelRotation.None, PrintQualityPreset.Standard, 10, 4,
            RotatedBarcodeCompensation.Off, TextEnhancement.Off, regions, [], 100, 100);
        Assert.True(result.UsedFallback);
        Assert.Equal("template-required-zone-blank:required zone", result.WarningCode);
        Assert.Equal(812, result.Image.Width);
        Assert.Equal(812, result.Image.Height);
    }

    [Fact]
    public void ContentDetectorRecognizesVisibleRequiredZoneContentWithZeroPadding()
    {
        var page = GrayImage.White(800, 1200);
        for (var y = 40; y < 100; y++)
        for (var x = 40; x < 220; x++)
            page[x, y] = 32;
        var crop = page.Crop(new PixelRect(20, 20, 240, 110));
        Assert.False(ImageAnalysis.FindContentBounds(crop, padding: 0).IsEmpty);
        Assert.True(ImageAnalysis.FindContentBounds(crop, whiteThreshold: 0).IsEmpty);
    }

    [Fact]
    public void DefinedCompositionUsesVerifiedDestinationBoxFitForDenseBarcodeAt203Dpi()
    {
        var page = GrayImage.White(1200, 1800);
        PasteBarcode(page, "DENSE-TRACKING-12345678901234567890", 100, 500, 1000, 350);
        var regions = new[]
        {
            new DefinedLayoutRegion(new PixelRect(100, 500, 1000, 350),
                new NormalizedLayoutRect(.01, .35, .98, .22), BlockPriority.ScanCritical,
                true, true, "Shipping Barcode", true)
        };

        var result = new LabelPipeline().PrepareDefined(page, 300, 203, .125, 4, 4,
            LabelFitMode.Proportional, LabelRotation.None, PrintQualityPreset.Standard, 10, 4,
            RotatedBarcodeCompensation.Off, TextEnhancement.Off, regions, [], 100, 100);

        Assert.False(result.UsedFallback);
        Assert.Null(result.WarningCode);
    }

    [Fact]
    public void ShippingBarcodeAlwaysUsesTheFullDrawnDestinationWidth()
    {
        var page = GrayImage.White(1200, 1800);
        PasteBarcode(page, "SHIP-123456789012345678901234567890", 100, 500, 1000, 350);
        var region = new DefinedLayoutRegion(new PixelRect(100, 500, 1000, 350),
            new NormalizedLayoutRect(.01, .35, .98, .22), BlockPriority.ScanCritical,
            true, true, "Shipping Barcode", true, StretchToDestination: true);

        var result = new LabelComposer(new BarcodeInspector()).ComposeDefined(page, [region],
            203, .125, 4, 4, LabelFitMode.Proportional, PrintQualityPreset.Standard);
        Assert.False(result.UsedFallback);
        Assert.True(Assert.Single(result.BarcodeRegions!).Width > 740);
    }

    [Fact]
    public void StagedSourceZoneKeepsItsPhysicalFourBySixSizeWhenPlaced()
    {
        var source = Zone(TemplateZoneType.ToAddress, .2, .1, .3, .2) with { Placed4x4 = false };

        var placed = CarrierTemplateEngine.PlaceAtReferenceSize(source);

        Assert.Equal(.3, placed.Destination4x4.Width, 6);
        Assert.Equal(.3, placed.Destination4x4.Height, 6);
        Assert.Equal(source.Source.Width * 4, placed.Destination4x4.Width * 4, 6);
        Assert.Equal(source.Source.Height * 6, placed.Destination4x4.Height * 4, 6);
    }

    [Fact]
    public void LayerActionsMoveSelectedZoneAroundOverlappingZonesOnly()
    {
        var lower = Zone(TemplateZoneType.FromAddress, .1, .1, .3, .2) with
            { Destination4x4 = new NormalizedRect(.1, .1, .4, .3) };
        var upper = Zone(TemplateZoneType.ToAddress, .2, .2, .3, .2) with
            { Destination4x4 = new NormalizedRect(.2, .2, .4, .3) };
        var separate = Zone(TemplateZoneType.OtherSection, .1, .6, .3, .1) with
            { Destination4x4 = new NormalizedRect(.1, .7, .3, .1) };

        var above = CarrierTemplateEngine.MoveLayer([lower, upper, separate], lower.Id, true);
        var below = CarrierTemplateEngine.MoveLayer(above, lower.Id, false);

        Assert.True(Array.FindIndex(above.ToArray(), zone => zone.Id == lower.Id) >
                    Array.FindIndex(above.ToArray(), zone => zone.Id == upper.Id));
        Assert.True(Array.FindIndex(below.ToArray(), zone => zone.Id == lower.Id) <
                    Array.FindIndex(below.ToArray(), zone => zone.Id == upper.Id));
        Assert.Equal(separate.Id, above[^1].Id);
    }

    [Fact]
    public void GeneratedHeadersAndLinesRenderWithoutSourceRegions()
    {
        var header = new TemplateZone { Type = TemplateZoneType.BuiltInAsset,
            BuiltInAsset = TemplateBuiltInAssetKind.FromDe };
        var line = new TemplateZone { Type = TemplateZoneType.DividerLine };

        var renderedHeader = TemplateGeneratedContent.Render(header, 203);
        var renderedLine = TemplateGeneratedContent.Render(line, 203);

        Assert.Contains(renderedHeader.Pixels, pixel => pixel < 16);
        Assert.Contains(renderedHeader.Pixels, pixel => pixel > 240);
        Assert.All(renderedLine.Pixels, pixel => Assert.Equal(0, pixel));
    }

    [Fact]
    public void EdgeStretchAndLineRotationStayInsideSafeMargin()
    {
        var zone = Zone(TemplateZoneType.OtherSection, .1, .1, .2, .1) with
            { Destination4x4 = new NormalizedRect(.2, .3, .3, .1) };
        var stretched = CarrierTemplateEngine.StretchToHorizontalEdges(zone);
        var line = CarrierTemplateEngine.RotateDividerLine(new TemplateZone
        {
            Type = TemplateZoneType.DividerLine,
            Destination4x4 = new NormalizedRect(.08, .48, .84, .006),
            Placed4x4 = true
        });

        Assert.Equal(.03125, stretched.Destination4x4.X, 6);
        Assert.Equal(.9375, stretched.Destination4x4.Width, 6);
        Assert.True(line.Destination4x4.Height > line.Destination4x4.Width);
        Assert.True(line.Destination4x4.Y >= .03125);
        Assert.True(line.Destination4x4.Bottom <= .96875);
    }

    [Fact]
    public void EngineNeverMatchesLetterPage()
    {
        var page = GrayImage.White(2550, 3300);
        var inspection = new PdfInspection(page, [], "", "", 0);
        var result = new CarrierTemplateEngine().Match(inspection, []);
        Assert.Equal(TemplateMatchDisposition.None, result.Match.Disposition);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void AutomaticArrangementPreservesSourceLayoutInsideSafeMargin()
    {
        var zones = new[]
        {
            Zone(TemplateZoneType.FromAddress, .04, .04, .42, .15),
            Zone(TemplateZoneType.ToAddress, .52, .04, .42, .18),
            Zone(TemplateZoneType.CarrierLogo, .04, .01, .3, .05),
            Zone(TemplateZoneType.CarrierBarcode, .04, .35, .92, .18),
            Zone(TemplateZoneType.ShippingBarcode, .04, .65, .92, .2),
            Zone(TemplateZoneType.OtherSection, .04, .56, .3, .08)
        };
        var arranged = CarrierTemplateEngine.AutoArrange(zones);
        Assert.All(arranged, zone =>
        {
            Assert.True(zone.IsPlaced4x4);
            Assert.True(zone.Destination4x4.X >= .03125);
            Assert.True(zone.Destination4x4.Y >= .03125);
            Assert.True(zone.Destination4x4.Right <= .96875);
            Assert.True(zone.Destination4x4.Bottom <= .96875);
        });
        var from = arranged.Single(zone => zone.Type == TemplateZoneType.FromAddress).Destination4x4;
        var to = arranged.Single(zone => zone.Type == TemplateZoneType.ToAddress).Destination4x4;
        var carrierBarcode = arranged.Single(zone => zone.Type == TemplateZoneType.CarrierBarcode).Destination4x4;
        var shippingBarcode = arranged.Single(zone => zone.Type == TemplateZoneType.ShippingBarcode).Destination4x4;
        Assert.True(from.X < to.X);
        Assert.True(from.Y < carrierBarcode.Y);
        Assert.True(carrierBarcode.Y < shippingBarcode.Y);
        Assert.True(carrierBarcode.Width > from.Width);
    }

    [Fact]
    public void SuggestedBarcodeInsideDetectedAddressIsDiscarded()
    {
        var page = GrayImage.White(600, 900);
        var tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
            "5\t1\t1\t1\t1\t1\t300\t70\t170\t28\t95\tTO\n" +
            "5\t1\t1\t1\t2\t1\t300\t105\t190\t28\t95\tCUSTOMER\n" +
            "5\t1\t1\t1\t3\t1\t300\t140\t190\t28\t95\tADDRESS\n";
        // Address-located content intentionally resembles a short 1D barcode.
        for (var x = 315; x < 485; x += 5)
        for (var y = 115; y < 165; y++)
            if ((x / 5) % 2 == 0) page[x, y] = 0;

        var zones = new CarrierTemplateEngine().SuggestZones(page, tsv);

        Assert.DoesNotContain(zones, zone =>
            (zone.Type is TemplateZoneType.CarrierBarcode or TemplateZoneType.ShippingBarcode) &&
            zone.Source.Y < .25);
    }

    [Fact]
    public void BarcodeCoverageIgnoresAddressFalsePositiveAndAllowsSmallEdgeVariance()
    {
        var zones = new[]
        {
            Zone(TemplateZoneType.FromAddress, .04, .05, .35, .18),
            Zone(TemplateZoneType.ToAddress, .42, .05, .52, .22),
            Zone(TemplateZoneType.CarrierBarcode, .05, .34, .90, .20)
        };
        var detections = new[]
        {
            new PixelRect(270, 70, 120, 40), // Text-like bars inside the TO address.
            new PixelRect(27, 302, 548, 188) // Detector extends slightly past the marked barcode.
        };

        var uncovered = CarrierTemplateEngine.FindUncoveredBarcodes(detections, zones, 600, 900);

        Assert.Empty(uncovered);
    }

    [Fact]
    public void BarcodeCoverageStillReportsGenuinelyUnmarkedBarcode()
    {
        var zones = new[]
        {
            Zone(TemplateZoneType.ToAddress, .42, .05, .52, .22),
            Zone(TemplateZoneType.CarrierBarcode, .05, .34, .90, .20)
        };
        var unmarked = new PixelRect(60, 650, 480, 100);

        var uncovered = CarrierTemplateEngine.FindUncoveredBarcodes([unmarked], zones, 600, 900);

        Assert.Equal(unmarked, Assert.Single(uncovered));
    }

    [Fact]
    public void LockedZonesAndBuiltInScaleRemainBackwardCompatibleModelValues()
    {
        var template = new CarrierTemplate
        {
            Carrier = "Example", LayoutName = "Locked layout", BuiltInAssetScale = 1.75,
            Zones = [Zone(TemplateZoneType.ToAddress, .1, .1, .4, .2) with { Locked = true }]
        };
        var json = System.Text.Json.JsonSerializer.Serialize(template);
        var restored = System.Text.Json.JsonSerializer.Deserialize<CarrierTemplate>(json)!;
        Assert.Equal(1.75, restored.BuiltInAssetScale, 3);
        Assert.True(restored.Zones.Single().Locked);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<CarrierTemplate>(
            """{"Carrier":"Old","LayoutName":"Layout"}""")!;
        Assert.Equal(1, legacy.BuiltInAssetScale);
    }

    [Fact]
    public void NewPlacementFlagSupportsStagingAndLegacyCatalogs()
    {
        var staged = Zone(TemplateZoneType.ToAddress, .1, .1, .3, .2) with { Placed4x4 = false };
        var legacy = Zone(TemplateZoneType.CarrierBarcode, .1, .4, .7, .2);
        Assert.False(staged.IsPlaced4x4);
        Assert.True(legacy.IsPlaced4x4);
    }

    [Fact]
    public void ValidatorExplainsWhichStagedZoneMustBePlaced()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.pdf");
        File.WriteAllBytes(source, [1]);
        var template = ValidTemplate(Guid.NewGuid(), source);
        template = template with { Zones = template.Zones.Select((zone, index) =>
            index == 0 ? zone with { Placed4x4 = false } : zone).ToArray() };
        var error = Assert.Throws<InvalidOperationException>(() => CarrierTemplateValidator.Validate(template));
        Assert.Contains("To Address", error.Message);
        Assert.Contains("staged", error.Message);
    }

    private CarrierTemplate ValidTemplate(Guid id, string source) => new()
    {
        Id = id, Carrier = "Test Carrier", LayoutName = "Standard", SourcePdfPath = source,
        Zones =
        [
            new TemplateZone { Type = TemplateZoneType.ToAddress,
                Source = new NormalizedRect(.05, .05, .4, .2),
                Destination4x4 = new NormalizedRect(.04, .04, .4, .2) },
            new TemplateZone { Type = TemplateZoneType.CarrierBarcode,
                Source = new NormalizedRect(.05, .4, .9, .2),
                Destination4x4 = new NormalizedRect(.04, .35, .92, .25) }
        ]
    };
    private static TemplateZone Zone(TemplateZoneType type, double x, double y, double width, double height) => new()
    {
        Type = type, Source = new NormalizedRect(x, y, width, height),
        Destination4x4 = new NormalizedRect(.04, .04, .2, .1)
    };
    private static void PasteBarcode(GrayImage target, string value, int left, int top, int width, int height)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions { Width = width, Height = height, Margin = 20, PureBarcode = true }
        };
        var barcode = writer.Write(value);
        for (var y = 0; y < barcode.Height; y++)
        for (var x = 0; x < barcode.Width; x++)
            target[left + x, top + y] = barcode.Pixels[((y * barcode.Width) + x) * 4];
    }
    private static double Overlap(NormalizedRect a, NormalizedRect b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
    private static AppliedTemplateSnapshot Snapshot(NormalizedRect rect) => new()
    {
        TemplateId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Revision = 1,
        Carrier = "Test", LayoutName = "Layout", Zones =
        [
            new TemplateZone { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Type = TemplateZoneType.ToAddress, Source = rect, Destination4x4 = rect }
        ]
    };
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
