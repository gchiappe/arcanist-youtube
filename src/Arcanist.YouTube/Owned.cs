using System.Text.RegularExpressions;
using Arcanist.Core.Auth;
using Arcanist.Core.Data;
using Arcanist.Core.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Arcanist.YouTube;

/// <summary>A copy of a song the user already has, in a library they can play.</summary>
/// <param name="Lossless">The best of its files is lossless (FLAC, ALAC, WAV…).</param>
/// <param name="Libraries">The libraries it is in.</param>
public sealed record OwnedCopy(
    Guid TrackId,
    string Title,
    string Artist,
    string Album,
    int? Year,
    int DurationMs,
    Guid? ArtworkId,
    string? Format,
    bool Lossless,
    IReadOnlyList<string> Libraries);

/// <summary>A song asked about: its title and artist (as YouTube names them).</summary>
public sealed record SongQuery(string Key, string? Title, string? Artist);

/// <summary>An album or single asked about.</summary>
public sealed record AlbumQuery(string Key, string? Title, string? Artist);

/// <summary>
/// What the user already has, before anything is downloaded again: a song is the same when its name, without what
/// says which pressing or which kind of video it is ("(Official Video)", "(Remastered 2011)", "feat. …"), and one of
/// its artists are the same, on any album. "Live", "Acoustic", "Remix" and the like stay in the name: those are other
/// songs. The album does not count: YouTube often names another one (the single, a compilation) than the library.
/// </summary>
public sealed partial class OwnedSongs(IArcanistDbContextFactory contexts)
{
    private const int MaxArtists = 200;

