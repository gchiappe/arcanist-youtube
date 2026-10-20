using System.Globalization;
using System.Text.Json.Nodes;
using Arcanist.Core.Media;
using Microsoft.Extensions.Logging;

namespace Arcanist.YouTube;

/// <summary>How far a download got.</summary>
/// <param name="Fraction">0–1, when the size is known.</param>
/// <param name="Speed">Bytes per second.</param>
/// <param name="Eta">Seconds left.</param>
public sealed record DownloadProgress(double? Fraction, long? Downloaded, long? Total, double? Speed, int? Eta);

/// <param name="Chapters">Split the audio at the video's chapters (an album uploaded as one video).</param>
public sealed record DownloadOptions(AudioFormat Format, AudioQuality Quality, bool Chapters);

/// <summary>What a download left: the whole audio, or one file per chapter.</summary>
public sealed record DownloadedFiles(string Main, IReadOnlyList<string> Chapters);

/// <summary>
/// Runs yt-dlp: reads what a link has (<c>-J</c>, flat for lists), downloads one video's audio (converting it with
/// Arcanist's ffmpeg when asked) and finds a video's audio stream for previews. Its errors become the extension's codes.
/// </summary>
public sealed class YtDlp(ToolManager tools, FfmpegLocator ffmpeg, ExtensionPaths paths, CookieStore cookies, ILogger<YtDlp> logger)
{
    private const string ProgressMark = "ARCPROG";
    private const string FileMark = "ARCFILE:";

    private static readonly AsyncLocal<Guid?> User = new();

    public async Task<string?> FfmpegAsync(CancellationToken cancellationToken) => (await ffmpeg.GetAsync(cancellationToken))?.Path;

    /// <summary>Runs from here on (in this flow) are for this user: their own cookies, when they have them.</summary>
    public static IDisposable ActingFor(Guid user)
    {
        var previous = User.Value;
        User.Value = user;
        return new Restore(previous);
    }

    private sealed class Restore(Guid? previous) : IDisposable
    {
        public void Dispose() => User.Value = previous;
    }

    /// <summary>yt-dlp and the arguments every run takes; dispose it after the run (it removes the run's cookies).</summary>
    private sealed class Run(string path, List<string> common, string? cookies) : IDisposable
    {
        public string Path { get; } = path;

        public List<string> Common { get; } = common;

