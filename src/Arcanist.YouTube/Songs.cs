using Arcanist.Core.Application.Libraries;
using Arcanist.Core.Auth;
using Arcanist.Core.Data;
using Arcanist.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arcanist.YouTube;

/// <summary>A song of the user's libraries, to look for its music video.</summary>
/// <param name="HasVideo">A music video is linked to it already (to this track or to its recording).</param>
public sealed record LibrarySong(Guid Id, string Title, string? Artist, string Album, int? Year, int DurationMs, Guid? ArtworkId, bool HasVideo);

/// <param name="Next">The offset of the next page; null on the last one.</param>
public sealed record SongPage(IReadOnlyList<LibrarySong> Items, int? Next, int TotalCount);

/// <summary>
/// The songs of the libraries the user can bring music videos into (those they can upload to): searched by their title,
/// artists and album, and optionally only those without a music video yet, for the batch search.
/// </summary>
public sealed class SongList(LibraryService libraries, IArcanistDbContextFactory contexts)
{
    public const int MaxPage = 500;

    public async Task<SongPage> ListAsync(UserAccess access, string? search, Guid? libraryId, bool withoutVideo, int offset, int first,
        CancellationToken cancellationToken)
    {
        var targets = (await libraries.ListAsync(cancellationToken)).Where(l => l.CanUpload && !l.Disabled && !l.Virtual).Select(l => l.Id).ToList();
        if (libraryId is { } library)
        {
            if (!targets.Contains(library))
                throw Arcanist.Core.Application.ArcanistException.Field("libraryId", Arcanist.Core.Application.ErrorCodes.NotFound);
            targets = [library];
        }

        await using var db = contexts.CreateDbContext();
        var tracks = db.Tracks.Where(t => db.MediaFiles.Any(f => f.TrackId == t.Id && f.MissingSince == null && targets.Contains(f.LibrarySource.LibraryId)));
        // Every word, as the start of a word of its title, artists or album (the catalog's own search text).
        foreach (var word in TextNormalizer.Normalize(search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8))
        {
            var start = word;
            var inner = " " + word;
            tracks = tracks.Where(t => t.SearchText != null && (t.SearchText.StartsWith(start) || t.SearchText.Contains(inner)));
        }

        var videos = db.MusicVideos.Where(v => v.MissingSince == null);
        if (withoutVideo)
            tracks = tracks.Where(t => !videos.Any(v => v.TrackId == t.Id || v.RecordingId == t.RecordingId));

        var total = await tracks.CountAsync(cancellationToken);
        offset = Math.Max(0, offset);
        first = Math.Clamp(first, 1, MaxPage);
        var page = await tracks
            .OrderBy(t => t.NormalizedTitle).ThenBy(t => t.Id)
            .Skip(offset).Take(first)
            .Select(t => new
            {
                t.Id,
                t.Title,
                Artists = t.Artists.Where(a => a.Role == ArtistRole.Primary).OrderBy(a => a.Position).Select(a => a.Artist.Name).ToList(),
                AlbumArtist = t.Album.AlbumArtist.Name,
                Album = t.Album.Title,
                t.Album.Year,
                t.DurationMs,
                ArtworkId = t.ArtworkId ?? t.Album.ArtworkId,
                HasVideo = videos.Any(v => v.TrackId == t.Id || v.RecordingId == t.RecordingId),
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var items = page.Select(t => new LibrarySong(t.Id, t.Title, t.Artists.Count > 0 ? string.Join(", ", t.Artists) : t.AlbumArtist, t.Album, t.Year,
            t.DurationMs, t.ArtworkId, t.HasVideo)).ToList();
        return new SongPage(items, offset + items.Count < total ? offset + items.Count : null, total);
    }
}
