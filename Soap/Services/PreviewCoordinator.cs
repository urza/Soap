using System.Collections.Concurrent;
using Soap.Models;

namespace Soap.Services;

/// <summary>
/// Wires LinkPreviewService + MediaCacheService callbacks together (mirroring what
/// ChatService does in the parent Yap project) and re-broadcasts as multicast events
/// so multiple subscribers can react. Also exposes an awaitable RequestAsync for
/// simple request/response usage (e.g. HTTP endpoints, tests).
/// </summary>
public class PreviewCoordinator
{
    private readonly LinkPreviewService _linkPreview;
    private readonly MediaCacheService _mediaCache;
    private readonly LinkPreviewSettingsService _settings;

    /// <summary>Fired when OG scrape completes for a queued URL. (messageId, url, preview)</summary>
    public event Action<Guid, string, LinkPreview>? OnLinkPreviewReady;

    /// <summary>Fired when media download completes for a queued URL. (messageId, url, preview-with-media)</summary>
    public event Action<Guid, string, LinkPreview>? OnMediaCacheReady;

    public PreviewCoordinator(
        LinkPreviewService linkPreview,
        MediaCacheService mediaCache,
        LinkPreviewSettingsService settings)
    {
        _linkPreview = linkPreview;
        _mediaCache = mediaCache;
        _settings = settings;

        // Re-broadcast OG scrape completion
        _linkPreview.OnPreviewFetched = (msgId, url, preview) =>
            OnLinkPreviewReady?.Invoke(msgId, url, preview);

        // Attach media info to the LinkPreview (so one card carries both), then re-broadcast
        _mediaCache.OnMediaCached = (msgId, url, entry) =>
        {
            var preview = _linkPreview.GetOrCreatePreview(url);
            preview.CachedMediaUrl = entry.LocalUrl;
            preview.MediaType = entry.MediaType;
            preview.MediaDurationSeconds = entry.DurationSeconds;
            preview.PosterUrl = entry.PosterUrl;
            OnMediaCacheReady?.Invoke(msgId, url, preview);
        };
    }

    /// <summary>
    /// Queues link preview fetch and (optionally) media download for a single URL.
    /// Returns a messageId you can use to correlate the resulting events.
    /// </summary>
    public Guid Queue(string url, bool includeMedia = true)
    {
        var messageId = Guid.NewGuid();
        QueueWithId(messageId, url, includeMedia);
        return messageId;
    }

    /// <summary>
    /// Queues link preview fetch and (optionally) media download under a caller-supplied messageId.
    /// Useful when correlating multiple URLs from the same logical message.
    /// </summary>
    public void QueueWithId(Guid messageId, string url, bool includeMedia = true)
    {
        if (!_settings.Enabled) return;

        _linkPreview.QueueFetch(messageId, url);

        if (includeMedia && _settings.MediaCachingEnabled)
            _mediaCache.QueueDownload(messageId, url);
    }

    /// <summary>
    /// Awaitable convenience wrapper for HTTP endpoints / tests. Queues a fetch and
    /// resolves when the OG scrape completes (or returns the cached value immediately).
    /// </summary>
    public async Task<LinkPreview?> RequestPreviewAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var cached = _linkPreview.GetCachedPreview(url);
        if (cached != null) return cached;

        var tcs = new TaskCompletionSource<LinkPreview>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(Guid _, string u, LinkPreview p)
        {
            if (string.Equals(u, url, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(p);
        }

        OnLinkPreviewReady += Handler;
        try
        {
            QueueWithId(Guid.NewGuid(), url, includeMedia: false);

            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return _linkPreview.GetCachedPreview(url);
        }
        finally
        {
            OnLinkPreviewReady -= Handler;
        }
    }

    /// <summary>
    /// Awaitable convenience wrapper that queues both OG scrape and media download and
    /// resolves once the media is ready (returns the merged LinkPreview).
    /// </summary>
    public async Task<LinkPreview?> RequestMediaAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var cached = _mediaCache.GetCachedMedia(url);
        if (cached != null)
        {
            var preview = _linkPreview.GetOrCreatePreview(url);
            preview.CachedMediaUrl = cached.LocalUrl;
            preview.MediaType = cached.MediaType;
            preview.MediaDurationSeconds = cached.DurationSeconds;
            preview.PosterUrl = cached.PosterUrl;
            return preview;
        }

        var tcs = new TaskCompletionSource<LinkPreview>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(Guid _, string u, LinkPreview p)
        {
            if (string.Equals(u, url, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(p);
        }

        OnMediaCacheReady += Handler;
        try
        {
            QueueWithId(Guid.NewGuid(), url, includeMedia: true);

            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(6));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            OnMediaCacheReady -= Handler;
        }
    }
}
