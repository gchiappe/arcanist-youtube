using System.Text.RegularExpressions;
using System.Web;

namespace Arcanist.YouTube;

/// <summary>What a link points to.</summary>
public enum LinkKind
{
    /// <summary>One video (or YouTube Music song).</summary>
    Video,

    /// <summary>A playlist.</summary>
    Playlist,

    /// <summary>A mix YouTube makes up (lists starting with RD): endless, so cut short.</summary>
    Mix,

    /// <summary>A YouTube Music album (an OLAK5uy… playlist or a browse/MPREb… page).</summary>
    Album,

    /// <summary>A channel, or one of its tabs (videos, releases…).</summary>
    Channel,

    /// <summary>A YouTube Music artist.</summary>
    Artist,

    /// <summary>A page of another site the administrator allows (yt-dlp knows many).</summary>
    Other,
}

/// <summary>A link understood: what it is and its canonical form, the one handed to yt-dlp.</summary>
/// <param name="Url">The canonical link: tracking and time parameters dropped.</param>
/// <param name="Id">The video, list or channel id (the handle for @channels); null for other sites.</param>
/// <param name="ListUrl">For a video opened inside a list, the list: the user picks one or the other.</param>
/// <param name="Music">On YouTube Music.</param>
/// <param name="Tab">A channel's tab, when the link names one (<c>videos</c>, <c>releases</c>…).</param>
public sealed record ClassifiedLink(LinkKind Kind, string Url, string? Id, string? ListUrl = null, bool Music = false, string? Tab = null);

/// <summary>Why a link was not taken.</summary>
public enum LinkProblem
{
    /// <summary>Not a link (or not one this understands).</summary>
    Invalid,

    /// <summary>A site the administrator does not allow.</summary>
    NotAllowed,
}

/// <summary>
/// Recognizes links before asking yt-dlp anything, so the app knows what it is dealing with at once: every form of
/// YouTube and YouTube Music link (watch, youtu.be, shorts, live, embed, playlists, mixes, albums, channels and their
/// tabs, artists), links inside pasted text, and pages of the other sites the administrator allows.
/// </summary>
public static partial class UrlClassifier
{
    public static readonly IReadOnlyList<string> ChannelTabs = ["releases", "videos", "shorts", "streams", "playlists"];

