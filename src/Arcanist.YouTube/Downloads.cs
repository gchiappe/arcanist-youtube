using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Arcanist.Core.Application;
using Arcanist.Core.Application.Libraries;
using Arcanist.Core.Application.Organization;
using Arcanist.Core.Auth;
using Arcanist.Core.Data;
using Arcanist.Core.Extensions;
using Arcanist.Core.Organization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arcanist.YouTube;

public enum BatchStatus
{
    /// <summary>Finding the songs of a list or channel (all of them were asked for).</summary>
    Listing,

    /// <summary>Some songs are waiting or on their way.</summary>
    Active,

    /// <summary>Every song is in a library (or was already there).</summary>
    Done,

    /// <summary>Finished, some songs failed.</summary>
    Failed,

    /// <summary>Finished, some songs were cancelled.</summary>
    Cancelled,
}

public enum ItemStatus
{
    Queued,
    Downloading,

    /// <summary>Converting to the format asked for.</summary>
    Converting,

    /// <summary>Putting it into the library.</summary>
    Importing,

    /// <summary>In the library (its scan catalogues it in a few seconds).</summary>
    Added,

    /// <summary>The library already had this audio.</summary>
    Duplicate,
    Failed,
    Cancelled,
}

/// <param name="EmbedArtwork">Put the video's picture in the file as its cover.</param>
/// <param name="SquareArtwork">Crop that picture to a square (video frames are 16:9; YouTube Music covers are square).</param>
/// <param name="SplitChapters">Split videos at their chapters (an album uploaded as one video).</param>
/// <param name="NumberTracks">Number the songs in list order (albums).</param>
public sealed record DownloadSettings(
    AudioFormat Format,
    AudioQuality Quality,
    bool EmbedArtwork,
    bool SquareArtwork,
    bool SplitChapters,
    bool NumberTracks,
    string? Album,
    string? AlbumArtist);

/// <summary>One song (one video) of a download.</summary>
/// <param name="Tags">The tags it is saved with (those proposed, or the user's for a single video).</param>
/// <param name="Files">How many files it gave (more than one when split at its chapters).</param>
/// <param name="TrackId">The track of its only file, once the library has catalogued it (as before 0.3; see <paramref name="TrackIds"/>).</param>
/// <param name="ContentHashes">The audio of each file it put into the library (Arcanist's <c>ContentHash</c>), to find their tracks.</param>
/// <param name="TrackIds">The track each of those files became (same order; null until the library catalogues it): the app plays them.</param>
public sealed record DownloadItem(
    Guid Id,
    string VideoId,
    string Url,
    int? Index,
    string? Title,
    string? Uploader,
    double? Duration,
    ItemStatus Status,
    double? Progress,
    double? Speed,
    int? Eta,
    string? ErrorCode,
    SongTags? Tags,
    string? FileName,
    Guid? TrackId,
    int Files,
    DateTime UpdatedAt,
    IReadOnlyList<string>? ContentHashes = null,
    IReadOnlyList<Guid?>? TrackIds = null)
{
    /// <summary>In the library, but some of its files are not catalogued yet (their tracks are still unknown).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AwaitsTracks =>
        Status is ItemStatus.Added or ItemStatus.Duplicate && ContentHashes is { Count: > 0 } hashes
        && (TrackIds is not { } tracks || tracks.Count != hashes.Count || tracks.Any(t => t is null));
}

/// <summary>What one link brought: one song, or many from a list.</summary>
public sealed record DownloadBatch(
    Guid Id,
    Guid UserId,
    string Url,
    LinkKind Kind,
    string? Title,
    string? Thumbnail,
    Guid LibraryId,
    string? LibraryName,
    DownloadSettings Settings,
    BatchStatus Status,
    string? ErrorCode,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<DownloadItem> Items,
    IReadOnlyList<string>? Exclude = null,
    string? ListUrl = null)
{
    public int Total => Items.Count;

    public int Finished => Items.Count(i => i.Status is ItemStatus.Added or ItemStatus.Duplicate or ItemStatus.Failed or ItemStatus.Cancelled);
}

/// <summary>A song asked for, as the app knows it from the list.</summary>
public sealed record RequestedEntry(string Id, string? Url, int? Index, string? Title, string? Uploader, double? Duration);

