using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Soap.Services;

/// <summary>
/// Lists TikTok video URLs from a user's reposts (or main) feed via yt-dlp --flat-playlist,
/// then queues each one through the PreviewCoordinator. The reposts tab usually requires
/// a logged-in cookies file at Data/tiktok-cookies.txt (Netscape format).
/// </summary>
public class TikTokScraperService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TikTokScraperService> _logger;
    private readonly RepostStore _store;
    private readonly PreviewCoordinator _coordinator;

    private const int EnumerateTimeoutMs = 120_000;

    public TikTokScraperService(
        IWebHostEnvironment env,
        ILogger<TikTokScraperService> logger,
        RepostStore store,
        PreviewCoordinator coordinator)
    {
        _env = env;
        _logger = logger;
        _store = store;
        _coordinator = coordinator;
    }

    private string CookiesPath => Path.Combine(_env.ContentRootPath, "Data", "tiktok-cookies.txt");

    public record ScrapeResult(
        int Queued,
        int AlreadyKnown,
        int Found,
        string? Warning,
        string? ProfileUrl = null,
        int? YtDlpExitCode = null,
        string? YtDlpStderr = null);

    /// <summary>
    /// Scrapes a TikTok username's reposts page and queues each video URL.
    /// </summary>
    public async Task<ScrapeResult> ScrapeRepostsAsync(string username, CancellationToken ct = default)
    {
        username = username.TrimStart('@').Trim();
        if (string.IsNullOrWhiteSpace(username))
            return new ScrapeResult(0, 0, 0, "username is empty");

        if (!MediaCacheService.IsAvailable)
            return new ScrapeResult(0, 0, 0, "yt-dlp not installed");

        var profileUrl = $"https://www.tiktok.com/@{username}/reposts";
        var enumResult = await EnumerateUrlsAsync(profileUrl, ct);

        string? warning = null;
        if (enumResult.Urls.Count == 0)
        {
            if (enumResult.ExitCode != 0)
                warning = $"yt-dlp exited with code {enumResult.ExitCode}. See ytDlpStderr below.";
            else if (!File.Exists(CookiesPath))
                warning = "Found 0 URLs. Set TikTok cookies above and retry.";
            else
                warning = "Found 0 URLs (yt-dlp exited cleanly). Cookies may be stale, or this yt-dlp version may not support the /reposts feed.";
        }

        int newCount = 0, knownCount = 0;
        foreach (var url in enumResult.Urls)
        {
            if (_store.TryAdd(url, username))
            {
                newCount++;
                _coordinator.Queue(url, includeMedia: true);
            }
            else
            {
                knownCount++;
            }
        }

        _logger.LogInformation("Scraped {Profile}: found={Found} new={New} known={Known} exit={Exit}",
            profileUrl, enumResult.Urls.Count, newCount, knownCount, enumResult.ExitCode);

        return new ScrapeResult(
            newCount, knownCount, enumResult.Urls.Count, warning,
            profileUrl, enumResult.ExitCode, Truncate(enumResult.Stderr));
    }

    private record EnumerateResult(List<string> Urls, int ExitCode, string Stderr);

    private async Task<EnumerateResult> EnumerateUrlsAsync(string profileUrl, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--flat-playlist",
            "--print", "%(url)s",
        };

        if (File.Exists(CookiesPath))
        {
            args.Add("--cookies");
            args.Add(CookiesPath);
        }

        args.Add("--");
        args.Add(profileUrl);

        var psi = new ProcessStartInfo("yt-dlp")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process == null)
            return new EnumerateResult(new(), -1, "failed to start yt-dlp");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(EnumerateTimeoutMs);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            _logger.LogWarning("yt-dlp enumeration timed out for {Profile}", profileUrl);
            return new EnumerateResult(new(), -1, "yt-dlp timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var exitCode = process.ExitCode;

        if (exitCode != 0)
        {
            _logger.LogWarning("yt-dlp enumerate failed for {Profile} (exit={Code}): {Err}",
                profileUrl, exitCode, Truncate(stderr));
            return new EnumerateResult(new(), exitCode, stderr);
        }

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // yt-dlp may emit either a full URL or just an ID for some extractors.
            if (line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(line)) urls.Add(line);
            }
            else if (Regex.IsMatch(line, @"^\d{6,}$"))
            {
                // Bare numeric video id; reconstruct.
                var reconstructed = $"https://www.tiktok.com/video/{line}";
                if (seen.Add(reconstructed)) urls.Add(reconstructed);
            }
        }
        return new EnumerateResult(urls, exitCode, stderr);
    }

    private static string Truncate(string s, int max = 500) =>
        s.Length <= max ? s : s[..max] + "...";
}