    private static readonly string[] YouTubeHosts =
        ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "www.youtu.be", "youtube-nocookie.com", "www.youtube-nocookie.com"];

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,64}$")]
    private static partial Regex ListId();

    // Links in pasted text: with a scheme, or bare YouTube hosts ("youtu.be/…", "www.youtube.com/…").
    [GeneratedRegex(@"(?:https?://[^\s<>""']+)|(?:(?<![\w./:])(?:(?:www\.|m\.|music\.)?youtube\.com|youtu\.be)/[^\s<>""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkInText();

    /// <summary>The links in pasted text, in order and without repeats (at most 50).</summary>
    public static IReadOnlyList<string> FindLinks(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : LinkInText().Matches(text).Select(m => m.Value.TrimEnd(')', ']', '>', ',', '.', ';', '!', '?', '"', '\''))
                .Select(link => link.Contains("://", StringComparison.Ordinal) ? link : "https://" + link)
                .Distinct(StringComparer.Ordinal).Take(50).ToList();

    public static bool IsYouTubeHost(string host) => YouTubeHosts.Contains(host.ToLowerInvariant());

    /// <summary>Whether a host is one of the other sites allowed (a site allows its subdomains too).</summary>
    public static bool IsAllowedSite(string host, IEnumerable<string> sites)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        return sites.Select(s => s.Trim().ToLowerInvariant().TrimStart('.')).Where(s => s.Length > 0)
            .Any(site => host == site || host.EndsWith("." + site, StringComparison.Ordinal));
    }

    /// <summary>Understands a link; <paramref name="problem"/> says why not when it returns null.</summary>
    public static ClassifiedLink? Classify(string? text, IEnumerable<string> otherSites, out LinkProblem problem)
    {
        problem = LinkProblem.Invalid;
        var link = FindLinks(text).FirstOrDefault();
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;

        if (!IsYouTubeHost(uri.Host))
        {
            if (!IsAllowedSite(uri.Host, otherSites))
            {
                problem = LinkProblem.NotAllowed;
                return null;
            }

            return new ClassifiedLink(LinkKind.Other, uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped), null);
        }

        var host = uri.Host.ToLowerInvariant();
        var music = host == "music.youtube.com";
        var query = HttpUtility.ParseQueryString(uri.Query);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var list = query["list"] is { } l && ListId().IsMatch(l) ? l : null;

        string? video = null;
        if (host is "youtu.be" or "www.youtu.be")
            video = segments.FirstOrDefault();
        else if (segments is ["watch"])
            video = query["v"];
        else if (segments is ["shorts" or "live" or "embed" or "v" or "e", var id, ..])
            video = id;

        if (video is not null)
        {
            if (!VideoId().IsMatch(video))
                return null;
            // Mixes are only reachable from one of their videos.
            var listUrl = list is null ? null
                : list.StartsWith("RD", StringComparison.Ordinal) ? $"{(music ? "https://music.youtube.com" : "https://www.youtube.com")}/watch?v={video}&list={list}"
                : ListLink(list, music);
            return new ClassifiedLink(LinkKind.Video, music ? $"https://music.youtube.com/watch?v={video}" : $"https://www.youtube.com/watch?v={video}",
                video, listUrl, music);
        }

        if (segments is ["playlist"] && list is not null)
            return ListLinkOf(list, music);

        if (music && segments is ["browse", var browse] && browse.StartsWith("MPREb", StringComparison.Ordinal) && ListId().IsMatch(browse))
            return new ClassifiedLink(LinkKind.Album, $"https://music.youtube.com/browse/{browse}", browse, Music: true);

        // Channels: /@handle, /channel/UC…, /c/name and /user/name, with an optional tab.
        string? channel = null;
        var rest = segments.AsSpan();
        if (segments.Length >= 1 && segments[0].StartsWith('@') && segments[0].Length > 1)
        {
            channel = segments[0];
            rest = segments.AsSpan(1);
        }
        else if (segments.Length >= 2 && segments[0] is "channel" or "c" or "user")
        {
            channel = $"{segments[0]}/{segments[1]}";
            rest = segments.AsSpan(2);
        }

        if (channel is not null)
        {
            var tab = rest.Length > 0 && ChannelTabs.Contains(rest[0].ToLowerInvariant()) ? rest[0].ToLowerInvariant() : null;
            if (music && channel.StartsWith("channel/", StringComparison.Ordinal))
                return new ClassifiedLink(LinkKind.Artist, $"https://music.youtube.com/{channel}", channel["channel/".Length..], Music: true);
            return new ClassifiedLink(LinkKind.Channel, $"https://www.youtube.com/{channel}", channel.Split('/')[^1], Music: false, Tab: tab);
        }

        // A list with no video (watch?list=…).
        if (list is not null)
            return ListLinkOf(list, music);
        return null;
    }

    /// <summary>A channel's tab as a link yt-dlp lists.</summary>
    public static string ChannelTab(ClassifiedLink channel, string tab) => $"{channel.Url}/{tab}";

    private static string ListLink(string list, bool music) =>
        music ? $"https://music.youtube.com/playlist?list={list}" : $"https://www.youtube.com/playlist?list={list}";

    private static ClassifiedLink ListLinkOf(string list, bool music)
    {
        if (list.StartsWith("RD", StringComparison.Ordinal))
        {
            // A video's mix (RD + its id) opens from that video; others open as a list.
            var seed = list.Length == 13 && VideoId().IsMatch(list[2..]) ? list[2..] : null;
            var url = seed is null ? ListLink(list, music) : $"{(music ? "https://music.youtube.com" : "https://www.youtube.com")}/watch?v={seed}&list={list}";
            return new ClassifiedLink(LinkKind.Mix, url, list, Music: music);
        }

        var kind = list.StartsWith("OLAK5uy", StringComparison.Ordinal) ? LinkKind.Album : LinkKind.Playlist;
        return new ClassifiedLink(kind, ListLink(list, music), list, Music: music);
    }
}
