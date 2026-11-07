using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Arcanist.YouTube;

public enum MusicItemKind
{
    /// <summary>A song (YouTube Music's own audio track).</summary>
    Song,

    /// <summary>A video: a music video, a live performance, an upload.</summary>
    Video,

    /// <summary>An album, single or EP (<c>label</c> says which, as YouTube Music names it).</summary>
    Album,

    Artist,

    Playlist,
}

public sealed record MusicArtistRef(string Name, string? Id);

/// <summary>Something found on YouTube Music.</summary>
/// <param name="Id">The video's id, the album's or artist's browse id, or the playlist's id.</param>
/// <param name="Url">The link the app inspects (and downloads) for a song, video or playlist; null for an album (ask for
/// <c>explore/album</c>) or an artist (<c>explore/artist</c>).</param>
/// <param name="Label">How YouTube Music calls it, in the language asked ("Single", "EP", "Álbum"…), when it says.</param>
/// <param name="InLibrary">The user has it: a song with its name and artist, or an album with its name and artist.</param>
/// <param name="Owned">For a song or video: the copies the user has.</param>
public sealed record MusicItem(
    MusicItemKind Kind,
    string Id,
    string Title,
    IReadOnlyList<MusicArtistRef> Artists,
    string? Album,
    string? AlbumId,
    int? Year,
    double? Duration,
    string? Thumbnail,
    string? Url,
    string? Label,
    string? Detail,
    bool InLibrary = false,
    IReadOnlyList<OwnedCopy>? Owned = null);

/// <summary>A group of items, as YouTube Music shows them ("Albums", "Fans also like"…).</summary>
public sealed record MusicSection(string Title, IReadOnlyList<MusicItem> Items);

public sealed record MusicArtistPage(string Id, string Name, string? Thumbnail, string? Description, IReadOnlyList<MusicSection> Sections);

/// <param name="Countries">ISO codes of the countries with charts (<c>ZZ</c> is the whole world); the app names them.</param>
public sealed record MusicCharts(string Country, IReadOnlyList<string> Countries, IReadOnlyList<MusicSection> Sections);

public sealed record AlbumLink(string Url);

/// <summary>
/// Reads YouTube Music (search, artists, albums, new releases and charts) through the API its own web app uses, so
/// the app can explore music to bring in. Nothing here downloads audio: what the user chooses goes through yt-dlp
/// like a pasted link. Answers are kept for a while: the same search or page does not ask YouTube again.
/// </summary>
public sealed partial class YouTubeMusic(IHttpClientFactory clients, IConfiguration configuration, Images images, ILogger<YouTubeMusic> logger)
{
    public const string HttpClientName = "youtube-music";

    private const string SongsParams = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
    private const string VideosParams = "EgWKAQIQAWoKEAkQBRAKEAMQBA%3D%3D";
    private const string AlbumsParams = "EgWKAQIYAWoKEAkQBRAKEAMQBA%3D%3D";
    private const string ArtistsParams = "EgWKAQIgAWoKEAkQBRAKEAMQBA%3D%3D";
    private const string PlaylistsParams = "EgeKAQQoADgBagwQDhAKEAMQBBAJEAU%3D";

    private readonly ConcurrentDictionary<string, (DateTime Expires, JsonObject Value)> _cache = new();

    private string BaseUrl => (configuration["MusicApiUrl"] ?? "https://music.youtube.com/youtubei/v1/").TrimEnd('/') + "/";

    /// <summary>One kind of results, or for <c>all</c> the best songs, albums and artists together.</summary>
    public async Task<IReadOnlyList<MusicSection>> SearchAsync(string query, string kind, string language, string country, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length is 0 or > 200)
            throw new ExtensionException(ErrorCodes.InvalidLink, 400);

        async Task<IReadOnlyList<MusicItem>> One(string param) =>
            Items(await PostAsync("search", new JsonObject { ["query"] = query, ["params"] = Uri.UnescapeDataString(param) }, language, country, TimeSpan.FromMinutes(15),
                cancellationToken));

