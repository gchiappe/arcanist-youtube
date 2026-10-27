using System.Text;
using System.Text.Json;
using Arcanist.Core.Application;
using Arcanist.Core.Application.Libraries;
using Arcanist.Core.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;

namespace Arcanist.YouTube;

/// <summary>The library a user can bring songs into.</summary>
public sealed record TargetLibrary(Guid Id, string Name);

public sealed record FfmpegStatus(bool Installed, string? Version);

/// <param name="Formats">The formats that can be chosen now (converting needs ffmpeg).</param>
/// <param name="Settings">The administrator's settings (only <c>otherSites</c>, the limits and the default format for other users).</param>
/// <param name="Cookies">For administrators: YouTube cookies are set.</param>
public sealed record StatusModel(
    bool Ready,
    bool IsAdmin,
    bool Cookies,
    IReadOnlyList<ToolStatus> Tools,
    FfmpegStatus Ffmpeg,
    IReadOnlyList<AudioFormat> Formats,
    IReadOnlyList<TargetLibrary> Libraries,
    YouTubeSettings Settings,
    string Version,
    bool MusicVideos = true);

/// <summary>What to download.</summary>
/// <param name="Url">The link, as pasted.</param>
/// <param name="Entries">For a list: the songs chosen (as <c>inspect</c> gave them). Leave out with <c>all</c>.</param>
/// <param name="All">For a list: all of its songs (the server lists them), but those in <c>exclude</c>.</param>
/// <param name="Tab">For a channel: the tab (<c>videos</c>, <c>releases</c>…).</param>
/// <param name="Tags">For a single video: the tags to save it with (null fields are proposed).</param>
/// <param name="Album">For a list: the album every song gets (an album's own name by default).</param>
/// <param name="Title">What the download is called in the queue (the link's title, as inspect gave it).</param>
/// <param name="Video">For a single video: bring its music video too, next to the song.</param>
/// <param name="VideoFor">Only the video of the link, as the music video of this track of the library (it goes next to it).</param>
public sealed record DownloadRequest(
    string Url,
    Guid LibraryId,
    AudioFormat? Format = null,
    AudioQuality? Quality = null,
    bool? EmbedArtwork = null,
    bool? SquareArtwork = null,
    bool? SplitChapters = null,
    bool? NumberTracks = null,
    string? Tab = null,
    bool? All = null,
    IReadOnlyList<RequestedEntry>? Entries = null,
    IReadOnlyList<string>? Exclude = null,
    SongTags? Tags = null,
    string? Album = null,
    string? AlbumArtist = null,
    bool? WholeList = null,
    string? Title = null,
    bool? Video = null,
    Guid? VideoFor = null);

public sealed record PreviewLink(string Url);

public sealed record PreviewRequest(string Url);

public sealed record InstallRequest(ToolKind? Tool);

