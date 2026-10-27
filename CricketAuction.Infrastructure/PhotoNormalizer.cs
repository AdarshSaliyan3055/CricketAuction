using ImageMagick;

namespace CricketAuction.Infrastructure;

/// <summary>One conversion path for local files and downloads; stored photos are always PNG.</summary>
public static class PhotoNormalizer
{
    public const int MaxDimension = 1600;
    public const long MaxDownloadBytes = 512L * 1024 * 1024;
    public const string FileFilter = "Pictures|*.jpg;*.jpeg;*.jfif;*.png;*.webp;*.heic;*.heif;*.avif;*.gif;*.bmp;*.tif;*.tiff;*.ico;*.jxl;*.psd;*.tga;*.dng;*.cr2;*.nef|All files|*.*";
    private static readonly object ConversionLock = new();

    static PhotoNormalizer()
    {
        ResourceLimits.Memory = 256UL * 1024 * 1024;
        ResourceLimits.Disk = 2UL * 1024 * 1024 * 1024;
        ResourceLimits.Thread = 2;
        ResourceLimits.Width = 100_000;
        ResourceLimits.Height = 100_000;
    }

    public static byte[] FromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return FromStream(stream);
    }

    public static byte[] FromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        return FromStream(stream);
    }

    public static byte[] FromStream(Stream stream)
    {
        // Serialize native decoding so a batch of large photos cannot exhaust RAM.
        lock (ConversionLock)
        {
            try
            {
                var start = stream.Position;
                var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
                Span<byte> signature = stackalloc byte[4];
                var signatureLength = stream.Read(signature);
                stream.Position = start;
                if (signatureLength == 4 && signature.SequenceEqual(new byte[] { 0, 0, 1, 0 })) settings.Format = MagickFormat.Ico;
                using var image = new MagickImage();
                image.Ping(stream, settings);
                if (!IsPhotoFormat(image.Format))
                    throw new InvalidOperationException("This file is not a supported raster photo. Choose a JPG, PNG, HEIC, AVIF, WebP, TIFF or another picture file.");
                if ((ulong)image.Width * image.Height > 250_000_000)
                    throw new InvalidOperationException("This photo exceeds 250 megapixels. Resize it first or use a Drive preview.");
                stream.Position = start;
                image.Read(stream, settings);
                image.AutoOrient();
                image.ColorSpace = ColorSpace.sRGB;
                image.Resize(new MagickGeometry(MaxDimension, MaxDimension) { Greater = true });
                image.Strip();
                image.Depth = 8;
                return image.ToByteArray(MagickFormat.Png);
            }
            catch (MagickException ex)
            {
                throw new InvalidOperationException("Could not convert this photo. It may be damaged, use an unavailable codec, or exceed available memory/disk space. Try a Drive preview or export it as JPG/PNG.", ex);
            }
        }
    }

    private static bool IsPhotoFormat(MagickFormat format) => format.ToString().ToUpperInvariant() is
        "JPEG" or "JPG" or "PNG" or "PNG8" or "PNG24" or "PNG32" or "PNG48" or "PNG64"
        or "WEBP" or "HEIC" or "HEIF" or "AVIF" or "GIF" or "GIF87" or "BMP" or "BMP2" or "BMP3"
        or "TIFF" or "TIFF64" or "ICO" or "ICON" or "CUR" or "JXL" or "PSD" or "PSB" or "TGA"
        or "DNG" or "CR2" or "CR3" or "NEF" or "ARW" or "RAF" or "ORF" or "RW2" or "PEF"
        or "JP2" or "J2K" or "JPC" or "JPF" or "PCX" or "PPM" or "PGM" or "PBM" or "PAM";
}
