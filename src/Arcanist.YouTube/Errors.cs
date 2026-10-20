using Arcanist.Core.Application;

namespace Arcanist.YouTube;

/// <summary>The extension's own error codes (its app translates them; Arcanist's own codes can also come).</summary>
public static class ErrorCodes
{
    /// <summary>Not a link the extension understands.</summary>
    public const string InvalidLink = "invalid_link";

    /// <summary>A site the administrator does not allow.</summary>
    public const string SiteNotAllowed = "site_not_allowed";

    /// <summary>yt-dlp is not installed (an administrator installs it from the app).</summary>
    public const string ToolsMissing = "tools_missing";

    /// <summary>yt-dlp and Deno have no build for this machine.</summary>
    public const string ToolUnsupported = "tool_unsupported";

    /// <summary>Downloading a tool failed.</summary>
    public const string ToolDownloadFailed = "tool_download_failed";

    /// <summary>A downloaded tool did not match its published SHA-256: it was not installed.</summary>
    public const string ToolChecksumMismatch = "tool_checksum_mismatch";

    /// <summary>Converting, or this format, needs ffmpeg (Arcanist's, installed from its panel).</summary>
    public const string FfmpegRequired = "ffmpeg_required";

    /// <summary>The video does not exist, is private or was removed.</summary>
    public const string Unavailable = "unavailable_video";

    /// <summary>The video needs signing in (age-restricted or members only).</summary>
    public const string SignInRequired = "sign_in_required";

    /// <summary>Not available in the server's country.</summary>
    public const string GeoBlocked = "geo_blocked";

    /// <summary>A live stream that has not ended.</summary>
    public const string LiveNotEnded = "live_not_ended";

    /// <summary>YouTube is refusing this server for now (it asks to prove it is not a bot, or too many requests).</summary>
    public const string BlockedByYouTube = "blocked_by_youtube";

    /// <summary>YouTube refused, or something else went wrong (the server log has yt-dlp's message).</summary>
    public const string DownloadFailed = "download_failed";

    /// <summary>yt-dlp could not read the link (often an old yt-dlp: update it).</summary>
    public const string InspectFailed = "inspect_failed";

    /// <summary>The link has nothing to download.</summary>
    public const string Empty = "empty";

    /// <summary>Nothing chosen to download.</summary>
    public const string NothingSelected = "nothing_selected";

    /// <summary>Not a cookies.txt (Netscape format) with YouTube's cookies.</summary>
    public const string InvalidCookies = "invalid_cookies";

    /// <summary>The download is not in a state that allows that.</summary>
    public const string WrongState = "wrong_state";
}

/// <summary>An expected failure; Arcanist turns it into problem details with its code.</summary>
public sealed class ExtensionException(string code, int statusCode) : ArcanistException(code, statusCode);
