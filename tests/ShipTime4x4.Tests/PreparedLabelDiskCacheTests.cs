using ShipTime4x4.Core.Processing;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Hotfolder.Models;
using ShipTime4x4.Hotfolder.Services;

namespace ShipTime4x4.Tests;

public sealed class PreparedLabelDiskCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ReLabel-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoundTripPreservesExactRasterAndZpl()
    {
        var cache = new PreparedLabelDiskCache(_folder);
        var id = Guid.NewGuid();
        var expected = new PreparedLabel(new GrayImage(3, 2, [0, 255, 12, 42, 88, 190]),
            [94, 88, 65, 10], true, "TEST", 4, 2)
        {
            PrintPages = [
                new ShipTime4x4.Core.Printing.MonochromePage(3, 2, [0x80, 0x40]),
                new ShipTime4x4.Core.Printing.MonochromePage(3, 2, [0x20, 0xe0])]
        };

        cache.Put(id, "exact-key", expected);

        Assert.True(cache.TryGet(id, "exact-key", out var actual));
        Assert.Equal(expected.Image.Pixels, actual.Image.Pixels);
        Assert.Equal(expected.Zpl, actual.Zpl);
        Assert.Equal(expected.UsedFallback, actual.UsedFallback);
        Assert.Equal(expected.WarningCode, actual.WarningCode);
        Assert.Equal(expected.AppliedBarcodeCompensationDots, actual.AppliedBarcodeCompensationDots);
        Assert.Equal(expected.PageCount, actual.PageCount);
        Assert.Equal(expected.PrintPages[0].Data, actual.PrintPages[0].Data);
        Assert.Equal(expected.PrintPages[1].Data, actual.PrintPages[1].Data);
    }

    [Fact]
    public void RemovingLabelPurgesAllOfItsPreparedVariants()
    {
        var cache = new PreparedLabelDiskCache(_folder);
        var id = Guid.NewGuid();
        var prepared = new PreparedLabel(new GrayImage(1, 1, [255]), [1], false, null);
        cache.Put(id, "rotated", prepared);
        cache.Put(id, "unrotated", prepared);

        cache.Remove(id);

        Assert.False(cache.TryGet(id, "rotated", out _));
        Assert.False(cache.TryGet(id, "unrotated", out _));
    }

    [Fact]
    public void ScannerVerificationIsDisabledByDefault()
    {
        Assert.False(new HotfolderConfiguration().ScannerVerificationEnabled);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void OldOrTruncatedCacheIsAMissInsteadOfPrintingAnIncompleteShipment(int version)
    {
        var cache = new PreparedLabelDiskCache(_folder);
        var id = Guid.NewGuid();
        cache.Put(id, "bad", new PreparedLabel(GrayImage.White(8, 1), [1], false, null));
        var path = Directory.GetFiles(Path.Combine(_folder, "PreparedCache"), "*.rlcache").Single();
        using (var file = File.Create(path))
        using (var compressed = new System.IO.Compression.DeflateStream(file, System.IO.Compression.CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(compressed))
        {
            writer.Write(version);
            // Truncated deliberately: no allocation or partial-page print is permitted.
        }
        Assert.False(cache.TryGet(id, "bad", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
