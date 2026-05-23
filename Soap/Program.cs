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

// Wire repost-store status updates onto the existing coordinator events.
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
        if (e.Status == RepostStatus.Queued)
            e.Status = RepostStatus.Downloading;
    });
};

coordinator.OnMediaCacheReady += (_, url, preview) =>
{
    repostStore.Update(url, e =>
    {
        e.CachedMediaUrl = preview.CachedMediaUrl;
        e.MediaType = preview.MediaType;
        e.MediaDurationSeconds = preview.MediaDurationSeconds;
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

app.MapGet("/reposts/status", (RepostStore store) =>
{
    var (q, d, r, f) = store.GetCounts();
    return Results.Ok(new { queued = q, downloading = d, ready = r, failed = f, total = q + d + r + f });
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
