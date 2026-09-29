using System.Text;
using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Tests;

public sealed class LabelPipelineTests
{
    [Theory]
    [InlineData(203, 812)]
    [InlineData(300, 1200)]
    [InlineData(600, 2400)]
    public void UnknownLabelUsesDeterministicFallback(int targetDpi, int expectedSize)
    {
        var page = GrayImage.White(400, 600);
        Fill(page, new PixelRect(20, 20, 360, 80), 0);
        Fill(page, new PixelRect(20, 200, 360, 100), 0);

        var result = new LabelPipeline().Process(page, 100, targetDpi, 0.04);

        Assert.True(result.UsedFallback);
        Assert.Equal(expectedSize, result.Width);
        Assert.Equal(expectedSize, result.Height);
        var zpl = Encoding.ASCII.GetString(result.Zpl);
        Assert.Contains($"^PW{expectedSize}", zpl);
        Assert.Contains($"^LL{expectedSize}", zpl);
    }

    [Fact]
    public void MultipleSeparatedLabelsAreRejected()
    {
        var page = GrayImage.White(500, 1200);
        Fill(page, new PixelRect(20, 20, 460, 480), 0);
        Fill(page, new PixelRect(20, 700, 460, 480), 0);

        var error = Assert.Throws<LabelAnalysisException>(() =>
            new LabelPipeline().Process(page, 100, 300, 0.04));

        Assert.Equal("multi-label-page", error.Code);
    }

    [Fact]
    public void PreparedLabelIncludesPreviewImageAndZpl()
    {
        var page = GrayImage.White(400, 600);
        Fill(page, new PixelRect(20, 20, 360, 80), 0);
        Fill(page, new PixelRect(20, 200, 360, 100), 0);

        var result = new LabelPipeline().Prepare(page, 100, 203, 0.04);

        Assert.Equal(812, result.Image.Width);
        Assert.Equal(812, result.Image.Height);
        Assert.NotEmpty(result.Zpl);
    }

    [Theory]
    [InlineData(203, 26)]
    [InlineData(300, 38)]
    [InlineData(600, 75)]
    public void FallbackKeepsContentInsideOneEighthInchMargin(int dpi, int minimumMargin)
    {
        var page = GrayImage.White(400, 600);
        Fill(page, new PixelRect(0, 0, 400, 80), 0);
        Fill(page, new PixelRect(0, 520, 400, 80), 0);

        var prepared = new LabelPipeline().Prepare(page, 100, dpi, 0);
        var bounds = ImageAnalysis.FindContentBounds(prepared.Image);

        Assert.True(bounds.X >= minimumMargin);
        Assert.True(bounds.Y >= minimumMargin);
        Assert.True(prepared.Image.Width - bounds.Right >= minimumMargin);
        Assert.True(prepared.Image.Height - bounds.Bottom >= minimumMargin);
    }

    [Fact]
    public void FallbackCompactsOversizedHorizontalBlankGaps()
    {
        var page = GrayImage.White(400, 600);
        Fill(page, new PixelRect(20, 20, 360, 60), 0);
        Fill(page, new PixelRect(20, 500, 360, 60), 0);

        var compacted = ImageAnalysis.CompactVerticalWhitespace(page);

        Assert.True(compacted.Height < 200);
        Assert.Equal(400, compacted.Width);
    }

    [Fact]
    public void AddressEditsPreserveNewInkWhileKeepingOriginalLayoutRows()
    {
        var original = GrayImage.White(200, 400);
        Fill(original, new PixelRect(20, 20, 160, 45), 0);
        Fill(original, new PixelRect(20, 300, 160, 70), 0);
        var edited = new GrayImage(original.Width, original.Height, (byte[])original.Pixels.Clone());
        Fill(edited, new PixelRect(20, 90, 160, 130), 0);

        var baseline = ImageAnalysis.CompactVerticalWhitespace(original);
        var enhanced = ImageAnalysis.CompactVerticalWhitespace(edited, original);

        Assert.Equal(baseline.Width, enhanced.Width);
        Assert.True(enhanced.Height > baseline.Height);
        Assert.True(CountInk(enhanced, new PixelRect(0, 0, enhanced.Width, enhanced.Height)) >
                    CountInk(baseline, new PixelRect(0, 0, baseline.Width, baseline.Height)));
    }

