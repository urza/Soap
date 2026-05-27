using System.Net;
using System.Text;
using Soap.Models;

namespace Soap.Services;

/// <summary>
/// Renders the standalone /v/{id} detail page. Self-contained dark-themed HTML
/// with OG and Twitter player meta tags so messengers (Discord, Telegram, Slack,
/// Twitter) embed a preview when the URL is shared.
/// </summary>
public static class VideoPageBuilder
{
    public static string Build(RepostEntry entry, HttpRequest request)
    {
        var origin = $"{request.Scheme}://{request.Host}";
        var pageUrl = $"{origin}/v/{entry.Id}";
        var videoAbsUrl = entry.CachedMediaUrl != null ? origin + entry.CachedMediaUrl : null;
        var thumbAbsUrl = entry.ThumbnailUrl; // already an absolute URL from TikTok scrape

        var title = !string.IsNullOrWhiteSpace(entry.Title) ? entry.Title! : "TikTok video";
        var description = !string.IsNullOrWhiteSpace(entry.Description)
            ? entry.Description!
            : (!string.IsNullOrWhiteSpace(entry.SourceProfile) ? $"Reposted from @{entry.SourceProfile}" : "Backed up via Soap.");
        var sourceLink = entry.Url;
        var sourceProfile = entry.SourceProfile;
        var durationStr = entry.MediaDurationSeconds is int s && s > 0
            ? $"{s / 60}:{(s % 60):00}"
            : null;

        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
        sb.Append($"<title>{H(title)} — Soap</title>\n");
        sb.Append($"<meta name=\"description\" content=\"{H(description)}\">\n");

        // --- Open Graph ---
        sb.Append("<meta property=\"og:type\" content=\"video.other\">\n");
        sb.Append($"<meta property=\"og:url\" content=\"{H(pageUrl)}\">\n");
        sb.Append($"<meta property=\"og:title\" content=\"{H(title)}\">\n");
        sb.Append($"<meta property=\"og:description\" content=\"{H(description)}\">\n");
        if (!string.IsNullOrEmpty(thumbAbsUrl))
        {
            sb.Append($"<meta property=\"og:image\" content=\"{H(thumbAbsUrl)}\">\n");
            sb.Append("<meta property=\"og:image:width\" content=\"720\">\n");
            sb.Append("<meta property=\"og:image:height\" content=\"1280\">\n");
        }
        if (!string.IsNullOrEmpty(videoAbsUrl))
        {
            sb.Append($"<meta property=\"og:video\" content=\"{H(videoAbsUrl)}\">\n");
            sb.Append($"<meta property=\"og:video:secure_url\" content=\"{H(videoAbsUrl)}\">\n");
            sb.Append("<meta property=\"og:video:type\" content=\"video/mp4\">\n");
            sb.Append("<meta property=\"og:video:width\" content=\"720\">\n");
            sb.Append("<meta property=\"og:video:height\" content=\"1280\">\n");
        }

        // --- Twitter card ---
        sb.Append("<meta name=\"twitter:card\" content=\"player\">\n");
        sb.Append($"<meta name=\"twitter:title\" content=\"{H(title)}\">\n");
        sb.Append($"<meta name=\"twitter:description\" content=\"{H(description)}\">\n");
        if (!string.IsNullOrEmpty(thumbAbsUrl))
            sb.Append($"<meta name=\"twitter:image\" content=\"{H(thumbAbsUrl)}\">\n");
        if (!string.IsNullOrEmpty(videoAbsUrl))
        {
            sb.Append($"<meta name=\"twitter:player\" content=\"{H(videoAbsUrl)}\">\n");
            sb.Append("<meta name=\"twitter:player:width\" content=\"720\">\n");
            sb.Append("<meta name=\"twitter:player:height\" content=\"1280\">\n");
        }

        sb.Append("<style>");
        sb.Append(@"
:root { color-scheme: dark; }
* { box-sizing: border-box; }
body {
  margin: 0; padding: 24px 16px;
  font: 15px/1.45 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  background: #0c0d10; color: #e6e8ec;
  min-height: 100vh;
  display: flex; flex-direction: column; align-items: center; gap: 18px;
}
header.top { width: 100%; max-width: 720px; display: flex; gap: 12px; align-items: baseline; }
header.top a { color: #8a8f99; text-decoration: none; font-size: 13px; }
header.top a:hover { color: #e6e8ec; }
.player { width: 100%; max-width: min(440px, 95vw); }
.player video {
  width: 100%; display: block; border-radius: 12px; background: #000;
  max-height: 85vh;
}
.placeholder {
  width: 100%; aspect-ratio: 9/16;
  background: #15171c; border-radius: 12px;
  display: flex; align-items: center; justify-content: center;
  color: #8a8f99;
}
.info { width: 100%; max-width: 720px; }
.info h1 { font-size: 18px; font-weight: 600; margin: 0 0 6px; line-height: 1.3; }
.info .desc { color: #c1c5cc; margin: 0 0 14px; white-space: pre-wrap; }
.meta-row { display: flex; gap: 12px; flex-wrap: wrap; color: #8a8f99; font-size: 13px; margin-bottom: 14px; }
.actions { display: flex; gap: 10px; flex-wrap: wrap; }
.actions a {
  display: inline-flex; align-items: center; gap: 6px;
  padding: 8px 14px; border-radius: 8px; font-weight: 600; text-decoration: none;
  font-size: 14px;
}
.actions .primary { background: #ff3b6f; color: #fff; }
.actions .primary:hover { filter: brightness(1.08); }
.actions .secondary { background: transparent; color: #c1c5cc; border: 1px solid #262931; }
.actions .secondary:hover { border-color: #4a4f59; color: #fff; }
");
        sb.Append("</style>\n</head>\n<body>\n");

        // Top bar
        sb.Append("<header class=\"top\"><a href=\"/\">← Back to gallery</a></header>\n");

        // Player
        sb.Append("<div class=\"player\">");
        if (!string.IsNullOrEmpty(videoAbsUrl))
        {
            sb.Append("<video controls preload=\"metadata\" playsinline");
            if (!string.IsNullOrEmpty(thumbAbsUrl))
                sb.Append($" poster=\"{H(thumbAbsUrl)}\"");
            sb.Append($" src=\"{H(entry.CachedMediaUrl!)}\"></video>");
        }
        else
        {
            sb.Append($"<div class=\"placeholder\">Video not available ({H(entry.Status.ToString())})</div>");
        }
        sb.Append("</div>\n");

        // Info
        sb.Append("<div class=\"info\">\n");
        sb.Append($"<h1>{H(title)}</h1>\n");
        if (!string.IsNullOrWhiteSpace(entry.Description))
            sb.Append($"<p class=\"desc\">{H(entry.Description)}</p>\n");
        sb.Append("<div class=\"meta-row\">");
        if (!string.IsNullOrEmpty(sourceProfile))
            sb.Append($"<span>@{H(sourceProfile)}</span>");
        if (durationStr != null)
            sb.Append($"<span>{H(durationStr)}</span>");
        sb.Append($"<span>Saved {H(entry.AddedAt.ToString("yyyy-MM-dd"))}</span>");
        sb.Append("</div>\n");

        sb.Append("<div class=\"actions\">");
        sb.Append($"<a class=\"primary\" href=\"{H(sourceLink)}\" target=\"_blank\" rel=\"noopener noreferrer\">Open on TikTok ↗</a>");
        if (!string.IsNullOrEmpty(videoAbsUrl))
            sb.Append($"<a class=\"secondary\" href=\"{H(entry.CachedMediaUrl!)}\" download>Download .mp4</a>");
        sb.Append("</div>\n");

        sb.Append("</div>\n</body>\n</html>");
        return sb.ToString();
    }

    private static string H(string? s) => s == null ? "" : WebUtility.HtmlEncode(s);
}
