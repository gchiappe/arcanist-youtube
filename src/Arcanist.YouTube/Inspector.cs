using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Arcanist.Core.Application.Catalog;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Arcanist.YouTube;

/// <summary>A channel's tab the app offers.</summary>
public sealed record ChannelTab(string Id, string Title);

/// <summary>A song of a list (playlist, album, channel tab).</summary>
/// <param name="Index">Its place in the list, from 1.</param>
/// <param name="Available">False for private or deleted videos: they cannot be downloaded.</param>
/// <param name="InLibrary">The user already has it: downloaded with this extension, or a song with its title and
/// artist is in their libraries. The app leaves those unchosen.</param>
public sealed record LinkEntry(
    string Id,
    string Url,
    int Index,
    string? Title,
    string? Uploader,
    double? Duration,
    string? Thumbnail,
    bool Available,
    bool InLibrary,
    SongTags Tags);

/// <summary>What a link has, for the app to show before downloading anything.</summary>
/// <param name="ListUrl">A video opened inside a list: the list, so the user can take one or the other.</param>
/// <param name="Thumbnail">The largest picture (signed link through this server).</param>
/// <param name="Avatar">The channel's picture, when known.</param>
/// <param name="Date">Published (YYYY-MM-DD).</param>
/// <param name="EntryCount">How many songs the list has, when YouTube says.</param>
/// <param name="Tags">For a video: the tags proposed.</param>
/// <param name="Chapters">For a video: how many chapters it has (it can be split at them).</param>
/// <param name="Tabs">For a channel: its tabs; <c>tab</c> is the one listed.</param>
/// <param name="NextCursor">More songs: pass as <c>after</c>.</param>
/// <param name="Truncated">A mix (endless) cut to its first songs.</param>
public sealed record LinkPreview(
    string Url,
    LinkKind Kind,
    string? Id,
    bool Music,
    string? ListUrl,
    string? Title,
    string? Uploader,
    string? Thumbnail,
    string? Avatar,
    double? Duration,
    string? Date,
    long? Views,
    int? EntryCount,
    SongTags? Tags,
    int Chapters,
    IReadOnlyList<ChannelTab>? Tabs,
    string? Tab,
    IReadOnlyList<LinkEntry>? Entries,
    string? NextCursor,
    bool Truncated,
    bool InLibrary);

/// <summary>
/// Reads links for the app: a video's details with the tags proposed, or a list's songs a page at a time (a channel
/// can have thousands), marking those the user already has.
/// </summary>
public sealed class Inspector(YtDlp ytDlp, Images images, DownloadQueue downloads, SettingsStore settings, IServiceProvider services)
{
    public const int PageSize = 100;
    public const int MixLimit = 50;

    public static ClassifiedLink Classify(string? url, YouTubeSettings settings) =>
        UrlClassifier.Classify(url, settings.OtherSites, out var problem)
        ?? throw (problem == LinkProblem.NotAllowed
            ? new ExtensionException(ErrorCodes.SiteNotAllowed, 400)
            : new ExtensionException(ErrorCodes.InvalidLink, 400));