    [Fact]
    public void FallbackDoesNotStretchCompactedContentVerticallyWhenSpaceRemains()
    {
        var page = GrayImage.White(400, 600);
        Fill(page, new PixelRect(20, 20, 360, 70), 0);
        Fill(page, new PixelRect(20, 470, 360, 70), 0);

        var prepared = new LabelPipeline().Prepare(page, 100, 203, 0.125);
        var bounds = ImageAnalysis.FindContentBounds(prepared.Image);
        var compacted = ImageAnalysis.CompactVerticalWhitespace(page);
        var compactedBounds = ImageAnalysis.FindContentBounds(compacted);

        var sourceRatio = compactedBounds.Width / (double)compactedBounds.Height;
        var outputRatio = bounds.Width / (double)bounds.Height;
        Assert.InRange(Math.Abs(sourceRatio - outputRatio), 0, 0.05);
    }

    [Theory]
    [InlineData(203, 4, 6, 812, 1218)]
    [InlineData(300, 4, 8, 1200, 2400)]
    [InlineData(600, 4, 6, 2400, 3600)]
    public void SelectedLabelSizeControlsRasterAndZplDimensions(
        int dpi, int widthInches, int heightInches, int expectedWidth, int expectedHeight)
    {
        var page = GrayImage.White(400, 600);
        Fill(page, new PixelRect(20, 20, 360, 80), 0);
        Fill(page, new PixelRect(20, 200, 360, 100), 0);

        var prepared = new LabelPipeline().Prepare(page, 100, dpi, 0.125,
            widthInches, heightInches, LabelFitMode.Proportional);

        Assert.Equal(expectedWidth, prepared.Image.Width);
        Assert.Equal(expectedHeight, prepared.Image.Height);
        var zpl = Encoding.ASCII.GetString(prepared.Zpl);
        Assert.Contains($"^PW{expectedWidth}", zpl);
        Assert.Contains($"^LL{expectedHeight}", zpl);
    }

    [Fact]
    public void SquishToFillStillKeepsOneEighthInchSafeMargin()
    {
        var page = GrayImage.White(400, 600);
        Fill(page, new PixelRect(0, 0, 400, 80), 0);
        Fill(page, new PixelRect(0, 520, 400, 80), 0);

        var prepared = new LabelPipeline().Prepare(page, 100, 203, 0,
            4, 6, LabelFitMode.SquishToFill);
        var bounds = ImageAnalysis.FindContentBounds(prepared.Image);

        Assert.True(bounds.X >= 26);
        Assert.True(bounds.Y >= 26);
        Assert.True(prepared.Image.Width - bounds.Right >= 26);
        Assert.True(prepared.Image.Height - bounds.Bottom >= 26);
    }

    [Fact]
    public void HighQualityProducesNative203DpiMonochromeRasterWithPrintControls()
    {
        var page = GrayImage.White(1200, 1800);
        Fill(page, new PixelRect(60, 60, 1080, 240), 0);
        Fill(page, new PixelRect(60, 600, 1080, 300), 0);

        var prepared = new LabelPipeline().Prepare(page, 300, 203, 0.125,
            4, 4, LabelFitMode.SquishToFill, LabelRotation.None,
            PrintQualityPreset.HighQuality, 12.5, 3);

        Assert.Equal(812, prepared.Image.Width);
        Assert.Equal(812, prepared.Image.Height);
        Assert.All(prepared.Image.Pixels, pixel => Assert.True(pixel is 0 or 255));
        var zpl = Encoding.ASCII.GetString(prepared.Zpl);
        Assert.StartsWith("~SD12.5\n^XA\n^PR3", zpl);
    }

    [Theory]
    [InlineData(LabelRotation.Clockwise90)]
    [InlineData(LabelRotation.UpsideDown180)]
    [InlineData(LabelRotation.Clockwise270)]
    public void RotationKeepsPhysicalMediaDimensionsAndSafeMargin(LabelRotation rotation)
    {
        var page = GrayImage.White(400, 600);
        Fill(page, new PixelRect(20, 20, 360, 80), 0);
        Fill(page, new PixelRect(20, 200, 360, 100), 0);

        var prepared = new LabelPipeline().Prepare(page, 100, 203, 0.125,
            4, 6, LabelFitMode.Proportional, rotation);
        var bounds = ImageAnalysis.FindContentBounds(prepared.Image);

        Assert.Equal(812, prepared.Image.Width);
        Assert.Equal(1218, prepared.Image.Height);
        Assert.True(bounds.X >= 26);
        Assert.True(bounds.Y >= 26);
        Assert.True(prepared.Image.Width - bounds.Right >= 26);
        Assert.True(prepared.Image.Height - bounds.Bottom >= 26);
        var zpl = Encoding.ASCII.GetString(prepared.Zpl);
        Assert.Contains("^PW812", zpl);
        Assert.Contains("^LL1218", zpl);
    }

