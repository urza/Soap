# Soap

Standalone ASP.NET Core (.NET 10) app for keeping your own copies of TikTok
videos and reposts. Downloads use `yt-dlp`; `ffmpeg` converts videos to H.264
for browser playback and creates local thumbnails.

## Using the library

- **Your library** is the home screen. Search by title or creator, and filter by
  Saved, Saving, Waiting, or Needs attention. Videos appear newest repost first,
  following the order collected from TikTok (or the order of a pasted list).
  Download completion and retries do not change their position.
- **Add videos** accepts a single video URL or a collection of URLs. Expand the
  collection guide to install the bookmarklet, collect repost links on TikTok,
  then paste them into the import field.
- **Settings** holds TikTok cookies, download speed, and title refresh. Setup
  instructions stay out of the library until you need them.
- The activity panel explains what is saving or waiting and lets you pause or
  resume. Failed cards show details and a **Try again** button; **Retry failed**
  retries all failed videos.

Completed cached files are restored to the gallery on restart. Downloads and
transcodes are staged separately until complete, and retries remain queued
through the download cooldown. If browser conversion fails, the video is marked
failed so it can be retried rather than shown as a successful backup.

Repost ordering is saved with the gallery. Paste collections **newest first**;
the bookmarklet collects them from top to bottom. New imports appear at the top,
and re-importing a list restores that list's order without downloading known
videos again. Older galleries keep their previous order until re-imported:
Soap stores URLs, not original TikTok repost timestamps, so it cannot infer
missing historical dates or compare repost dates across separate accounts.

## Requirements

- .NET 10 SDK
- `yt-dlp` on `PATH` (only required for media caching)
- `ffmpeg` + `ffprobe` on `PATH` (only required for H.264 re-encode of HEVC sources like TikTok)

If `yt-dlp` is missing, `MediaCacheService.IsAvailable` stays `false` and
`/media` responds with 503. Gallery imports show an actionable failure instead
of waiting indefinitely; already-cached files remain usable.

## Run

```
dotnet run --project Soap
```

Defaults to http://localhost:5080. The gallery UI is served at `/`.

## Run in Docker

The image bundles current stable `yt-dlp`, `ffmpeg`, and `ffprobe` builds, verified
against their published SHA-256 checksums. FFmpeg comes from BtbN's static GPL
builds with H.264 support, using the latest stable release branch (not master).
The container listens on **8080**; map it to whatever host port you want.

```bash
docker buildx build --pull --no-cache-filter media-tools --load -t soap ./Soap
docker run -d --name soap \
  -p 5080:8080 \
  -v /srv/soap/data:/app/Data \
  --restart=unless-stopped \
  soap
```

The `--no-cache-filter media-tools` option refreshes tool downloads even when
application code has not changed. The publishing workflow does this on every
build, after running the download regression checks. Installed versions are
printed in the build log. Existing containers keep their bundled versions until
you rebuild/pull an image and recreate them.

Optional build arguments: `YTDLP_VERSION=2026.08.19` selects a yt-dlp release;
`FFMPEG_VERSION=9.0` selects the latest available build of that stable FFmpeg
branch. Both default to `latest`. Linux amd64 and arm64 downloads are supported;
the image was validated locally on amd64.

`/app/Data` holds the gallery state and downloaded videos — mount it so the
backup survives container replacements.

For the full GitHub Actions → GHCR → server pipeline, see
[`GHCR-DEPLOYMENT-GUIDE.md`](./GHCR-DEPLOYMENT-GUIDE.md). The workflow at
`.github/workflows/docker-publish.yml` is already wired to that recipe; push
to `main` and the image lands at `ghcr.io/<owner>/<repo>:latest`.

## TikTok cookies (required for reposts)

The reposts tab (`/@user/reposts`) is gated behind a logged-in session.

Open **Settings → TikTok cookies**.

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
| POST   | `/reposts/{id}/retry` | Retry one failed gallery entry |
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

The core services originated in Yap and have since been adapted for backups,
reliable queue recovery, and browser-compatible media processing.
`PreviewCoordinator` connects previews, downloads, and gallery notifications.

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
- `Data/media-cache/{sha256(url)[..16]}.{ext}` — completed media files (survive restarts)
- `Data/media-work/{hash}/` — temporary downloads and transcodes; never served as media
- `Data/pacing-settings.json` — saved download speed settings

OG previews are memory-only with a 1-hour TTL; failed URLs are negatively cached
for 1 hour on both sides. Repost entries in `reposts.json` are permanent.

## Validation

On Linux with .NET 10 and Python 3:

```bash
dotnet build Soap.sln -c Release --nologo
python3 -m unittest discover -s tests -v
```

The regression checks launch isolated app instances with temporary data and
controlled media-tool fixtures. They cover cached imports, restart recovery,
partial-file rejection, single/bulk retries during cooldown, noisy subprocesses,
failed conversion, staging, and missing yt-dlp. No TikTok credentials are needed.
