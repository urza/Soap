using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Soap.Models;
using Soap.Services;

var builder = WebApplication.CreateBuilder(args);

// Serialize enums (RepostStatus, CachedMediaType) as strings, not integers, so
// the frontend can do e.g. item.status.toLowerCase() without surprises.
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Named HttpClient for link preview OG scraping
builder.Services.AddHttpClient("LinkPreview", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SoapBot/1.0)");
    client.MaxResponseContentBufferSize = 256 * 1024; // 256KB
});

builder.Services.AddSingleton<LinkPreviewSettingsService>();
builder.Services.AddSingleton<LinkPreviewService>();
builder.Services.AddSingleton<PacingSettings>();
builder.Services.AddSingleton<MediaCacheService>();
builder.Services.AddSingleton<PreviewCoordinator>();
builder.Services.AddSingleton<RepostStore>();
builder.Services.AddSingleton<TikTokScraperService>();

// Allow the in-page bookmarklet running on tiktok.com to POST URLs back to us.
const string BookmarkletCors = "bookmarklet";
builder.Services.AddCors(options =>
{
    options.AddPolicy(BookmarkletCors, policy =>
    {
        policy.WithOrigins("https://www.tiktok.com", "https://tiktok.com")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Eagerly resolve the coordinator so its constructor wires the service callbacks
// before any request can arrive.
var coordinator = app.Services.GetRequiredService<PreviewCoordinator>();
var repostStore = app.Services.GetRequiredService<RepostStore>();
var mediaCache = app.Services.GetRequiredService<MediaCacheService>();

// Resume any work that was in flight when the previous instance stopped.
// Downloading entries get reset to Queued (the new semantics reserve Downloading
// for "yt-dlp actively running"). Both Queued and just-reset entries are re-fed
// to the coordinator so they actually get processed — otherwise they'd sit
// Queued forever until the user clicks something.
var pending = repostStore.GetAll()
    .Where(e => e.Status == RepostStatus.Queued || e.Status == RepostStatus.Downloading)
    .ToList();
foreach (var e in pending)
{
    if (e.Status == RepostStatus.Downloading)
        repostStore.Update(e.Url, x => x.Status = RepostStatus.Queued);
    coordinator.Queue(e.Url, includeMedia: true);
}
if (pending.Count > 0)
    app.Logger.LogInformation("Resumed {Count} pending entries on startup", pending.Count);

// Backfill local poster JPEGs for any Ready entries that don't have one yet
// (e.g. backed up before this feature existed, or whose stored ThumbnailUrl is a
// remote OG URL that won't load in a browser). Runs in the background so it
// doesn't delay app start; entries update individually as their poster lands.
_ = Task.Run(async () =>
{
    var needsPoster = repostStore.GetAll()
        .Where(e => e.Status == RepostStatus.Ready
                 && !string.IsNullOrEmpty(e.CachedMediaUrl)
                 && (string.IsNullOrEmpty(e.ThumbnailUrl) || !e.ThumbnailUrl.StartsWith("/media-cache/")))
        .ToList();

    if (needsPoster.Count == 0) return;
    app.Logger.LogInformation("Backfilling posters for {Count} Ready entries", needsPoster.Count);

    int generated = 0, skipped = 0;
    foreach (var e in needsPoster)
    {
        try
        {
            var poster = await mediaCache.EnsurePosterForAsync(e.Url);
            if (poster != null)
            {
                repostStore.Update(e.Url, x => x.ThumbnailUrl = poster);
                generated++;
            }
            else
            {
                skipped++;
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogDebug(ex, "Poster backfill failed for {Url}", e.Url);
            skipped++;
        }
    }
    app.Logger.LogInformation("Poster backfill done: generated={Generated} skipped={Skipped}", generated, skipped);
});

// Wire repost-store status updates onto the existing coordinator events.
//
// IMPORTANT: status only flips to Downloading when yt-dlp actually starts on a
// URL (OnMediaDownloadStarting below). OG scraping is fast and runs for every
// URL in parallel — so if we flipped on OG-ready, every entry would look like
// "Downloading" the moment retry-failed fired. That was confusing.
coordinator.OnLinkPreviewReady += (_, url, preview) =>
{
    repostStore.Update(url, e =>
    {
        if (!preview.Failed)
        {
            e.Title = preview.Title;
            e.Description = preview.Description;
            e.ThumbnailUrl = preview.ImageUrl;
        }
        // Don't touch e.Status here — let OnMediaDownloadStarting do it.
    });
};

mediaCache.OnMediaDownloadStarting = (_, url) =>
{
    repostStore.Update(url, e =>
    {
        if (e.Status == RepostStatus.Queued)
            e.Status = RepostStatus.Downloading;
    });
};

mediaCache.OnMetadataResolved = (_, url, title, uploader) =>
{
    // yt-dlp gave us real metadata — overwrite anything the OG scrape produced.
    repostStore.Update(url, e =>
    {
        if (!string.IsNullOrEmpty(title)) e.Title = title;
        if (!string.IsNullOrEmpty(uploader) && string.IsNullOrEmpty(e.SourceProfile))
            e.SourceProfile = uploader;
    });
};

coordinator.OnMediaCacheReady += (_, url, preview) =>
{
    repostStore.Update(url, e =>
    {
        e.CachedMediaUrl = preview.CachedMediaUrl;
        e.MediaType = preview.MediaType;
        e.MediaDurationSeconds = preview.MediaDurationSeconds;
        // Prefer the locally-generated poster over the OG image URL (which usually rots).
        if (!string.IsNullOrEmpty(preview.PosterUrl))
            e.ThumbnailUrl = preview.PosterUrl;
        e.Status = RepostStatus.Ready;
        e.CompletedAt = DateTime.UtcNow;
        e.ErrorMessage = null;
    });
};

mediaCache.OnMediaFailed = (_, url, reason) =>
{
    repostStore.Update(url, e =>
    {
        e.Status = RepostStatus.Failed;
        e.CompletedAt = DateTime.UtcNow;
        e.ErrorMessage = reason;
    });
};

// Make sure link previews + media caching are enabled (settings default to true on first run).
var settings = app.Services.GetRequiredService<LinkPreviewSettingsService>();
if (!settings.Enabled) await settings.SetEnabledAsync(true);
if (!settings.MediaCachingEnabled) await settings.SetMediaCachingEnabledAsync(true);

// Serve cached media files from Data/media-cache/ at /media-cache/...
var mediaCachePath = Path.Combine(app.Environment.ContentRootPath, "Data", "media-cache");
Directory.CreateDirectory(mediaCachePath);

var mediaCacheContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
mediaCacheContentTypes.Mappings[".opus"] = "audio/ogg";
mediaCacheContentTypes.Mappings[".m4a"] = "audio/mp4";

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(mediaCachePath),
    RequestPath = "/media-cache",
    ContentTypeProvider = mediaCacheContentTypes
});

// Serve the gallery UI from wwwroot/
app.UseDefaultFiles();
app.UseStaticFiles();

// CORS for the tiktok.com bookmarklet (applied per-endpoint via RequireCors).
app.UseCors();

// --- Backup endpoints ---

app.MapPost("/scrape", async (ScrapeRequest req, TikTokScraperService scraper, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Username))
        return Results.BadRequest(new { error = "username is required" });
    var result = await scraper.ScrapeRepostsAsync(req.Username, ct);
    return Results.Ok(result);
});

app.MapGet("/reposts", (RepostStore store) => Results.Ok(store.GetAll()));

app.MapGet("/v/{id}", (string id, HttpContext ctx, RepostStore store) =>
{
    var entry = store.GetById(id);
    if (entry == null) return Results.NotFound();
    var html = VideoPageBuilder.Build(entry, ctx.Request);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapPost("/reposts/bulk", (BulkImportRequest req, RepostStore store, PreviewCoordinator coord) =>
{
    if (req.Urls == null || req.Urls.Count == 0)
        return Results.BadRequest(new { error = "urls is required" });

    int newCount = 0, knownCount = 0, skipped = 0;
    foreach (var raw in req.Urls)
    {
        var url = (raw ?? "").Trim();
        if (url.Length == 0) { skipped++; continue; }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            skipped++;
            continue;
        }
        if (store.TryAdd(url, req.SourceProfile))
        {
            newCount++;
            coord.Queue(url, includeMedia: true);
        }
        else
        {
            knownCount++;
        }
    }

    return Results.Ok(new
    {
        queued = newCount,
        alreadyKnown = knownCount,
        skipped,
        total = req.Urls.Count
    });
}).RequireCors(BookmarkletCors);

app.MapGet("/reposts/status", (RepostStore store, MediaCacheService svc) =>
{
    var (q, d, r, f) = store.GetCounts();
    var active = svc.GetActiveDownloads()
        .Select(url => store.Get(url))
        .Where(e => e != null)
        .Select(e => new { id = e!.Id, url = e.Url, title = e.Title })
        .ToList();
    var titleRefresh = new
    {
        pending = svc.GetPendingTitleRefreshCount(),
        active = svc.GetActiveTitleRefreshes()
            .Select(url => store.Get(url))
            .Where(e => e != null)
            .Select(e => new { id = e!.Id, url = e.Url, title = e.Title })
            .ToList()
    };
    return Results.Ok(new { queued = q, downloading = d, ready = r, failed = f, total = q + d + r + f, active, titleRefresh, paused = svc.DownloadsPaused });
});

app.MapGet("/pacing", (PacingSettings p) =>
    Results.Ok(new { minMs = p.MinDelayMs, maxMs = p.MaxDelayMs, hardMaxMs = PacingSettings.HardMaxDelayMs }));

app.MapPost("/pacing", async (PacingRequest req, PacingSettings p) =>
{
    await p.SetAsync(req.MinMs, req.MaxMs);
    return Results.Ok(new { minMs = p.MinDelayMs, maxMs = p.MaxDelayMs });
});

// Known-bogus titles the OG scrape produces when TikTok serves its generic
// homepage HTML to unauthenticated requests. Treated as missing for refresh.
var bogusTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "TikTok - Make Your Day",
};

app.MapPost("/reposts/refresh-titles", (bool? all, RepostStore store, MediaCacheService mediaSvc) =>
{
    IEnumerable<RepostEntry> entries = store.GetAll().Where(e => !string.IsNullOrEmpty(e.Url));
    if (all != true)
        entries = entries.Where(e => string.IsNullOrWhiteSpace(e.Title) || bogusTitles.Contains(e.Title!));

    var candidates = entries.ToList();
    int queued = 0;
    foreach (var e in candidates)
    {
        if (mediaSvc.QueueMetadataRefresh(Guid.NewGuid(), e.Url))
            queued++;
    }
    return Results.Ok(new { queued, candidates = candidates.Count, mode = all == true ? "all" : "missing-only" });
});

// --- Pause / resume downloads ---

app.MapGet("/downloads/pause", (MediaCacheService svc) =>
    Results.Ok(new { paused = svc.DownloadsPaused }));

app.MapPost("/downloads/pause", (PauseRequest req, MediaCacheService svc, RepostStore store, PreviewCoordinator coord) =>
{
    var wasPaused = svc.DownloadsPaused;
    svc.SetDownloadsPaused(req.Paused);

    int requeued = 0;
    if (wasPaused && !req.Paused)
    {
        // Resuming: paused tasks already drained without doing work, so the entries
        // that were Queued aren't in any in-flight set anymore. Re-feed them so
        // they actually start downloading again.
        foreach (var e in store.GetAll().Where(x => x.Status == RepostStatus.Queued))
        {
            coord.Queue(e.Url, includeMedia: true);
            requeued++;
        }
    }
    return Results.Ok(new { paused = svc.DownloadsPaused, requeued });
});

app.MapPost("/reposts/retry-failed", (RepostStore store, MediaCacheService mediaSvc, PreviewCoordinator coord) =>
{
    var failed = store.GetAll().Where(e => e.Status == RepostStatus.Failed).ToList();
    foreach (var e in failed)
    {
        mediaSvc.ForgetFailure(e.Url);
        store.Update(e.Url, x =>
        {
            x.Status = RepostStatus.Queued;
            x.ErrorMessage = null;
            x.CompletedAt = null;
        });
        coord.Queue(e.Url, includeMedia: true);
    }
    return Results.Ok(new { retried = failed.Count });
});

// --- Cookie management ---

string CookiesPath() => Path.Combine(app.Environment.ContentRootPath, "Data", "tiktok-cookies.txt");

app.MapGet("/cookies", () =>
{
    var path = CookiesPath();
    return Results.Ok(new { present = File.Exists(path) });
});

app.MapPost("/cookies", async (CookieRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.CookieHeader))
        return Results.BadRequest(new { error = "cookieHeader is required" });

    var parsed = CookieImporter.Parse(req.CookieHeader);
    if (parsed.Count == 0)
        return Results.BadRequest(new { error = "no cookies found in the pasted text" });

    var body = CookieImporter.BuildNetscape(req.CookieHeader);
    if (body == null)
        return Results.BadRequest(new { error = "failed to build cookies file" });

    var path = CookiesPath();
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, body);

    string? warning = parsed.HasSession
        ? null
        : "No session cookie (sessionid/sid_tt) was found — yt-dlp probably won't be able to see private reposts.";

    return Results.Ok(new { count = parsed.Count, hasSession = parsed.HasSession, warning });
});