    [Fact]
    public void GrayImageRotationMapsPixelsClockwise()
    {
        var image = new GrayImage(2, 3, [1, 2, 3, 4, 5, 6]);

        var rotated = image.RotateClockwise(90);

        Assert.Equal(3, rotated.Width);
        Assert.Equal(2, rotated.Height);
        Assert.Equal(new byte[] { 5, 3, 1, 6, 4, 2 }, rotated.Pixels);
    }

    [Fact]
    public void AddressSizingEnlargesOnlySelectedRegionsAndDoesNotThickenOtherText()
    {
        var image = GrayImage.White(400, 500);
        var from = new PixelRect(20, 20, 120, 50);
        var to = new PixelRect(210, 20, 140, 50);
        DrawTextPattern(image, from);
        DrawTextPattern(image, to);
        Fill(image, new PixelRect(180, 15, 2, 110), 0);
        Fill(image, new PixelRect(30, 250, 90, 7), 0);
        var originalUnrelatedInk = CountInk(image, new PixelRect(30, 250, 90, 7));

        var enlarged = LabelPrintEnhancements.EnlargeAddressText(image, [from, to], TextEnhancement.ExtraLarge);

        Assert.Equal(image.Width, enlarged.Width);
        Assert.Equal(image.Height, enlarged.Height);
        Assert.True(CountInk(enlarged, new PixelRect(from.X, from.Y, 154, 64)) >
                    CountInk(image, from));
        Assert.Equal(originalUnrelatedInk, CountInk(enlarged, new PixelRect(30, 250, 90, 7)));
        Assert.Equal(0, CountInk(enlarged, new PixelRect(178, 15, 8, 110)));
        Assert.True(CountInk(enlarged, new PixelRect(194, 15, 22, 110)) > 40);
    }

    [Fact]
    public void AddressResizePreservesEveryThinSourceMark()
    {
        var source = GrayImage.White(80, 32);
        Fill(source, new PixelRect(3, 3, 1, 1), 0);
        Fill(source, new PixelRect(15, 5, 2, 17), 0);
        Fill(source, new PixelRect(34, 8, 15, 2), 0);
        Fill(source, new PixelRect(70, 26, 1, 1), 0);

        var enlarged = source.ResizeBilinear(102, 41);

        Assert.True(LabelPrintEnhancements.PreservesAllInk(source, enlarged));
    }

    [Fact]
    public void ExtraLargeAddressesUseBlankVerticalRoomWithoutMovingContentBelow()
    {
        var image = GrayImage.White(420, 360);
        var from = new PixelRect(20, 20, 135, 72);
        var to = new PixelRect(220, 20, 155, 72);
        DrawDenseAddress(image, from);
        DrawDenseAddress(image, to);
        Fill(image, new PixelRect(190, 16, 2, 115), 0);
        Fill(image, new PixelRect(20, 180, 370, 45), 0);
        var protectedContent = image.Crop(new PixelRect(20, 180, 370, 45)).Pixels.ToArray();

        var enlarged = LabelPrintEnhancements.EnlargeAddressText(
            image, [from, to], TextEnhancement.ExtraLarge);

        var enlargedFromBounds = ImageAnalysis.FindContentBounds(
            enlarged.Crop(new PixelRect(20, 20, 165, 150)));
        Assert.True(enlargedFromBounds.Height >= 100);
        Assert.Equal(protectedContent, enlarged.Crop(new PixelRect(20, 180, 370, 45)).Pixels);
    }

    [Fact]
    public void CustomAddressScaleOverridesPresetAndUsesEnteredPercentage()
    {
        var image = GrayImage.White(800, 500);
        var from = new PixelRect(20, 20, 120, 60);
        Fill(image, new PixelRect(25, 25, 100, 40), 0);

        var preset = LabelPrintEnhancements.EnlargeAddressText(
            image, [from], TextEnhancement.ExtraLarge);
        var custom = LabelPrintEnhancements.EnlargeAddressText(
            image, [from], TextEnhancement.Custom, 220);
        var presetBounds = ImageAnalysis.FindContentBounds(preset.Crop(new PixelRect(10, 10, 400, 250)));
        var customBounds = ImageAnalysis.FindContentBounds(custom.Crop(new PixelRect(10, 10, 400, 250)));

        Assert.True(customBounds.Width > presetBounds.Width);
        Assert.True(customBounds.Height > presetBounds.Height);
    }

