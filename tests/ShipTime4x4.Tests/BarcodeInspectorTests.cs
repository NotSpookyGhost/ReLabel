using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ZXing;
using ZXing.Common;

namespace ShipTime4x4.Tests;

public sealed class BarcodeInspectorTests
{
    [Fact]
    public void DetectsTwoIndependentBarcodePayloads()
    {
        var label = GrayImage.White(500, 700);
        PasteBarcode(label, "TRACKING-10001", 50, 120);
        PasteBarcode(label, "ROUTING-20002", 50, 430);

        var results = new BarcodeInspector().Detect(label);

        Assert.Contains(results, result => result.Value == "TRACKING-10001");
        Assert.Contains(results, result => result.Value == "ROUTING-20002");
    }

    [Fact]
    public void OneDimensionalDetectionCoversTheFullBarHeight()
    {
        var label = GrayImage.White(500, 350);
        PasteBarcode(label, "FULL-HEIGHT-10001", 50, 120);

        var detected = new BarcodeInspector().Detect(label)
            .Single(result => result.Value == "FULL-HEIGHT-10001");

        Assert.True(detected.Bounds.Y <= 132);
        Assert.True(detected.Bounds.Bottom >= 218);
    }

    [Fact]
    public void MarkedRegionsSelectOnlyCoveredBarcodePayloads()
    {
        var label = GrayImage.White(500, 700);
        PasteBarcode(label, "TRACKING-10001", 50, 120);
        PasteBarcode(label, "ROUTING-20002", 50, 430);
        var payloads = new BarcodeInspector().PayloadsInRegions(label,
            [new PixelRect(35, 100, 430, 155)]);
        Assert.Contains((BarcodeFormat.CODE_128, "TRACKING-10001"), payloads);
        Assert.DoesNotContain((BarcodeFormat.CODE_128, "ROUTING-20002"), payloads);
    }

    [Fact]
    public void OutputVerificationCombinesIndividuallyMarkedBarcodeRegions()
    {
        var label = GrayImage.White(500, 700);
        PasteBarcode(label, "TRACKING-10001", 50, 120);
        PasteBarcode(label, "ROUTING-20002", 50, 430);
        var inspector = new BarcodeInspector();
        var expected = new HashSet<(BarcodeFormat Format, string Value)>
        {
            (BarcodeFormat.CODE_128, "TRACKING-10001"),
            (BarcodeFormat.CODE_128, "ROUTING-20002")
        };
        Assert.True(inspector.OutputContainsPayloads(label, expected,
        [
            new PixelRect(35, 100, 430, 155),
            new PixelRect(35, 410, 430, 155)
        ]));
    }

    [Fact]
    public void OneDotRotatedCompensationRetractsFeedDirectionTrailingEdgesAndPreservesPayload()
    {
        var label = GrayImage.White(500, 700);
        PasteBarcode(label, "TRACKING-10001", 50, 120);
        var inspector = new BarcodeInspector();
        var original = label.RotateClockwise(90);

        var compensated = LabelPrintEnhancements.CompensateRotatedBarcodes(original,
            LabelRotation.Clockwise90, RotatedBarcodeCompensation.OneDot, inspector);

        Assert.True(inspector.ContainsSamePayloads(original, compensated));
        Assert.Contains(original.Pixels.Zip(compensated.Pixels), pair => pair.First != pair.Second);
    }

    [Fact]
    public void CompensationIsIgnoredWithoutQuarterTurn()
    {
        var label = GrayImage.White(500, 700);
        PasteBarcode(label, "TRACKING-10001", 50, 120);

        var compensated = LabelPrintEnhancements.CompensateRotatedBarcodes(label,
            LabelRotation.None, RotatedBarcodeCompensation.TwoDots, new BarcodeInspector());

        Assert.Equal(label.Pixels, compensated.Pixels);
    }

    [Fact]
    public void FiveDotRequestUsesStrongestDecodableRotatedCompensation()
    {
        var label = GrayImage.White(500, 700);
        PasteBarcode(label, "TRACKING-10001", 50, 120);
        var inspector = new BarcodeInspector();
        var original = label.RotateClockwise(90);

        var twoDots = LabelPrintEnhancements.CompensateRotatedBarcodes(original,
            LabelRotation.Clockwise90, RotatedBarcodeCompensation.TwoDots, inspector);
        var fiveDotResult = LabelPrintEnhancements.CompensateRotatedBarcodesWithResult(original,
            LabelRotation.Clockwise90, RotatedBarcodeCompensation.FiveDots, inspector);
        var fiveDots = fiveDotResult.Image;

        Assert.True(inspector.ContainsSamePayloads(original, fiveDots));
        Assert.True(fiveDots.Pixels.Count(pixel => pixel < 128) <=
                    twoDots.Pixels.Count(pixel => pixel < 128));
        Assert.InRange(fiveDotResult.AppliedDots, 1, 5);
    }

    [Fact]
    public void TrailingEdgeRetractionChangesOnlyBlackToWhiteFeedTransitions()
    {
        var image = GrayImage.White(40, 8);
        for (var y = 1; y <= 5; y++)
        for (var x = 5; x <= 34; x++)
            image[x, y] = 0;

        LabelPrintEnhancements.RetractFeedDirectionTrailingEdges(
            image, new PixelRect(0, 0, 40, 8), 1);

        Assert.Equal(255, image[20, 5]);
        Assert.Equal(0, image[20, 4]);
        Assert.Equal(255, image[4, 4]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TrailingEdgeRetractionAcceptsEveryExposedDotChoice(int dots)
    {
        var image = GrayImage.White(60, 16);
        for (var y = 2; y <= 12; y++)
        for (var x = 5; x <= 54; x++)
            image[x, y] = 0;

        LabelPrintEnhancements.RetractFeedDirectionTrailingEdges(
            image, new PixelRect(0, 0, 60, 16), dots);

        Assert.Equal(255, image[30, 12]);
        Assert.Equal(0, image[30, 2]);
    }

    [Fact]
    public void StrongCompensationNeverErasesOneDotBar()
    {
        var image = GrayImage.White(40, 12);
        for (var x = 5; x <= 34; x++) image[x, 6] = 0;

        LabelPrintEnhancements.RetractFeedDirectionTrailingEdges(
            image, new PixelRect(0, 0, 40, 12), 5);

        Assert.Equal(0, image[20, 6]);
    }

    [Fact]
    public void HighQualityOptimizerDoesNotThinUndecodableBarcodePattern()
    {
        var label = GrayImage.White(500, 300);
        for (var x = 45; x < 455; x += 7)
        for (var y = 120; y < 220; y++)
            if ((x / 7) % 5 != 0)
                label[x, y] = 60;
        var barcode = new PixelRect(40, 115, 420, 110);
        var before = label.Crop(barcode).Pixels.ToArray();

        var optimized = PrintRasterOptimizer.Apply(label, PrintQualityPreset.HighQuality,
            new BarcodeInspector());

        Assert.Empty(new BarcodeInspector().Detect(label));
        Assert.Equal(before.Select(pixel => pixel < 128),
            optimized.Crop(barcode).Pixels.Select(pixel => pixel < 128));
    }

    private static void PasteBarcode(GrayImage target, string value, int left, int top)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions { Width = 400, Height = 110, Margin = 12, PureBarcode = true }
        };
        var barcode = writer.Write(value);
        for (var y = 0; y < barcode.Height; y++)
        for (var x = 0; x < barcode.Width; x++)
            target[left + x, top + y] = barcode.Pixels[((y * barcode.Width) + x) * 4];
    }
}
