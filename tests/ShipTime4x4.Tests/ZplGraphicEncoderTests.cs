using System.Text;
using ShipTime4x4.Core.Raster;
using ShipTime4x4.Core.Zpl;

namespace ShipTime4x4.Tests;

public sealed class ZplGraphicEncoderTests
{
    [Theory]
    [InlineData(1200)]
    [InlineData(2400)]
    public void EncoderUsesExactCanvasAndBandsWithinLimit(int size)
    {
        var image = GrayImage.White(size, size);
        for (var index = 0; index < size; index++)
            image[index, index] = 0;

        var zpl = Encoding.ASCII.GetString(new ZplGraphicEncoder().Encode(image));
        Assert.Contains($"^PW{size}", zpl);
        Assert.Contains($"^LL{size}", zpl);
        var fields = zpl.Split("^GFA,", StringSplitOptions.RemoveEmptyEntries).Skip(1);
        Assert.NotEmpty(fields);
        foreach (var field in fields)
        {
            var byteCount = int.Parse(field.Split(',')[0]);
            Assert.InRange(byteCount, 1, ZplGraphicEncoder.MaximumGraphicBytes);
        }
    }

    [Fact]
    public void EncoderEmitsConfiguredDarknessAndSpeed()
    {
        var zpl = Encoding.ASCII.GetString(new ZplGraphicEncoder()
            .Encode(GrayImage.White(16, 16), darkness: 12.5, speedIps: 2.4));

        Assert.StartsWith("~SD12.5\n^XA\n^PR2.4", zpl);
    }
}