        public void Dispose()
        {
            try
            {
                if (cookies is not null && File.Exists(cookies))
                    File.Delete(cookies);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<Run> PrepareAsync(CancellationToken cancellationToken)
    {
        var tool = tools.Find(ToolKind.YtDlp) ?? throw new ExtensionException(ErrorCodes.ToolsMissing, 409);
        Directory.CreateDirectory(paths.Cache);
        var common = new List<string> { "--ignore-config", "--no-warnings", "--cache-dir", Path.Combine(paths.Cache, "yt-dlp") };
        // YouTube needs a JavaScript runtime for most formats; yt-dlp only uses Deno unless told otherwise.
        if (tools.Find(ToolKind.Deno) is { } deno)
            common.AddRange(["--js-runtimes", $"deno:{deno.Path}"]);
        if (await FfmpegAsync(cancellationToken) is { } ffmpegPath)
            common.AddRange(["--ffmpeg-location", ffmpegPath]);
        var copy = cookies.Copy(User.Value);
        if (copy is not null)
            common.AddRange(["--cookies", copy]);
        return new Run(tool.Path, common, copy);
    }

    /// <summary>
    /// What a link has: a video's details, or a list's (playlist, album, channel tab…) with the entries from
    /// <paramref name="first"/> (1-based) to <paramref name="last"/>, without visiting each one.
    /// </summary>
    public async Task<JsonObject> InspectAsync(string url, int first, int last, CancellationToken cancellationToken)
    {
        using var run = await PrepareAsync(cancellationToken);
        var (path, common) = (run.Path, run.Common);
        var arguments = new List<string>(common) { "-J", "--flat-playlist", "--playlist-items", $"{first}:{last}", "--", url };
        var result = await ProcessRunner.RunAsync(path, arguments, null, TimeSpan.FromMinutes(3), cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            throw Failure(result.Error, ErrorCodes.InspectFailed, url);
        try
        {
            return JsonNode.Parse(result.Output.Trim().Split('\n')[^1]) as JsonObject ?? throw Failure(result.Error, ErrorCodes.InspectFailed, url);
        }
        catch (System.Text.Json.JsonException)
        {
            throw Failure(result.Error, ErrorCodes.InspectFailed, url);
        }
    }

    /// <summary>Every entry of a list (ids, titles, durations), up to <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<JsonObject>> ListAllAsync(string url, int limit, CancellationToken cancellationToken)
    {
        var info = await InspectAsync(url, 1, limit, cancellationToken);
        return (info["entries"] as JsonArray ?? []).OfType<JsonObject>().ToList();
    }

    /// <summary>
    /// Downloads a video's audio into <paramref name="folder"/>: as YouTube has it, or converted. Without ffmpeg only
    /// the original can be had (and only as AAC/M4A, which needs no remuxing).
    /// </summary>
    public async Task<DownloadedFiles> DownloadAsync(string url, string folder, DownloadOptions options, Action<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var run = await PrepareAsync(cancellationToken);
        var (path, common) = (run.Path, run.Common);
        var hasFfmpeg = await FfmpegAsync(cancellationToken) is not null;
        if (!hasFfmpeg && (options.Format != AudioFormat.Original || options.Chapters))
            throw new ExtensionException(ErrorCodes.FfmpegRequired, 409);

        Directory.CreateDirectory(folder);
        var arguments = new List<string>(common)
        {
            "--no-playlist", "--no-mtime", "--newline", "--progress", "--quiet",
            "--progress-template", $"download:{ProgressMark} %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(progress.speed)s %(progress.eta)s",
            "--print", $"after_move:{FileMark}%(filepath)s",
            "-o", Path.Combine(folder, "audio.%(ext)s"),
        };
        if (hasFfmpeg)
        {
            arguments.AddRange(["-f", "bestaudio/best", "-x"]);
            var (format, quality) = options.Format switch
            {
                AudioFormat.Opus => ("opus", options.Quality switch { AudioQuality.Best => "192K", AudioQuality.High => "160K", _ => "128K" }),
                AudioFormat.Mp3 => ("mp3", options.Quality switch { AudioQuality.Best => "0", AudioQuality.High => "2", _ => "5" }),
                AudioFormat.M4a => ("m4a", options.Quality switch { AudioQuality.Best => "256K", AudioQuality.High => "192K", _ => "128K" }),
                AudioFormat.Flac => ("flac", (string?)null),
                // Keeps the codec YouTube has (Opus or AAC), only changing the container.
                _ => ("best", null),
            };
            arguments.AddRange(["--audio-format", format]);
            if (quality is not null)
                arguments.AddRange(["--audio-quality", quality]);
            if (options.Chapters)
                arguments.AddRange(["--split-chapters", "-o", "chapter:" + Path.Combine(folder, "chapters", "%(section_number)03d.%(ext)s")]);
        }
        else
        {
            arguments.AddRange(["-f", "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]"]);
        }

        arguments.AddRange(["--", url]);

        string? file = null;
        var result = await ProcessRunner.RunAsync(path, arguments, line =>
        {
            if (line.StartsWith(FileMark, StringComparison.Ordinal))
                file = line[FileMark.Length..].Trim();
            else if (line.StartsWith(ProgressMark, StringComparison.Ordinal))
                progress(ParseProgress(line));
        }, TimeSpan.FromHours(2), cancellationToken);

        if (result.ExitCode != 0)
            throw Failure(result.Error, ErrorCodes.DownloadFailed, url);
        file ??= Directory.EnumerateFiles(folder, "audio.*").FirstOrDefault();
        if (file is null || !File.Exists(file))
            throw Failure(result.Error, ErrorCodes.DownloadFailed, url);

        var chapters = Directory.Exists(Path.Combine(folder, "chapters"))
            ? Directory.GetFiles(Path.Combine(folder, "chapters")).Order(StringComparer.Ordinal).ToList()
            : [];
        return new DownloadedFiles(file, chapters);
    }

    private static DownloadProgress ParseProgress(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double? Number(int index) =>
            index < parts.Length && double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        var downloaded = Number(1);
        var total = Number(2) ?? Number(3);
        var fraction = downloaded is { } d && total is > 0 ? Math.Clamp(d / total.Value, 0, 1) : (double?)null;
        return new DownloadProgress(fraction, (long?)downloaded, (long?)total, Number(4), Number(5) is { } eta ? (int)eta : null);
    }

    /// <summary>The address of a video's audio stream, for a short preview (it expires within hours).</summary>
    public async Task<string> AudioStreamAsync(string url, CancellationToken cancellationToken)
    {
        using var run = await PrepareAsync(cancellationToken);
        var (path, common) = (run.Path, run.Common);
        var arguments = new List<string>(common) { "--no-playlist", "-f", "bestaudio/best", "-g", "--", url };
        var result = await ProcessRunner.RunAsync(path, arguments, null, TimeSpan.FromMinutes(1), cancellationToken);
        var stream = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (result.ExitCode != 0 || stream is null || !Uri.TryCreate(stream, UriKind.Absolute, out _))
            throw Failure(result.Error, ErrorCodes.DownloadFailed, url);
        return stream;
    }

    /// <summary>yt-dlp's message as one of the extension's codes (and the message in the log).</summary>
    private ExtensionException Failure(string error, string fallback, string url)
    {
        var message = error.Split('\n').LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal))?.Trim() ?? error.Trim();
        logger.LogWarning("yt-dlp failed for {Url}: {Message}", url, message.Length > 500 ? message[..500] : message);
        var text = message.ToLowerInvariant();
        var code = text switch
        {
            _ when text.Contains("not a bot") || text.Contains("http error 429") => ErrorCodes.BlockedByYouTube,
            _ when text.Contains("sign in to confirm your age") || text.Contains("members-only") || text.Contains("join this channel")
                   || text.Contains("sign in") => ErrorCodes.SignInRequired,
            _ when text.Contains("not available in your country") || text.Contains("geo restrict") || text.Contains("geo-restrict") => ErrorCodes.GeoBlocked,
            _ when text.Contains("live event will begin") || text.Contains("is live") || text.Contains("premieres in") => ErrorCodes.LiveNotEnded,
            _ when text.Contains("video unavailable") || text.Contains("private video") || text.Contains("has been removed")
                   || text.Contains("does not exist") || text.Contains("not available") || text.Contains("http error 404") => ErrorCodes.Unavailable,
            _ when text.Contains("unsupported url") => ErrorCodes.InvalidLink,
            _ => fallback,
        };
        return new ExtensionException(code, code is ErrorCodes.InvalidLink ? 400 : 422);
    }
}
