using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Arcanist.YouTube;

/// <summary>The tags a song is saved with. The app proposes them and the user can change them.</summary>
public sealed record SongTags(
    string? Title,
    string? Artist,
    string? Album,
    string? AlbumArtist = null,
    int? Year = null,
    int? TrackNumber = null,
    string? Genre = null);

/// <summary>
/// Proposes tags from what YouTube says about a video. YouTube Music songs (and the "Topic" channels behind them)
/// carry the track, artist and album; for other videos the title is read as "Artist - Title", without the usual
/// "(Official Video)" and "[Lyrics]" decorations, and the channel is the artist otherwise.
/// </summary>
public static partial class TagGuesser
{
    // Decorations uploaders add to titles, in English and Spanish, in brackets or after a dash or pipe.
    [GeneratedRegex(@"\s*[\(\[](?:official\s*)?(?:music\s*)?(?:video|audio|lyric(?:s)?\s*video|lyrics?|visualizer|video\s*oficial|audio\s*oficial|letra|con\s*letra|v[ií]deo\s*musical|hd|hq|4k|remastered\s*\d*|remasterizado|en\s*vivo|official)[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase)]
    private static partial Regex Decorations();

    [GeneratedRegex(@"\s*\|\s*.*$")]
    private static partial Regex AfterPipe();

    [GeneratedRegex(@"\s+(?:-|–|—|~)\s+")]
    private static partial Regex Dash();

    [GeneratedRegex(@"\s*(?:-\s*Topic|VEVO|\s+Official)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelSuffix();

    public static string? CleanChannel(string? channel) =>
        string.IsNullOrWhiteSpace(channel) ? null : ChannelSuffix().Replace(channel.Trim(), "").Trim() is { Length: > 0 } clean ? clean : channel.Trim();

    /// <summary>Tags for a video, from its yt-dlp description (a flat entry, or a full one).</summary>
    public static SongTags Guess(JsonObject info, string? album = null, int? trackNumber = null)
    {
        var title = Text(info, "title");
        var channel = CleanChannel(Text(info, "channel") ?? Text(info, "uploader"));
        var year = YearOf(info);

        // YouTube Music knows the song.
        var track = Text(info, "track");
        var artist = (info["artists"] as JsonArray)?.Select(a => a?.GetValue<string>()).OfType<string>().ToList() is { Count: > 0 } artists
            ? string.Join("; ", artists)
            : Text(info, "artist") ?? Text(info, "creator");
        if (track is not null && artist is not null)
            return new SongTags(track, artist, Text(info, "album") ?? album, null, year, trackNumber, Text(info, "genre"));

        var clean = AfterPipe().Replace(Decorations().Replace(title ?? "", ""), "").Trim();
        var parts = Dash().Split(clean, 2);
        if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            return new SongTags(parts[1].Trim(), parts[0].Trim(), album, null, year, trackNumber);
        return new SongTags(clean.Length > 0 ? clean : title, channel, album, null, year, trackNumber);
    }

    public static int? YearOf(JsonObject info)
    {
        if (info["release_year"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            return info["release_year"]!.GetValue<int>();
        var date = Text(info, "release_date") ?? Text(info, "upload_date");
        return date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
    }

    public static string? Text(JsonObject info, string key) =>
        info[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    public static double? Number(JsonObject info, string key) =>
        info[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
