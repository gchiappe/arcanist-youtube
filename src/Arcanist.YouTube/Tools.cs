using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Arcanist.YouTube;

/// <summary>A program the extension runs.</summary>
public enum ToolKind
{
    /// <summary>yt-dlp: reads YouTube (and many other sites) and downloads.</summary>
    YtDlp,

    /// <summary>Deno, the JavaScript runtime yt-dlp needs for YouTube since late 2025 (without it, fewer formats).</summary>
    Deno,
}

/// <summary>Where a tool came from.</summary>
public enum ToolSource
{
    /// <summary>Set in the configuration (<c>Arcanist:Extensions:youtube:YtDlpPath</c>/<c>DenoPath</c>).</summary>
    Configured,

    /// <summary>Downloaded by the extension from the tool's official releases.</summary>
    Installed,

    /// <summary>Found on the system's PATH (also a yt-dlp installed with pip).</summary>
    System,
}

/// <param name="Latest">The newest release, when it was checked.</param>
public sealed record ToolStatus(ToolKind Kind, string? Path, string? Version, ToolSource? Source, string? Latest, bool CanInstall)
{
    public bool Installed => Path is not null;

    public bool UpdateAvailable => Latest is not null && Version is not null && Source == ToolSource.Installed
                                   && !string.Equals(Normalize(Latest), Normalize(Version), StringComparison.Ordinal);

    private static string Normalize(string version) => version.Trim().TrimStart('v');
}

/// <summary>
/// Finds yt-dlp and Deno, and installs or updates them from their official GitHub releases (checked against the
/// SHA-256 sums those releases publish) into the extension's data folder. They are separate programs the
/// extension runs, as Arcanist runs ffmpeg: yt-dlp is in the public domain (Unlicense) and Deno is MIT.
/// </summary>
public sealed partial class ToolManager(ExtensionPaths paths, IConfiguration configuration, IHttpClientFactory clients, ILogger<ToolManager> logger)
{
    public const string HttpClientName = "arcanist-youtube";

    private readonly SemaphoreSlim _installing = new(1, 1);
    private readonly Dictionary<ToolKind, (string Path, DateTime Written, string? Version)> _versions = [];
    private readonly Dictionary<ToolKind, string?> _latest = [];

