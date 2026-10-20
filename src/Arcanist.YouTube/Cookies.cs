using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Arcanist.YouTube;

/// <summary>
/// The cookies of a YouTube session (a <c>cookies.txt</c> in Netscape format, exported from a browser), for servers
/// YouTube asks to "confirm you are not a bot": the server's (administrators add them) and each user's own, which
/// are used for that user's links instead. They are kept encrypted with the server's Data Protection keys and each
/// run of yt-dlp gets a copy of its own, removed when it ends. They are never sent back.
/// </summary>
public sealed class CookieStore(ExtensionPaths paths, IDataProtectionProvider protection)
{
    public const int MaxBytes = 1024 * 1024;

    private readonly IDataProtector _protector = protection.CreateProtector("Arcanist.YouTube.Cookies");

    /// <summary>The server's file, or a user's.</summary>
    private string FileOf(Guid? user) => user is { } id ? Path.Combine(paths.Root, "users", $"{id:N}.cookies.bin") : Path.Combine(paths.Root, "cookies.bin");

    public bool Present => System.IO.File.Exists(FileOf(null));

    public bool PresentFor(Guid user) => System.IO.File.Exists(FileOf(user));

    /// <summary>Checks and keeps the cookies (replacing others): the server's, or a user's.</summary>
    public void Save(string text, Guid? user = null)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes || !LooksLikeCookies(text))
            throw new ExtensionException(ErrorCodes.InvalidCookies, 400);
        // yt-dlp wants the Netscape header on the first line.
        var normalized = text.Replace("\r\n", "\n").Trim() + "\n";
        if (!normalized.StartsWith("# Netscape HTTP Cookie File", StringComparison.Ordinal) && !normalized.StartsWith("# HTTP Cookie File", StringComparison.Ordinal))
            normalized = "# Netscape HTTP Cookie File\n" + normalized;
        var file = FileOf(user);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllBytes(file, _protector.Protect(Encoding.UTF8.GetBytes(normalized)));
    }

    public void Remove(Guid? user = null)
    {
        if (System.IO.File.Exists(FileOf(user)))
            System.IO.File.Delete(FileOf(user));
    }

    /// <summary>Lines of seven tab-separated fields, some of them for a YouTube or Google domain.</summary>
    private static bool LooksLikeCookies(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && (!l.StartsWith('#') || l.StartsWith("#HttpOnly_", StringComparison.Ordinal)))
            .Select(l => l.Split('\t')).Any(f => f.Length >= 7 && (f[0].Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || f[0].Contains("google.com", StringComparison.OrdinalIgnoreCase)));

    /// <summary>A copy for one run of yt-dlp (it may write them back): the user's, or else the server's; null without any.</summary>
    public string? Copy(Guid? user = null)
    {
        var file = user is { } id && PresentFor(id) ? FileOf(id) : FileOf(null);
        if (!System.IO.File.Exists(file))
            return null;
        try
        {
            var text = _protector.Unprotect(System.IO.File.ReadAllBytes(file));
            Directory.CreateDirectory(paths.Work);
            var copy = Path.Combine(paths.Work, $".cookies-{Guid.NewGuid():N}.txt");
            System.IO.File.WriteAllBytes(copy, text);
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return copy;
        }
        catch (CryptographicException)
        {
            // Keys changed (a restored server): the cookies are useless.
            return null;
        }
    }
}
