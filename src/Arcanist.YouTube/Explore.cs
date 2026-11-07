using System.Collections.Concurrent;
using Arcanist.Core.Auth;
using Arcanist.Core.Data;
using Arcanist.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arcanist.YouTube;

/// <summary>The latest releases of the artists of the user's libraries that they do not have yet.</summary>
/// <param name="Artists">The artists looked at (those with the most songs in the user's libraries).</param>
/// <param name="CheckedAt">When YouTube Music was asked (the answer is kept for some hours).</param>
public sealed record MyArtistsNews(IReadOnlyList<MusicItem> Items, IReadOnlyList<string> Artists, DateTime CheckedAt);

/// <summary>
/// Exploring YouTube Music from the app: what <see cref="YouTubeMusic"/> finds, each item marked when the user already
/// has it (a song by its name and artist, an album by its name and artist), so the app says so before anything is
/// downloaded again; and the new releases of the artists the user listens to.
/// </summary>
public sealed class Explore(YouTubeMusic music, OwnedSongs owned, IArcanistDbContextFactory contexts, ILogger<Explore> logger)
{
    /// <summary>How many of the user's artists are looked at.</summary>
    public const int MyArtistsCount = 20;

    /// <summary>Releases of this year and the one before count as new.</summary>
    private const int RecentYears = 1;

    private static readonly TimeSpan NewsKept = TimeSpan.FromHours(6);

    private static readonly HashSet<string> NotArtists = new(StringComparer.Ordinal)
    {
        "various artists", "varios artistas", "artistas varios", "unknown artist", "artista desconocido", "va",
    };

    private readonly ConcurrentDictionary<string, (DateTime Expires, MyArtistsNews News)> _news = new();
    private readonly ConcurrentDictionary<string, string?> _channels = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<MusicSection>> MarkAsync(UserAccess access, IReadOnlyList<MusicSection> sections, CancellationToken cancellationToken)
    {
        var marked = await MarkAsync(access, sections.SelectMany(s => s.Items).ToList(), cancellationToken);
        var byKey = marked.DistinctBy(Key).ToDictionary(Key);
        return sections.Select(s => s with { Items = s.Items.Select(i => byKey[Key(i)]).ToList() }).ToList();
    }

    /// <summary>The items, with <c>inLibrary</c> (and for songs, the copies) when the user has them.</summary>
    public async Task<IReadOnlyList<MusicItem>> MarkAsync(UserAccess access, IReadOnlyList<MusicItem> items, CancellationToken cancellationToken)
    {
        var distinct = items.DistinctBy(Key).ToList();
        var songs = distinct.Where(i => i.Kind is MusicItemKind.Song or MusicItemKind.Video)
            .Select(i => new SongQuery(Key(i), i.Title, Credit(i))).ToList();
        var albums = distinct.Where(i => i.Kind == MusicItemKind.Album).Select(i => new AlbumQuery(Key(i), i.Title, Credit(i))).ToList();
        // A single of one song is that song: having it on any album is having the single.
        var singles = distinct.Where(i => i.Kind == MusicItemKind.Album).Select(i => new SongQuery(Key(i), i.Title, Credit(i))).ToList();

        var copies = await owned.SongsAsync(access, songs, cancellationToken);
        var ownedAlbums = await owned.AlbumsAsync(access, albums, cancellationToken);
        var ownedSingles = (await owned.SongsAsync(access, singles, cancellationToken)).Keys.ToHashSet(StringComparer.Ordinal);

        return items.Select(i =>
        {
            var key = Key(i);
            return i.Kind switch
            {
                MusicItemKind.Song or MusicItemKind.Video when copies.TryGetValue(key, out var mine) => i with { InLibrary = true, Owned = mine },
                MusicItemKind.Album when ownedAlbums.Contains(key) || IsSingle(i) && ownedSingles.Contains(key) => i with { InLibrary = true },
                _ => i,
            };
        }).ToList();
    }