    [Fact]
    public void CustomAddressScaleRejectsOutOfRangePercentages()
    {
        var image = GrayImage.White(200, 200);
        var address = new PixelRect(10, 10, 80, 40);
        DrawDenseAddress(image, address);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LabelPrintEnhancements.EnlargeAddressText(image, [address], TextEnhancement.Custom, 451));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LabelPrintEnhancements.EnlargeAddressText(image, [address], TextEnhancement.Custom, 100, 251));
    }

    [Fact]
    public void CustomFromAndToScalesAreAppliedIndependently()
    {
        var image = GrayImage.White(900, 500);
        var from = new PixelRect(20, 20, 120, 60);
        var to = new PixelRect(500, 20, 120, 60);
        Fill(image, new PixelRect(25, 25, 100, 40), 0);
        Fill(image, new PixelRect(505, 25, 100, 40), 0);

        var enlarged = LabelPrintEnhancements.EnlargeAddressText(
            image, [from, to], TextEnhancement.Custom, 120, 220);
        var fromBounds = ImageAnalysis.FindContentBounds(enlarged.Crop(new PixelRect(10, 10, 350, 250)));
        var toBounds = ImageAnalysis.FindContentBounds(enlarged.Crop(new PixelRect(450, 10, 440, 250)));

        Assert.InRange(fromBounds.Width, 115, 125);
        Assert.InRange(toBounds.Width, 215, 225);
        Assert.True(toBounds.Height > fromBounds.Height);
    }

    [Fact]
    public void CustomFromScaleSupportsFourHundredFiftyPercent()
    {
        var image = GrayImage.White(1000, 600);
        var from = new PixelRect(20, 20, 100, 50);
        Fill(image, new PixelRect(25, 25, 80, 30), 0);

        var enlarged = LabelPrintEnhancements.EnlargeAddressText(
            image, [from], TextEnhancement.Custom, 450, 100);
        var bounds = ImageAnalysis.FindContentBounds(enlarged);

        Assert.InRange(bounds.Width, 355, 365);
        Assert.InRange(bounds.Height, 130, 140);
    }

    [Fact]
    public void IndependentCustomScalingDoesNotLetToLimitFrom()
    {
        var image = GrayImage.White(1000, 650);
        var from = new PixelRect(20, 20, 100, 50);
        var to = new PixelRect(500, 20, 400, 100);
        Fill(image, new PixelRect(25, 25, 80, 30), 0);
        Fill(image, new PixelRect(505, 25, 380, 80), 0);

        var enlarged = LabelPrintEnhancements.EnlargeAddressText(
            image, [from, to], TextEnhancement.Custom, 350, 250);
        var fromBounds = ImageAnalysis.FindContentBounds(enlarged.Crop(new PixelRect(10, 10, 400, 400)));

        Assert.InRange(fromBounds.Width, 275, 285);
    }

    private static void DrawTextPattern(GrayImage image, PixelRect rectangle)
    {
        for (var y = rectangle.Y + 4; y < rectangle.Bottom - 4; y += 10)
        for (var x = rectangle.X + 4; x < rectangle.Right - 4; x += 8)
            Fill(image, new PixelRect(x, y, 3, 6), 0);
    }

    private static void DrawDenseAddress(GrayImage image, PixelRect rectangle)
    {
        for (var y = rectangle.Y + 2; y < rectangle.Bottom - 2; y += 10)
        for (var x = rectangle.X + 2; x < rectangle.Right - 2; x += 8)
            Fill(image, new PixelRect(x, y, 4, 7), 0);
    }

    private static int CountInk(GrayImage image, PixelRect rectangle)
    {
        var count = 0;
        for (var y = rectangle.Y; y < rectangle.Bottom; y++)
        for (var x = rectangle.X; x < rectangle.Right; x++)
            if (image[x, y] < 128) count++;
        return count;
    }

    private static int FindLastInkRow(GrayImage image)
    {
        for (var y = image.Height - 1; y >= 0; y--)
        for (var x = 0; x < image.Width; x++)
            if (image[x, y] < 128) return y;
        return -1;
    }

    private static void Fill(GrayImage image, PixelRect rectangle, byte value)
    {
        for (var y = rectangle.Y; y < rectangle.Bottom; y++)
        for (var x = rectangle.X; x < rectangle.Right; x++)
            image[x, y] = value;
    }
}
