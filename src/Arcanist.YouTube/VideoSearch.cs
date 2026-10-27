using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Arcanist.Core.Application.Catalog;

namespace Arcanist.YouTube;

/// <summary>A song of the library whose music video is looked for.</summary>
public sealed record VideoSearchSong(Guid Id, string Title, string? Artist, int DurationMs, bool HasVideo);

/// <summary>A video that may be the song's music video, the likeliest first.</summary>
/// <param name="Score">How likely, 0–100: the title and the artist, an official channel, a duration close to the song's.</param>
/// <param name="Official">From the artist's channel or VEVO, or titled as the official video.</param>
public sealed record VideoCandidate(string Id, string Url, string Title, string? Channel, double? Duration, string? Thumbnail, int Score, bool Official);

public sealed record VideoSearchResult(VideoSearchSong Song, string Query, IReadOnlyList<VideoCandidate> Candidates);

/// <summary>
/// Looks for a library song's music video on YouTube: a search for its artist and title ("official video", or "live"
/// for a live recording), each result scored by how well its title names the song, whether it comes from the artist's
/// channel (or VEVO) and how close its length is to the song's. Lyric videos, covers and the like count against.
/// </summary>
public sealed class VideoSearch(YtDlp ytDlp, CatalogService catalog)
{
    private const int Count = 10;

    private static readonly string[] Unwanted =
        ["lyric", "lyrics", "letra", "karaoke", "cover", "reaction", "instrumental", "8d", "slowed", "sped up", "nightcore", "tutorial", "remix"];

    private static readonly string[] Wanted = ["official video", "official music video", "videoclip", "video oficial", "videoclip oficial", "music video", "mv"];

    public async Task<VideoSearchResult> SearchAsync(Guid trackId, CancellationToken cancellationToken)
    {
        var track = await catalog.TrackAsync(trackId, cancellationToken);
        var artist = string.Join(", ", track.Artists.Where(a => a.Role == Arcanist.Core.Data.Entities.ArtistRole.Primary).Select(a => a.Name));
        if (artist.Length == 0)
            artist = track.AlbumArtist.Name;
        var live = Words(track.Title).Contains("live") || Words(track.Album.Title).Contains("live");
        var song = new VideoSearchSong(track.Id, track.Title, artist, track.DurationMs, track.Videos.Count > 0);
        var query = $"{artist} {track.Title} {(live ? "live" : "official video")}".Trim();

        var entries = await ytDlp.SearchAsync(query, Count, cancellationToken);
        var candidates = entries
            .Select(entry => Candidate(entry, song))
            .OfType<VideoCandidate>()
            .OrderByDescending(c => c.Score)
            .ToList();
        return new VideoSearchResult(song, query, candidates);
    }

    private static VideoCandidate? Candidate(JsonObject entry, VideoSearchSong song)
    {
        if (TagGuesser.Text(entry, "id") is not { } id || TagGuesser.Text(entry, "title") is not { } title)
            return null;
        var channel = TagGuesser.Text(entry, "channel") ?? TagGuesser.Text(entry, "uploader");
        var duration = TagGuesser.Number(entry, "duration");
        var titleWords = Words(title);
        var songWords = Words(song.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var artistWords = Words(song.Artist ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var channelWords = Words(channel ?? "");

        // How much of the song's title and artist the video names.
        var score = 0.0;
        score += 40.0 * songWords.Count(w => titleWords.Split(' ').Contains(w)) / Math.Max(1, songWords.Length);
        score += 20.0 * artistWords.Count(w => titleWords.Contains(w) || channelWords.Contains(w)) / Math.Max(1, artistWords.Length);

        var vevo = channel?.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase) == true;
        var artistChannel = artistWords.Length > 0 && Words(song.Artist ?? "") == channelWords.Replace(" official", "").Replace(" vevo", "").Trim();
        var titledOfficial = Wanted.Any(w => $" {titleWords} ".Contains($" {w} ", StringComparison.Ordinal));
        var official = vevo || artistChannel || titledOfficial;
        if (vevo || artistChannel)
            score += 15;
        if (titledOfficial)
            score += 10;
        // "Artist - Topic" channels have the song with a still picture, not its video.
        if (channel?.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase) == true)
            score -= 25;
        if (Unwanted.Any(w => $" {titleWords} ".Contains($" {w} ", StringComparison.Ordinal)) && !Unwanted.Any(w => Words(song.Title).Contains(w)))
            score -= 30;
        // Music videos are often a little longer than the song (an intro, a scene at the end).
        if (duration is { } seconds && song.DurationMs > 0)
        {
            var difference = seconds - song.DurationMs / 1000.0;
            score += difference is >= -10 and <= 90 ? 15 : difference is >= -30 and <= 240 ? 5 : -10;
        }

        var url = TagGuesser.Text(entry, "url") is { } link && link.StartsWith("http", StringComparison.Ordinal) ? link : $"https://www.youtube.com/watch?v={id}";
        return new VideoCandidate(id, url, title, channel, duration, Inspector.Thumbnail(entry, large: false), (int)Math.Clamp(Math.Round(score), 0, 100), official);
    }

    /// <summary>Lower-case words without accents or punctuation.</summary>
    private static string Words(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
