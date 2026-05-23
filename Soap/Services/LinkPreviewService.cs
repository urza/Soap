using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Soap.Models;

namespace Soap.Services;

public partial class LinkPreviewService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LinkPreviewService> _logger;

    // Cache: URL -> LinkPreview (with 1-hour TTL)
    private readonly ConcurrentDictionary<string, LinkPreview> _cache = new();

    // Dedup in-flight requests
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private const int MaxResponseBytes = 256 * 1024; // 256KB

    /// <summary>
    /// Callback invoked when a preview fetch completes. Parameters: (messageId, url, preview).
    /// </summary>
    public Action<Guid, string, LinkPreview>? OnPreviewFetched { get; set; }

    public LinkPreviewService(IHttpClientFactory httpClientFactory, ILogger<LinkPreviewService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Extracts URLs from plain text. Handles balanced parentheses (Wikipedia) and strips trailing punctuation.
    /// </summary>
    public static List<string> ExtractUrls(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var matches = UrlRegex().Matches(text);
        var urls = new List<string>();

        foreach (Match match in matches)
        {
            var url = match.Value;

            // Strip trailing punctuation that's likely not part of the URL
            url = StripTrailingPunctuation(url);

            // Only allow http/https schemes (or no scheme = assume https)
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
            else if (!url.Contains("://"))
            {
                // Bare domain like example.com/path — prepend https
                urls.Add("https://" + url);
            }
            // Skip javascript:, ftp:, etc.
        }

        return urls;
    }

    /// <summary>
    /// Pure cache lookup — no side effects.
    /// </summary>
    public LinkPreview? GetCachedPreview(string url)
    {
        if (_cache.TryGetValue(url, out var preview))
        {
            if (DateTime.UtcNow - preview.FetchedAt < CacheTtl)
                return preview;

            // Expired
            _cache.TryRemove(url, out _);
        }
        return null;
    }

    /// <summary>
    /// Gets existing preview or creates a minimal one (for media cache to attach to
    /// when OG scrape failed or hasn't completed).
    /// </summary>
    public LinkPreview GetOrCreatePreview(string url)
    {
        if (_cache.TryGetValue(url, out var existing) && DateTime.UtcNow - existing.FetchedAt < CacheTtl)
            return existing;

        var preview = new LinkPreview { Url = url, FetchedAt = DateTime.UtcNow };
        _cache[url] = preview;
        return preview;
    }

    /// <summary>
    /// Fire-and-forget background fetch. Invokes OnPreviewFetched when done.
    /// </summary>
    public void QueueFetch(Guid messageId, string url)
    {
        // Already cached?
        if (GetCachedPreview(url) != null)
        {
            OnPreviewFetched?.Invoke(messageId, url, _cache[url]);
            return;
        }

        // Already in flight?
        if (!_inFlight.TryAdd(url, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var preview = await FetchPreviewAsync(url);
                _cache[url] = preview;
                OnPreviewFetched?.Invoke(messageId, url, preview);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch link preview for {Url}", url);
                var failed = new LinkPreview { Url = url, Failed = true, FetchedAt = DateTime.UtcNow };
                _cache[url] = failed;
            }
            finally
            {
                _inFlight.TryRemove(url, out _);
            }
        });
    }

    private async Task<LinkPreview> FetchPreviewAsync(string url)
    {
        // SSRF prevention: resolve hostname and block private IPs
        var uri = new Uri(url);
        if (!await IsPublicHostAsync(uri.Host))
        {
            _logger.LogDebug("Blocked SSRF attempt to private host: {Host}", uri.Host);
            return new LinkPreview { Url = url, Failed = true, FetchedAt = DateTime.UtcNow };
        }

        var client = _httpClientFactory.CreateClient("LinkPreview");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "text/html");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
            return new LinkPreview { Url = url, Failed = true, FetchedAt = DateTime.UtcNow };

        // Only process HTML responses
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType != null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return new LinkPreview { Url = url, Failed = true, FetchedAt = DateTime.UtcNow };

        // Read limited bytes
        var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[MaxResponseBytes];
        var bytesRead = await ReadUpToAsync(stream, buffer);
        var html = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);

        return ParseOpenGraph(url, html);
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    private static LinkPreview ParseOpenGraph(string url, string html)
    {
        var preview = new LinkPreview
        {
            Url = url,
            FetchedAt = DateTime.UtcNow
        };

        // Parse og:title
        preview.Title = ExtractMetaContent(html, "og:title");

        // Parse og:description
        preview.Description = ExtractMetaContent(html, "og:description");

        // Parse og:image
        preview.ImageUrl = ExtractMetaContent(html, "og:image");

        // Parse og:site_name
        preview.SiteName = ExtractMetaContent(html, "og:site_name");

        // Fallback: <title> tag if no og:title
        if (string.IsNullOrEmpty(preview.Title))
        {
            var titleMatch = TitleTagRegex().Match(html);
            if (titleMatch.Success)
                preview.Title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
        }

        // Fallback: meta description if no og:description
        if (string.IsNullOrEmpty(preview.Description))
        {
            preview.Description = ExtractMetaContent(html, "description", isOg: false);
        }

        // Truncate description
        if (preview.Description?.Length > 300)
            preview.Description = preview.Description[..300] + "...";

        // Mark as failed if we got nothing useful
        if (!preview.HasContent)
            preview.Failed = true;

        return preview;
    }

    private static string? ExtractMetaContent(string html, string property, bool isOg = true)
    {
        // Match both property="og:X" and name="description" variants
        var attr = isOg ? "property" : "name";
        var q = "[\"']";    // character class matching " or '
        var nq = "[^\"']*"; // character class matching anything except " or '

        // Pattern: <meta ... property="og:X" ... content="value" ...>
        var pattern = $@"<meta[^>]+{attr}\s*=\s*{q}{Regex.Escape(property)}{q}[^>]+content\s*=\s*{q}({nq}){q}";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());

        // Try reversed attribute order: <meta ... content="value" ... property="og:X" ...>
        var reversePattern = $@"<meta[^>]+content\s*=\s*{q}({nq}){q}[^>]+{attr}\s*=\s*{q}{Regex.Escape(property)}{q}";
        match = Regex.Match(html, reversePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());

        return null;
    }

    private static async Task<bool> IsPublicHostAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            foreach (var addr in addresses)
            {
                if (IsPrivateAddress(addr))
                    return false;
            }
            return addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        // Map IPv6-mapped IPv4 to IPv4
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,                                      // 10.0.0.0/8
                172 => bytes[1] >= 16 && bytes[1] <= 31,        // 172.16.0.0/12
                192 => bytes[1] == 168,                          // 192.168.0.0/16
                169 => bytes[1] == 254,                          // 169.254.0.0/16 (link-local)
                127 => true,                                     // 127.0.0.0/8
                0 => true,                                       // 0.0.0.0/8
                _ => false
            };
        }

        // IPv6 link-local, unique-local
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;

        // IPv6 unique local (fc00::/7)
        var ipv6Bytes = address.GetAddressBytes();
        if ((ipv6Bytes[0] & 0xFE) == 0xFC)
            return true;

        return false;
    }

    private static string StripTrailingPunctuation(string url)
    {
        // Handle balanced parentheses (Wikipedia URLs like https://en.wikipedia.org/wiki/Foo_(bar))
        var openParens = url.Count(c => c == '(');
        var closeParens = url.Count(c => c == ')');

        // If more closing parens than opening, strip the excess trailing ones
        while (closeParens > openParens && url.EndsWith(')'))
        {
            url = url[..^1];
            closeParens--;
        }

        // Strip trailing punctuation that's not part of URLs
        while (url.Length > 0 && ".,:;!?".Contains(url[^1]))
        {
            url = url[..^1];
        }

        return url;
    }

    // URL regex: matches http(s)://... or www. or domain.tld/path patterns
    [GeneratedRegex(
        @"(?:https?://|www\.)[^\s<>""'\]\)]*[^\s<>""'\]\).,;:!?\-]|(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+(?:com|org|net|io|dev|app|co|me|info|edu|gov)\b(?:/[^\s<>""']*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTagRegex();
}
