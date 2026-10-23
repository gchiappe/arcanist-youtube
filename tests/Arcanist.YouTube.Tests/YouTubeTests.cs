using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arcanist.Core.Data;
using Arcanist.Core.Data.Entities;
using Arcanist.Core.Setup;
using Arcanist.Core.Tests;
using Arcanist.Core.Tests.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace Arcanist.YouTube.Tests;

/// <summary>
/// The extension running in a real Arcanist (in memory), installed from its folder as an administrator's install
/// leaves it, with a fake yt-dlp that answers from a catalog: nothing reaches the internet.
/// </summary>
public sealed class YouTubeTests : IAsyncDisposable
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly string Extension = Path.Combine(AppContext.BaseDirectory, "extension");

    private static string FakeYtDlp => Path.Combine(AppContext.BaseDirectory, "fake-yt-dlp", OperatingSystem.IsWindows() ? "yt-dlp-fake.exe" : "yt-dlp-fake");

    private readonly string _music = Path.Combine(Path.GetTempPath(), "arcanist-youtube-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonObject _catalog = new();
    private ArcanistFactory _factory = null!;
    private HttpClient _admin = null!;

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        try
        {
            Directory.Delete(_music, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CatalogFile => Path.Combine(_factory.DataPath, "extension-data", "youtube", "fake-catalog.json");

    private void SaveCatalog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogFile)!);
        File.WriteAllText(CatalogFile, _catalog.ToJsonString());
    }

    private string[] Calls() => File.Exists(CatalogFile + ".calls") ? File.ReadAllLines(CatalogFile + ".calls") : [];

    /// <summary>A song YouTube "has": its description and the audio it gives.</summary>
    private void Video(string id, string title, string channel, int seed, JsonObject? extra = null, int delayMs = 0, string? url = null)
    {
        var file = AudioFixtures.WriteWav(Path.Combine(_music + "-sources", $"{id}.wav"), seed, new FixtureTags());
        var info = new JsonObject
        {
            ["id"] = id, ["title"] = title, ["channel"] = channel, ["duration"] = 2.0, ["upload_date"] = "20240315", ["view_count"] = 1234,
            ["thumbnails"] = new JsonArray(new JsonObject { ["url"] = $"https://i.ytimg.invalid/vi/{id}/hq.jpg", ["width"] = 480 }),
        };
        foreach (var (key, value) in extra ?? [])
            info[key] = value?.DeepClone();
        _catalog[url ?? $"https://www.youtube.com/watch?v={id}"] = new JsonObject { ["info"] = info, ["file"] = file, ["delayMs"] = delayMs };
    }

    private void Playlist(string list, string title, int count, string kind = "PL")
    {
        var entries = new JsonArray();
        for (var i = 1; i <= count; i++)
        {
            var id = $"{kind}vid{i:0000}xx"[..11];
            Video(id, $"Band - Song {i}", "Band - Topic", i);
            entries.Add(new JsonObject
            {
                ["id"] = id, ["url"] = $"https://www.youtube.com/watch?v={id}", ["title"] = i == 3 ? "[Private video]" : $"Band - Song {i}",
                ["channel"] = "Band - Topic", ["duration"] = 2.0,
            });
        }

        _catalog[$"https://www.youtube.com/playlist?list={list}"] = new JsonObject
        {
            ["info"] = new JsonObject { ["_type"] = "playlist", ["id"] = list, ["title"] = title, ["channel"] = "Band - Topic", ["playlist_count"] = count, ["entries"] = entries },
        };
    }

    /// <summary>A server with the extension installed and switched on, a library, and the fake yt-dlp.</summary>
    private async Task StartAsync(Dictionary<string, string?>? settings = null)
    {
        Directory.CreateDirectory(_music);
        _factory = new ArcanistFactory(new Dictionary<string, string?>(settings ?? [])
        {
            ["Arcanist:Uploads:ScanDelay"] = "00:00:00.2",
            ["Arcanist:Extensions:youtube:YtDlpPath"] = FakeYtDlp,
            // No Deno and no ffmpeg from the machine: the tests do not depend on what it has.
            ["Arcanist:Extensions:youtube:DenoPath"] = Path.Combine(Path.GetTempPath(), "no-deno"),
            ["Arcanist:Ffmpeg:SearchSystem"] = "false",
        });
        Install(_factory.DataPath, enabled: true);
        SaveCatalog();
        await _factory.CompleteSetupAsync(new SetupLibrary("Music", [_music]));
        _admin = await _factory.CreateAuthenticatedClientAsync();
        await _factory.WaitForScansAsync(_admin);
    }

    /// <summary>The extension's files where installing its package leaves them.</summary>
    private static void Install(string dataPath, bool enabled)
    {
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(Path.Combine(dataPath, "arcanist.json"),
            JsonSerializer.Serialize(new { Arcanist = new { Extensions = new { youtube = new { Enabled = enabled } } } }));
        CopyFolder(Extension, Path.Combine(dataPath, "extensions", "youtube"));
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static byte[] Package()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(Extension, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, Path.GetRelativePath(Extension, file).Replace('\\', '/'));
        }

        return buffer.ToArray();
    }

    private async Task<JsonElement> GetAsync(HttpClient client, string path) =>
        await client.GetFromJsonAsync<JsonElement>("/ext/youtube/api/v1/" + path, ArcanistFactory.Json, Cancel);

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, "/ext/youtube/api/v1/" + path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: ArcanistFactory.Json);
        var response = await client.SendAsync(request, Cancel);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private async Task<string> LibraryAsync() =>
        (await _admin.GraphQLAsync("query { libraries { id } }")).GetProperty("data").GetProperty("libraries")[0].GetProperty("id").GetString()!;

    /// <summary>Waits until a download finishes (or <paramref name="until"/> says so).</summary>
    private async Task<JsonElement> WaitAsync(HttpClient client, string id, Func<JsonElement, bool>? until = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var batch = await GetAsync(client, $"downloads/{id}");
            if (until?.Invoke(batch) ?? batch.GetProperty("status").GetString() is "DONE" or "FAILED" or "CANCELLED")
                return batch;
            Assert.True(DateTime.UtcNow < deadline, batch.ToString());
            await Task.Delay(100, Cancel);
        }
    }

    [Fact]
    public async Task The_package_installs_and_runs_after_a_restart()
    {
        Directory.CreateDirectory(_music);
        var first = new ArcanistFactory { KeepData = true };
        await first.CompleteSetupAsync(new SetupLibrary("Music", [_music]));
        var admin = await first.CreateAuthenticatedClientAsync();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Package()), "file", "arcanist-youtube.zip" } };
        var installed = await (await admin.PostAsync("/rest/v1/extensions", form, Cancel)).Content.ReadFromJsonAsync<JsonElement>(ArcanistFactory.Json, Cancel);
        Assert.Equal(("youtube", "NOT_LOADED"), (installed.GetProperty("id").GetString(), installed.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/rest/v1/extensions/youtube/enabled", new { enabled = true }, ArcanistFactory.Json, Cancel)).StatusCode);
        await first.DisposeAsync();

        // The restart the panel asks for.
        _factory = new ArcanistFactory { DataPath = first.DataPath };
        _admin = await _factory.CreateAuthenticatedClientAsync();
        var extension = (await _admin.GetFromJsonAsync<List<JsonElement>>("/rest/v1/extensions", ArcanistFactory.Json, Cancel))!.Single(e => e.GetProperty("id").GetString() == "youtube");
        Assert.Equal("RUNNING", extension.GetProperty("status").GetString());

        var app = Assert.Single((await _admin.GetFromJsonAsync<List<JsonElement>>("/rest/v1/extension-apps?language=es", ArcanistFactory.Json, Cancel))!);
        Assert.Equal(("Importar desde YouTube", "/ext/youtube/"), (app.GetProperty("name").GetString(), app.GetProperty("url").GetString()));
        var english = Assert.Single((await _admin.GetFromJsonAsync<List<JsonElement>>("/rest/v1/extension-apps?language=en", ArcanistFactory.Json, Cancel))!);
        Assert.Equal("YouTube Import", english.GetProperty("name").GetString());

        // Its web app, at its address with or without the slash.
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal("/ext/youtube/", (await client.GetAsync("/ext/youtube", Cancel)).Headers.Location?.OriginalString);
        var page = await client.GetAsync("/ext/youtube/", Cancel);
        Assert.Contains("<div id=\"app\">", await page.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ext/youtube/icon.svg", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/ext/youtube/api/v1/nothing", Cancel)).StatusCode);

        // Without yt-dlp yet: it says so.
        var status = await GetAsync(_admin, "status");
        Assert.True(status.GetProperty("isAdmin").GetBoolean());
        Assert.Contains(status.GetProperty("tools").EnumerateArray(), t => t.GetProperty("kind").GetString() == "YT_DLP");
    }

    [Fact]
    public async Task A_video_is_described_with_its_tags_and_comes_in_as_the_users_upload()
    {
        _catalog["placeholder"] = new JsonObject();
        await StartAsync();
        Video("abcdefghijk", "Band Name - Great Song (Official Video)", "Band NameVEVO", 7,
            new JsonObject { ["chapters"] = new JsonArray(new JsonObject { ["title"] = "Intro" }, new JsonObject { ["title"] = "Main" }) });
        SaveCatalog();

        var status = await GetAsync(_admin, "status");
        Assert.True(status.GetProperty("ready").GetBoolean());
        Assert.Equal(["ORIGINAL"], status.GetProperty("formats").EnumerateArray().Select(f => f.GetString()));

        var preview = await GetAsync(_admin, "inspect?url=" + Uri.EscapeDataString("https://youtu.be/abcdefghijk?si=x&t=10"));
        Assert.Equal(("VIDEO", "https://www.youtube.com/watch?v=abcdefghijk", 2), (preview.GetProperty("kind").GetString(), preview.GetProperty("url").GetString(),
            preview.GetProperty("chapters").GetInt32()));
        var tags = preview.GetProperty("tags");
        Assert.Equal(("Great Song", "Band Name"), (tags.GetProperty("title").GetString(), tags.GetProperty("artist").GetString()));
        Assert.StartsWith("/ext/youtube/api/v1/image/", preview.GetProperty("thumbnail").GetString());

        var library = await LibraryAsync();
        var (sent, batch) = await SendAsync(_admin, HttpMethod.Post, "downloads", new
        {
            url = "https://youtu.be/abcdefghijk", libraryId = library, tags = new { title = "Great Song", artist = "Band Name", album = "Singles", year = 2024 },
        });
        Assert.True(sent == HttpStatusCode.OK, batch.ToString());
        var done = await WaitAsync(_admin, batch.GetProperty("id").GetString()!);
        var item = done.GetProperty("items")[0];
        Assert.Equal("ADDED", item.GetProperty("status").GetString());

        // It is the user's upload, from the extension, and in the catalog once scanned.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement upload;
        while (true)
        {
            upload = (await _admin.GraphQLAsync("query { myUploads { items { name status source track { id title album { title } } } } }"))
                .GetProperty("data").GetProperty("myUploads").GetProperty("items")[0];
            if (upload.GetProperty("status").GetString() == "ADDED")
                break;
            Assert.True(DateTime.UtcNow < deadline, upload.ToString());
            await Task.Delay(100, Cancel);
        }

        Assert.Equal(("Band Name - Great Song.wav", "youtube", "Great Song", "Singles"), (upload.GetProperty("name").GetString(), upload.GetProperty("source").GetString(),
            upload.GetProperty("track").GetProperty("title").GetString(), upload.GetProperty("track").GetProperty("album").GetProperty("title").GetString()));

        // The download learns the track it became (the app plays it from its history).
        var trackId = upload.GetProperty("track").GetProperty("id").GetString();
        var batchId = batch.GetProperty("id").GetString()!;
        // Reading the list looks the tracks up at once (no need to wait for the extension's own search).
        var added = (await GetAsync(_admin, "downloads")).EnumerateArray().Single(b => b.GetProperty("id").GetString() == batchId).GetProperty("items")[0];
        Assert.Equal((trackId, trackId, 1), (added.GetProperty("trackIds")[0].GetString(), added.GetProperty("trackId").GetString(),
            added.GetProperty("contentHashes").GetArrayLength()));

        // The same song again is a duplicate that knows its track at once.
        var (_, again) = await SendAsync(_admin, HttpMethod.Post, "downloads", new { url = "https://youtu.be/abcdefghijk", libraryId = library });
        var duplicate = (await WaitAsync(_admin, again.GetProperty("id").GetString()!)).GetProperty("items")[0];
        Assert.Equal(("DUPLICATE", trackId), (duplicate.GetProperty("status").GetString(), duplicate.GetProperty("trackIds")[0].GetString()));

        // Seen again, it is marked as already in the library.
        Assert.True((await GetAsync(_admin, "inspect?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=abcdefghijk"))).GetProperty("inLibrary").GetBoolean());
    }

    [Fact]
    public async Task Lists_come_a_page_at_a_time_and_all_of_them_can_be_taken()
    {
        await StartAsync();
        Playlist("PLlonglist12", "Long list", 130);
        SaveCatalog();

        var first = await GetAsync(_admin, "inspect?url=" + Uri.EscapeDataString("https://www.youtube.com/playlist?list=PLlonglist12"));
        Assert.Equal(("PLAYLIST", 130, 100), (first.GetProperty("kind").GetString(), first.GetProperty("entryCount").GetInt32(), first.GetProperty("entries").GetArrayLength()));
        Assert.False(first.GetProperty("entries")[2].GetProperty("available").GetBoolean());
        var second = await GetAsync(_admin, $"inspect?url={Uri.EscapeDataString("https://www.youtube.com/playlist?list=PLlonglist12")}&after={first.GetProperty("nextCursor").GetString()}");
        Assert.Equal(30, second.GetProperty("entries").GetArrayLength());
        Assert.Equal(101, second.GetProperty("entries")[0].GetProperty("index").GetInt32());
        Assert.False(second.TryGetProperty("nextCursor", out var more) && more.ValueKind == JsonValueKind.String);

        // Some chosen, as an album with its songs numbered.
        var library = await LibraryAsync();
        var chosen = first.GetProperty("entries").EnumerateArray().Take(2)
            .Select(e => new { id = e.GetProperty("id").GetString(), url = e.GetProperty("url").GetString(), index = e.GetProperty("index").GetInt32() }).ToList();
        var (_, some) = await SendAsync(_admin, HttpMethod.Post, "downloads", new
        {
            url = "https://www.youtube.com/playlist?list=PLlonglist12", libraryId = library, entries = chosen, album = "Long Album", numberTracks = true,
        });
        var done = await WaitAsync(_admin, some.GetProperty("id").GetString()!);
        Assert.All(done.GetProperty("items").EnumerateArray(), i => Assert.Equal("ADDED", i.GetProperty("status").GetString()));
        Assert.Equal((2, "Long Album"), (done.GetProperty("items")[1].GetProperty("tags").GetProperty("trackNumber").GetInt32(),
            done.GetProperty("items")[1].GetProperty("tags").GetProperty("album").GetString()));

        // All of them but a few: the server lists them (the private one is left out).
        var (_, all) = await SendAsync(_admin, HttpMethod.Post, "downloads", new
        {
            url = "https://www.youtube.com/playlist?list=PLlonglist12", libraryId = library, all = true, exclude = new[] { chosen[0].id, chosen[1].id },
        });
        Assert.Equal("LISTING", all.GetProperty("status").GetString());
        var listed = await WaitAsync(_admin, all.GetProperty("id").GetString()!, b => b.GetProperty("status").GetString() != "LISTING");
        Assert.Equal(127, listed.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Failures_say_why_and_can_be_retried()
    {
        await StartAsync();
        _catalog["https://www.youtube.com/watch?v=blockedvid1"] = new JsonObject { ["error"] = "blockedvid1: Sign in to confirm you're not a bot. Use --cookies" };
        SaveCatalog();

        var (status, problem) = await SendAsync(_admin, HttpMethod.Get, "inspect?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=blockedvid1"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "blocked_by_youtube"), (status, problem.GetProperty("code").GetString()));
        Assert.Equal("invalid_link", (await SendAsync(_admin, HttpMethod.Get, "inspect?url=nothing")).Body.GetProperty("code").GetString());
        Assert.Equal("site_not_allowed", (await SendAsync(_admin, HttpMethod.Get, "inspect?url=" + Uri.EscapeDataString("https://example.org/song"))).Body.GetProperty("code").GetString());

        var library = await LibraryAsync();
        var (_, batch) = await SendAsync(_admin, HttpMethod.Post, "downloads", new { url = "https://www.youtube.com/watch?v=blockedvid1", libraryId = library });
        var failed = await WaitAsync(_admin, batch.GetProperty("id").GetString()!);
        Assert.Equal(("FAILED", "blocked_by_youtube"), (failed.GetProperty("status").GetString(), failed.GetProperty("items")[0].GetProperty("errorCode").GetString()));

        // Fixed (cookies, a new yt-dlp…): retrying brings it.
        Video("blockedvid1", "Band - Back", "Band", 11);
        SaveCatalog();
        await SendAsync(_admin, HttpMethod.Post, $"downloads/{batch.GetProperty("id").GetString()}/retry");
        Assert.Equal("DONE", (await WaitAsync(_admin, batch.GetProperty("id").GetString()!)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Downloads_can_be_cancelled_and_are_followed_live()
    {
        await StartAsync();
        Video("slowvideo01", "Band - Slow", "Band", 12, delayMs: 2000);
        SaveCatalog();
        var library = await LibraryAsync();

        // The live view gets each change.
        using var events = await _admin.GetAsync("/ext/youtube/api/v1/downloads/events", HttpCompletionOption.ResponseHeadersRead, Cancel);
        Assert.Equal("text/event-stream", events.Content.Headers.ContentType?.MediaType);
        await using var stream = await events.Content.ReadAsStreamAsync(Cancel);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var (_, batch) = await SendAsync(_admin, HttpMethod.Post, "downloads", new { url = "https://www.youtube.com/watch?v=slowvideo01", libraryId = library });
        var id = batch.GetProperty("id").GetString()!;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancel))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            string? line;
            while ((line = await reader.ReadLineAsync(timeout.Token)) is not null && !(line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains(id)))
            {
            }

            Assert.NotNull(line);
        }

        await WaitAsync(_admin, id, b => b.GetProperty("items")[0].GetProperty("status").GetString() == "DOWNLOADING");
        await SendAsync(_admin, HttpMethod.Post, $"downloads/{id}/cancel");
        var cancelled = await WaitAsync(_admin, id);
        Assert.Equal("CANCELLED", cancelled.GetProperty("items")[0].GetProperty("status").GetString());

        // Finished, it can be forgotten.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(_admin, HttpMethod.Delete, $"downloads/{id}")).Status);
        Assert.Equal(0, (await GetAsync(_admin, "downloads")).GetArrayLength());
    }

    [Fact]
    public async Task Only_users_who_may_upload_use_it_and_only_administrators_change_it()
    {
        await StartAsync();
        await _factory.CreateUserAsync("listener");
        var listener = await _factory.CreateAuthenticatedClientAsync("listener");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(listener, HttpMethod.Get, "status")).Status);
        Assert.Empty((await listener.GetFromJsonAsync<List<JsonElement>>("/rest/v1/extension-apps", ArcanistFactory.Json, Cancel))!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(_factory.CreateClient(), HttpMethod.Get, "status")).Status);

        await _factory.CreateUserAsync("uploader", permissions: Permissions.Upload);
        var uploader = await _factory.CreateAuthenticatedClientAsync("uploader");
        var status = await GetAsync(uploader, "status");
        Assert.False(status.GetProperty("isAdmin").GetBoolean());
        // The library of the setup is everyone's: they may upload to it.
        Assert.Equal(1, status.GetProperty("libraries").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(uploader, HttpMethod.Put, "settings", new { otherSites = new[] { "example.org" }, concurrency = 1, autoUpdate = true, maxItems = 10, defaultFormat = "ORIGINAL" })).Status);

        // The administrator allows another site.
        var (saved, settings) = await SendAsync(_admin, HttpMethod.Put, "settings",
            new { otherSites = new[] { "https://Example.org/x", "youtube.com", "nonsense" }, concurrency = 9, autoUpdate = false, maxItems = 99999, defaultFormat = "ORIGINAL" });
        Assert.True(saved == HttpStatusCode.OK, settings.ToString());
        Assert.Equal(["example.org"], settings.GetProperty("otherSites").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal((4, 5000), (settings.GetProperty("concurrency").GetInt32(), settings.GetProperty("maxItems").GetInt32()));
        _catalog["https://example.org/song"] = new JsonObject { ["info"] = new JsonObject { ["id"] = "song", ["title"] = "Someone - Elsewhere" } };
        SaveCatalog();
        Assert.Equal("OTHER", (await GetAsync(_admin, "inspect?url=" + Uri.EscapeDataString("https://example.org/song"))).GetProperty("kind").GetString());

        // A list there is a playlist, its songs with their own links.
        _catalog["https://example.org/album"] = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["_type"] = "playlist", ["id"] = "album", ["title"] = "Elsewhere",
                ["entries"] = new JsonArray(
                    new JsonObject { ["id"] = "one", ["title"] = "One", ["url"] = "https://example.org/album/one" },
                    new JsonObject { ["id"] = "two", ["title"] = "Two" }),
            },
        };
        SaveCatalog();
        var list = await GetAsync(_admin, "inspect?url=" + Uri.EscapeDataString("https://example.org/album"));
        Assert.Equal("PLAYLIST", list.GetProperty("kind").GetString());
        Assert.Equal(("https://example.org/album/one", true), (list.GetProperty("entries")[0].GetProperty("url").GetString(), list.GetProperty("entries")[0].GetProperty("available").GetBoolean()));
        Assert.False(list.GetProperty("entries")[1].GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task YouTube_cookies_are_kept_encrypted_and_given_to_each_run()
    {
        await StartAsync();
        Video("cookievid01", "Band - Cookie", "Band", 13);
        SaveCatalog();

        using var invalid = new StringContent("not cookies", Encoding.UTF8, "text/plain");
        var refused = await _admin.PutAsync("/ext/youtube/api/v1/cookies", invalid, Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        using var cookies = new StringContent(".youtube.com\tTRUE\t/\tTRUE\t1999999999\tSID\tsecret-value\n", Encoding.UTF8, "text/plain");
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PutAsync("/ext/youtube/api/v1/cookies", cookies, Cancel)).StatusCode);
        Assert.True((await GetAsync(_admin, "status")).GetProperty("cookies").GetBoolean());
        Assert.DoesNotContain("secret-value", await File.ReadAllTextAsync(Path.Combine(_factory.DataPath, "extension-data", "youtube", "cookies.bin"), Cancel));

        await GetAsync(_admin, "inspect?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=cookievid01"));
        var call = Calls().Last();
        Assert.Contains("--cookies", call);
        // The run's copy is gone once it ends.
        Assert.Empty(Directory.GetFiles(Path.Combine(_factory.DataPath, "extension-data", "youtube", "work"), ".cookies-*"));

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync("/ext/youtube/api/v1/cookies", Cancel)).StatusCode);
        Assert.False((await GetAsync(_admin, "status")).GetProperty("cookies").GetBoolean());
    }

    [Fact]
    public async Task Each_user_sets_their_own_cookies_and_whether_songs_play_when_they_arrive()
    {
        await StartAsync();
        Video("mycookie001", "Band - Mine", "Band", 14);
        SaveCatalog();
        await _factory.CreateUserAsync("uploader", permissions: Permissions.Upload);
        var uploader = await _factory.CreateAuthenticatedClientAsync("uploader");

        var me = await GetAsync(uploader, "me");
        Assert.False(me.GetProperty("hasCookies").GetBoolean());
        Assert.False(me.GetProperty("playWhenDone").GetBoolean());
        var (status, preferences) = await SendAsync(uploader, HttpMethod.Put, "me/preferences", new { playWhenDone = true });
        Assert.True(status == HttpStatusCode.OK, preferences.ToString());
        Assert.True((await GetAsync(uploader, "me")).GetProperty("playWhenDone").GetBoolean());
        // Each one's: the administrator's are untouched.
        Assert.False((await GetAsync(_admin, "me")).GetProperty("playWhenDone").GetBoolean());

        using var cookies = new StringContent(".youtube.com\tTRUE\t/\tTRUE\t1999999999\tSID\tmy-secret\n", Encoding.UTF8, "text/plain");
        Assert.Equal(HttpStatusCode.OK, (await uploader.PutAsync("/ext/youtube/api/v1/me/cookies", cookies, Cancel)).StatusCode);
        Assert.True((await GetAsync(uploader, "me")).GetProperty("hasCookies").GetBoolean());
        Assert.False((await GetAsync(_admin, "status")).GetProperty("cookies").GetBoolean());

        // Their links go with their cookies; the others' without any (the server has none).
        var url = "inspect?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=mycookie001");
        await GetAsync(uploader, url);
        Assert.Contains("--cookies", Calls().Last());
        await GetAsync(_admin, url);
        Assert.DoesNotContain("--cookies", Calls().Last());

        Assert.Equal(HttpStatusCode.OK, (await uploader.DeleteAsync("/ext/youtube/api/v1/me/cookies", Cancel)).StatusCode);
        Assert.False((await GetAsync(uploader, "me")).GetProperty("hasCookies").GetBoolean());

        await _factory.CreateUserAsync("listener");
        var listener = await _factory.CreateAuthenticatedClientAsync("listener");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(listener, HttpMethod.Get, "me")).Status);
    }

    [Fact]
    public async Task The_admin_panel_draws_the_extension_settings_and_saves_them()
    {
        await StartAsync();

        var page = await _admin.GetFromJsonAsync<JsonElement>("/rest/v1/extensions/youtube/admin?language=es", ArcanistFactory.Json, Cancel);
        Assert.Equal(["tools", "settings", "cookies", "legal"], page.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal("Herramientas", page.GetProperty("sections")[0].GetProperty("title").GetString());
        // Deno is missing here: the panel offers to install it right after the extension starts.
        var setup = page.GetProperty("setup");
        Assert.Equal("install", setup.GetProperty("action").GetString());
        Assert.Contains("Deno", Assert.Single(setup.GetProperty("items").EnumerateArray()).GetString());

        var saved = await _admin.PutAsJsonAsync("/rest/v1/extensions/youtube/admin",
            new { values = new { otherSites = "https://Example.org/x\nyoutube.com", concurrency = 9, maxItems = 20, autoUpdate = false, defaultFormat = "Mp3" } },
            ArcanistFactory.Json, Cancel);
        var result = await saved.Content.ReadFromJsonAsync<JsonElement>(ArcanistFactory.Json, Cancel);
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());

        var settings = await GetAsync(_admin, "settings");
        Assert.Equal(["example.org"], settings.GetProperty("otherSites").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal((4, 20, false, "MP3"), (settings.GetProperty("concurrency").GetInt32(), settings.GetProperty("maxItems").GetInt32(),
            settings.GetProperty("autoUpdate").GetBoolean(), settings.GetProperty("defaultFormat").GetString()));

        // The server's cookies from the page, then removed with its action.
        var withCookies = await _admin.PutAsJsonAsync("/rest/v1/extensions/youtube/admin",
            new { values = new { cookies = ".youtube.com\tTRUE\t/\tTRUE\t1999999999\tSID\tserver-secret\n" } }, ArcanistFactory.Json, Cancel);
        Assert.True((await withCookies.Content.ReadFromJsonAsync<JsonElement>(ArcanistFactory.Json, Cancel)).GetProperty("ok").GetBoolean());
        Assert.True((await GetAsync(_admin, "status")).GetProperty("cookies").GetBoolean());
        var removed = await _admin.PostAsJsonAsync("/rest/v1/extensions/youtube/admin/actions/removeCookies", new { values = new { } }, ArcanistFactory.Json, Cancel);
        Assert.True((await removed.Content.ReadFromJsonAsync<JsonElement>(ArcanistFactory.Json, Cancel)).GetProperty("ok").GetBoolean());
        Assert.False((await GetAsync(_admin, "status")).GetProperty("cookies").GetBoolean());
    }
}
