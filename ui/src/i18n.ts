import { host } from "./host";

const en = {
  mine: {
    title: "My settings",
    playWhenDone: "Play each song when it arrives",
    playWhenDoneHelp: "When a song is added to your library, the player plays it after what is playing now.",
    playing: "Playing {count} new song(s)",
    cookies: "My YouTube cookies",
    cookiesSaved: "You have cookies of your own (encrypted); they are used for your links instead of the server's.",
    cookiesNone: "None: your links use the server's, if it has any.",
    cookiesHelp: "If YouTube asks to \"confirm you are not a bot\", export the cookies.txt of your YouTube session from your browser and paste it or choose the file. They are never shown back.",
    cookiesFile: "Choose cookies.txt",
    cookiesSave: "Save my cookies",
    cookiesSavedNow: "Cookies saved",
    cookiesRemove: "Remove mine",
    cookiesRemoved: "Your cookies were removed",
  },
  title: "From YouTube",
  subtitle: "Paste a link to a video, playlist, channel or album and add its audio to your library.",
  placeholder: "Paste a YouTube link (or several, one per line)",
  inspect: "Look",
  clear: "Clear",
  loading: "Reading the link…",
  settings: "Settings",
  close: "Close",
  legal: "Download only what you have the right to keep. Uses yt-dlp.",
  kinds: {
    VIDEO: "Video",
    PLAYLIST: "Playlist",
    MIX: "Mix",
    ALBUM: "Album",
    CHANNEL: "Channel",
    ARTIST: "Artist",
    OTHER: "Link",
  },
  tabs: { releases: "Releases", videos: "Videos", shorts: "Shorts", streams: "Live", playlists: "Playlists" },
  views: "{count} views",
  songs: "{count} songs",
  chapters: "{count} chapters",
  listen: "Listen",
  stop: "Stop",
  inLibrary: "In your library",
  unavailable: "Unavailable",
  whichOne: "This video is part of a list.",
  onlyThis: "Only this song",
  wholeList: "The whole list",
  truncated: "A mix never ends: only its first songs are shown.",
  filter: "Search in this list",
  selectAll: "All ({count})",
  selectNone: "None",
  selected: "{count} chosen · {time}",
  allChosen: "All the songs of the list",
  loadMore: "Load more",
  tags: "Tags",
  tagTitle: "Title",
  tagArtist: "Artist",
  tagAlbum: "Album",
  tagAlbumArtist: "Album artist",
  tagYear: "Year",
  albumFor: "Album for every song",
  numberTracks: "Number the songs in order",
  format: "Format",
  formats: {
    ORIGINAL: ["Original", "As YouTube has it (Opus or AAC): the best quality, no conversion."],
    OPUS: ["Opus", "Small and very good."],
    MP3: ["MP3", "Plays everywhere."],
    M4A: ["M4A (AAC)", "For Apple devices."],
    FLAC: ["FLAC", "Lossless container (it cannot add quality YouTube does not have)."],
  },
  quality: "Quality",
  qualities: { BEST: "Best", HIGH: "High", MEDIUM: "Smaller" },
  library: "Library",
  noLibrary: "You cannot upload to any library. Ask an administrator.",
  artwork: "Use the picture as cover",
  square: "Crop it square",
  split: "Split at its chapters",
  noFfmpeg: "Converting needs ffmpeg (an administrator installs it in Arcanist's panel).",
  add: "Add to my library",
  addCount: "Add {count} to my library",
  addAll: "Add all to my library",
  added: "On its way to your library",
  queue: "Downloads",
  queueEmpty: "What you add shows here while it downloads.",
  clearDone: "Clear finished",
  openUploads: "My uploads",
  cancel: "Cancel",
  retry: "Retry",
  remove: "Remove",
  batchStatus: {
    LISTING: "Finding the songs…",
    ACTIVE: "{done} of {total}",
    DONE: "Done",
    FAILED: "Finished with errors",
    CANCELLED: "Cancelled",
  },
  itemStatus: {
    QUEUED: "Waiting",
    DOWNLOADING: "Downloading",
    CONVERTING: "Converting",
    IMPORTING: "Adding to the library",
    ADDED: "In your library",
    DUPLICATE: "Already in the library",
    FAILED: "Failed",
    CANCELLED: "Cancelled",
  },
  setupTitle: "Almost ready",
  setupAdmin: "This extension uses yt-dlp, and Deno so YouTube gives every format. Install them from their official releases:",
  setupUser: "An administrator has to finish setting up this extension (it needs yt-dlp).",
  install: "Install yt-dlp and Deno",
  setupAdminPanel: "Install yt-dlp and Deno in Arcanist's admin panel: Extensions › YouTube Audio.",
  openSettings: "Open its settings",
  installing: "Installing…",
  installed: "Installed",
  tools: "Tools",
  update: "Update",
  check: "Check for updates",
  upToDate: "Up to date",
  notInstalled: "Not installed",
  fromSystem: "From the system",
  ffmpeg: "ffmpeg",
  ffmpegMissing: "Not found: only the original format is possible.",
  otherSites: "Other sites",
  otherSitesHelp: "Besides YouTube, users may bring audio from these sites (one per line, such as soundcloud.com).",
  concurrency: "Downloads at a time",
  maxItems: "Most songs per link",
  autoUpdate: "Update yt-dlp and Deno by themselves",
  defaultFormat: "Format suggested",
  cookies: "YouTube cookies",
  cookiesHelp: "If YouTube asks this server to confirm it is not a bot, give yt-dlp the cookies of a YouTube session: export them from a browser where you are signed in (as cookies.txt, with an extension such as “Get cookies.txt LOCALLY”) and load the file here. Use an account of its own: they are as good as signing in.",
  cookiesSet: "Cookies loaded",
  cookiesNone: "No cookies",
  cookiesLoad: "Load cookies.txt",
  cookiesRemove: "Remove",
  save: "Save",
  saved: "Saved",
  signIn: "Sign in to Arcanist to use this.",
  speed: "{speed}/s",
  eta: "{time} left",
  errors: {
    invalid_link: "That is not a link this understands.",
    site_not_allowed: "That site is not allowed. An administrator can allow it.",
    tools_missing: "yt-dlp is not installed yet.",
    tool_unsupported: "There is no yt-dlp for this server's system.",
    tool_download_failed: "The download failed. Try again later.",
    tool_checksum_mismatch: "The file downloaded was not the right one; nothing was installed.",
    ffmpeg_required: "This needs ffmpeg in Arcanist.",
    unavailable_video: "The video is not available (private, removed or does not exist).",
    sign_in_required: "YouTube asks to sign in for this one (age or members only).",
    geo_blocked: "Not available in the server's country.",
    live_not_ended: "It is live: try again when it ends.",
    blocked_by_youtube: "YouTube asks this server to prove it is not a bot. Add your YouTube cookies in My settings (or an administrator, the server's), or try again later.",
    invalid_cookies: "That is not a cookies.txt with YouTube cookies.",
    download_failed: "The download failed. Try again, or update yt-dlp.",
    inspect_failed: "The link could not be read. Check it, or update yt-dlp.",
    empty: "There is nothing to download there.",
    nothing_selected: "Choose at least one song.",
    wrong_state: "That is not possible now.",
    forbidden: "You do not have permission for this.",
    reauthentication_required: "Sign in to Arcanist again to change this.",
    not_found: "Not found.",
    read_only: "That library cannot take files.",
    duplicate: "Already there.",
    network: "The server did not answer.",
    unknown: "Something went wrong.",
  },
};

