// A stand-in for yt-dlp. It reads "fake-catalog.json" two folders above the --cache-dir it is given (the extension's
// data folder, one per test server): { "<url>": { info (as yt-dlp -J prints it), "file": WAV to "download",
// "error": message for stderr, "delayMs": time per progress step } }. It answers --version, -J (with
// --playlist-items a:b), -g and downloads (with the progress template and --print after_move:…).
using System.Text.Json.Nodes;

var arguments = args.ToList();
if (arguments.Contains("--version"))
{
    Console.WriteLine("2026.01.01");
    return 0;
}

string? Option(string name)
{
    var index = arguments.IndexOf(name);
    return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
}

var url = arguments.IndexOf("--") is var separator and >= 0 && separator + 1 < arguments.Count ? arguments[separator + 1] : arguments.LastOrDefault() ?? "";
var cache = Option("--cache-dir") ?? throw new InvalidOperationException("No --cache-dir");
var catalogFile = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cache)!)!, "fake-catalog.json");
var catalog = JsonNode.Parse(File.ReadAllText(catalogFile))!.AsObject();

// Every call is written down, for the tests to check what was asked.
File.AppendAllText(catalogFile + ".calls", string.Join(" ", arguments) + Environment.NewLine);

// Searches (ytsearchN:…) answer with the catalog's "search" entry, whatever is searched.
if (url.StartsWith("ytsearch", StringComparison.Ordinal))
    url = "search";

if (catalog[url] is not JsonObject entry)
{
    Console.Error.WriteLine($"ERROR: [youtube] {url}: Video unavailable");
    return 1;
}

if (entry["error"]?.GetValue<string>() is { } error)
{
    Console.Error.WriteLine($"ERROR: [youtube] {error}");
    return 1;
}

var info = entry["info"]!.AsObject();
if (arguments.Contains("-g"))
{
    Console.WriteLine("https://media.invalid/audio");
    return 0;
}

if (arguments.Contains("-J"))
{
    var copy = info.DeepClone().AsObject();
    if (Option("--playlist-items") is { } range && copy["entries"] is JsonArray entries)
    {
        var parts = range.Split(':');
        var first = int.Parse(parts[0]);
        var last = parts.Length > 1 && parts[1].Length > 0 ? int.Parse(parts[1]) : int.MaxValue;
        var slice = entries.Skip(first - 1).Take(Math.Max(0, last - first + 1)).Select(e => e!.DeepClone()).ToArray();
        copy["entries"] = new JsonArray(slice);
    }

    Console.WriteLine(copy.ToJsonString());
    return 0;
}

// A download: progress, the file, and where it is. A video (its picture asked for) is the entry's "video" (an MP4).
var video = Option("-f")?.StartsWith("bv", StringComparison.Ordinal) == true || Option("-f")?.StartsWith("b[ext=mp4]", StringComparison.Ordinal) == true;
var file = entry[video ? "video" : "file"]!.GetValue<string>();
var output = Option("-o")!.Replace("%(ext)s", video ? "mp4" : "wav");
var delay = entry["delayMs"]?.GetValue<int>() ?? 0;
var total = new FileInfo(file).Length;
var template = arguments.FirstOrDefault(a => a.StartsWith("download:", StringComparison.Ordinal))?["download:".Length..] ?? "";
for (var step = 1; step <= 4; step++)
{
    var line = template
        .Replace("%(progress.downloaded_bytes)s", (total * step / 4).ToString())
        .Replace("%(progress.total_bytes)s", total.ToString())
        .Replace("%(progress.total_bytes_estimate)s", "NA")
        .Replace("%(progress.speed)s", "1048576")
        .Replace("%(progress.eta)s", (4 - step).ToString());
    Console.WriteLine(line);
    Console.Out.Flush();
    if (delay > 0)
        Thread.Sleep(delay);
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.Copy(file, output, overwrite: true);
var print = arguments.FirstOrDefault(a => a.StartsWith("after_move:", StringComparison.Ordinal))?["after_move:".Length..];
if (print is not null)
    Console.WriteLine(print.Replace("%(filepath)s", output));

if (arguments.Contains("--split-chapters") && info["chapters"] is JsonArray chapters)
{
    var folder = Path.Combine(Path.GetDirectoryName(output)!, "chapters");
    Directory.CreateDirectory(folder);
    for (var i = 0; i < chapters.Count; i++)
        File.Copy(file, Path.Combine(folder, $"{i + 1:000}.wav"), overwrite: true);
}

return 0;