    /// <summary>The asset of each tool for this machine, or null when there is none.</summary>
    private static string? Asset(ToolKind kind)
    {
        var arch = RuntimeInformation.OSArchitecture;
        return (kind, OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), arch) switch
        {
            (ToolKind.YtDlp, true, _, _, Architecture.X64) => "yt-dlp.exe",
            (ToolKind.YtDlp, true, _, _, Architecture.Arm64) => "yt-dlp_arm64.exe",
            (ToolKind.YtDlp, _, true, _, Architecture.X64) => "yt-dlp_linux",
            (ToolKind.YtDlp, _, true, _, Architecture.Arm64) => "yt-dlp_linux_aarch64",
            (ToolKind.YtDlp, _, _, true, _) => "yt-dlp_macos",
            (ToolKind.Deno, true, _, _, Architecture.X64) => "deno-x86_64-pc-windows-msvc.zip",
            (ToolKind.Deno, true, _, _, Architecture.Arm64) => "deno-aarch64-pc-windows-msvc.zip",
            (ToolKind.Deno, _, true, _, Architecture.X64) => "deno-x86_64-unknown-linux-gnu.zip",
            (ToolKind.Deno, _, true, _, Architecture.Arm64) => "deno-aarch64-unknown-linux-gnu.zip",
            (ToolKind.Deno, _, _, true, Architecture.X64) => "deno-x86_64-apple-darwin.zip",
            (ToolKind.Deno, _, _, true, Architecture.Arm64) => "deno-aarch64-apple-darwin.zip",
            _ => null,
        };
    }

    private static string Repository(ToolKind kind) => kind == ToolKind.YtDlp ? "yt-dlp/yt-dlp" : "denoland/deno";

    private static string ExecutableName(ToolKind kind) =>
        (kind == ToolKind.YtDlp ? "yt-dlp" : "deno") + (OperatingSystem.IsWindows() ? ".exe" : "");

    private string InstalledPath(ToolKind kind) => Path.Combine(paths.Tools, kind == ToolKind.YtDlp ? "yt-dlp" : "deno", ExecutableName(kind));

    /// <summary>The tool to run: the configured one, the installed one, or the system's.</summary>
    public (string Path, ToolSource Source)? Find(ToolKind kind)
    {
        var configured = configuration[kind == ToolKind.YtDlp ? "YtDlpPath" : "DenoPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? (configured, ToolSource.Configured) : null;
        if (File.Exists(InstalledPath(kind)))
            return (InstalledPath(kind), ToolSource.Installed);
        return OnPath(ExecutableName(kind)) is { } system ? (system, ToolSource.System) : null;
    }

    private static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder.Trim('"'), name))
        .FirstOrDefault(File.Exists);

    public async Task<ToolStatus> StatusAsync(ToolKind kind, CancellationToken cancellationToken)
    {
        var found = Find(kind);
        var version = found is { } f ? await VersionAsync(kind, f.Path, cancellationToken) : null;
        return new ToolStatus(kind, found?.Path, version, found?.Source, _latest.GetValueOrDefault(kind), Asset(kind) is not null);
    }

    /// <summary>The tool's version (cached while the file does not change).</summary>
    private async Task<string?> VersionAsync(ToolKind kind, string path, CancellationToken cancellationToken)
    {
        var written = File.GetLastWriteTimeUtc(path);
        lock (_versions)
        {
            if (_versions.TryGetValue(kind, out var cached) && cached.Path == path && cached.Written == written)
                return cached.Version;
        }

        string? version = null;
        try
        {
            var result = await ProcessRunner.RunAsync(path, ["--version"], null, TimeSpan.FromSeconds(30), cancellationToken);
            var first = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            // "2025.11.12" for yt-dlp; "deno 2.5.6 (stable, release, x86_64-…)" for Deno.
            version = first is null ? null : kind == ToolKind.Deno ? first.Split(' ').ElementAtOrDefault(1) : first;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "{Tool} at {Path} does not run", kind, path);
        }

        lock (_versions)
            _versions[kind] = (path, written, version);
        return version;
    }

    /// <summary>The newest release of a tool (the tag GitHub's "latest" link leads to).</summary>
    public async Task<string?> LatestAsync(ToolKind kind, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{Repository(kind)}/releases/latest");
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var final = response.RequestMessage?.RequestUri?.AbsolutePath ?? "";
            var tag = final.Split('/').LastOrDefault();
            _latest[kind] = string.IsNullOrEmpty(tag) || tag == "latest" ? null : tag;
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("Could not check the latest {Tool}: {Message}", kind, e.Message);
        }

        return _latest.GetValueOrDefault(kind);
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SumLine();

    // Any SHA-256 in a file: Deno publishes "hash  name" for Linux and macOS, and PowerShell's Get-FileHash output for Windows.
    [GeneratedRegex(@"\b([0-9a-fA-F]{64})\b")]
    private static partial Regex AnySum();

    /// <summary>
    /// Downloads a tool's latest release for this machine, checks it against the release's published SHA-256 and
    /// puts it in place of the previous one (which keeps working until then).
    /// </summary>
    public async Task<ToolStatus> InstallAsync(ToolKind kind, CancellationToken cancellationToken)
    {
        var asset = Asset(kind) ?? throw new ExtensionException(ErrorCodes.ToolUnsupported, 409);
        await _installing.WaitAsync(cancellationToken);
        try
        {
            var client = clients.CreateClient(HttpClientName);
            var baseUrl = $"https://github.com/{Repository(kind)}/releases/latest/download/";
            string expected;
            try
            {
                if (kind == ToolKind.YtDlp)
                {
                    var sums = await client.GetStringAsync(baseUrl + "SHA2-256SUMS", cancellationToken);
                    expected = SumLine().Matches(sums).FirstOrDefault(m => m.Groups[2].Value == asset)?.Groups[1].Value
                               ?? throw new ExtensionException(ErrorCodes.ToolDownloadFailed, 502);
                }
                else
                {
                    var sum = await client.GetStringAsync(baseUrl + asset + ".sha256sum", cancellationToken);
                    var match = AnySum().Match(sum);
                    if (!match.Success)
                    {
                        logger.LogWarning("No checksum for {Asset} in {Sums}", asset, sum.Length > 300 ? sum[..300] : sum);
                        throw new ExtensionException(ErrorCodes.ToolDownloadFailed, 502);
                    }

                    expected = match.Groups[1].Value;
                }
            }
            catch (HttpRequestException e)
            {
                logger.LogWarning("Could not read the checksums of {Tool}: {Message}", kind, e.Message);
                throw new ExtensionException(ErrorCodes.ToolDownloadFailed, 502);
            }

            var folder = Path.GetDirectoryName(InstalledPath(kind))!;
            Directory.CreateDirectory(folder);
            var download = Path.Combine(folder, $".download-{Guid.NewGuid():N}");
            try
            {
                try
                {
                    await using var response = await client.GetStreamAsync(baseUrl + asset, cancellationToken);
                    await using var file = File.Create(download);
                    await response.CopyToAsync(file, cancellationToken);
                }
                catch (HttpRequestException e)
                {
                    logger.LogWarning("Could not download {Asset}: {Message}", asset, e.Message);
                    throw new ExtensionException(ErrorCodes.ToolDownloadFailed, 502);
                }

                await using (var check = File.OpenRead(download))
                {
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken));
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        throw new ExtensionException(ErrorCodes.ToolChecksumMismatch, 502);
                }

                var staged = Path.Combine(folder, $".staged-{Guid.NewGuid():N}");
                if (asset.EndsWith(".zip", StringComparison.Ordinal))
                {
                    using var zip = ZipFile.OpenRead(download);
                    var entry = zip.Entries.FirstOrDefault(e => e.Name == ExecutableName(kind))
                                ?? throw new ExtensionException(ErrorCodes.ToolDownloadFailed, 502);
                    entry.ExtractToFile(staged);
                }
                else
                {
                    File.Move(download, staged);
                }

                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                                 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(staged, InstalledPath(kind), overwrite: true);
                logger.LogInformation("Installed {Tool} from {Asset}", kind, asset);
            }
            finally
            {
                if (File.Exists(download))
                    File.Delete(download);
            }

            await LatestAsync(kind, cancellationToken);
            return await StatusAsync(kind, cancellationToken);
        }
        finally
        {
            _installing.Release();
        }
    }
}