type Dictionary = typeof en;

const es: Dictionary = {
  mine: {
    title: "Mis ajustes",
    playWhenDone: "Reproducir cada canción al llegar",
    playWhenDoneHelp: "Cuando una canción entra en tu biblioteca, el reproductor la pone después de lo que suena.",
    playing: "Reproduciendo {count} canción(es) nueva(s)",
    cookies: "Mis cookies de YouTube",
    cookiesSaved: "Tienes cookies propias (cifradas); se usan para tus enlaces en vez de las del servidor.",
    cookiesNone: "Ninguna: tus enlaces usan las del servidor, si tiene.",
    cookiesHelp: "Si YouTube pide «confirmar que no eres un bot», exporta el cookies.txt de tu sesión de YouTube desde tu navegador y pégalo o elige el archivo. Nunca se muestran de vuelta.",
    cookiesFile: "Elegir cookies.txt",
    cookiesSave: "Guardar mis cookies",
    cookiesSavedNow: "Cookies guardadas",
    cookiesRemove: "Quitar las mías",
    cookiesRemoved: "Tus cookies se quitaron",
  },
  title: "Desde YouTube",
  subtitle: "Pega un enlace a un vídeo, lista, canal o álbum y añade su audio a tu biblioteca.",
  placeholder: "Pega un enlace de YouTube (o varios, uno por línea)",
  inspect: "Ver",
  clear: "Limpiar",
  loading: "Leyendo el enlace…",
  settings: "Ajustes",
  close: "Cerrar",
  legal: "Descarga solo lo que tengas derecho a guardar. Usa yt-dlp.",
  kinds: {
    VIDEO: "Vídeo",
    PLAYLIST: "Lista",
    MIX: "Mix",
    ALBUM: "Álbum",
    CHANNEL: "Canal",
    ARTIST: "Artista",
    OTHER: "Enlace",
  },
  tabs: { releases: "Lanzamientos", videos: "Vídeos", shorts: "Shorts", streams: "En directo", playlists: "Listas" },
  views: "{count} visualizaciones",
  songs: "{count} canciones",
  chapters: "{count} capítulos",
  listen: "Escuchar",
  stop: "Parar",
  inLibrary: "En tu biblioteca",
  unavailable: "No disponible",
  whichOne: "Este vídeo es parte de una lista.",
  onlyThis: "Solo esta canción",
  wholeList: "Toda la lista",
  truncated: "Un mix no termina nunca: solo se muestran sus primeras canciones.",
  filter: "Buscar en esta lista",
  selectAll: "Todas ({count})",
  selectNone: "Ninguna",
  selected: "{count} elegidas · {time}",
  allChosen: "Todas las canciones de la lista",
  loadMore: "Cargar más",
  tags: "Etiquetas",
  tagTitle: "Título",
  tagArtist: "Artista",
  tagAlbum: "Álbum",
  tagAlbumArtist: "Artista del álbum",
  tagYear: "Año",
  albumFor: "Álbum para todas las canciones",
  numberTracks: "Numerar las canciones en orden",
  format: "Formato",
  formats: {
    ORIGINAL: ["Original", "Tal como lo tiene YouTube (Opus o AAC): la mejor calidad, sin convertir."],
    OPUS: ["Opus", "Pequeño y muy bueno."],
    MP3: ["MP3", "Suena en cualquier sitio."],
    M4A: ["M4A (AAC)", "Para dispositivos de Apple."],
    FLAC: ["FLAC", "Contenedor sin pérdida (no añade la calidad que YouTube no tiene)."],
  },
  quality: "Calidad",
  qualities: { BEST: "La mejor", HIGH: "Alta", MEDIUM: "Más pequeña" },
  library: "Biblioteca",
  noLibrary: "No puedes subir a ninguna biblioteca. Pídeselo a un administrador.",
  artwork: "Usar la imagen como carátula",
  square: "Recortarla cuadrada",
  split: "Separar por capítulos",
  noFfmpeg: "Convertir necesita ffmpeg (lo instala un administrador en el panel de Arcanist).",
  add: "Añadir a mi biblioteca",
  addCount: "Añadir {count} a mi biblioteca",
  addAll: "Añadir todas a mi biblioteca",
  added: "En camino a tu biblioteca",
  queue: "Descargas",
  queueEmpty: "Lo que añadas aparece aquí mientras se descarga.",
  clearDone: "Quitar las terminadas",
  openUploads: "Mis subidas",
  cancel: "Cancelar",
  retry: "Reintentar",
  remove: "Quitar",
  batchStatus: {
    LISTING: "Buscando las canciones…",
    ACTIVE: "{done} de {total}",
    DONE: "Listo",
    FAILED: "Terminó con errores",
    CANCELLED: "Cancelado",
  },
  itemStatus: {
    QUEUED: "En espera",
    DOWNLOADING: "Descargando",
    CONVERTING: "Convirtiendo",
    IMPORTING: "Añadiendo a la biblioteca",
    ADDED: "En tu biblioteca",
    DUPLICATE: "Ya estaba en la biblioteca",
    FAILED: "Falló",
    CANCELLED: "Cancelada",
  },
  setupTitle: "Casi listo",
  setupAdmin: "Esta extensión usa yt-dlp, y Deno para que YouTube dé todos los formatos. Instálalos desde sus releases oficiales:",
  setupUser: "Un administrador tiene que terminar de configurar esta extensión (necesita yt-dlp).",
  install: "Instalar yt-dlp y Deno",
  setupAdminPanel: "Instala yt-dlp y Deno en el panel de administración de Arcanist: Extensiones › YouTube Audio.",
  openSettings: "Abrir sus ajustes",
  installing: "Instalando…",
  installed: "Instalado",
  tools: "Herramientas",
  update: "Actualizar",
  check: "Buscar actualizaciones",
  upToDate: "Al día",
  notInstalled: "Sin instalar",
  fromSystem: "Del sistema",
  ffmpeg: "ffmpeg",
  ffmpegMissing: "No encontrado: solo se puede el formato original.",
  otherSites: "Otros sitios",
  otherSitesHelp: "Además de YouTube, los usuarios pueden traer audio de estos sitios (uno por línea, como soundcloud.com).",
  concurrency: "Descargas a la vez",
  maxItems: "Canciones como máximo por enlace",
  autoUpdate: "Actualizar yt-dlp y Deno solos",
  defaultFormat: "Formato sugerido",
  cookies: "Cookies de YouTube",
  cookiesHelp: "Si YouTube pide a este servidor que confirme que no es un bot, dale a yt-dlp las cookies de una sesión de YouTube: expórtalas desde un navegador con la sesión iniciada (como cookies.txt, con una extensión como «Get cookies.txt LOCALLY») y carga el archivo aquí. Usa una cuenta aparte: valen lo mismo que iniciar sesión.",
  cookiesSet: "Cookies cargadas",
  cookiesNone: "Sin cookies",
  cookiesLoad: "Cargar cookies.txt",
  cookiesRemove: "Quitar",
  save: "Guardar",
  saved: "Guardado",
  signIn: "Inicia sesión en Arcanist para usar esto.",
  speed: "{speed}/s",
  eta: "faltan {time}",
  errors: {
    invalid_link: "Ese no es un enlace que se entienda.",
    site_not_allowed: "Ese sitio no está permitido. Un administrador puede permitirlo.",
    tools_missing: "yt-dlp aún no está instalado.",
    tool_unsupported: "No hay yt-dlp para el sistema de este servidor.",
    tool_download_failed: "La descarga falló. Inténtalo más tarde.",
    tool_checksum_mismatch: "El archivo descargado no era el correcto; no se instaló nada.",
    ffmpeg_required: "Esto necesita ffmpeg en Arcanist.",
    unavailable_video: "El vídeo no está disponible (privado, eliminado o no existe).",
    sign_in_required: "YouTube pide iniciar sesión para este (edad o solo miembros).",
    geo_blocked: "No está disponible en el país del servidor.",
    live_not_ended: "Está en directo: inténtalo cuando termine.",
    blocked_by_youtube: "YouTube pide a este servidor que demuestre que no es un bot. Añade tus cookies de YouTube en Mis ajustes (o un administrador, las del servidor), o inténtalo más tarde.",
    invalid_cookies: "Eso no es un cookies.txt con cookies de YouTube.",
    download_failed: "La descarga falló. Inténtalo de nuevo o actualiza yt-dlp.",
    inspect_failed: "No se pudo leer el enlace. Revísalo o actualiza yt-dlp.",
    empty: "Ahí no hay nada que descargar.",
    nothing_selected: "Elige al menos una canción.",
    wrong_state: "Eso no se puede ahora.",
    forbidden: "No tienes permiso para esto.",
    reauthentication_required: "Vuelve a iniciar sesión en Arcanist para cambiar esto.",
    not_found: "No encontrado.",
    read_only: "Esa biblioteca no admite archivos.",
    duplicate: "Ya estaba.",
    network: "El servidor no respondió.",
    unknown: "Algo salió mal.",
  },
};

