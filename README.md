# Soap

Standalone ASP.NET Core (.NET 10) host that backs up your TikTok reposts: enter a
username, the server enumerates the reposts feed via `yt-dlp`, downloads each
video (H.264 re-encoded for browser playback) and renders a persistent gallery.

Built on the link-preview + media-cache services extracted from the Yap chat app.

## Requirements

- .NET 10 SDK
- `yt-dlp` on `PATH` (only required for media caching)
- `ffmpeg` + `ffprobe` on `PATH` (only required for H.264 re-encode of HEVC sources like TikTok)

If `yt-dlp` is missing, `MediaCacheService.IsAvailable` stays `false` and media
endpoints respond with 503 — link previews still work.

## Run

```
dotnet run --project Soap
```

Defaults to http://localhost:5080. The gallery UI is served at `/`.

## Run in Docker

The image bundles `ffmpeg` and `yt-dlp` so the container is self-contained.
The container listens on **8080**; map it to whatever host port you want.

```bash
docker build -t soap ./Soap
docker run -d --name soap \
  -p 5080:8080 \
  -v /srv/soap/data:/app/Data \
  --restart=unless-stopped \
  soap
```

`/app/Data` holds the gallery state and downloaded videos — mount it so the
backup survives container replacements.

For the full GitHub Actions → GHCR → server pipeline, see
[`GHCR-DEPLOYMENT-GUIDE.md`](./GHCR-DEPLOYMENT-GUIDE.md). The workflow at
`.github/workflows/docker-publish.yml` is already wired to that recipe; push
to `main` and the image lands at `ghcr.io/<owner>/<repo>:latest`.

## TikTok cookies (required for reposts)

The reposts tab (`/@user/reposts`) is gated behind a logged-in session.

The easiest way: use the in-page **TikTok cookies** panel.

1. Log into `tiktok.com` in your browser.
2. F12 → **Network** tab → reload if empty → click any `tiktok.com` request.
3. Scroll the right panel to **Request Headers** → copy the line starting with `Cookie:`.
4. Paste it into the gallery's cookie panel and click **Save**.

The server converts it to Netscape format and writes it to
`Soap/Data/tiktok-cookies.txt`. If you already have a Netscape file, you can
drop it there directly instead.

If no cookies are set, `/scrape` still runs but will almost certainly return 0
URLs and the UI tells you so.

## Endpoints

### Backup

| Method | Path              | Notes                                                                 |
|-------:|-------------------|-----------------------------------------------------------------------|
| GET    | `/`               | Gallery UI                                                            |
| POST   | `/scrape`         | Body: `{"username":"..."}`. Enumerates reposts, queues each video.    |
| GET    | `/reposts`        | Full list of backed-up entries (newest first)                         |
| GET    | `/reposts/status` | Counts: `{queued, downloading, ready, failed, total}`                 |
| GET    | `/cookies`        | `{present: bool}` — whether `Data/tiktok-cookies.txt` exists          |
| POST   | `/cookies`        | Body: `{"cookieHeader":"..."}`. Writes a Netscape cookies file.        |
| DELETE | `/cookies`        | Removes the cookies file                                              |

### Underlying services

| Method | Path                      | Notes                                                   |
|-------:|---------------------------|---------------------------------------------------------|
| GET    | `/health`                 | yt-dlp availability + settings + cookies presence       |
| GET    | `/preview?url=...`        | Returns OG preview (awaits fetch, 15s timeout)          |
| GET    | `/media?url=...`          | Returns cached media entry (awaits download, 6min)      |
| POST   | `/queue`                  | Fire-and-forget queue. Body: `{"url":"...","includeMedia":true}` |
| GET    | `/cache/preview?url=...`  | Returns cached OG entry or 404                          |
| GET    | `/cache/media?url=...`    | Returns cached media entry or 404                       |
| GET    | `/media-cache/{hash}.ext` | Static-file route serving downloaded videos/audio       |

## Architecture

```
Program.cs
  └─ DI: HttpClient("LinkPreview")
        LinkPreviewSettingsService  (singleton, persists Data/link-preview-settings.json)
        LinkPreviewService          (singleton, in-memory OG cache, 1h TTL)
        MediaCacheService           (singleton, yt-dlp subprocess + disk cache)
        PreviewCoordinator          (singleton, wires the two callbacks together)

PreviewCoordinator
  ├─ Sets LinkPreviewService.OnPreviewFetched → re-broadcasts as OnLinkPreviewReady
  ├─ Sets MediaCacheService.OnMediaCached    → mutates the LinkPreview to attach
  │                                            CachedMediaUrl/MediaType/Duration,
  │                                            then re-broadcasts as OnMediaCacheReady
  └─ Exposes Queue(url) / RequestPreviewAsync(url) / RequestMediaAsync(url)
```

Both core services are **unmodified** copies of Yap's — only the namespace changed
(`Yap.*` → `Soap.*`). The wiring that `ChatService` did in Yap is isolated in
`PreviewCoordinator` here so the rest of your app stays clean.

## Building on it

Subscribe to coordinator events from anywhere:

```csharp
coordinator.OnLinkPreviewReady += (msgId, url, preview) => { /* … */ };
coordinator.OnMediaCacheReady  += (msgId, url, preview) => { /* preview.CachedMediaUrl is set */ };
```

Queue work from anywhere:

```csharp
var id = coordinator.Queue("https://youtu.be/dQw4w9WgXcQ", includeMedia: true);
```

## Storage

- `Data/reposts.json` — persistent gallery: every URL the scraper has seen, with
  its current status, title, thumbnail, and cached video path.
- `Data/tiktok-cookies.txt` — your Netscape-format TikTok cookies (gitignored).
- `Data/link-preview-settings.json` — persisted on/off toggles
- `Data/media-cache/{sha256(url)[..16]}.{ext}` — downloaded media files (survive restarts)

OG previews are memory-only with a 1-hour TTL; failed URLs are negatively cached
for 1 hour on both sides. Repost entries in `reposts.json` are permanent.
