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
