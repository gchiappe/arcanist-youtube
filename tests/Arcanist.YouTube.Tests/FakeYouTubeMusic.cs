using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcanist.YouTube.Tests;

/// <summary>
/// YouTube Music's API, as its web app uses it, answering from what each test sets: searches by the filter asked
/// for, and pages (artists, albums, new releases, charts) by their browse id. Built like YouTube's own answers, small.
/// </summary>
public sealed class FakeYouTubeMusic : IAsyncDisposable
{
    private readonly WebApplication _app;

    public Dictionary<string, JsonObject> Searches { get; } = new();

    public Dictionary<string, JsonObject> Pages { get; } = new();

    public List<string> Calls { get; } = [];

    public string Url { get; }

    private FakeYouTubeMusic(WebApplication app, string url) => (_app, Url) = (app, url);

    public static async Task<FakeYouTubeMusic> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeYouTubeMusic? fake = null;
        app.MapPost("/youtubei/v1/{endpoint}", async (string endpoint, HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            lock (fake!.Calls)
                fake.Calls.Add(endpoint + " " + body.ToJsonString());
            var answer = endpoint == "search"
                ? fake.Searches.GetValueOrDefault(Filter((string?)body["params"]) + ":" + (string?)body["query"])
                : fake.Pages.GetValueOrDefault((string)body["browseId"]! + ((body["formData"]?["selectedValues"] as JsonArray)?.FirstOrDefault() is { } country ? ":" + country : ""));
            return answer is null ? Results.Json(new JsonObject()) : Results.Text(answer.ToJsonString(), "application/json");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake = new FakeYouTubeMusic(app, address + "/youtubei/v1/");
        return fake;
    }

    /// <summary>The filter of a search, as its <c>params</c> say (YouTube Music's own codes).</summary>
    private static string Filter(string? param) => param switch
    {
        null => "all",
        _ when param.StartsWith("EgWKAQII", StringComparison.Ordinal) => "songs",
        _ when param.StartsWith("EgWKAQIQ", StringComparison.Ordinal) => "videos",
        _ when param.StartsWith("EgWKAQIY", StringComparison.Ordinal) => "albums",
        _ when param.StartsWith("EgWKAQIg", StringComparison.Ordinal) => "artists",
        _ => "playlists",
    };

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    // —— YouTube's shapes ——

    public static JsonObject Run(string text, string? browseId = null, string? pageType = null) =>
        browseId is null
            ? new JsonObject { ["text"] = text }
            : new JsonObject { ["text"] = text, ["navigationEndpoint"] = Browse(browseId, pageType!) };

    public static JsonObject Browse(string id, string pageType) => new()
    {
        ["browseEndpoint"] = new JsonObject
        {
            ["browseId"] = id,
            ["browseEndpointContextSupportedConfigs"] = new JsonObject { ["browseEndpointContextMusicConfig"] = new JsonObject { ["pageType"] = pageType } },
        },
    };

    private static JsonObject Thumbnail(string id) => new()
    {
        ["musicThumbnailRenderer"] = new JsonObject
        {
            ["thumbnail"] = new JsonObject
            {
                ["thumbnails"] = new JsonArray(new JsonObject { ["url"] = $"https://lh3.googleusercontent.invalid/{id}=w120-h120-l90-rj", ["width"] = 120, ["height"] = 120 }),
            },
        },
    };

    private static JsonObject Column(params JsonObject[] runs) => new()
    {
        ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject { ["text"] = new JsonObject { ["runs"] = new JsonArray(runs) } },
    };

    /// <summary>A song row: its title (the video), artist and album links, and length.</summary>
    public static JsonObject Song(string videoId, string title, string artist, string artistId, string album, string albumId, string length) => new()
    {
        ["musicResponsiveListItemRenderer"] = new JsonObject
        {
            ["thumbnail"] = Thumbnail(videoId),
            ["playlistItemData"] = new JsonObject { ["videoId"] = videoId },
            ["flexColumns"] = new JsonArray(
                Column(new JsonObject
                {
                    ["text"] = title,
                    ["navigationEndpoint"] = new JsonObject
                    {
                        ["watchEndpoint"] = new JsonObject
                        {
                            ["videoId"] = videoId,
                            ["watchEndpointMusicSupportedConfigs"] = new JsonObject
                            {
                                ["watchEndpointMusicConfig"] = new JsonObject { ["musicVideoType"] = "MUSIC_VIDEO_TYPE_ATV" },
                            },
                        },
                    },
                }),
                Column(Run(artist, artistId, "MUSIC_PAGE_TYPE_ARTIST"), Run(" • "), Run(album, albumId, "MUSIC_PAGE_TYPE_ALBUM"), Run(" • "), Run(length))),
        },
    };

    /// <summary>A row that opens a page (an album or an artist found by a search).</summary>
    public static JsonObject PageRow(string title, string browseId, string pageType, params JsonObject[] subtitle) => new()
    {
        ["musicResponsiveListItemRenderer"] = new JsonObject
        {
            ["thumbnail"] = Thumbnail(browseId),
            ["navigationEndpoint"] = Browse(browseId, pageType),
            ["flexColumns"] = new JsonArray(Column(Run(title)), Column(subtitle)),
        },
    };

    /// <summary>A card (an album, single or artist on a page).</summary>
    public static JsonObject Card(string title, string browseId, string pageType, params JsonObject[] subtitle) => new()
    {
        ["musicTwoRowItemRenderer"] = new JsonObject
        {
            ["thumbnailRenderer"] = Thumbnail(browseId),
            ["title"] = new JsonObject { ["runs"] = new JsonArray(Run(title, browseId, pageType)) },
            ["subtitle"] = new JsonObject { ["runs"] = new JsonArray(subtitle) },
            ["navigationEndpoint"] = Browse(browseId, pageType),
        },
    };

    public static JsonObject Shelf(string title, params JsonObject[] items) => new()
    {
        ["musicShelfRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(Run(title)) }, ["contents"] = new JsonArray(items) },
    };

    public static JsonObject Carousel(string title, params JsonObject[] items) => new()
    {
        ["musicCarouselShelfRenderer"] = new JsonObject
        {
            ["header"] = new JsonObject { ["musicCarouselShelfBasicHeaderRenderer"] = new JsonObject { ["title"] = new JsonObject { ["runs"] = new JsonArray(Run(title)) } } },
            ["contents"] = new JsonArray(items),
        },
    };

    public static JsonObject Contents(params JsonObject[] shelves) => new()
    {
        ["contents"] = new JsonObject { ["sectionListRenderer"] = new JsonObject { ["contents"] = new JsonArray(shelves) } },
    };
}
