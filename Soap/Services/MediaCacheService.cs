using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Soap.Models;

namespace Soap.Services;

public class MediaCacheService
{
    private readonly ILogger<MediaCacheService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly LinkPreviewSettingsService _settings;
    private readonly LinkPreviewService? _linkPreviewService;

    // Cache: normalized URL -> MediaCacheEntry
    private readonly ConcurrentDictionary<string, MediaCacheEntry> _cache = new();

    // Failed URLs: don't retry URLs that yt-dlp can't handle (TTL: 1 hour)
    private readonly ConcurrentDictionary<string, DateTime> _failedUrls = new();

    // Dedup in-flight downloads
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    // Limit concurrent downloads to avoid rate limiting
    private readonly SemaphoreSlim _downloadSemaphore = new(2);

    /// <summary>Whether yt-dlp is available on this system.</summary>
    public static bool IsAvailable { get; private set; }


    private const int MaxDurationSeconds = 600; // 10 minutes
    private const long MaxFileSizeBytes = 200 * 1024 * 1024; // 200MB safety limit
    private const int DownloadTimeoutMs = 300_000; // 5 minutes
    private const int MetadataTimeoutMs = 15_000; // 15 seconds for metadata check
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Callback invoked when media caching completes. Parameters: (messageId, url, entry).
    /// </summary>
    public Action<Guid, string, MediaCacheEntry>? OnMediaCached { get; set; }

    /// <summary>
    /// Callback invoked when media download fails (yt-dlp couldn't handle it, exceeded
    /// limits, or threw). Parameters: (messageId, url, reason).
    /// </summary>
    public Action<Guid, string, string>? OnMediaFailed { get; set; }

    public MediaCacheService(ILogger<MediaCacheService> logger, IWebHostEnvironment env, LinkPreviewSettingsService settings, LinkPreviewService linkPreviewService)
    {
        _logger = logger;
        _env = env;
        _settings = settings;
        _linkPreviewService = linkPreviewService;
        DetectYtDlp();
        EnsureCacheDirectory();
    }

    private string CacheDirectory => Path.Combine(_env.ContentRootPath, "Data", "media-cache");

    private void EnsureCacheDirectory() => Directory.CreateDirectory(CacheDirectory);

