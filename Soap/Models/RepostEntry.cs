using System.Text.Json.Serialization;
using Soap.Services;

namespace Soap.Models;

public enum RepostStatus
{
    Queued,
    Downloading,
    Ready,
    Failed
}

public class RepostEntry
{
    public string Url { get; set; } = "";

    /// <summary>
    /// 16-char hash of the URL. Computed on the fly, serialized to JSON for the
    /// frontend, and used as the public /v/{id} slug. Never deserialized — it's
    /// always derived from Url, even for entries loaded from older JSON files.
    /// </summary>
    [JsonInclude]
    public string Id => string.IsNullOrEmpty(Url) ? "" : UrlHash.Compute(Url);

    public string? SourceProfile { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CachedMediaUrl { get; set; }
    public CachedMediaType? MediaType { get; set; }
    public int? MediaDurationSeconds { get; set; }
    public RepostStatus Status { get; set; } = RepostStatus.Queued;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