    /// <summary>
    /// The albums, singles and EPs of this year and the last by the artists with the most songs in the user's
    /// libraries, without those they already have, the newest first.
    /// </summary>
    public async Task<MyArtistsNews> MyArtistsAsync(UserAccess access, string language, string country, bool refresh, CancellationToken cancellationToken)
    {
        var key = $"{access.UserId}|{language}|{country}";
        if (!refresh && _news.TryGetValue(key, out var kept) && kept.Expires > DateTime.UtcNow)
            return kept.News;

        var artists = await MyArtistNamesAsync(access, cancellationToken);
        var thisYear = DateTime.UtcNow.Year;
        var found = new ConcurrentBag<(int Order, int Position, MusicItem Item)>();
        await Parallel.ForEachAsync(artists.Select((name, order) => (name, order)), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (artist, token) =>
            {
                try
                {
                    if (await ChannelAsync(artist.name, language, country, token) is not { } channel)
                        return;
                    var page = await music.ArtistAsync(channel, language, country, token);
                    var position = 0;
                    foreach (var item in page.Sections.SelectMany(s => s.Items).Where(i => i.Kind == MusicItemKind.Album && i.Year >= thisYear - RecentYears))
                        found.Add((artist.order, position++, item.Artists.Count > 0 ? item : item with { Artists = [new MusicArtistRef(page.Name, channel)] }));
                }
                catch (ExtensionException e)
                {
                    logger.LogDebug("New releases of {Artist}: {Code}", artist.name, e.Code);
                }
            });

        var releases = found.DistinctBy(f => f.Item.Id)
            .OrderByDescending(f => f.Item.Year).ThenBy(f => f.Order).ThenBy(f => f.Position)
            .Select(f => f.Item).ToList();
        var marked = await MarkAsync(access, releases, cancellationToken);
        var news = new MyArtistsNews(marked.Where(i => !i.InLibrary).Take(60).ToList(), artists, DateTime.UtcNow);
        _news[key] = (DateTime.UtcNow + NewsKept, news);
        return news;
    }

    /// <summary>The artists with the most songs the user can play (primary artists, not "Various Artists").</summary>
    private async Task<List<string>> MyArtistNamesAsync(UserAccess access, CancellationToken cancellationToken)
    {
        await using var db = contexts.CreateDbContext();
        var counts = await (
                from f in db.MediaFiles
                where f.MissingSince == null && f.CueTrack >= 0
                from a in f.Track.Artists
                where a.Role == ArtistRole.Primary
                group f by new { a.ArtistId, f.LibrarySource.LibraryId } into g
                select new { g.Key.ArtistId, g.Key.LibraryId, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var top = counts.Where(c => access.CanAccessLibrary(c.LibraryId))
            .GroupBy(c => c.ArtistId).Select(g => (Id: g.Key, Count: g.Sum(c => c.Count)))
            .OrderByDescending(a => a.Count).Select(a => a.Id).Take(MyArtistsCount * 2).ToList();
        var names = await db.Artists.AsNoTracking().Where(a => top.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => (a.Name, a.NormalizedName), cancellationToken);
        return top.Where(names.ContainsKey).Select(id => names[id]).Where(n => !NotArtists.Contains(n.NormalizedName)).Select(n => n.Name)
            .Take(MyArtistsCount).ToList();
    }

    /// <summary>The artist's page on YouTube Music: the first artist found with exactly their name.</summary>
    private async Task<string?> ChannelAsync(string artist, string language, string country, CancellationToken cancellationToken)
    {
        var name = TextNormalizer.Normalize(artist);
        if (_channels.TryGetValue(name, out var known))
            return known;
        var found = (await music.SearchAsync(artist, "artists", language, country, cancellationToken)).SelectMany(s => s.Items)
            .FirstOrDefault(i => i.Kind == MusicItemKind.Artist && TextNormalizer.Normalize(i.Title) == name)?.Id;
        _channels[name] = found;
        return found;
    }

    private static bool IsSingle(MusicItem item) => item.Label is { } label && label.Contains("single", StringComparison.OrdinalIgnoreCase);

    private static string? Credit(MusicItem item) => item.Artists.Count > 0 ? string.Join(", ", item.Artists.Select(a => a.Name)) : null;

    private static string Key(MusicItem item) => item.Kind + ":" + item.Id;
}