    private void DetectYtDlp()
    {
        try
        {
            var psi = new ProcessStartInfo("yt-dlp", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            var exited = process?.WaitForExit(3000) ?? false;
            IsAvailable = exited && process?.ExitCode == 0;

            if (IsAvailable)
            {
                var version = process!.StandardOutput.ReadToEnd().Trim();
                _logger.LogInformation("yt-dlp detected: {Version}", version);
            }
            else
                _logger.LogWarning("yt-dlp not found — media caching will be unavailable");
        }
        catch
        {
            IsAvailable = false;
            _logger.LogWarning("yt-dlp not found — media caching will be unavailable");
        }
    }


    private static bool IsSpotifyUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets cached media entry from memory or disk. Returns null if not cached.
    /// </summary>
    public MediaCacheEntry? GetCachedMedia(string url)
    {
        return GetOrLoadCachedMedia(url);
    }

    /// <summary>
    /// Checks memory cache, then disk. Populates memory cache on disk hit.
    /// </summary>
    private MediaCacheEntry? GetOrLoadCachedMedia(string url)
    {
        if (_cache.TryGetValue(url, out var existing))
            return existing;

        // Check disk (covers app restart)
        var hash = ComputeHash(url);
        var diskFile = FindOutputFile(hash);
        if (diskFile != null)
        {
            var ext = Path.GetExtension(diskFile);
            var type = IsVideoExtension(ext) ? CachedMediaType.Video : CachedMediaType.Audio;
            var entry = new MediaCacheEntry($"/media-cache/{hash}{ext}", type, 0);
            _cache[url] = entry;
            return entry;
        }

        return null;
    }

    /// <summary>
    /// Fire-and-forget download. Invokes OnMediaCached when done.
    /// Tries any URL — yt-dlp determines if it's supported.
    /// </summary>
    public void QueueDownload(Guid messageId, string url)
    {
        if (!IsAvailable) return;

        // Only http/https
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        // Already cached (memory or disk)?
        if (GetOrLoadCachedMedia(url) != null)
            return;

        // Known failure? (with TTL)
        if (_failedUrls.TryGetValue(url, out var failedAt) && DateTime.UtcNow - failedAt < FailureCacheTtl)
        {
            _logger.LogDebug("Skipping {Url}: cached failure from {Ago}s ago", url, (DateTime.UtcNow - failedAt).TotalSeconds);
            return;
        }

        // Already in flight?
        if (!_inFlight.TryAdd(url, 0))
            return;

        _ = Task.Run(async () =>
        {
            await _downloadSemaphore.WaitAsync();
            try
            {
                var result = await DownloadMediaAsync(url);
                if (result != null)
                {
                    _cache[url] = result;
                    OnMediaCached?.Invoke(messageId, url, result);
                }
                else
                {
                    _failedUrls[url] = DateTime.UtcNow;
                    OnMediaFailed?.Invoke(messageId, url, "yt-dlp could not download this URL");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache media for {Url}", url);
                _failedUrls[url] = DateTime.UtcNow;
                OnMediaFailed?.Invoke(messageId, url, ex.Message);
            }
            finally
            {
                _downloadSemaphore.Release();
                _inFlight.TryRemove(url, out _);
            }
        });
    }

    private async Task<MediaCacheEntry?> DownloadMediaAsync(string url)
    {
        var sw = Stopwatch.StartNew();
        var hash = ComputeHash(url);

        // Check if already downloaded (file exists from previous app run)
        var existingFile = FindOutputFile(hash);
        if (existingFile != null)
        {
            var ext = Path.GetExtension(existingFile);
            var type = IsVideoExtension(ext) ? CachedMediaType.Video : CachedMediaType.Audio;
            _logger.LogDebug("Media cache hit (file exists): {Url}", url);
            return new MediaCacheEntry($"/media-cache/{hash}{ext}", type, 0);
        }

        // Route Spotify URLs to spotdl
        if (IsSpotifyUrl(url))
            return await DownloadSpotifyAsync(url, hash, sw);

        // Step 1: Get metadata — duration + whether video is available
        var metadata = await GetMetadataAsync(url);
        if (metadata == null)
        {
            _logger.LogDebug("yt-dlp cannot handle {Url}, skipping", url);
            return null;
        }

        if (metadata.Duration > MaxDurationSeconds)
        {
            _logger.LogDebug("Skipping {Url}: duration {Duration}s exceeds {Max}s limit",
                url, metadata.Duration, MaxDurationSeconds);
            return null;
        }

        // Step 2: Download — try video first, fall back to audio-only
        var outputPathMp4 = Path.Combine(CacheDirectory, $"{hash}.mp4");
        var outputPathAudio = Path.Combine(CacheDirectory, $"{hash}.mp3");

        // Try video download first — prefer H.264 for browser compatibility
        var videoArgs = $"--no-playlist -S \"vcodec:h264\" -f \"bestvideo[height<=720]+bestaudio/best[height<=720]/best\" --merge-output-format mp4 -o \"{outputPathMp4}\" -- \"{url}\"";
        var (exitCode, stderr) = await RunYtDlpAsync(videoArgs, DownloadTimeoutMs);

        if (exitCode != 0)
        {
            // Video download failed — try audio-only
            _logger.LogDebug("Video download failed for {Url}, trying audio-only: {Err}", url, TruncateLog(stderr));
            TryDeleteFile(outputPathMp4);

            var audioArgs = $"--no-playlist -x --audio-format mp3 --audio-quality 2 -o \"{outputPathAudio}\" -- \"{url}\"";
            (exitCode, stderr) = await RunYtDlpAsync(audioArgs, DownloadTimeoutMs);

            if (exitCode != 0)
            {
                _logger.LogWarning("yt-dlp download failed for {Url} (exit: {Code}): {Stderr}",
                    url, exitCode, TruncateLog(stderr));
                TryDeleteFile(outputPathAudio);
                return null;
            }
        }

        // yt-dlp may output with a different extension — find the actual file
        var actualPath = FindOutputFile(hash);
        if (actualPath == null)
        {
            _logger.LogWarning("yt-dlp completed but output file not found for {Url}", url);
            return null;
        }

        var actualExt = Path.GetExtension(actualPath);
        var mediaType = IsVideoExtension(actualExt) ? CachedMediaType.Video : CachedMediaType.Audio;

        // Re-encode video to H.264 if needed for browser compatibility.
        // Some sites (e.g. TikTok) serve H.265/HEVC which browsers can't decode —
        // the <video> element plays audio but renders a black screen.
        // The .mp4 container tells us nothing about the codec inside.
        if (mediaType == CachedMediaType.Video && !await IsH264Async(actualPath))
        {
            _logger.LogInformation("Re-encoding {File} to H.264 for browser compatibility", Path.GetFileName(actualPath));
            var reEncodedPath = Path.Combine(CacheDirectory, $"{hash}_h264.mp4");
            var ffmpegResult = await RunProcessAsync("ffmpeg",
                $"-i \"{actualPath}\" -c:v libx264 -crf 23 -preset fast -c:a aac -movflags +faststart -y \"{reEncodedPath}\"",
                DownloadTimeoutMs);
            if (ffmpegResult == 0 && File.Exists(reEncodedPath))
            {
                TryDeleteFile(actualPath);
                File.Move(reEncodedPath, outputPathMp4);
                actualPath = outputPathMp4;
                actualExt = ".mp4";
            }
            else
            {
                TryDeleteFile(reEncodedPath);
                _logger.LogWarning("ffmpeg re-encode failed for {Url}", url);
            }
        }

        var localUrl = $"/media-cache/{hash}{actualExt}";

        // Safety: check file size
        var fileSize = new FileInfo(actualPath).Length;
        if (fileSize > MaxFileSizeBytes)
        {
            _logger.LogWarning("Cached file too large ({SizeMB}MB), deleting: {Url}",
                fileSize / (1024 * 1024), url);
            TryDeleteFile(actualPath);
            return null;
        }

        _logger.LogInformation("Cached media: {Url} -> {File} ({SizeKB}KB, {Duration}s, {Type}, {ElapsedMs}ms)",
            url, Path.GetFileName(actualPath), fileSize / 1024,
            (int)metadata.Duration, mediaType, sw.ElapsedMilliseconds);

        return new MediaCacheEntry(localUrl, mediaType, (int)metadata.Duration);
    }

    /// <summary>
    /// Downloads a Spotify track by searching YouTube for the song title + artist via yt-dlp.
    /// Uses the OG metadata from LinkPreviewService to build the search query.
    /// </summary>
    private async Task<MediaCacheEntry?> DownloadSpotifyAsync(string url, string hash, Stopwatch sw)
    {
        if (!IsAvailable)
        {
            _logger.LogDebug("yt-dlp not available, cannot download Spotify via YouTube search");
            return null;
        }

        // Get OG metadata for song title + artist
        var preview = _linkPreviewService?.GetCachedPreview(url);
        string? searchQuery = null;

        if (preview != null && !string.IsNullOrEmpty(preview.Title))
        {
            // OG title is usually "Song Name - Artist" or "Song Name · Artist"
            searchQuery = preview.Title;
        }

        if (string.IsNullOrEmpty(searchQuery))
        {
            // OG scrape hasn't completed yet or failed — wait briefly and retry
            await Task.Delay(3000);
            preview = _linkPreviewService?.GetCachedPreview(url);
            searchQuery = preview?.Title;
        }

        if (string.IsNullOrEmpty(searchQuery))
        {
            _logger.LogWarning("No title found for Spotify URL {Url}, cannot search YouTube", url);
            return null;
        }

        // Clean up the title — remove "song on Spotify" suffix if present
        searchQuery = searchQuery.Replace(" | Spotify", "").Replace(" - song by ", " ").Replace(" on Spotify", "").Trim();

        _logger.LogInformation("Spotify -> YouTube search: \"{Query}\" for {Url}", searchQuery, url);

        // Use yt-dlp's YouTube search to find and download audio
        var outputPath = Path.Combine(CacheDirectory, $"{hash}.mp3");
        var args = $"--no-playlist -x --audio-format mp3 --audio-quality 2 \"ytsearch1:{searchQuery}\" -o \"{outputPath}\"";
        var (exitCode, stderr) = await RunYtDlpAsync(args, DownloadTimeoutMs);

        if (exitCode != 0)
        {
            _logger.LogWarning("yt-dlp YouTube search failed for Spotify {Url} (query=\"{Query}\", exit={Code}): {Err}",
                url, searchQuery, exitCode, TruncateLog(stderr));
            TryDeleteFile(outputPath);
            return null;
        }

        var actualPath = FindOutputFile(hash);
        if (actualPath == null)
        {
            _logger.LogWarning("yt-dlp completed but output file not found for Spotify {Url}", url);
            return null;
        }

        var fileSize = new FileInfo(actualPath).Length;
        if (fileSize > MaxFileSizeBytes)
        {
            _logger.LogWarning("Spotify cached file too large ({SizeMB}MB), deleting: {Url}", fileSize / (1024 * 1024), url);
            TryDeleteFile(actualPath);
            return null;
        }

        var actualExt = Path.GetExtension(actualPath);
        var localUrl = $"/media-cache/{hash}{actualExt}";

        _logger.LogInformation("Cached Spotify: {Url} -> {File} ({SizeKB}KB, \"{Query}\", {ElapsedMs}ms)",
            url, Path.GetFileName(actualPath), fileSize / 1024, searchQuery, sw.ElapsedMilliseconds);

        return new MediaCacheEntry(localUrl, CachedMediaType.Audio, 0);
    }

    /// <summary>
    /// Gets duration via yt-dlp metadata query. Returns null if yt-dlp can't handle the URL.
    /// </summary>
    private async Task<MediaMetadata?> GetMetadataAsync(string url)
    {
        var (exitCode, stdout) = await RunYtDlpAsync(
            $"--print duration --no-playlist -- \"{url}\"", MetadataTimeoutMs);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return null;

        var line = stdout.Trim().Split('\n')[0].Trim();
        if (!double.TryParse(line, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var duration))
            return null;

        return new MediaMetadata(duration);
    }

    /// <summary>
    /// Finds an output file by hash prefix. Uses glob to catch any extension
    /// yt-dlp may have chosen. Video extensions preferred over audio.
    /// </summary>
    private string? FindOutputFile(string hash)
    {
        try
        {
            var files = Directory.GetFiles(CacheDirectory, $"{hash}.*");
            if (files.Length == 0) return null;

            // Prefer video files over audio
            var video = files.FirstOrDefault(f => IsVideoExtension(Path.GetExtension(f)));
            return video ?? files[0];
        }
        catch
        {
            return null;
        }
    }

    private static bool IsVideoExtension(string ext)
    {
        return ext is ".mp4" or ".webm" or ".mkv";
    }

    private static string ComputeHash(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private async Task<(int ExitCode, string Output)> RunYtDlpAsync(string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo("yt-dlp", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "Failed to start yt-dlp");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, process.ExitCode == 0 ? stdout : stderr);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("yt-dlp timed out after {TimeoutMs}ms", timeoutMs);
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "Timeout");
        }
    }

    /// <summary>
    /// Checks if a video file uses H.264 codec (browser-compatible).
    /// </summary>
    private async Task<bool> IsH264Async(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe",
                $"-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 \"{filePath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return true; // assume OK if can't check

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var codec = output.Trim().ToLowerInvariant();
            return codec == "h264";
        }
        catch
        {
            return true; // assume OK on error
        }
    }

    private async Task<int> RunProcessAsync(string fileName, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) return -1;

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return -1;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string TruncateLog(string text, int maxLength = 500)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private record MediaMetadata(double Duration);
}

public record MediaCacheEntry(string LocalUrl, CachedMediaType MediaType, int DurationSeconds);
