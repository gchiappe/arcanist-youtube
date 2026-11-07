using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcanist.YouTube;

/// <summary>How a download is saved.</summary>
public enum AudioFormat
{
    /// <summary>The audio as YouTube has it (usually Opus, sometimes AAC), without converting: the best quality.</summary>
    Original,
    Opus,
    Mp3,
    M4a,
    Flac,
}

/// <summary>For the formats that are converted: how much space against how much detail.</summary>
public enum AudioQuality
{
    Best,
    High,
    Medium,
}

/// <summary>The extension's settings, which administrators change from its app.</summary>
/// <param name="OtherSites">Sites besides YouTube that users may bring audio from (yt-dlp knows many), by domain.</param>
/// <param name="Concurrency">Downloads at a time, for everyone.</param>
/// <param name="AutoUpdate">Keep yt-dlp and Deno up to date (YouTube changes often, and old versions stop working).</param>
/// <param name="MaxItems">The most songs one link brings (a channel can have thousands).</param>
/// <param name="DefaultFormat">The format the app suggests.</param>
/// <param name="VideoHeight">The highest picture of the music videos brought when the user leaves it to the server (one of
/// <see cref="VideoHeights"/>): taller ones weigh much more (UHD, several times 1080p).</param>
public sealed record YouTubeSettings(
    IReadOnlyList<string> OtherSites,
    int Concurrency,
    bool AutoUpdate,
    int MaxItems,
    AudioFormat DefaultFormat,
    int VideoHeight = YouTubeSettings.DefaultVideoHeight)
{
    public static readonly YouTubeSettings Default = new([], 2, true, 500, AudioFormat.Original);

    public const int MaxConcurrency = 4;
    public const int MaxItemsLimit = 5000;

    public const int DefaultVideoHeight = 1080;

    /// <summary>The pictures a music video can be brought at, as Arcanist's own versions of them (UHD down to 360p).</summary>
    public static readonly IReadOnlyList<int> VideoHeights = [2160, 1440, 1080, 720, 480, 360];

    /// <summary>The nearest allowed height not above <paramref name="height"/> (the lowest for less).</summary>
    public static int ValidHeight(int height) => VideoHeights.FirstOrDefault(h => h <= height) is var h and > 0 ? h : VideoHeights[^1];

    /// <summary>Within limits; the other sites by domain (a URL gives its host), never YouTube's own.</summary>
    public YouTubeSettings Normalized() => new(
        (OtherSites ?? []).Select(s => s.Trim().ToLowerInvariant().TrimStart('.'))
            .Select(s => s.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri.Host : s.Split('/')[0])
            .Where(s => s.Length is > 0 and <= 253 && s.Contains('.') && !UrlClassifier.IsYouTubeHost(s))
            .Distinct().Take(100).ToList(),
        Math.Clamp(Concurrency, 1, MaxConcurrency), AutoUpdate, Math.Clamp(MaxItems, 1, MaxItemsLimit), DefaultFormat,
        ValidHeight(VideoHeight <= 0 ? DefaultVideoHeight : VideoHeight));
}

/// <summary>Keeps the settings in the extension's data folder.</summary>
public sealed class SettingsStore(ExtensionPaths paths)
{
    private readonly Lock _gate = new();
    private YouTubeSettings? _current;

    public YouTubeSettings Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is not null)
                    return _current;
                try
                {
                    _current = File.Exists(paths.SettingsFile)
                        ? JsonSerializer.Deserialize<YouTubeSettings>(File.ReadAllText(paths.SettingsFile), Json.Options) ?? YouTubeSettings.Default
                        : YouTubeSettings.Default;
                }
                catch (JsonException)
                {
                    _current = YouTubeSettings.Default;
                }

                return _current;
            }
        }
    }

    public void Save(YouTubeSettings settings)
    {
        lock (_gate)
        {
            Files.WriteAllTextAtomically(paths.SettingsFile, JsonSerializer.Serialize(settings, Json.Options));
            _current = settings;
        }
    }
}

/// <summary>Where the extension keeps its things, inside its data folder.</summary>
public sealed class ExtensionPaths(string root, string? package)
{
    public string Root { get; } = root;

    /// <summary>The installed package (its web app is in <c>wwwroot</c>); null when run from a build.</summary>
    public string? Package { get; } = package;

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string DownloadsFile => Path.Combine(Root, "downloads.json");

    public string Tools => Path.Combine(Root, "tools");

    public string Work => Path.Combine(Root, "work");

    public string Cache => Path.Combine(Root, "cache");