/// <summary>Runs a program and collects what it prints.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, string Error);

    /// <summary>
    /// Runs <paramref name="path"/> with <paramref name="arguments"/> (never through a shell). Each line of output goes
    /// to <paramref name="onLine"/> as it comes. Cancelling, or going past <paramref name="timeout"/>, kills it.
    /// </summary>
    public static async Task<Result> RunAsync(string path, IEnumerable<string> arguments, Action<string>? onLine, TimeSpan timeout,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null, Stream? stdout = null)
    {
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            start.Environment[key] = value;
        // Python (yt-dlp) prints UTF-8 whatever the console's code page.
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        start.StandardOutputEncoding = System.Text.Encoding.UTF8;
        start.StandardErrorEncoding = System.Text.Encoding.UTF8;

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{path} did not start.");
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        await using var registration = limit.Token.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();
        Task readOut;
        if (stdout is not null)
        {
            readOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, limit.Token);
        }
        else
        {
            readOut = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    output.AppendLine(line);
                    onLine?.Invoke(line);
                }
            }, CancellationToken.None);
        }

        var readErr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (error.Length < 64 * 1024)
                    error.AppendLine(line);
                onLine?.Invoke(line);
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(readOut, readErr);
        }
        catch (OperationCanceledException) when (stdout is not null)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (limit.IsCancellationRequested)
            throw new TimeoutException($"{Path.GetFileName(path)} took too long.");
        return new Result(process.ExitCode, output.ToString(), error.ToString());
    }
}