app.MapDelete("/cookies", () =>
{
    var path = CookiesPath();
    if (File.Exists(path)) File.Delete(path);
    return Results.Ok(new { present = false });
});

// --- Original demo endpoints ---

app.MapGet("/health", (LinkPreviewSettingsService s) => Results.Ok(new
{
    ytDlpAvailable = MediaCacheService.IsAvailable,
    linkPreviewsEnabled = s.Enabled,
    mediaCachingEnabled = s.MediaCachingEnabled,
    cookiesPresent = File.Exists(Path.Combine(app.Environment.ContentRootPath, "Data", "tiktok-cookies.txt"))
}));

app.MapGet("/preview", async (string url, PreviewCoordinator coord, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest("url query param is required");
    var preview = await coord.RequestPreviewAsync(url, ct: ct);
    return preview is null ? Results.StatusCode(504) : Results.Ok(preview);
});

app.MapGet("/media", async (string url, PreviewCoordinator coord, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest("url query param is required");
    if (!MediaCacheService.IsAvailable) return Results.Problem("yt-dlp not installed", statusCode: 503);
    var preview = await coord.RequestMediaAsync(url, ct: ct);
    return preview is null ? Results.StatusCode(504) : Results.Ok(preview);
});

app.MapPost("/queue", (QueueRequest req, PreviewCoordinator coord) =>
{
    if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest("url is required");
    var id = coord.Queue(req.Url, req.IncludeMedia ?? true);
    return Results.Accepted(value: new { messageId = id });
});

app.MapGet("/cache/preview", (string url, LinkPreviewService svc) =>
{
    var hit = svc.GetCachedPreview(url);
    return hit is null ? Results.NotFound() : Results.Ok(hit);
});

app.MapGet("/cache/media", (string url, MediaCacheService svc) =>
{
    var hit = svc.GetCachedMedia(url);
    return hit is null ? Results.NotFound() : Results.Ok(hit);
});

app.Run();

internal record QueueRequest(string Url, bool? IncludeMedia);
internal record ScrapeRequest(string Username);
internal record CookieRequest(string CookieHeader);
internal record BulkImportRequest(List<string> Urls, string? SourceProfile);
internal record PacingRequest(int MinMs, int MaxMs);
internal record PauseRequest(bool Paused);
