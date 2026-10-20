using Arcanist.Core.Extensions;
using Arcanist.Extensibility;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arcanist.YouTube;

/// <summary>
/// YouTube Audio for Arcanist: brings the audio of YouTube videos, playlists, channels and YouTube Music albums (and,
/// when the administrator allows them, of other sites yt-dlp reads) into a library, as the user's uploads. It offers
/// an app players open (<c>/ext/youtube/</c>) and a REST API (<c>/ext/youtube/api/v1</c>).
/// </summary>
public sealed class YouTubeExtension : IArcanistExtension
{
    public const string Id = "youtube";

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        var paths = new ExtensionPaths(context.DataDirectory, context.PackageDirectory);
        services.AddSingleton(paths);
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(provider => new ToolManager(paths, context.Configuration, provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<ToolManager>>()));
        services.AddSingleton<CookieStore>();
        services.AddSingleton<UserPreferences>();
        services.AddKeyedScoped<IExtensionAdminPage, YouTubeAdminPage>(context.Id);
        services.AddSingleton<YtDlp>();
        services.AddSingleton<Images>();
        services.AddScoped<Inspector>();
        services.AddSingleton<DownloadQueue>();
        services.AddHostedService(provider => provider.GetRequiredService<DownloadQueue>());
        services.AddHostedService<ToolUpdater>();
        services.AddHttpClient(ToolManager.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Arcanist-YouTube/" + (typeof(YouTubeExtension).Assembly.GetName().Version?.ToString(3) ?? "0"));
            client.Timeout = TimeSpan.FromMinutes(10);
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ExtensionContext context)
    {
        var paths = endpoints.ServiceProvider.GetRequiredService<ExtensionPaths>();
        Api.Map(endpoints, context.BasePath);
        Api.MapApp(endpoints, context.BasePath, paths);
    }
}

/// <summary>
/// Keeps yt-dlp and Deno up to date, once a day, when the administrator wants it (by default): YouTube changes often
/// and an old yt-dlp stops working. Only the copies the extension installed are updated.
/// </summary>
public sealed class ToolUpdater(ToolManager tools, SettingsStore settings, IServiceProvider services, ILogger<ToolUpdater> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = services.GetRequiredService<ExtensionHost>();
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                if (!host.IsEnabled(YouTubeExtension.Id) || !settings.Current.AutoUpdate)
                    continue;
                foreach (var kind in Enum.GetValues<ToolKind>())
                {
                    try
                    {
                        await tools.LatestAsync(kind, stoppingToken);
                        var status = await tools.StatusAsync(kind, stoppingToken);
                        if (status.UpdateAvailable)
                        {
                            logger.LogInformation("Updating {Tool} from {Version} to {Latest}", kind, status.Version, status.Latest);
                            await tools.InstallAsync(kind, stoppingToken);
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogWarning(e, "Could not update {Tool}", kind);
                    }
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