        return kind switch
        {
            "songs" => [new MusicSection("songs", await One(SongsParams))],
            "videos" => [new MusicSection("videos", await One(VideosParams))],
            "albums" => [new MusicSection("albums", await One(AlbumsParams))],
            "artists" => [new MusicSection("artists", await One(ArtistsParams))],
            "playlists" => [new MusicSection("playlists", await One(PlaylistsParams))],
            _ => await AllAsync(),
        };

        async Task<IReadOnlyList<MusicSection>> AllAsync()
        {
            var artists = One(ArtistsParams);
            var songs = One(SongsParams);
            var albums = One(AlbumsParams);
            await Task.WhenAll(artists, songs, albums);
            return [new MusicSection("artists", (await artists).Take(4).ToList()), new MusicSection("songs", (await songs).Take(10).ToList()),
                new MusicSection("albums", (await albums).Take(8).ToList())];
        }
    }

    /// <summary>An artist's page: their songs, albums, singles, videos and the artists their fans also like.</summary>
    public async Task<MusicArtistPage> ArtistAsync(string id, string language, string country, CancellationToken cancellationToken)
    {
        if (!BrowseId().IsMatch(id))
            throw new ExtensionException(ErrorCodes.InvalidLink, 400);
        var page = await PostAsync("browse", new JsonObject { ["browseId"] = id }, language, country, TimeSpan.FromHours(6), cancellationToken);
        var header = Find(page, "musicImmersiveHeaderRenderer") ?? Find(page, "musicVisualHeaderRenderer");
        var name = Text(header?["title"]) ?? "";
        var description = Text(header?["description"]);
        var thumbnail = Largest(header?["thumbnail"]);
        var sections = new List<MusicSection>();
        foreach (var shelf in Shelves(page))
        {
            // The artist's own albums and songs do not name them: they are theirs.
            var items = Items(shelf.Content).Select(i => i.Kind is MusicItemKind.Album or MusicItemKind.Song or MusicItemKind.Video && i.Artists.Count == 0
                ? i with { Artists = [new MusicArtistRef(name, id)] }
                : i).ToList();
            if (items.Count > 0)
                sections.Add(new MusicSection(shelf.Title ?? "", items));
        }

        return new MusicArtistPage(id, name, images.Link(thumbnail), description is { Length: > 600 } ? description[..600].TrimEnd() + "…" : description, sections);
    }

    /// <summary>An album's list (its playlist), which the app inspects and downloads like a pasted album.</summary>
    public async Task<AlbumLink> AlbumAsync(string id, string language, string country, CancellationToken cancellationToken)
    {
        if (!id.StartsWith("MPREb_", StringComparison.Ordinal) || !BrowseId().IsMatch(id))
            throw new ExtensionException(ErrorCodes.InvalidLink, 400);
        var page = await PostAsync("browse", new JsonObject { ["browseId"] = id }, language, country, TimeSpan.FromDays(1), cancellationToken);
        var list = AlbumPlaylist().Match(page.ToJsonString());
        return list.Success
            ? new AlbumLink($"https://music.youtube.com/playlist?list={list.Groups[1].Value}")
            : throw new ExtensionException(ErrorCodes.Empty, 404);
    }

    /// <summary>The albums and singles just out, in a country.</summary>
    public async Task<IReadOnlyList<MusicItem>> NewReleasesAsync(string language, string country, CancellationToken cancellationToken)
    {
        var page = await PostAsync("browse", new JsonObject { ["browseId"] = "FEmusic_new_releases_albums" }, language, country, TimeSpan.FromHours(6),
            cancellationToken);
        return Items(page);
    }

    /// <summary>A country's charts (or the world's, <c>ZZ</c>): its lists of the most played and the artists most heard.</summary>
    public async Task<MusicCharts> ChartsAsync(string language, string country, CancellationToken cancellationToken)
    {
        var page = await PostAsync("browse", new JsonObject
        {
            ["browseId"] = "FEmusic_charts",
            ["formData"] = new JsonObject { ["selectedValues"] = new JsonArray(country) },
        }, language, country, TimeSpan.FromHours(6), cancellationToken);
        var countries = OpaqueToken().Matches(page.ToJsonString()).Select(m => m.Groups[1].Value).Distinct().ToList();
        var sections = Shelves(page).Select(s => new MusicSection(s.Title ?? "", Items(s.Content))).Where(s => s.Items.Count > 0).ToList();
        return new MusicCharts(country, countries, sections);
    }

    // —— The requests ——

    private async Task<JsonObject> PostAsync(string endpoint, JsonObject body, string language, string country, TimeSpan keep,
        CancellationToken cancellationToken)
    {
        body["context"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["clientName"] = "WEB_REMIX",
                // YouTube Music's web app names its version after the day it was built; yesterday's is always accepted.
                ["clientVersion"] = "1." + DateTime.UtcNow.AddDays(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".01.00",
                ["hl"] = language,
                ["gl"] = country,
            },
        };
        var key = endpoint + body.ToJsonString();
        if (_cache.TryGetValue(key, out var kept) && kept.Expires > DateTime.UtcNow)
            return kept.Value;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpoint + "?prettyPrint=false") { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is 403 or 429)
                throw new ExtensionException(ErrorCodes.BlockedByYouTube, 503);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("YouTube Music answered {Status} to {Endpoint}", (int)response.StatusCode, endpoint);
                throw new ExtensionException(ErrorCodes.ExploreFailed, 502);
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? throw new ExtensionException(ErrorCodes.ExploreFailed, 502);
            if (_cache.Count > 500)
                foreach (var old in _cache.Where(c => c.Value.Expires <= DateTime.UtcNow).Select(c => c.Key).ToList())
                    _cache.TryRemove(old, out _);
            _cache[key] = (DateTime.UtcNow + keep, json);
            return json;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not reach YouTube Music ({Endpoint})", endpoint);
            throw new ExtensionException(ErrorCodes.ExploreFailed, 502);
        }
    }

    // —— Reading the answers ——

    /// <summary>Every item (list rows and cards) under a node, in order, once each.</summary>
    internal List<MusicItem> Items(JsonNode? node)
    {
        var found = new List<MusicItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (kind, renderer) in Renderers(node))
        {
            var item = kind == "musicResponsiveListItemRenderer" ? ListItem(renderer) : CardItem(renderer);
            if (item is not null && seen.Add(item.Kind + item.Id))
                found.Add(item);
        }

        return found;
    }

    private static IEnumerable<(string Kind, JsonObject Renderer)> Renderers(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o)
                {
                    if (name is "musicResponsiveListItemRenderer" or "musicTwoRowItemRenderer" && value is JsonObject r)
                    {
                        yield return (name, r);
                        continue;
                    }

                    // Not into menus: they hold other songs ("Go to album", radio…).
                    if (name is "menu" or "frameworkUpdates" or "header" or "responseContext")
                        continue;
                    foreach (var inner in Renderers(value))
                        yield return inner;
                }

                break;
            case JsonArray a:
                foreach (var value in a)
                foreach (var inner in Renderers(value))
                    yield return inner;
                break;
        }
    }

    /// <summary>The shelves of a page (titled groups), in order.</summary>
    private static IEnumerable<(string? Title, JsonNode Content)> Shelves(JsonNode page)
    {
        foreach (var node in Descendants(page))
        {
            if (node is not JsonObject o)
                continue;
            if (o["musicCarouselShelfRenderer"] is JsonObject carousel)
                yield return (Text(carousel["header"]?["musicCarouselShelfBasicHeaderRenderer"]?["title"]), carousel["contents"]!);
            else if (o["musicShelfRenderer"] is JsonObject shelf && shelf["contents"] is { } contents)
                yield return (Text(shelf["title"]), contents);
            else if (o["gridRenderer"] is JsonObject grid && grid["items"] is { } items)
                yield return (Text(grid["header"]?["gridHeaderRenderer"]?["title"]), items);
        }
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        var stack = new Stack<JsonNode>();
        stack.Push(node);
        var order = new List<JsonNode>();
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            order.Add(current);
            var children = current switch
            {
                JsonObject o => o.Where(p => p.Value is not null && p.Key is not ("musicCarouselShelfRenderer" or "musicShelfRenderer" or "gridRenderer" or "frameworkUpdates"))
                    .Select(p => p.Value!).ToList(),
                JsonArray a => a.Where(v => v is not null).Select(v => v!).ToList(),
                _ => [],
            };
            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }

        return order;
    }

    private static JsonObject? Find(JsonNode node, string name) =>
        Descendants(node).OfType<JsonObject>().Select(o => o[name] as JsonObject).FirstOrDefault(o => o is not null);

    /// <summary>A row: a song, video, album, artist or playlist, with its details in its columns.</summary>
    private MusicItem? ListItem(JsonObject r)
    {
        var columns = (r["flexColumns"] as JsonArray ?? []).Select(c => c?["musicResponsiveListItemFlexColumnRenderer"]?["text"]).ToList();
        var titleRun = (columns.FirstOrDefault()?["runs"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var title = Text(columns.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var runs = columns.Skip(1).SelectMany(c => c?["runs"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var fixedText = (r["fixedColumns"] as JsonArray ?? []).Select(c => Text(c?["musicResponsiveListItemFixedColumnRenderer"]?["text"])).OfType<string>();
        var videoId = (string?)r["playlistItemData"]?["videoId"] ?? (string?)titleRun?["navigationEndpoint"]?["watchEndpoint"]?["videoId"];
        var videoType = (string?)titleRun?["navigationEndpoint"]?["watchEndpoint"]?["watchEndpointMusicSupportedConfigs"]?["watchEndpointMusicConfig"]?["musicVideoType"];
        var thumbnail = Largest(r["thumbnail"]);
        return Build(title, videoId, videoType, r["navigationEndpoint"], runs, fixedText, thumbnail);
    }

    /// <summary>A card: an album, single, artist, playlist or video.</summary>
    private MusicItem? CardItem(JsonObject r)
    {
        var title = Text(r["title"]);
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var endpoint = r["navigationEndpoint"] ?? (r["title"]?["runs"] as JsonArray)?.FirstOrDefault()?["navigationEndpoint"];
        var videoId = (string?)endpoint?["watchEndpoint"]?["videoId"];
        var videoType = (string?)endpoint?["watchEndpoint"]?["watchEndpointMusicSupportedConfigs"]?["watchEndpointMusicConfig"]?["musicVideoType"];
        var runs = (r["subtitle"]?["runs"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var thumbnail = Largest(r["thumbnailRenderer"]);
        return Build(title, videoId, videoType, endpoint, runs, [], thumbnail);
    }

    private MusicItem? Build(string title, string? videoId, string? videoType, JsonNode? endpoint, List<JsonObject> runs, IEnumerable<string> fixedText,
        string? thumbnail)
    {
        var artists = new List<MusicArtistRef>();
        string? album = null, albumId = null, label = null;
        int? year = null;
        double? duration = null;
        var details = new List<string>();
        foreach (var run in runs)
        {
            var text = ((string?)run["text"])?.Trim();
            if (string.IsNullOrEmpty(text) || text is "•" or "&" or "," || Joiner().IsMatch(text))
                continue;
            var browse = run["navigationEndpoint"]?["browseEndpoint"];
            var pageType = (string?)browse?["browseEndpointContextSupportedConfigs"]?["browseEndpointContextMusicConfig"]?["pageType"];
            var id = (string?)browse?["browseId"];
            if (pageType is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL")
                artists.Add(new MusicArtistRef(text, pageType == "MUSIC_PAGE_TYPE_ARTIST" ? id : null));
            else if (pageType == "MUSIC_PAGE_TYPE_ALBUM")
                (album, albumId) = (text, id);
            else if (Duration(text) is { } seconds)
                duration = seconds;
            else if (text.Length == 4 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is > 1900 and < 2200)
                year = y;
            else if (run["navigationEndpoint"] is null && label is null && artists.Count == 0 && details.Count == 0 && !Digits().IsMatch(text))
                label = text;
            else if (run["navigationEndpoint"] is null)
                details.Add(text);
        }

        foreach (var text in fixedText)
            duration ??= Duration(text);

        // An artist named without a link (a song by someone with no artist page): the first plain text is them.
        var image = images.Link(thumbnail);
        var detail = details.Count > 0 ? string.Join(" • ", details) : null;
        if (videoId is not null)
        {
            if (artists.Count == 0 && label is not null)
                (artists, label) = ([new MusicArtistRef(label, null)], null);
            var song = videoType is null or "MUSIC_VIDEO_TYPE_ATV" && (album is not null || videoType is not null);
            return new MusicItem(song ? MusicItemKind.Song : MusicItemKind.Video, videoId, title, artists, album, albumId, year, duration, image,
                song ? $"https://music.youtube.com/watch?v={videoId}" : $"https://www.youtube.com/watch?v={videoId}", label, detail);
        }

        var target = endpoint?["browseEndpoint"];
        var browseId = (string?)target?["browseId"];
        var type = (string?)target?["browseEndpointContextSupportedConfigs"]?["browseEndpointContextMusicConfig"]?["pageType"];
        if (browseId is null)
            return null;
        return type switch
        {
            "MUSIC_PAGE_TYPE_ALBUM" => new MusicItem(MusicItemKind.Album, browseId, title, artists, null, null, year, null, image, null, label, detail),
            // "Artist • 1.9M monthly listeners": the kind is known, the audience is what helps choose.
            "MUSIC_PAGE_TYPE_ARTIST" => new MusicItem(MusicItemKind.Artist, browseId, title, [], null, null, null, null, image, null, null, detail),
            "MUSIC_PAGE_TYPE_PLAYLIST" when browseId.StartsWith("VL", StringComparison.Ordinal) => new MusicItem(MusicItemKind.Playlist, browseId[2..], title,
                artists, null, null, null, null, image, $"https://music.youtube.com/playlist?list={browseId[2..]}", label, detail),
            _ => null,
        };
    }

    // —— Small readers ——

    internal static string? Text(JsonNode? text) =>
        text is null ? null
        : text["runs"] is JsonArray runs ? string.Concat(runs.Select(r => (string?)r?["text"]))
        : (string?)text["simpleText"];

    /// <summary>The largest picture under a node.</summary>
    private static string? Largest(JsonNode? node) =>
        node is null ? null
        : Descendants(node).OfType<JsonObject>()
            .Where(o => o["url"] is JsonValue && o["width"] is JsonValue)
            .MaxBy(o => (int?)o["width"] ?? 0)?["url"]?.GetValue<string>() is { } url
            ? Sized(url)
            : null;

    /// <summary>YouTube's pictures come in any size the link asks for: 544 px is enough for a card and a cover.</summary>
    private static string Sized(string url) => SizeSuffix().IsMatch(url) ? SizeSuffix().Replace(url, "=w544-h544-l90-rj") : url;

    private static double? Duration(string text)
    {
        if (!DurationText().IsMatch(text))
            return null;
        var parts = text.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return parts.Aggregate(0, (total, part) => total * 60 + part);
    }

    [GeneratedRegex(@"^(\d{1,2}:)?\d{1,2}:\d{2}$")]
    private static partial Regex DurationText();

    [GeneratedRegex(@"^\s*(y|and|e|et|und|&)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Joiner();

    [GeneratedRegex(@"\d")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{4,64}$")]
    private static partial Regex BrowseId();

    [GeneratedRegex(@"""(?:audioPlaylistId|playlistId)"":""(OLAK5uy_[A-Za-z0-9_-]+)""")]
    private static partial Regex AlbumPlaylist();

    [GeneratedRegex(@"""opaqueToken"":""([A-Z]{2})""")]
    private static partial Regex OpaqueToken();

    [GeneratedRegex(@"=w\d+-h\d+[^/]*$")]
    private static partial Regex SizeSuffix();
}
