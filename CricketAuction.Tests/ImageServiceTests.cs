using System.Net;
using CricketAuction.Infrastructure;
using ImageMagick;

namespace CricketAuction.Tests;

public sealed class ImageServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PhotoTests", Guid.NewGuid().ToString());
    private static byte[] Png { get { using var image = new MagickImage(MagickColors.Red, 32, 16); return image.ToByteArray(MagickFormat.Png); } }

    [Theory]
    [InlineData("html")]
    [InlineData("avif")]
    [InlineData("oversized")]
    [InlineData("forbidden")]
    public async Task DriveFallsBackToPreview(string failure)
    {
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (requests.Count == 2) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
            return failure switch
            {
                "html" => new(HttpStatusCode.OK) { Content = new StringContent("<html>Download confirmation</html>", System.Text.Encoding.UTF8, "text/html") },
                "avif" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0, 0, 24, 102, 116, 121, 112, 97, 118, 105, 102]) },
                "oversized" => OversizedResponse(),
                _ => new(HttpStatusCode.Forbidden)
            };
        }));
        var result = await Service(client).FetchWithResultAsync("https://drive.google.com/open?id=photo_123&resourcekey=key_456");
        Assert.Null(result.Error);
        using var image = new MagickImage(result.Bytes!);
        Assert.Equal(MagickFormat.Png, image.Format);
        Assert.Equal(32U, image.Width);
        Assert.Equal(new[] {
            "https://drive.google.com/uc?export=download&id=photo_123&resourcekey=key_456",
            "https://drive.google.com/thumbnail?sz=w1600&id=photo_123&resourcekey=key_456"
        }, requests);
    }

    [Theory]
    [InlineData("https://drive.google.com/file/d/photo_123/view", true)]
    [InlineData("https://example.com/photo.jpg", false)]
    public async Task SuccessfulOriginalOrNonDriveFailureDoesNotTryPreview(string url, bool success)
    {
        var count = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            count++;
            return new(success ? HttpStatusCode.OK : HttpStatusCode.Forbidden) { Content = new ByteArrayContent(Png) };
        }));
        var result = await Service(client).FetchWithResultAsync(url);
        Assert.Equal(1, count);
        Assert.Equal(success, result.Bytes != null);
    }

    [Fact]
    public async Task PrivateDriveFileReportsFailureAfterPreviewAlsoFails()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden)));
        var result = await Service(client).FetchWithResultAsync("https://drive.google.com/open?id=private");
        Assert.Null(result.Bytes);
        Assert.Contains("Access denied", result.Error);
        Assert.Contains("Drive preview also failed", result.Error);
    }

    [Fact]
    public async Task DownloadOverTenMegabytesIsConvertedBeforeReturning()
    {
        using var source = new MagickImage(MagickColors.Coral, 2200, 2200);
        var bytes = source.ToByteArray(MagickFormat.Bmp);
        Assert.True(bytes.Length > 10_000_000);
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var result = await Service(client).FetchWithResultAsync("https://example.com/photo");
        Assert.Null(result.Error);
        using var converted = new MagickImage(result.Bytes!);
        Assert.Equal(MagickFormat.Png, converted.Format);
        Assert.Equal(1600U, converted.Width);
    }

    [Theory]
    [InlineData(MagickFormat.Heic)]
    [InlineData(MagickFormat.Avif)]
    public async Task ModernPhonePhotoDownloadsAreConverted(MagickFormat format)
    {
        using var source = new MagickImage(MagickColors.Coral, 64, 32);
        var bytes = format == MagickFormat.Heic ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "example.heic")) : source.ToByteArray(format);
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var result = await Service(client).FetchWithResultAsync("https://example.com/photo");
        Assert.Null(result.Error);
        using var converted = new MagickImage(result.Bytes!);
        Assert.Equal(MagickFormat.Png, converted.Format);
    }

    [Fact]
    public async Task CancellationDoesNotStartPreview()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            count++;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));
        var result = await Service(client).FetchWithResultAsync("https://drive.google.com/open?id=photo", cancellation.Token);
        Assert.Equal(1, count);
        Assert.Equal("Download cancelled.", result.Error);
    }

    private ImageService Service(HttpClient client) => new(new AppLog(new Workspace(root)), client);
    private static HttpResponseMessage OversizedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = PhotoNormalizer.MaxDownloadBytes + 1;
        return response;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
