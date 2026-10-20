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
public sealed record YouTubeSettings(
    IReadOnlyList<string> OtherSites,
    int Concurrency,
    bool AutoUpdate,
    int MaxItems,
    AudioFormat DefaultFormat)
{
    public static readonly YouTubeSettings Default = new([], 2, true, 500, AudioFormat.Original);

    public const int MaxConcurrency = 4;
    public const int MaxItemsLimit = 5000;

    /// <summary>Within limits; the other sites by domain (a URL gives its host), never YouTube's own.</summary>
    public YouTubeSettings Normalized() => new(
        (OtherSites ?? []).Select(s => s.Trim().ToLowerInvariant().TrimStart('.'))
            .Select(s => s.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri.Host : s.Split('/')[0])
            .Where(s => s.Length is > 0 and <= 253 && s.Contains('.') && !UrlClassifier.IsYouTubeHost(s))
            .Distinct().Take(100).ToList(),
        Math.Clamp(Concurrency, 1, MaxConcurrency), AutoUpdate, Math.Clamp(MaxItems, 1, MaxItemsLimit), DefaultFormat);
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
public sealed record UserPreference(bool PlayWhenDone);

/// <param name="HasCookies">The user has cookies of their own (used for their links instead of the server's).</param>
public sealed record MyYouTube(bool HasCookies, bool PlayWhenDone);

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