/// <summary>
/// The downloads of every user: what they asked for (kept 30 days in the extension's data folder, so they survive
/// restarts) and the workers that bring each song into its library as the user's upload. The administrator's
/// setting says how many go at once.
/// </summary>
public sealed class DownloadQueue(
    ExtensionPaths paths,
    SettingsStore settings,
    IServiceScopeFactory scopes,
    ILogger<DownloadQueue> logger) : BackgroundService
{
    public const string Source = "youtube";

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _running = [];
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly List<Channel<DownloadBatch>> _listeners = [];
    private List<DownloadBatch>? _batches;

    private List<DownloadBatch> Batches
    {
        get
        {
            if (_batches is not null)
                return _batches;
            try
            {
                _batches = File.Exists(paths.DownloadsFile)
                    ? JsonSerializer.Deserialize<List<DownloadBatch>>(File.ReadAllText(paths.DownloadsFile), Json.Options) ?? []
                    : [];
            }
            catch (JsonException e)
            {
                logger.LogWarning(e, "The list of downloads could not be read; starting a new one");
                _batches = [];
            }

            // Those interrupted by a restart start again.
            _batches = _batches.Select(b => b with
            {
                Items = b.Items.Select(i => i.Status is ItemStatus.Downloading or ItemStatus.Converting or ItemStatus.Importing
                    ? i with { Status = ItemStatus.Queued, Progress = null, Speed = null, Eta = null }
                    : i).ToList(),
            }).ToList();
            return _batches;
        }
    }

    private void Save()
    {
        try
        {
            Files.WriteAllTextAtomically(paths.DownloadsFile, JsonSerializer.Serialize(Batches, Json.Options));
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "The list of downloads could not be saved");
        }
    }

    // ── For the API ──────────────────────────────────────────────────────────

    public IReadOnlyList<DownloadBatch> Of(Guid user)
    {
        lock (_gate)
            return Batches.Where(b => b.UserId == user).OrderByDescending(b => b.CreatedAt).ToList();
    }

    public DownloadBatch? Find(Guid user, Guid id)
    {
        lock (_gate)
            return Batches.FirstOrDefault(b => b.Id == id && b.UserId == user);
    }

    /// <summary>Of <paramref name="videoIds"/>, those this user already brought into a library.</summary>
    public IEnumerable<string> DownloadedIds(Guid user, IEnumerable<string> videoIds)
    {
        lock (_gate)
        {
            var have = Batches.Where(b => b.UserId == user).SelectMany(b => b.Items)
                .Where(i => i.Status is ItemStatus.Added or ItemStatus.Duplicate).Select(i => i.VideoId).ToHashSet(StringComparer.Ordinal);
            return videoIds.Where(have.Contains).ToList();
        }
    }

    public DownloadBatch Add(DownloadBatch batch)
    {
        lock (_gate)
        {
            Batches.Add(batch);
            Save();
        }

        Changed(batch);
        Wake();
        return batch;
    }

    /// <summary>Cancels what is waiting or on its way (of a batch, or one of its songs).</summary>
    public DownloadBatch Cancel(Guid user, Guid batchId, Guid? itemId)
    {
        DownloadBatch batch;
        lock (_gate)
        {
            batch = Batches.FirstOrDefault(b => b.Id == batchId && b.UserId == user) ?? throw new ArcanistException.NotFound();
            foreach (var item in batch.Items.Where(i => itemId is null || i.Id == itemId))
            {
                if (_running.TryGetValue(item.Id, out var running))
                    running.Cancel();
            }

            batch = Update(batchId, b => b with
            {
                Status = b.Status == BatchStatus.Listing ? BatchStatus.Cancelled : b.Status,
                Items = b.Items.Select(i => (itemId is null || i.Id == itemId) && i.Status is ItemStatus.Queued or ItemStatus.Downloading or ItemStatus.Converting
                    ? i with { Status = ItemStatus.Cancelled, Progress = null, Speed = null, Eta = null, UpdatedAt = DateTime.UtcNow }
                    : i).ToList(),
            });
            Save();
        }

        Changed(batch);
        return batch;
    }

    /// <summary>Queues again what failed or was cancelled (of a batch, or one of its songs).</summary>
    public DownloadBatch Retry(Guid user, Guid batchId, Guid? itemId)
    {
        DownloadBatch batch;
        lock (_gate)
        {
            batch = Batches.FirstOrDefault(b => b.Id == batchId && b.UserId == user) ?? throw new ArcanistException.NotFound();
            if (!batch.Items.Any(i => (itemId is null || i.Id == itemId) && i.Status is ItemStatus.Failed or ItemStatus.Cancelled)
                && batch.Status is not (BatchStatus.Failed or BatchStatus.Cancelled))
                throw new ExtensionException(ErrorCodes.WrongState, 409);
            batch = Update(batchId, b => b with
            {
                // A list that failed to be listed is listed again.
                Status = b.Items.Count == 0 && b.Status is BatchStatus.Failed or BatchStatus.Cancelled ? BatchStatus.Listing : b.Status,
                ErrorCode = null,
                Items = b.Items.Select(i => (itemId is null || i.Id == itemId) && i.Status is ItemStatus.Failed or ItemStatus.Cancelled
                    ? i with { Status = ItemStatus.Queued, ErrorCode = null, UpdatedAt = DateTime.UtcNow }
                    : i).ToList(),
            });
            Save();
        }

        Changed(batch);
        Wake();
        return batch;
    }

    /// <summary>Forgets a batch (cancelling what is left of it); nothing already in a library is touched.</summary>
    public void Remove(Guid user, Guid batchId)
    {
        lock (_gate)
        {
            var batch = Batches.FirstOrDefault(b => b.Id == batchId && b.UserId == user) ?? throw new ArcanistException.NotFound();
            foreach (var item in batch.Items)
            {
                if (_running.TryGetValue(item.Id, out var running))
                    running.Cancel();
            }

            Batches.RemoveAll(b => b.Id == batchId);
            Save();
        }
    }

    /// <summary>Forgets the user's finished batches.</summary>
    public int Clear(Guid user)
    {
        lock (_gate)
        {
            var removed = Batches.RemoveAll(b => b.UserId == user && b.Status is BatchStatus.Done or BatchStatus.Failed or BatchStatus.Cancelled);
            Save();
            return removed;
        }
    }

    /// <summary>Follows the changes of the user's downloads (for the app's live view).</summary>
    public async IAsyncEnumerable<DownloadBatch> WatchAsync(Guid user, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<DownloadBatch>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_listeners)
            _listeners.Add(channel);
        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (batch.UserId == user)
                    yield return batch;
            }
        }
        finally
        {
            lock (_listeners)
                _listeners.Remove(channel);
        }
    }

    private void Changed(DownloadBatch batch)
    {
        lock (_listeners)
        {
            foreach (var listener in _listeners)
                listener.Writer.TryWrite(batch);
        }
    }

    public void Wake() => _wake.Release();

    /// <summary>Changes a batch (under the lock) and works out its status from its songs.</summary>
    private DownloadBatch Update(Guid batchId, Func<DownloadBatch, DownloadBatch> change)
    {
        var index = Batches.FindIndex(b => b.Id == batchId);
        if (index < 0)
            throw new ArcanistException.NotFound();
        var changed = change(Batches[index]);
        if (changed.Status != BatchStatus.Listing)
        {
            var items = changed.Items;
            changed = changed with
            {
                Status = items.Any(i => i.Status is ItemStatus.Queued or ItemStatus.Downloading or ItemStatus.Converting or ItemStatus.Importing) ? BatchStatus.Active
                    : items.Count == 0 ? changed.Status is BatchStatus.Active ? BatchStatus.Done : changed.Status
                    : items.Any(i => i.Status == ItemStatus.Failed) ? BatchStatus.Failed
                    : items.Any(i => i.Status == ItemStatus.Cancelled) ? BatchStatus.Cancelled
                    : BatchStatus.Done,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        Batches[index] = changed;
        return changed;
    }

    /// <summary>Looks for the tracks of an item's files for three minutes, while its library is scanned.</summary>
    private async Task FindTracksAsync(Guid batchId, Guid itemId, CancellationToken stopping)
    {
        try
        {
            for (var attempt = 0; attempt < 36; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stopping);
                DownloadBatch? batch;
                lock (_gate)
                    batch = Batches.FirstOrDefault(b => b.Id == batchId);
                if (batch?.Items.FirstOrDefault(i => i.Id == itemId) is not { AwaitsTracks: true } item)
                    return;
                if (!await ResolveAsync(batch.LibraryId, [item], stopping))
                    continue;
                lock (_gate)
                    if (Batches.FirstOrDefault(b => b.Id == batchId)?.Items.FirstOrDefault(i => i.Id == itemId) is not { AwaitsTracks: true })
                        return;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            // The server stops, or the database is changing: the songs are in the library all the same.
        }
    }

    /// <summary>
    /// The tracks of this user's songs that are in their libraries but were not catalogued yet when last seen (a slow
    /// scan, a restart): looked up once, when the app reads its downloads.
    /// </summary>
    public async Task ResolveTracksAsync(Guid user, CancellationToken cancellationToken)
    {
        List<(Guid Library, DownloadItem Item)> waiting;
        lock (_gate)
            waiting = Batches.Where(b => b.UserId == user)
                .SelectMany(b => b.Items.Where(i => i.AwaitsTracks).Select(i => (b.LibraryId, i))).Take(500).ToList();
        try
        {
            foreach (var library in waiting.GroupBy(w => w.Library))
                await ResolveAsync(library.Key, library.Select(w => w.Item).ToList(), cancellationToken);
        }
        catch (InvalidOperationException e)
        {
            logger.LogDebug(e, "The tracks of the downloads could not be looked up now");
        }
    }

    /// <summary>Finds the tracks of these items' files in their library, by their audio; true when it found any.</summary>
    private async Task<bool> ResolveAsync(Guid libraryId, IReadOnlyList<DownloadItem> items, CancellationToken cancellationToken)
    {
        var hashes = items.SelectMany(i => i.ContentHashes ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (hashes.Count == 0)
            return false;
        using var scope = scopes.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<IArcanistDbContextFactory>().CreateDbContext();
        var found = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.MediaFiles.Where(f => hashes.Contains(f.ContentHash) && f.MissingSince == null && f.LibrarySource.LibraryId == libraryId)
                .Select(f => new { f.ContentHash, f.TrackId }), cancellationToken);
        if (found.Count == 0)
            return false;
        var tracks = found.GroupBy(f => f.ContentHash, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(f => f.TrackId), StringComparer.Ordinal);

        var any = false;
        foreach (var item in items)
        {
            var known = item.ContentHashes!.Select((hash, i) => tracks.TryGetValue(hash, out var track) ? track : item.TrackIds?.ElementAtOrDefault(i)).ToList();
            if (known.SequenceEqual(item.TrackIds ?? []))
                continue;
            any = true;
            var batchId = BatchOf(item.Id);
            if (batchId is not null)
                UpdateItem(batchId.Value, item.Id, i => i with { TrackIds = known, TrackId = known.Count == 1 ? known[0] : i.TrackId }, save: true);
        }

        return any;
    }

    private Guid? BatchOf(Guid itemId)
    {
        lock (_gate)
            return Batches.FirstOrDefault(b => b.Items.Any(i => i.Id == itemId))?.Id;
    }

    private void UpdateItem(Guid batchId, Guid itemId, Func<DownloadItem, DownloadItem> change, bool save)
    {
        DownloadBatch batch;
        lock (_gate)
        {
            if (Batches.All(b => b.Id != batchId))
                return;
            batch = Update(batchId, b => b with
            {
                Items = b.Items.Select(i => i.Id == itemId ? change(i) with { UpdatedAt = DateTime.UtcNow } : i).ToList(),
            });
            if (save)
                Save();
        }

        Changed(batch);
    }

    // ── The workers ──────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = scopes.CreateScope().ServiceProvider.GetRequiredService<ExtensionHost>();
        var running = new List<Task>();
        var lastPrune = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);
            if (DateTime.UtcNow - lastPrune > TimeSpan.FromHours(1))
            {
                Prune();
                lastPrune = DateTime.UtcNow;
            }

            // Switched off, nothing new starts.
            if (host.IsEnabled(YouTubeExtension.Id))
            {
                while (running.Count < Math.Clamp(settings.Current.Concurrency, 1, YouTubeSettings.MaxConcurrency) && Next() is { } next)
                    running.Add(Task.Run(() => next.Item is null ? ListAsync(next.Batch, stoppingToken) : ProcessAsync(next.Batch, next.Item, stoppingToken), CancellationToken.None));
            }

            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(running.Count > 0 ? 1 : 30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await Task.WhenAll(running);
    }

    /// <summary>The next thing to do: a list to find the songs of, or a song waiting (oldest first).</summary>
    private (DownloadBatch Batch, DownloadItem? Item)? Next()
    {
        lock (_gate)
        {
            foreach (var batch in Batches.OrderBy(b => b.CreatedAt))
            {
                if (batch.Status == BatchStatus.Listing && !_running.ContainsKey(batch.Id))
                {
                    _running[batch.Id] = new CancellationTokenSource();
                    return (batch, null);
                }

                var item = batch.Items.FirstOrDefault(i => i.Status == ItemStatus.Queued && !_running.ContainsKey(i.Id));
                if (item is not null)
                {
                    _running[item.Id] = new CancellationTokenSource();
                    return (batch, item);
                }
            }

            return null;
        }
    }

    private CancellationToken TokenFor(Guid id, CancellationToken stopping)
    {
        lock (_gate)
            return CancellationTokenSource.CreateLinkedTokenSource(_running[id].Token, stopping).Token;
    }

    private void Done(Guid id)
    {
        lock (_gate)
        {
            if (_running.Remove(id, out var source))
                source.Dispose();
        }

        Wake();
    }

    /// <summary>Finds every song of a list or channel (the user asked for all of them), up to the administrator's limit.</summary>
    private async Task ListAsync(DownloadBatch batch, CancellationToken stopping)
    {
        try
        {
            var token = TokenFor(batch.Id, stopping);
            using var scope = scopes.CreateScope();
            var ytDlp = scope.ServiceProvider.GetRequiredService<YtDlp>();
            var limit = Math.Clamp(settings.Current.MaxItems, 1, YouTubeSettings.MaxItemsLimit);
            using var acting = YtDlp.ActingFor(batch.UserId);
            var entries = await ytDlp.ListAllAsync(batch.ListUrl ?? batch.Url, batch.Kind == LinkKind.Mix ? Inspector.MixLimit : limit, token);
            var exclude = (batch.Exclude ?? []).ToHashSet(StringComparer.Ordinal);
            // Songs of another site need their own links (YouTube's are made from their ids).
            var youtube = Uri.TryCreate(batch.Url, UriKind.Absolute, out var listUri) && UrlClassifier.IsYouTubeHost(listUri.Host);
            var items = entries.Select((entry, i) => (Entry: entry, Index: i + 1))
                .Where(e => youtube || new[] { TagGuesser.Text(e.Entry, "url"), TagGuesser.Text(e.Entry, "webpage_url") }.Any(u => u?.StartsWith("http", StringComparison.Ordinal) == true))
                .Where(e => TagGuesser.Text(e.Entry, "id") is { } id && !exclude.Contains(id)
                            && TagGuesser.Text(e.Entry, "title") is not ("[Private video]" or "[Deleted video]"))
                .Select(e => NewItem(new RequestedEntry(TagGuesser.Text(e.Entry, "id")!,
                    new[] { TagGuesser.Text(e.Entry, "url"), TagGuesser.Text(e.Entry, "webpage_url") }.FirstOrDefault(u => u?.StartsWith("http", StringComparison.Ordinal) == true), e.Index,
                    TagGuesser.Text(e.Entry, "title"), TagGuesser.CleanChannel(TagGuesser.Text(e.Entry, "channel") ?? TagGuesser.Text(e.Entry, "uploader")),
                    TagGuesser.Number(e.Entry, "duration")), batch))
                .ToList();
            lock (_gate)
            {
                var updated = Update(batch.Id, b => b with { Status = items.Count == 0 ? BatchStatus.Failed : BatchStatus.Active, ErrorCode = items.Count == 0 ? ErrorCodes.Empty : null, Items = items });
                Save();
                Changed(updated);
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var code = e is ArcanistException known ? known.Code : ErrorCodes.InspectFailed;
            if (e is not ArcanistException)
                logger.LogWarning(e, "Listing {Url} failed", batch.Url);
            lock (_gate)
            {
                if (Batches.Any(b => b.Id == batch.Id))
                {
                    var updated = Update(batch.Id, b => b with { Status = BatchStatus.Failed, ErrorCode = code });
                    Save();
                    Changed(updated);
                }
            }
        }
        finally
        {
            Done(batch.Id);
        }
    }

    public static DownloadItem NewItem(RequestedEntry entry, DownloadBatch batch) =>
        new(Guid.CreateVersion7(), entry.Id,
            entry.Url is { } url && url.StartsWith("http", StringComparison.Ordinal) ? url
                : $"https://{(batch.Url.Contains("music.youtube.com", StringComparison.Ordinal) ? "music" : "www")}.youtube.com/watch?v={entry.Id}",
            entry.Index, entry.Title, entry.Uploader, entry.Duration, ItemStatus.Queued, null, null, null, null, null, null, null, 0, DateTime.UtcNow);

    /// <summary>One song: downloaded, converted, tagged and put into the library as the user's upload.</summary>
    private async Task ProcessAsync(DownloadBatch batch, DownloadItem item, CancellationToken stopping)
    {
        var folder = Path.Combine(paths.Work, item.Id.ToString("N"));
        try
        {
            var token = TokenFor(item.Id, stopping);
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            using var acting = YtDlp.ActingFor(batch.UserId);
            var access = await services.GetRequiredService<UserAccessCache>().GetUserAsync(batch.UserId, token);
            if (access is null || access.IsDisabled)
                throw new ArcanistException.Forbidden();

            UpdateItem(batch.Id, item.Id, i => i with { Status = ItemStatus.Downloading, Progress = 0 }, save: false);
            var ytDlp = services.GetRequiredService<YtDlp>();
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);

            // Its full description: the tags proposed, the picture and the chapters.
            var info = await ytDlp.InspectAsync(item.Url, 1, 1, token);
            var options = batch.Settings;
            var number = options.NumberTracks ? item.Index : null;
            // The user's tags (a single video) are written as they are; guessed ones only fill what the file lacks.
            var chosen = item.Tags is not null;
            // The list's title for the song, when it had one (some sites name the file instead).
            var described = info.DeepClone().AsObject();
            if (item.Title is { Length: > 0 } listed)
                described["title"] = listed;
            var tags = item.Tags ?? TagGuesser.Guess(described, options.Album, number);
            tags = tags with
            {
                Album = options.Album ?? tags.Album,
                AlbumArtist = options.AlbumArtist ?? tags.AlbumArtist,
                TrackNumber = number ?? tags.TrackNumber,
            };
            UpdateItem(batch.Id, item.Id, i => i with { Title = i.Title ?? TagGuesser.Text(info, "title"), Tags = tags }, save: false);

            var lastReport = DateTime.MinValue;
            var files = await ytDlp.DownloadAsync(item.Url, folder, new DownloadOptions(options.Format, options.Quality, options.SplitChapters), progress =>
            {
                // At most a few times a second; the whole file downloaded means converting.
                if (progress.Fraction < 1 && DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(400))
                    return;
                lastReport = DateTime.UtcNow;
                UpdateItem(batch.Id, item.Id, i => i with
                {
                    Status = progress.Fraction >= 1 && options.Format != AudioFormat.Original ? ItemStatus.Converting : ItemStatus.Downloading,
                    Progress = progress.Fraction, Speed = progress.Speed, Eta = progress.Eta,
                }, save: false);
            }, token);

            UpdateItem(batch.Id, item.Id, i => i with { Status = ItemStatus.Importing, Progress = 1, Speed = null, Eta = null }, save: false);
            var cover = options.EmbedArtwork
                ? await services.GetRequiredService<Images>().CoverAsync(Inspector.Thumbnail(info, large: true), options.SquareArtwork, token)
                : null;

            // Split at its chapters, each chapter is a song of the video's album; otherwise the whole audio.
            var chapters = (info["chapters"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var songs = files.Chapters.Count > 0
                ? files.Chapters.Select((file, i) => (File: file, Tags: tags with
                {
                    Title = chapters.ElementAtOrDefault(i) is { } chapter ? TagGuesser.Text(chapter, "title") ?? $"{tags.Title} ({i + 1})" : $"{tags.Title} ({i + 1})",
                    Album = tags.Album ?? tags.Title,
                    AlbumArtist = tags.AlbumArtist ?? tags.Artist,
                    TrackNumber = i + 1,
                })).ToList()
                : [(File: files.Main, Tags: tags)];

            var uploads = services.GetRequiredService<UploadService>();
            var maintenance = services.GetRequiredService<DatabaseMaintenance>();
            UploadResult? last = null;
            var duplicates = 0;
            var results = new List<UploadResult>();
            foreach (var song in songs)
            {
                try
                {
                    var write = chosen ? song.Tags : Missing(song.File, song.Tags);
                    TagWriter.Write(song.File, new ImportTagsInput(write.Title, write.Artist, write.Album, write.AlbumArtist,
                        write.Year, write.TrackNumber, null, write.Genre), cover is not null && (chosen || !HasPicture(song.File)) ? cover : null);
                }
                catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
                {
                    logger.LogWarning(e, "Could not tag {File}; it goes in as it came", song.File);
                }

                var name = Files.SafeName(song.Tags.Artist is { } artist ? $"{artist} - {song.Tags.Title}" : song.Tags.Title) + Path.GetExtension(song.File);
                using (await maintenance.WorkAsync(token))
                {
                    await using var stream = File.OpenRead(song.File);
                    last = await uploads.UploadAsync(access, batch.LibraryId, name, stream, Source, token);
                }

                results.Add(last);
                if (last.Status == UploadStatus.Duplicate)
                    duplicates++;
            }

            UpdateItem(batch.Id, item.Id, i => i with
            {
                Status = duplicates == songs.Count ? ItemStatus.Duplicate : ItemStatus.Added,
                FileName = songs.Count == 1 ? last?.Path ?? Path.GetFileName(songs[0].File) : null,
                TrackId = songs.Count == 1 ? last?.TrackId : null,
                ContentHashes = results.Select(r => r.ContentHash).ToList(),
                TrackIds = results.Select(r => r.TrackId).ToList(),
                Files = songs.Count,
                Progress = null,
            }, save: true);
            // A new song becomes a track once its library is scanned (some seconds later): the app learns it then, to
            // play it or open it.
            if (results.Any(r => r.TrackId is null))
                _ = FindTracksAsync(batch.Id, item.Id, stopping);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            UpdateItem(batch.Id, item.Id, i => i with { Status = ItemStatus.Cancelled, Progress = null, Speed = null, Eta = null }, save: true);
        }
        catch (OperationCanceledException)
        {
            // Stopping: it starts again with the server.
        }
        catch (Exception e)
        {
            var code = e is ArcanistException known
                ? known is ArcanistException.Invalid invalid ? invalid.Errors.ToDictionary().Values.SelectMany(c => c).FirstOrDefault() ?? known.Code : known.Code
                : ErrorCodes.DownloadFailed;
            if (e is not ArcanistException)
                logger.LogWarning(e, "Downloading {Url} failed", item.Url);
            UpdateItem(batch.Id, item.Id, i => i with { Status = ItemStatus.Failed, ErrorCode = code, Progress = null, Speed = null, Eta = null }, save: true);
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }

            Done(item.Id);
        }
    }

    /// <summary>Of the tags guessed, those the file does not have already (its own are usually better).</summary>
    private static SongTags Missing(string file, SongTags guessed)
    {
        try
        {
            var current = new ATL.Track(file);
            static bool Empty(string? text) => string.IsNullOrWhiteSpace(text);
            return new SongTags(
                Empty(current.Title) ? guessed.Title : null,
                Empty(current.Artist) ? guessed.Artist : null,
                Empty(current.Album) ? guessed.Album : null,
                Empty(current.AlbumArtist) ? guessed.AlbumArtist : null,
                current.Year is null or <= 0 ? guessed.Year : null,
                current.TrackNumber is null or <= 0 ? guessed.TrackNumber : null,
                Empty(current.Genre) ? guessed.Genre : null);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
        {
            return guessed;
        }
    }

    private static bool HasPicture(string file)
    {
        try
        {
            return new ATL.Track(file).EmbeddedPictures.Count > 0;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Forgets finished batches older than 30 days and pictures kept for long, and leftovers of downloads.</summary>
    private void Prune()
    {
        lock (_gate)
        {
            var removed = Batches.RemoveAll(b => b.Status is BatchStatus.Done or BatchStatus.Failed or BatchStatus.Cancelled
                                                 && b.UpdatedAt < DateTime.UtcNow.AddDays(-30));
            if (removed > 0)
                Save();
        }

        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<Images>().Prune();
            if (Directory.Exists(paths.Work))
            {
                lock (_gate)
                {
                    foreach (var folder in Directory.GetDirectories(paths.Work))
                    {
                        if (Guid.TryParseExact(Path.GetFileName(folder), "N", out var id) && !_running.ContainsKey(id))
                            Directory.Delete(folder, recursive: true);
                    }
                }
            }
        }
        catch (IOException e)
        {
            logger.LogDebug(e, "Cleaning up left something behind");
        }
    }
}