    /// <summary>The copies of each song the user has, by its key (only songs with at least one).</summary>
    public async Task<Dictionary<string, IReadOnlyList<OwnedCopy>>> SongsAsync(UserAccess access, IReadOnlyList<SongQuery> songs,
        CancellationToken cancellationToken)
    {
        var asked = songs
            .Where(s => !string.IsNullOrWhiteSpace(s.Title) && !string.IsNullOrWhiteSpace(s.Artist))
            .Select(s => (s.Key, Core: Core(s.Title!), Artists: ArtistNames(s.Artist!)))
            .Where(s => s.Core.Length > 0 && s.Artists.Count > 0)
            .ToList();
        var result = new Dictionary<string, IReadOnlyList<OwnedCopy>>(StringComparer.Ordinal);
        if (asked.Count == 0)
            return result;

        await using var db = contexts.CreateDbContext();
        var artistIds = await ArtistIdsAsync(db, asked.SelectMany(s => s.Artists), cancellationToken);
        if (artistIds.Count == 0)
            return result;

        var rows = await db.Tracks.AsNoTracking()
            .Where(t => t.Artists.Any(a => artistIds.Contains(a.ArtistId)) || artistIds.Contains(t.Album.AlbumArtistId))
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.NormalizedTitle,
                t.DurationMs,
                Album = t.Album.Title,
                t.Album.Year,
                ArtworkId = t.ArtworkId ?? t.Album.ArtworkId,
                AlbumArtist = t.Album.AlbumArtist.NormalizedName,
                AlbumArtistName = t.Album.AlbumArtist.Name,
                Artists = t.Artists.OrderBy(a => a.Position).Select(a => new { a.Artist.Name, a.Artist.NormalizedName, a.Role }).ToList(),
                Files = t.Files.Where(f => f.MissingSince == null && f.CueTrack >= 0)
                    .Select(f => new { f.RelativePath, f.Format, f.LibrarySource.LibraryId, Library = f.LibrarySource.Library.Name }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var mine = rows
            .Select(r => r with { Files = r.Files.Where(f => access.CanAccessLibrary(f.LibraryId)).ToList() })
            .Where(r => r.Files.Count > 0)
            .Select(r => (Row: r, Core: Core(r.NormalizedTitle), Names: r.Artists.Select(a => a.NormalizedName).Append(r.AlbumArtist).ToList()))
            .ToList();

        foreach (var song in asked)
        {
            var copies = mine
                .Where(m => m.Core == song.Core && SameArtist(song.Artists, m.Names))
                .Select(m =>
                {
                    var lossless = m.Row.Files.Any(f => AudioFormats.IsLossless(f.RelativePath));
                    var primary = m.Row.Artists.Where(a => a.Role == Arcanist.Core.Data.Entities.ArtistRole.Primary).Select(a => a.Name).ToList();
                    return new OwnedCopy(m.Row.Id, m.Row.Title, primary.Count > 0 ? string.Join(", ", primary) : m.Row.AlbumArtistName, m.Row.Album,
                        m.Row.Year, m.Row.DurationMs, m.Row.ArtworkId, BestFormat(m.Row.Files.Select(f => (f.RelativePath, f.Format))), lossless,
                        m.Row.Files.Select(f => f.Library).Distinct().ToList());
                })
                // Lossless copies first.
                .OrderByDescending(c => c.Lossless).ThenBy(c => c.Album, StringComparer.CurrentCultureIgnoreCase)
                .Take(10)
                .ToList();
            if (copies.Count > 0)
                result[song.Key] = copies;
        }

        return result;
    }

    /// <summary>The keys of the albums (or singles) the user already has: one of theirs with that name and artist.</summary>
    public async Task<HashSet<string>> AlbumsAsync(UserAccess access, IReadOnlyList<AlbumQuery> albums, CancellationToken cancellationToken)
    {
        var asked = albums
            .Where(a => !string.IsNullOrWhiteSpace(a.Title) && !string.IsNullOrWhiteSpace(a.Artist))
            .Select(a => (a.Key, Core: Core(a.Title!), Artists: ArtistNames(a.Artist!)))
            .Where(a => a.Core.Length > 0 && a.Artists.Count > 0)
            .ToList();
        var owned = new HashSet<string>(StringComparer.Ordinal);
        if (asked.Count == 0)
            return owned;

        await using var db = contexts.CreateDbContext();
        var artistIds = await ArtistIdsAsync(db, asked.SelectMany(a => a.Artists), cancellationToken);
        if (artistIds.Count == 0)
            return owned;

        // Each album of those artists with a library it has files in (one row per pair: SQLite has no APPLY).
        var rows = await db.MediaFiles.AsNoTracking()
            .Where(f => f.MissingSince == null && f.CueTrack >= 0
                        && (artistIds.Contains(f.Track.Album.AlbumArtistId) || f.Track.Artists.Any(x => artistIds.Contains(x.ArtistId))))
            .Select(f => new { f.Track.Album.NormalizedTitle, Artist = f.Track.Album.AlbumArtist.NormalizedName, f.LibrarySource.LibraryId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var mine = rows.Where(r => access.CanAccessLibrary(r.LibraryId)).Select(r => (Core: Core(r.NormalizedTitle), r.Artist)).Distinct().ToList();

        foreach (var album in asked)
            if (mine.Any(m => m.Core == album.Core && SameArtist(album.Artists, [m.Artist])))
                owned.Add(album.Key);
        return owned;
    }

    /// <summary>The ids of the library's artists with these names (normalized), or whose name is one of them.</summary>
    private static async Task<List<Guid>> ArtistIdsAsync(ArcanistDbContext db, IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var wanted = names.Distinct(StringComparer.Ordinal).Take(MaxArtists).ToList();
        return await db.Artists.AsNoTracking().Where(a => wanted.Contains(a.NormalizedName)).Select(a => a.Id).ToListAsync(cancellationToken);
    }

    /// <summary>The normalized names in an artist credit: "A, B & C feat. D" is A, B, C and D (and the whole credit).</summary>
    public static List<string> ArtistNames(string credit)
    {
        var whole = TextNormalizer.Normalize(credit);
        var names = Separators().Split(credit).Select(TextNormalizer.Normalize).Where(n => n.Length > 0).Prepend(whole).Distinct().ToList();
        return names.Where(n => n.Length > 0).ToList();
    }

    private static bool SameArtist(IReadOnlyCollection<string> asked, IReadOnlyCollection<string> theirs) =>
        asked.Any(a => theirs.Any(b => a == b));

    /// <summary>The name of a song without what says which pressing or kind of video it is, normalized.</summary>
    public static string Core(string title)
    {
        var text = TextNormalizer.Normalize(Featuring().Replace(title, ""));
        for (var previous = ""; previous != text;)
        {
            previous = text;
            text = Qualifier().Replace(text, m => Performance().IsMatch(m.Value) || !Pressing().IsMatch(m.Value) ? m.Value : "").Trim();
        }

        return text.Length > 0 ? text : TextNormalizer.Normalize(title);
    }

    /// <summary>The format of the best file: a lossless one if there is, as its tags or its extension say.</summary>
    private static string? BestFormat(IEnumerable<(string Path, string? Format)> files)
    {
        var all = files.ToList();
        var (path, format) = all.Where(f => AudioFormats.IsLossless(f.Path)).Concat(all).First();
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return format ?? (extension.Length > 0 ? extension : null);
    }

    /// <summary>A qualifier at the end of a (normalized) title: in brackets, or after a dash.</summary>
    [GeneratedRegex(@"(\s*[\(\[][^\)\]]*[\)\]]|\s+[-–—]\s+[^-–—]+)$")]
    private static partial Regex Qualifier();

    /// <summary>"feat. Someone", with or without brackets, at the end of a title.</summary>
    [GeneratedRegex(@"\s*[\(\[]?\b(feat\.?|ft\.?|featuring)\s[^\)\]]*[\)\]]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    /// <summary>Words that only say which pressing, or which kind of upload, it is.</summary>
    [GeneratedRegex(@"\b(remaster\w*|remasteriza\w*|\d{4}|deluxe|anniversary|aniversario|mono|stereo|estereo|explicit|clean|bonus|album|single|original|version|official|oficial|video|videoclip|audio|lyrics?|letra|visualizer|visualiser|hd|hq|4k|mv|topic)\b")]
    private static partial Regex Pressing();

    /// <summary>Words that make it another performance: those stay.</summary>
    [GeneratedRegex(@"\b(live|en vivo|directo|acoustic\w*|acustic\w*|unplugged|remix\w*|mix|demo|instrumental|karaoke|reprise|edit|radio|session\w*|cover)\b")]
    private static partial Regex Performance();

    /// <summary>Between the artists of a credit.</summary>
    [GeneratedRegex(@"\s*(?:,|&|\+|/|;|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b|\by\b|\band\b|\bcon\b|\bwith\b|\bvs\.?)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Separators();
}
