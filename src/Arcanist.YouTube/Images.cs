using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using SkiaSharp;

namespace Arcanist.YouTube;

/// <summary>
/// Thumbnails go through the server, so the app (and the user's browser) never connects to YouTube: the app gets
/// signed links to them (only images the server described can be fetched) and the server keeps a copy for a week.
/// It also makes the cover embedded in each song: the thumbnail, cropped square when asked (video frames are 16:9).
/// </summary>
public sealed class Images(IDataProtectionProvider protection, IHttpClientFactory clients, ExtensionPaths paths)
{
    private readonly IDataProtector _links = protection.CreateProtector("Arcanist.YouTube.Image");

    private string Folder => Path.Combine(paths.Cache, "images");

    /// <summary>The app's link to an image (null for none).</summary>
    public string? Link(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            ? $"/ext/youtube/api/v1/image/{WebEncoders.Base64UrlEncode(_links.Protect(Encoding.UTF8.GetBytes(url)))}"
            : null;

    /// <summary>The image a link names, from the copy kept or fetched now; null when the link is not one of ours.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetAsync(string token, CancellationToken cancellationToken)
    {
        string url;
        try
        {
            url = Encoding.UTF8.GetString(_links.Unprotect(WebEncoders.Base64UrlDecode(token)));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }

        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant());
        if (File.Exists(file))
            return (await File.ReadAllBytesAsync(file, cancellationToken), Sniff(file));

        var bytes = await FetchAsync(url, cancellationToken);
        if (bytes is null)
            return null;
        await File.WriteAllBytesAsync(file, bytes, cancellationToken);
        return (bytes, Sniff(file));
    }

    private async Task<byte[]?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await clients.CreateClient(ToolManager.HttpClientName).GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 10 * 1024 * 1024)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length is > 0 and <= 10 * 1024 * 1024 ? bytes : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static string Sniff(string file)
    {
        Span<byte> head = stackalloc byte[12];
        using var stream = File.OpenRead(file);
        var read = stream.Read(head);
        return read >= 4 && head[0] == 0x89 && head[1] == (byte)'P' ? "image/png"
            : read >= 12 && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' ? "image/webp"
            : "image/jpeg";
    }

    /// <summary>A JPEG cover from an image, at most 1200 px; cropped to its centered square when <paramref name="square"/>.</summary>
    public async Task<byte[]?> CoverAsync(string? url, bool square, CancellationToken cancellationToken)
    {
        if (url is null || await FetchAsync(url, cancellationToken) is not { } bytes)
            return null;
        using var image = SKBitmap.Decode(bytes);
        if (image is null)
            return null;

        var crop = new SKRectI(0, 0, image.Width, image.Height);
        if (square && image.Width != image.Height)
        {
            var side = Math.Min(image.Width, image.Height);
            crop = SKRectI.Create((image.Width - side) / 2, (image.Height - side) / 2, side, side);
        }

        var scale = Math.Min(1.0, 1200.0 / Math.Max(crop.Width, crop.Height));
        var info = new SKImageInfo((int)(crop.Width * scale), (int)(crop.Height * scale));
        using var target = new SKBitmap(info);
        using (var canvas = new SKCanvas(target))
        {
            using var source = SKImage.FromBitmap(image);
            canvas.DrawImage(source, crop, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var encoded = target.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded?.ToArray();
    }

    /// <summary>Forgets images kept for more than a week.</summary>
    public void Prune()
    {
        if (!Directory.Exists(Folder))
            return;
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                File.Delete(file);
        }
    }
}
