using ShipTime4x4.Core.Raster;

namespace ShipTime4x4.Tests;

public sealed class ImageAnalysisTests
{
    [Fact]
    public void PositionalZeroMeansNoPaddingAndStillFindsVisibleContent()
    {
        var image = GrayImage.White(40, 30);
        image[12, 9] = 0;
        image[13, 9] = 0;

        var bounds = ImageAnalysis.FindContentBounds(image, 0);

        Assert.False(bounds.IsEmpty);
        Assert.Equal(new PixelRect(12, 9, 2, 1), bounds);
    }
}