/// <summary>
/// The extension's REST API, under <c>/ext/youtube/api/v1</c> (Bearer token, as Arcanist's API). Everything needs the
/// UPLOAD permission; settings and tools are for administrators. Its contract is described in the repository's
/// README and in <c>/ext/youtube/api/v1/openapi.json</c>.
/// </summary>
public static class Api
{
    public static void Map(IEndpointRouteBuilder endpoints, string basePath)
    {
        var api = endpoints.MapGroup(basePath + "/api/v1");
        // Expected failures as Arcanist's API gives them: problem details with the code (and the fields' codes).
        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (ArcanistException e)
            {
                var extensions = new Dictionary<string, object?> { ["code"] = e.Code };
                if (e is ArcanistException.Invalid invalid)
                    extensions["errors"] = invalid.Errors.ToDictionary();
                return Results.Problem(statusCode: e.StatusCode, title: e.Code, extensions: extensions);
            }
        });

        api.MapGet("/status", StatusAsync);

        api.MapGet("/inspect", (string? url, string? tab, string? after, AccessGuard guard, Inspector inspector, CancellationToken ct) =>
            inspector.InspectAsync(guard.Permission(Permissions.Upload).UserId, url, tab, after, ct));

        api.MapPost("/downloads", DownloadAsync);

        // A library song's music video: candidates on YouTube, the likeliest first.
        api.MapGet("/video-search", (Guid trackId, AccessGuard guard, VideoSearch search, CancellationToken ct) =>
        {
            guard.Permission(Permissions.Upload);
            return search.SearchAsync(trackId, ct);
        });

        // The songs whose tracks were still unknown are looked up first (their library may have been scanned since).
        api.MapGet("/downloads", async (AccessGuard guard, DownloadQueue queue, CancellationToken ct) =>
        {
            var user = guard.Permission(Permissions.Upload).UserId;
            await queue.ResolveTracksAsync(user, ct);
            return queue.Of(user);
        });

        api.MapGet("/downloads/{id:guid}", (Guid id, AccessGuard guard, DownloadQueue queue) =>
            queue.Find(guard.Permission(Permissions.Upload).UserId, id) ?? throw new ArcanistException.NotFound());

        api.MapPost("/downloads/{id:guid}/cancel", (Guid id, Guid? item, AccessGuard guard, DownloadQueue queue) =>
            queue.Cancel(guard.Permission(Permissions.Upload).UserId, id, item));

        api.MapPost("/downloads/{id:guid}/retry", (Guid id, Guid? item, AccessGuard guard, DownloadQueue queue) =>
            queue.Retry(guard.Permission(Permissions.Upload).UserId, id, item));

        api.MapDelete("/downloads/{id:guid}", (Guid id, AccessGuard guard, DownloadQueue queue) =>
        {
            queue.Remove(guard.Permission(Permissions.Upload).UserId, id);
            return Results.NoContent();
        });

        api.MapDelete("/downloads", (AccessGuard guard, DownloadQueue queue) =>
            Results.Ok(new { removed = queue.Clear(guard.Permission(Permissions.Upload).UserId) }));

        api.MapGet("/downloads/events", EventsAsync);

        api.MapPost("/preview", (PreviewRequest request, AccessGuard guard, SettingsStore settings, IDataProtectionProvider protection) =>
        {
            var user = guard.Permission(Permissions.Upload).UserId;
            var link = Inspector.Classify(request.Url, settings.Current);
            var ticket = protection.CreateProtector("Arcanist.YouTube.Preview").ToTimeLimitedDataProtector()
                .Protect($"{user}\n{link.Url}", TimeSpan.FromMinutes(30));
            return new PreviewLink($"{basePath}/api/v1/preview?ticket={Uri.EscapeDataString(ticket)}");
        });

        // The audio element cannot send a token: the ticket (30 minutes) is the authorization.
        api.MapGet("/preview", PreviewAsync);

        api.MapGet("/image/{token}", async (string token, Images images, HttpContext context, CancellationToken ct) =>
        {
            if (await images.GetAsync(token, ct) is not { } image)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "private, max-age=604800, immutable";
            return Results.File(image.Bytes, image.ContentType);
        });

        api.MapGet("/settings", (AccessGuard guard, SettingsStore settings) =>
        {
            guard.Admin();
            return settings.Current;
        });

        api.MapPut("/settings", (YouTubeSettings input, AccessGuard guard, SettingsStore settings, DownloadQueue queue) =>
        {
            guard.Admin();
            guard.RecentAuthentication();
            var saved = input.Normalized();
            settings.Save(saved);
            queue.Wake();
            return saved;
        });

        api.MapPost("/tools/install", async (InstallRequest? request, AccessGuard guard, ToolManager tools, CancellationToken ct) =>
        {
            guard.Admin();
            guard.RecentAuthentication();
            var kinds = request?.Tool is { } one ? [one] : new[] { ToolKind.YtDlp, ToolKind.Deno };
            var result = new List<ToolStatus>();
            foreach (var kind in kinds)
                result.Add(await tools.InstallAsync(kind, ct));
            return result;
        });

        // The cookies of a YouTube session (cookies.txt), as plain text; never returned.
        api.MapPut("/cookies", async (HttpRequest request, AccessGuard guard, CookieStore cookies) =>
        {
            guard.Admin();
            guard.RecentAuthentication();
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var buffer = new char[CookieStore.MaxBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            cookies.Save(new string(buffer, 0, read));
            return Results.NoContent();
        });

        api.MapDelete("/cookies", (AccessGuard guard, CookieStore cookies) =>
        {
            guard.Admin();
            guard.RecentAuthentication();
            cookies.Remove();
            return Results.NoContent();
        });

        api.MapGet("/me", (AccessGuard guard, CookieStore cookies, UserPreferences preferences) =>
        {
            var user = guard.Permission(Permissions.Upload).UserId;
            return new MyYouTube(cookies.PresentFor(user), preferences.Of(user).PlayWhenDone);
        });

        api.MapPut("/me/preferences", (MyPreferences input, AccessGuard guard, CookieStore cookies, UserPreferences preferences) =>
        {
            var user = guard.Permission(Permissions.Upload).UserId;
            preferences.Save(user, new UserPreference(input.PlayWhenDone));
            return new MyYouTube(cookies.PresentFor(user), input.PlayWhenDone);
        });

        // A user's own cookies (cookies.txt, as plain text), used for their links instead of the server's; never returned.
        api.MapPut("/me/cookies", async (HttpRequest request, AccessGuard guard, CookieStore cookies, UserPreferences preferences) =>
        {
            var user = guard.Permission(Permissions.Upload).UserId;
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var buffer = new char[CookieStore.MaxBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            cookies.Save(new string(buffer, 0, read), user);
            return new MyYouTube(true, preferences.Of(user).PlayWhenDone);
        });

        api.MapDelete("/me/cookies", (AccessGuard guard, CookieStore cookies, UserPreferences preferences) =>
        {
            var user = guard.Permission(Permissions.Upload).UserId;
            cookies.Remove(user);
            return new MyYouTube(false, preferences.Of(user).PlayWhenDone);
        });

        api.MapPost("/tools/check", async (AccessGuard guard, ToolManager tools, CancellationToken ct) =>
        {
            guard.Admin();
            var result = new List<ToolStatus>();
            foreach (var kind in Enum.GetValues<ToolKind>())
            {
                await tools.LatestAsync(kind, ct);
                result.Add(await tools.StatusAsync(kind, ct));
            }

            return result;
        });

        api.MapGet("/openapi.json", (ExtensionPaths paths) =>
            Results.File(Path.Combine(paths.WebRoot, "openapi.json"), "application/json"));
    }

    private static async Task<StatusModel> StatusAsync(AccessGuard guard, ToolManager tools, YtDlp ytDlp, Arcanist.Core.Media.FfmpegLocator ffmpeg,
        SettingsStore settings, LibraryService libraries, CookieStore cookies, CancellationToken ct)
    {
        var access = guard.Permission(Permissions.Upload);
        var statuses = new List<ToolStatus> { await tools.StatusAsync(ToolKind.YtDlp, ct), await tools.StatusAsync(ToolKind.Deno, ct) };
        var installation = await ffmpeg.GetAsync(ct);
        IReadOnlyList<AudioFormat> formats = installation is null ? [AudioFormat.Original] : Enum.GetValues<AudioFormat>();
        var targets = (await libraries.ListAsync(ct)).Where(l => l.CanUpload && !l.Disabled).Select(l => new TargetLibrary(l.Id, l.Name)).ToList();
        var current = settings.Current;
        return new StatusModel(statuses[0].Installed, access.IsAdmin, access.IsAdmin && cookies.Present, access.IsAdmin ? statuses : statuses.Select(s => s with { Path = null }).ToList(),
            new FfmpegStatus(installation is not null, access.IsAdmin ? installation?.Version : null), formats, targets, current,
            typeof(Api).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    }

    private static async Task<DownloadBatch> DownloadAsync(DownloadRequest request, AccessGuard guard, SettingsStore settings, LibraryService libraries,
        DownloadQueue queue, Images images, CancellationToken ct)
    {
        var access = guard.Permission(Permissions.Upload);
        var current = settings.Current;
        var link = Inspector.Classify(request.Url, current);
        var uploadable = (await libraries.ListAsync(ct)).Where(l => l.CanUpload && !l.Disabled).ToList();
        // A track's music video goes where the track is (the library's own check comes with the upload).
        var library = request.VideoFor is { } trackId
            ? uploadable.FirstOrDefault(l => l.Id == request.LibraryId) ?? uploadable.FirstOrDefault()
            : uploadable.FirstOrDefault(l => l.Id == request.LibraryId);
        if (library is null)
            throw ArcanistException.Field("libraryId", Arcanist.Core.Application.ErrorCodes.NotFound);

        // A video opened in a list: the list when asked for.
        var whole = request.WholeList == true && link.ListUrl is not null;
        var kind = whole ? LinkKind.Playlist : link.Kind;
        var url = whole ? link.ListUrl! : link.Url;
        var listUrl = kind == LinkKind.Channel && request.Tab is { } tab && UrlClassifier.ChannelTabs.Contains(tab) ? UrlClassifier.ChannelTab(link, tab) : url;
        // A page of another site is one song, unless songs of it (a list) were chosen.
        var single = kind == LinkKind.Video && !whole || kind == LinkKind.Other && request.Entries is null && request.All != true;
        if (kind == LinkKind.Other && !single)
            kind = LinkKind.Playlist;

        var options = new DownloadSettings(
            request.Format ?? current.DefaultFormat,
            request.Quality ?? AudioQuality.Best,
            request.EmbedArtwork ?? true,
            request.SquareArtwork ?? true,
            request.SplitChapters ?? false,
            request.NumberTracks ?? kind == LinkKind.Album,
            Clean(request.Album),
            Clean(request.AlbumArtist),
            request.Video == true && request.VideoFor is null);

        var now = DateTime.UtcNow;
        var batch = new DownloadBatch(Guid.CreateVersion7(), access.UserId, url, kind, null, null, library.Id, library.Name, options,
            BatchStatus.Active, null, now, now, [], request.Exclude?.Take(YouTubeSettings.MaxItemsLimit).ToList(), listUrl, request.VideoFor);
        if (request.VideoFor is not null && !single)
            throw new ExtensionException(ErrorCodes.InvalidLink, 400);

        if (single)
        {
            var tags = request.Tags is { } t
                ? new SongTags(Clean(t.Title), Clean(t.Artist), Clean(t.Album), Clean(t.AlbumArtist), t.Year is > 0 and < 3000 ? t.Year : null,
                    t.TrackNumber is > 0 and < 1000 ? t.TrackNumber : null, Clean(t.Genre))
                : null;
            var item = DownloadQueue.NewItem(new RequestedEntry(link.Id ?? url, url, null, tags?.Title, tags?.Artist, null), batch) with
            {
                Url = url,
                Tags = tags is { Title: not null } ? tags : null,
            };
            batch = batch with { Title = tags?.Title, Items = [item] };
        }
        else if (request.All == true)
        {
            batch = batch with { Status = BatchStatus.Listing };
        }
        else
        {
            var entries = (request.Entries ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Id)).DistinctBy(e => e.Id)
                .Take(Math.Clamp(current.MaxItems, 1, YouTubeSettings.MaxItemsLimit)).ToList();
            if (entries.Count == 0)
                throw new ExtensionException(ErrorCodes.NothingSelected, 400);
            batch = batch with { Items = entries.Select(e => DownloadQueue.NewItem(e, batch)).ToList() };
        }

        return queue.Add(batch with { Title = batch.Title ?? Clean(request.Album) ?? Clean(request.Title) });
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 256)];

    /// <summary>Server-sent events: each change of the user's downloads, as JSON, while the app is open.</summary>
    private static async Task EventsAsync(HttpContext context, AccessGuard guard, DownloadQueue queue, CancellationToken ct)
    {
        var user = guard.Permission(Permissions.Upload).UserId;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.WriteAsync(": ok\n\n", ct);
        await context.Response.Body.FlushAsync(ct);

        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(20));
        var changes = queue.WatchAsync(user, ct).GetAsyncEnumerator(ct);
        var next = changes.MoveNextAsync().AsTask();
        var tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var done = await Task.WhenAny(next, tick);
                if (done == tick)
                {
                    await context.Response.WriteAsync(": ping\n\n", ct);
                    tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
                }
                else
                {
                    if (!await next)
                        break;
                    var json = JsonSerializer.Serialize(changes.Current, Json.Options);
                    await context.Response.WriteAsync($"event: batch\ndata: {json}\n\n", ct);
                    next = changes.MoveNextAsync().AsTask();
                }

                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await changes.DisposeAsync();
        }
    }

    /// <summary>30 seconds of a video's audio as MP3, through this server (the browser never reaches YouTube).</summary>
    private static async Task PreviewAsync(HttpContext context, string ticket, double? start, IDataProtectionProvider protection, YtDlp ytDlp,
        CancellationToken ct)
    {
        string url;
        Guid user;
        try
        {
            var parts = protection.CreateProtector("Arcanist.YouTube.Preview").ToTimeLimitedDataProtector().Unprotect(ticket).Split('\n');
            url = parts[1];
            user = Guid.Parse(parts[0]);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IndexOutOfRangeException or FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var ffmpeg = await ytDlp.FfmpegAsync(ct);
        if (ffmpeg is null)
            throw new ExtensionException(ErrorCodes.FfmpegRequired, 503);
        using var acting = YtDlp.ActingFor(user);
        var stream = await ytDlp.AudioStreamAsync(url, ct);
        var offset = Math.Clamp(start ?? 0, 0, 24 * 3600);
        context.Response.ContentType = "audio/mpeg";
        context.Response.Headers.CacheControl = "no-store";
        await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-reconnect", "1", "-ss", offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "-t", "30", "-i", stream, "-vn", "-c:a", "libmp3lame", "-b:a", "128k", "-f", "mp3", "pipe:1"],
            null, TimeSpan.FromMinutes(2), ct, stdout: context.Response.Body);
    }

    /// <summary>The web app: its files, and its page for any other path under the extension.</summary>
    public static void MapApp(IEndpointRouteBuilder endpoints, string basePath, ExtensionPaths paths)
    {
        var types = new FileExtensionContentTypeProvider();
        // The optional catch-all also takes the extension's own path: without its slash, relative links would break.
        endpoints.MapGet(basePath + "/{**path}", (string? path, HttpContext context) =>
        {
            if (string.IsNullOrEmpty(path) && context.Request.Path.Value?.EndsWith('/') != true)
                return Results.Redirect(context.Request.PathBase + basePath + "/" + context.Request.QueryString);
            if (path?.StartsWith("api/", StringComparison.Ordinal) == true)
                return Results.NotFound();
            var root = Path.GetFullPath(paths.WebRoot);
            var file = Path.GetFullPath(Path.Combine(root, path ?? ""));
            if (!file.StartsWith(root, StringComparison.Ordinal) || !File.Exists(file))
                file = Path.Combine(root, "index.html");
            if (!File.Exists(file))
                return Results.NotFound();
            var name = Path.GetFileName(file);
            context.Response.Headers.CacheControl = name == "index.html" ? "no-cache" : name.Contains('-') && path?.StartsWith("assets/", StringComparison.Ordinal) == true
                ? "public, max-age=31536000, immutable"
                : "public, max-age=3600";
            return Results.File(file, types.TryGetContentType(file, out var type) ? type : "application/octet-stream");
        });
    }
}