    public string WebRoot => Path.Combine(Package ?? AppContext.BaseDirectory, "wwwroot");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class Files
{
    /// <summary>Writes beside the file and swaps it in, so a crash never leaves half of it.</summary>
    public static void WriteAllTextAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>A name that works as a file name everywhere (Windows' rules are the strictest).</summary>
    public static string SafeName(string? name, string fallback = "audio")
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var clean = new string((name ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimStart('.').TrimEnd('.', ' ');
        if (clean.Length > 120)
            clean = clean[..120].Trim();
        return clean.Length == 0 ? fallback : clean;
    }
}

/// <summary>What a user set for themselves.</summary>
/// <param name="PlayWhenDone">The app asks the player to play each song as it is added to the library.</param>
/// <param name="TermsVersion">The version of the responsible use notice the user accepted (0: none).</param>
public sealed record UserPreference(bool PlayWhenDone, int TermsVersion = 0, DateTime? TermsAcceptedAt = null);

/// <param name="HasCookies">The user has cookies of their own (used for their links instead of the server's).</param>
/// <param name="TermsAccepted">The user accepted the current responsible use notice; until then the app shows it first.</param>
public sealed record MyYouTube(bool HasCookies, bool PlayWhenDone, bool TermsAccepted = true);

public sealed record MyPreferences(bool PlayWhenDone);

/// <summary>Each user's preferences, in the extension's data folder (<c>users/&lt;id&gt;.json</c>).</summary>
public sealed class UserPreferences(ExtensionPaths paths)
{
    private readonly Lock _gate = new();

    private string FileOf(Guid user) => Path.Combine(paths.Root, "users", $"{user:N}.json");

    public UserPreference Of(Guid user)
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(FileOf(user))
                    ? JsonSerializer.Deserialize<UserPreference>(File.ReadAllText(FileOf(user)), Json.Options) ?? new UserPreference(false)
                    : new UserPreference(false);
            }
            catch (JsonException)
            {
                return new UserPreference(false);
            }
        }
    }

    public void Save(Guid user, UserPreference preference)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileOf(user))!);
            Files.WriteAllTextAtomically(FileOf(user), JsonSerializer.Serialize(preference, Json.Options));
        }
    }
}

/// <summary>
/// The notice of responsible use each user accepts once (in the app, or an administrator in the panel's page): only
/// what they have the right to keep, and YouTube's terms are theirs to respect. It cannot be accepted before it has been
/// on screen for <see cref="ReadingTime"/>: the app counts it down, and the server checks it.
/// </summary>
public sealed class ResponsibleUse(UserPreferences preferences)
{
    /// <summary>Raise it when the notice says something new: everyone reads it again.</summary>
    public const int Version = 1;

    public static readonly TimeSpan ReadingTime = TimeSpan.FromSeconds(8);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _shown = new();

    public bool Accepted(Guid user) => preferences.Of(user).TermsVersion >= Version;

    /// <summary>The notice is on the user's screen from now (the first time counts).</summary>
    public void Shown(Guid user) => _shown.TryAdd(user, DateTime.UtcNow);

    /// <summary>Accepts it, if it was shown long enough ago to be read.</summary>
    public bool Accept(Guid user)
    {
        if (Accepted(user))
            return true;
        if (!_shown.TryGetValue(user, out var shown) || DateTime.UtcNow - shown < ReadingTime - TimeSpan.FromSeconds(1))
            return false;
        preferences.Save(user, preferences.Of(user) with { TermsVersion = Version, TermsAcceptedAt = DateTime.UtcNow });
        return true;
    }
}

/// <summary>The words of the responsible use notice in the panel (the app has its own copy, in <c>ui/src/i18n.ts</c>).</summary>
public static class ResponsibleUseText
{
    public static readonly IReadOnlyList<string> Spanish =
    [
        "Descarga solo lo que te pertenece o tienes derecho a guardar: tu propia música, la que su autor ofrece para descargar o la que está en el dominio público.",
        "Los Términos del Servicio de YouTube no permiten descargar su contenido salvo donde YouTube lo ofrece. Cumplirlos, y respetar los derechos de autor de tu país, es responsabilidad de quien descarga.",
        "Esta extensión no es de YouTube ni de Google, ni está avalada por ellos. No aloja ni comparte música: lo que traes queda solo en tu servidor.",
        "Si no estás seguro de tener derecho a guardar algo, no lo descargues.",
    ];

    public static readonly IReadOnlyList<string> English =
    [
        "Download only what belongs to you or what you have the right to keep: your own music, music its author offers for download, or music in the public domain.",
        "YouTube's Terms of Service do not allow downloading its content except where YouTube offers it. Complying with them, and with your country's copyright, is up to whoever downloads.",
        "This extension is not YouTube's or Google's, nor endorsed by them. It hosts and shares no music: what you bring stays on your server only.",
        "If you are not sure you have the right to keep something, do not download it.",
    ];
}
