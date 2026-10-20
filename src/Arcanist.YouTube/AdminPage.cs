using System.Text.Json;
using Arcanist.Core.Application;
using Arcanist.Extensibility;

namespace Arcanist.YouTube;

/// <summary>
/// The extension's settings in Arcanist's admin panel (Extensions › YouTube Audio), described for the panel to draw:
/// the tools and their updates, what users may bring and how, and the server's YouTube cookies.
/// </summary>
public sealed class YouTubeAdminPage(ToolManager tools, SettingsStore settings, CookieStore cookies, YtDlp ytDlp, DownloadQueue queue) : IExtensionAdminPage
{
    public async Task<AdminPage> GetAsync(AdminPageContext context, CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        var current = settings.Current;
        var toolBlocks = new List<AdminBlock>();
        foreach (var kind in Enum.GetValues<ToolKind>())
        {
            var status = await tools.StatusAsync(kind, cancellationToken);
            var name = kind == ToolKind.YtDlp ? "yt-dlp" : "Deno";
            toolBlocks.Add(status.Installed
                ? AdminBlock.Status(name,
                    status.UpdateAvailable ? t.UpdateTo(status.Version!, status.Latest!) : $"{status.Version} · {t.Source(status.Source)}",
                    status.UpdateAvailable ? AdminTone.Warning : AdminTone.Good)
                : AdminBlock.Status(name, t.Missing, AdminTone.Bad));
        }

        var ffmpeg = await ytDlp.FfmpegAsync(cancellationToken);
        toolBlocks.Add(AdminBlock.Status("ffmpeg", ffmpeg is null ? t.NoFfmpeg : t.FfmpegFromArcanist, ffmpeg is null ? AdminTone.Warning : AdminTone.Good));

        var formats = Enum.GetValues<AudioFormat>().Select(f => new AdminOption(f.ToString(), t.Format(f))).ToList();
        return new AdminPage(
        [
            new AdminSection("tools", t.ToolsTitle, t.ToolsHelp, toolBlocks,
            [
                new AdminAction("install", t.Install, AdminActionStyle.Primary),
                new AdminAction("check", t.Check),
            ]),
            new AdminSection("settings", t.SettingsTitle, null,
            [
                AdminBlock.Field("otherSites", t.OtherSites, AdminFieldType.TextArea, string.Join('\n', current.OtherSites), t.OtherSitesHelp,
                    placeholder: "soundcloud.com\nbandcamp.com"),
                AdminBlock.Field("defaultFormat", t.DefaultFormat, AdminFieldType.Select, current.DefaultFormat.ToString(), options: formats),
                AdminBlock.Field("concurrency", t.Concurrency, AdminFieldType.Number, current.Concurrency, min: 1, max: YouTubeSettings.MaxConcurrency),
                AdminBlock.Field("maxItems", t.MaxItems, AdminFieldType.Number, current.MaxItems, t.MaxItemsHelp, min: 1, max: YouTubeSettings.MaxItemsLimit),
                AdminBlock.Field("autoUpdate", t.AutoUpdate, AdminFieldType.Switch, current.AutoUpdate, t.AutoUpdateHelp),
            ]),
            new AdminSection("cookies", t.CookiesTitle, t.CookiesHelp,
            [
                AdminBlock.Status(t.CookiesState, cookies.Present ? t.CookiesSaved : t.CookiesNone, cookies.Present ? AdminTone.Good : AdminTone.Neutral),
                AdminBlock.Field("cookies", t.CookiesField, AdminFieldType.TextArea, "", t.CookiesFieldHelp, placeholder: "# Netscape HTTP Cookie File"),
            ], cookies.Present ? [new AdminAction("removeCookies", t.RemoveCookies, AdminActionStyle.Destructive, t.RemoveCookiesConfirm)] : null),
            new AdminSection("legal", t.LegalTitle, null, [AdminBlock.Notice(t.Legal, AdminTone.Warning)]),
        ]);
    }

    public Task<AdminPageResult> SaveAsync(AdminPageContext context, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        var current = settings.Current;
        string? Text(string key) => values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Number(string key) => values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        var format = current.DefaultFormat;
        if (Text("defaultFormat") is { } chosen && !Enum.TryParse(chosen, out format))
            return Task.FromResult(AdminPageResult.Failed(t.Invalid, new Dictionary<string, string> { ["defaultFormat"] = t.Invalid }));
        var saved = new YouTubeSettings(
            Text("otherSites") is { } sites ? sites.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : current.OtherSites,
            Number("concurrency") ?? current.Concurrency,
            values.TryGetValue("autoUpdate", out var update) && update.ValueKind is JsonValueKind.True or JsonValueKind.False ? update.GetBoolean() : current.AutoUpdate,
            Number("maxItems") ?? current.MaxItems,
            format).Normalized();
        settings.Save(saved);
        queue.Wake();

        // Cookies only when some were pasted: empty keeps those there are.
        if (Text("cookies") is { Length: > 0 } text)
        {
            try
            {
                cookies.Save(text);
            }
            catch (ExtensionException)
            {
                return Task.FromResult(AdminPageResult.Failed(t.InvalidCookies, new Dictionary<string, string> { ["cookies"] = t.InvalidCookies }));
            }
        }

        return Task.FromResult(AdminPageResult.Done(t.Saved));
    }