export type TextKey = string;

function dictionary(): Dictionary {
  return host.language.toLowerCase().startsWith("es") ? es : en;
}

/** A text by its dotted key, with {placeholders} filled in. */
export function t(key: string, values: Record<string, string | number> = {}): string {
  let value: unknown = dictionary();
  for (const part of key.split(".")) value = (value as Record<string, unknown> | undefined)?.[part];
  const text = Array.isArray(value) ? String(value[0]) : typeof value === "string" ? value : key;
  return text.replace(/\{(\w+)\}/g, (_, name: string) => String(values[name] ?? ""));
}

/** The second part of a pair (a format's explanation). */
export function help(key: string): string {
  let value: unknown = dictionary();
  for (const part of key.split(".")) value = (value as Record<string, unknown> | undefined)?.[part];
  return Array.isArray(value) ? String(value[1]) : "";
}

export function errorText(code: string): string {
  const errors = dictionary().errors as Record<string, string>;
  return errors[code] ?? errors.unknown;
}

export function locale(): string {
  return host.language || navigator.language;
}

export function duration(seconds?: number | null): string {
  if (seconds == null || !Number.isFinite(seconds)) return "";
  const whole = Math.round(seconds);
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = whole % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}` : `${m}:${String(s).padStart(2, "0")}`;
}

export function number(value: number): string {
  return new Intl.NumberFormat(locale(), { notation: value >= 10000 ? "compact" : "standard" }).format(value);
}

export function bytes(value: number): string {
  const units = ["B", "KB", "MB", "GB"];
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }
  return `${new Intl.NumberFormat(locale(), { maximumFractionDigits: 1 }).format(value)} ${units[unit]}`;
}

export function date(value?: string): string {
  if (!value) return "";
  const parsed = new Date(value + "T00:00:00");
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(locale(), { dateStyle: "medium" }).format(parsed);
}