    public async Task<LinkPreview> InspectAsync(Guid user, string? url, string? tab, string? after, CancellationToken cancellationToken)
    {
        using var acting = YtDlp.ActingFor(user);
        var link = Classify(url, settings.Current);
        var start = Cursor(after);

        // A channel lists one tab at a time: the one asked for, the one in the link, or its releases (then videos).
        IReadOnlyList<ChannelTab>? tabs = null;
        var target = link.Url;
        var chosenTab = (string?)null;
        if (link.Kind == LinkKind.Channel)
        {
            tabs = await TabsAsync(link, cancellationToken);
            chosenTab = new[] { tab, link.Tab }.FirstOrDefault(t => t is not null && tabs.Any(x => x.Id == t))
                        ?? tabs.FirstOrDefault(t => t.Id == "releases")?.Id ?? tabs.FirstOrDefault(t => t.Id == "videos")?.Id ?? tabs.FirstOrDefault()?.Id;
            if (chosenTab is not null)
                target = UrlClassifier.ChannelTab(link, chosenTab);
        }

        var last = link.Kind == LinkKind.Mix ? Math.Min(start + PageSize - 1, MixLimit) : start + PageSize - 1;
        var info = await ytDlp.InspectAsync(target, start, last, cancellationToken);
        var isList = info["_type"]?.GetValue<string>() is "playlist" || info["entries"] is JsonArray;

        var title = TagGuesser.Text(info, "title");
        var uploader = TagGuesser.CleanChannel(TagGuesser.Text(info, "channel") ?? TagGuesser.Text(info, "uploader"));
        var date = TagGuesser.Text(info, "release_date") ?? TagGuesser.Text(info, "upload_date");
        var count = TagGuesser.Number(info, "playlist_count") is { } c ? (int)c : (int?)null;
        if (!isList)
        {
            var tags = TagGuesser.Guess(info);
            var have = await HaveAsync(user, [(link.Id ?? TagGuesser.Text(info, "id") ?? "", tags)], cancellationToken);
            return new LinkPreview(link.Url, link.Kind == LinkKind.Other ? LinkKind.Other : LinkKind.Video, link.Id ?? TagGuesser.Text(info, "id"),
                link.Music, link.ListUrl, title, uploader, images.Link(Thumbnail(info, large: true)), null, TagGuesser.Number(info, "duration"),
                FormatDate(date), (long?)TagGuesser.Number(info, "view_count"), null, tags, (info["chapters"] as JsonArray)?.Count ?? 0,
                null, null, null, null, false, have.Count > 0);
        }

        var album = link.Kind == LinkKind.Album ? AlbumTitle(title) : null;
        var raw = (info["entries"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var entries = raw.Select((entry, i) =>
        {
            var id = TagGuesser.Text(entry, "id") ?? "";
            var entryTitle = TagGuesser.Text(entry, "title");
            var available = !string.IsNullOrEmpty(id) && entryTitle is not ("[Private video]" or "[Deleted video]")
                            && TagGuesser.Text(entry, "availability") is not ("private" or "needs_auth" or "subscriber_only" or "premium_only");
            var index = start + i;
            // Its own link; for YouTube, made from its id when the list does not give one.
            var url = new[] { TagGuesser.Text(entry, "url"), TagGuesser.Text(entry, "webpage_url") }.FirstOrDefault(u => u?.StartsWith("http", StringComparison.Ordinal) == true)
                      ?? (link.Kind == LinkKind.Other ? null
                          : link.Music ? $"https://music.youtube.com/watch?v={id}" : $"https://www.youtube.com/watch?v={id}");
            available &= url is not null;
            return new LinkEntry(id, url ?? "", index, entryTitle,
                TagGuesser.CleanChannel(TagGuesser.Text(entry, "channel") ?? TagGuesser.Text(entry, "uploader")),
                TagGuesser.Number(entry, "duration"), images.Link(Thumbnail(entry, large: false)), available, false,
                TagGuesser.Guess(entry, album, album is null ? null : index));
        }).ToList();

        var owned = await HaveAsync(user, entries.Select(e => (e.Id, e.Tags)).ToList(), cancellationToken);
        entries = entries.Select(e => owned.Contains(e.Id) ? e with { InLibrary = true } : e).ToList();

        var truncated = link.Kind == LinkKind.Mix && raw.Count >= MixLimit - start + 1;
        var more = link.Kind == LinkKind.Mix ? last < MixLimit && raw.Count == PageSize
            : count is { } total ? last < total : raw.Count == PageSize;
        // A list of another site is a playlist to the app.
        return new LinkPreview(link.Url, link.Kind == LinkKind.Other ? LinkKind.Playlist : link.Kind, link.Id, link.Music, null, album ?? title, uploader, images.Link(Thumbnail(info, large: true)),
            images.Link(Avatar(info)), null, FormatDate(date), (long?)TagGuesser.Number(info, "view_count"), link.Kind == LinkKind.Mix ? Math.Min(count ?? MixLimit, MixLimit) : count,
            null, 0, tabs, chosenTab, entries, more ? Encode(last + 1) : null, truncated, false);
    }

    /// <summary>The tabs a channel has (asking for the channel itself lists them).</summary>
    private async Task<IReadOnlyList<ChannelTab>> TabsAsync(ClassifiedLink channel, CancellationToken cancellationToken)
    {
        var info = await ytDlp.InspectAsync(channel.Url, 1, 10, cancellationToken);
        var found = (info["entries"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(e => TagGuesser.Text(e, "url") ?? TagGuesser.Text(e, "webpage_url") ?? "")
            .Select(u => u.TrimEnd('/').Split('/')[^1].ToLowerInvariant())
            .Where(UrlClassifier.ChannelTabs.Contains)
            .ToHashSet();
        // A channel page that lists videos rather than tabs: its videos.
        if (found.Count == 0)
            found.Add("videos");
        return UrlClassifier.ChannelTabs.Where(found.Contains).Select(id => new ChannelTab(id, id)).ToList();
    }

    /// <summary>The ids of those the user already has: downloaded here, or with the same title and artist in a library.</summary>
    private async Task<HashSet<string>> HaveAsync(Guid user, IReadOnlyList<(string Id, SongTags Tags)> songs, CancellationToken cancellationToken)
    {
        var have = downloads.DownloadedIds(user, songs.Select(s => s.Id)).ToHashSet(StringComparer.Ordinal);
        var candidates = songs.Where(s => !have.Contains(s.Id) && s.Tags.Title is not null && s.Tags.Artist is not null)
            .Select(s => new TrackCandidate(s.Id, null, null, null, s.Tags.Title, s.Tags.Artist, s.Tags.Album, null, null, null))
            .Take(TrackMatchService.MaxCandidates).ToList();
        if (candidates.Count > 0)
        {
            try
            {
                var matches = await services.GetRequiredService<TrackMatchService>().MatchAsync(new MatchTracksInput(candidates, null), cancellationToken);
                have.UnionWith(matches.Where(m => m.TrackId is not null).Select(m => m.Key));
            }
            catch (Arcanist.Core.Application.ArcanistException)
            {
                // Matching is a nicety: without it, nothing is marked.
            }
        }

        return have;
    }

    /// <summary>"Album - Title" lists of YouTube Music are named "Album - Album" sometimes; others "Album: Title".</summary>
    private static string? AlbumTitle(string? title) =>
        title is null ? null : title.StartsWith("Album - ", StringComparison.Ordinal) ? title["Album - ".Length..] : title;

    /// <summary>The best picture of a video or list: the largest, or one around 480 px wide for list rows.</summary>
    public static string? Thumbnail(JsonObject info, bool large)
    {
        var all = (info["thumbnails"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(t => TagGuesser.Text(t, "url") is not null
                        && !(TagGuesser.Text(t, "id")?.Contains("banner", StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(t => (Url: TagGuesser.Text(t, "url")!, Width: TagGuesser.Number(t, "width") ?? 0))
            .ToList();
        if (all.Count == 0)
            return TagGuesser.Text(info, "thumbnail");
        return large
            ? all.MaxBy(t => t.Width).Url
            : all.Where(t => t.Width >= 320).MinBy(t => t.Width).Url ?? all.MaxBy(t => t.Width).Url;
    }

    private static string? Avatar(JsonObject info) =>
        (info["thumbnails"] as JsonArray ?? []).OfType<JsonObject>()
        .Where(t => TagGuesser.Text(t, "id")?.Contains("avatar", StringComparison.OrdinalIgnoreCase) == true)
        .Select(t => TagGuesser.Text(t, "url")).FirstOrDefault(u => u is not null);

    private static string? FormatDate(string? date) =>
        date is { Length: 8 } && DateOnly.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private static string Encode(int start) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(start.ToString(CultureInfo.InvariantCulture)));

    private static int Cursor(string? after)
    {
        if (string.IsNullOrEmpty(after))
            return 1;
        try
        {
            return int.TryParse(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(after)), NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start >= 1
                ? start
                : throw new ExtensionException(ErrorCodes.InvalidLink, 400);
        }
        catch (FormatException)
        {
            throw new ExtensionException(ErrorCodes.InvalidLink, 400);
        }
    }
}