    public async Task<AdminPageResult> RunAsync(AdminPageContext context, string action, IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        switch (action)
        {
            case "install":
                try
                {
                    foreach (var kind in Enum.GetValues<ToolKind>())
                        await tools.InstallAsync(kind, cancellationToken);
                    return AdminPageResult.Done(t.Installed);
                }
                catch (ArcanistException e)
                {
                    return AdminPageResult.Failed(t.InstallFailed(e.Code));
                }
            case "check":
                foreach (var kind in Enum.GetValues<ToolKind>())
                    await tools.LatestAsync(kind, cancellationToken);
                return AdminPageResult.Done(t.Checked);
            case "removeCookies":
                cookies.Remove();
                return AdminPageResult.Done(t.CookiesRemoved);
            default:
                return AdminPageResult.Failed(t.Invalid);
        }
    }

    /// <summary>The page's words, in Spanish or English.</summary>
    private sealed record Texts(bool Es)
    {
        public static Texts For(string language) => new(language == "es");

        private string S(string es, string en) => Es ? es : en;

        public string ToolsTitle => S("Herramientas", "Tools");
        public string ToolsHelp => S("yt-dlp trae el audio y Deno le permite leer YouTube por completo. Se bajan de sus releases oficiales, comprobadas con su SHA-256.",
            "yt-dlp brings the audio and Deno lets it read YouTube fully. They come from their official releases, checked against their SHA-256.");
        public string Missing => S("No instalado", "Not installed");
        public string NoFfmpeg => S("No disponible: solo se guarda el audio original", "Not available: only the original audio is saved");
        public string FfmpegFromArcanist => S("El de Arcanist", "Arcanist's");
        public string UpdateTo(string version, string latest) => S($"{version} · hay una nueva: {latest}", $"{version} · {latest} is out");
        public string Source(ToolSource? source) => source switch
        {
            ToolSource.Installed => S("instalado por la extensión", "installed by the extension"),
            ToolSource.Configured => S("ruta configurada", "configured path"),
            _ => S("del sistema", "from the system"),
        };
        public string Install => S("Instalar o actualizar", "Install or update");
        public string Check => S("Buscar actualizaciones", "Check for updates");
        public string Installed => S("Herramientas al día", "Tools up to date");
        public string InstallFailed(string code) => S($"No se pudieron instalar ({code})", $"They could not be installed ({code})");
        public string Checked => S("Comprobado", "Checked");

        public string SettingsTitle => S("Qué y cómo se descarga", "What is downloaded and how");
        public string OtherSites => S("Otros sitios permitidos", "Other sites allowed");
        public string OtherSitesHelp => S("Además de YouTube y YouTube Music, uno por línea (yt-dlp sabe leer cientos).",
            "Besides YouTube and YouTube Music, one per line (yt-dlp reads hundreds).");
        public string DefaultFormat => S("Formato que se sugiere", "Format suggested");
        public string Format(AudioFormat format) => format switch
        {
            AudioFormat.Original => S("Original (sin convertir)", "Original (not converted)"),
            _ => format.ToString().ToUpperInvariant(),
        };
        public string Concurrency => S("Descargas a la vez", "Downloads at a time");
        public string MaxItems => S("Máximo de canciones por enlace", "Most songs per link");
        public string MaxItemsHelp => S("Un canal puede tener miles.", "A channel can have thousands.");
        public string AutoUpdate => S("Actualizar yt-dlp y Deno solos", "Update yt-dlp and Deno by themselves");
        public string AutoUpdateHelp => S("Una vez al día: YouTube cambia a menudo y un yt-dlp viejo deja de funcionar.",
            "Once a day: YouTube changes often and an old yt-dlp stops working.");

        public string CookiesTitle => S("Cookies de YouTube del servidor", "The server's YouTube cookies");
        public string CookiesHelp => S("Si YouTube pide a este servidor «confirmar que no es un bot», pega el cookies.txt de una sesión (exportado desde un navegador). Cada usuario puede poner las suyas en la app; las suyas ganan.",
            "If YouTube asks this server to \"confirm you are not a bot\", paste the cookies.txt of a session (exported from a browser). Each user can add their own in the app; theirs win.");
        public string CookiesState => S("Estado", "State");
        public string CookiesSaved => S("Guardadas (cifradas)", "Saved (encrypted)");
        public string CookiesNone => S("Ninguna", "None");
        public string CookiesField => S("Pegar cookies.txt", "Paste cookies.txt");
        public string CookiesFieldHelp => S("Nunca se muestran de vuelta. Déjalo vacío para conservar las que hay.", "They are never shown back. Leave it empty to keep those there are.");
        public string RemoveCookies => S("Quitar las cookies", "Remove the cookies");
        public string RemoveCookiesConfirm => S("Las descargas volverán a hacerse sin sesión.", "Downloads will go without a session again.");
        public string CookiesRemoved => S("Cookies quitadas", "Cookies removed");
        public string InvalidCookies => S("No parece un cookies.txt de YouTube.", "That does not look like a YouTube cookies.txt.");

        public string LegalTitle => S("Uso responsable", "Responsible use");
        public string Legal => S("Descarga solo contenido que tengas derecho a guardar. Respetar los términos de cada sitio y los derechos de autor es responsabilidad de quien descarga.",
            "Download only content you have the right to keep. Respecting each site's terms and copyright is up to whoever downloads.");

        public string Saved => S("Ajustes guardados", "Settings saved");
        public string Invalid => S("Valor no válido", "Invalid value");
    }
}
