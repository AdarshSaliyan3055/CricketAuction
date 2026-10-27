using CricketAuction.Infrastructure;
using ImageMagick;

namespace CricketAuction.Tests;

public sealed class PhotoNormalizerTests
{
    [Theory]
    [InlineData(MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.WebP)]
    [InlineData(MagickFormat.Avif)]
    [InlineData(MagickFormat.Gif)]
    [InlineData(MagickFormat.Bmp)]
    [InlineData(MagickFormat.Tiff)]
    [InlineData(MagickFormat.Ico)]
    [InlineData(MagickFormat.Jxl)]
    [InlineData(MagickFormat.Psd)]
    public void FormatsBecomeDisplayablePng(MagickFormat format)
    {
        using var source = new MagickImage(MagickColors.Coral, 64, 32);
        var bytes = PhotoNormalizer.FromBytes(source.ToByteArray(format));
        using var result = new MagickImage(bytes);
        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.Equal(64U, result.Width);
        Assert.Equal(32U, result.Height);
    }

    [Theory]
    [InlineData(4000, 2000, 1600, 800)]
    [InlineData(100, 15000, 11, 1600)]
    [InlineData(40, 20, 40, 20)]
    public void ResizesBothDimensionsWithoutUpscaling(uint width, uint height, uint expectedWidth, uint expectedHeight)
    {
        using var source = new MagickImage(MagickColors.Coral, width, height);
        using var result = new MagickImage(PhotoNormalizer.FromBytes(source.ToByteArray(MagickFormat.Png)));
        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Fact]
    public void LocalFileOverTenMegabytesIsConvertedRegardlessOfExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".photo");
        try
        {
            using var source = new MagickImage(MagickColors.Coral, 2200, 2200);
            source.Write(path, MagickFormat.Bmp);
            Assert.True(new FileInfo(path).Length > 10_000_000);
            using var result = new MagickImage(PhotoNormalizer.FromFile(path));
            Assert.Equal(1600U, result.Width);
            Assert.Equal(MagickFormat.Png, result.Format);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExifOrientationIsAppliedBeforeMetadataIsRemoved()
    {
        using var source = new MagickImage(MagickColors.Coral, 80, 40);
        source.Orientation = Orientation.RightTop;
        using var result = new MagickImage(PhotoNormalizer.FromBytes(source.ToByteArray(MagickFormat.Tiff)));
        Assert.Equal(40U, result.Width);
        Assert.Equal(80U, result.Height);
    }

    [Fact]
    public void AnimationUsesFirstFrame()
    {
        using var frames = new MagickImageCollection();
        frames.Add(new MagickImage(MagickColors.Red, 40, 20));
        frames.Add(new MagickImage(MagickColors.Blue, 40, 20));
        using var result = new MagickImageCollection(PhotoNormalizer.FromBytes(frames.ToByteArray(MagickFormat.Gif)));
        Assert.Single(result);
        Assert.Equal(MagickColors.Red, result[0].GetPixels().GetPixel(0, 0).ToColor());
    }

    [Fact]
    public void RealHeicPhotoIsConverted()
    {
        using var result = new MagickImage(PhotoNormalizer.FromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "example.heic")));
        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.True(result.Width > 0 && result.Width <= 1600);
        Assert.True(result.Height > 0 && result.Height <= 1600);
    }

    [Fact]
    public void InvalidContentReportsConversionFailure()
    {
        Assert.Throws<InvalidOperationException>(() => PhotoNormalizer.FromBytes("<html>not a picture</html>"u8.ToArray()));
    }
}
