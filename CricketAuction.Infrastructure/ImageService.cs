using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CricketAuction.Domain;

namespace CricketAuction.Infrastructure;
public sealed class ImageService(AppLog log, HttpClient? httpClient = null) : IImageService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 8 }) { Timeout = Timeout.InfiniteTimeSpan };
    public static string? NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        if (uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
        {
            var id = Regex.Match(uri.AbsolutePath, @"/file/d/([a-zA-Z0-9_-]+)");
            if (!id.Success) id = Regex.Match(uri.Query, @"[?&]id=([a-zA-Z0-9_-]+)");
            var resourceKey = Regex.Match(uri.Query, @"[?&]resourcekey=([^&]+)", RegexOptions.IgnoreCase);
            return id.Success ? "https://drive.google.com/uc?export=download&id=" + id.Groups[1].Value
                + (resourceKey.Success ? "&resourcekey=" + resourceKey.Groups[1].Value : "") : null;
        }
        return uri.ToString();
    }
    public static bool IsImage(byte[] bytes)
    {
        if (bytes.Length < 8) return false;
        if ((bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            || bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8)
            || bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)
            || bytes.AsSpan(0, 2).SequenceEqual("BM"u8)
            || bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x49, 0x49, 0x2a, 0x00 })
            || bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x4d, 0x4d, 0x00, 0x2a })
            || bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x00, 0x00, 0x01, 0x00 })) return true;
        return bytes.Length >= 12
            && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
    }
    public static bool IsAvif(byte[] bytes) => bytes.Length >= 12
        && bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8)
        && (bytes.AsSpan(8, 4).SequenceEqual("avif"u8) || bytes.AsSpan(8, 4).SequenceEqual("avis"u8));
    public async Task<byte[]?> FetchAsync(string url, CancellationToken cancellationToken = default) =>
        (await FetchWithResultAsync(url, cancellationToken)).Bytes;

    public async Task<PhotoFetchResult> FetchWithResultAsync(string url, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUrl(url);
        if (normalized == null) return new(null, "Invalid or unsupported URL. Use a public HTTPS image link.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var result = await FetchCandidateAsync(normalized, timeout.Token, cancellationToken);
        if (result.Bytes != null || timeout.IsCancellationRequested) return result;

        // Drive can render previews even when the original is too large, uses an
        // unsupported format, or is behind a download confirmation page.
        var uri = new Uri(normalized);
        if (uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
        {
            var preview = normalized.Replace("/uc?export=download&", "/thumbnail?sz=w1600&", StringComparison.Ordinal);
            var previewResult = await FetchCandidateAsync(preview, timeout.Token, cancellationToken);
            if (previewResult.Bytes != null || cancellationToken.IsCancellationRequested) return previewResult;
            return new(null, $"{result.Error} Drive preview also failed: {previewResult.Error}");
        }
        return result;
    }

    private async Task<PhotoFetchResult> FetchCandidateAsync(string url, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken);
        attempt.CancelAfter(TimeSpan.FromMinutes(2));
        timeoutToken = attempt.Token;
        try
        {
            using var response = await (httpClient ?? Client).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                return new(null, "Access denied. Set the Drive image to Anyone with the link, or choose a local photo.");
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "Image link was not found. Check that the file still exists.");
            if (!response.IsSuccessStatusCode) return new(null, $"Image server returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > PhotoNormalizer.MaxDownloadBytes) return new(null, "Download exceeds 512 MB. Choose the local file or use a Drive preview.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutToken);
            await using var output = new FileStream(Path.Combine(Path.GetTempPath(), $"auction-photo-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, timeoutToken)) > 0)
            {
                if (output.Length + read > PhotoNormalizer.MaxDownloadBytes) return new(null, "Download exceeds 512 MB. Choose the local file or use a Drive preview.");
                await output.WriteAsync(buffer.AsMemory(0, read), timeoutToken);
            }
            output.Position = 0;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            {
                var prefix = new byte[(int)Math.Min(output.Length, 32_768)];
                await output.ReadExactlyAsync(prefix, timeoutToken);
                var html = Encoding.UTF8.GetString(prefix);
                if (html.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("request access", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("you need access", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("sign in", StringComparison.OrdinalIgnoreCase))
                    return new(null, "Google Drive requires access. Set sharing to Anyone with the link, or choose a local photo.");
                return new(null, "The link returned a web page, not an image. Check public sharing or use a direct photo link.");
            }
            var converted = await Task.Run(() => PhotoNormalizer.FromStream(output), timeoutToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(converted, null);
        }
        catch (InvalidOperationException ex) { log.Write("Photo conversion failed", ex); return new(null, ex.Message); }
        catch (UnauthorizedAccessException ex) { log.Write("Photo temporary storage unavailable", ex); return new(null, "Could not store the download. Check temporary folder permissions or choose a local photo."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(null, "Download cancelled."); }
        catch (OperationCanceledException ex) { log.Write("Photo download timed out", ex); return new(null, "Download timed out. Retry later or use a local photo."); }
        catch (HttpRequestException ex) { log.Write("Photo download failed", ex); return new(null, "Could not reach the image link. Check the internet connection and retry."); }
        catch (IOException ex) { log.Write("Photo download failed", ex); return new(null, "Image download was interrupted. Retry later."); }
    }
}
