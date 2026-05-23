namespace Soap.Models;

public class LinkPreview
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? SiteName { get; set; }
    public DateTime FetchedAt { get; set; }
    public bool Failed { get; set; }
    public bool HasContent => !Failed && (!string.IsNullOrEmpty(Title) || !string.IsNullOrEmpty(Description));

    // Media cache fields (populated by MediaCacheService)
    public string? CachedMediaUrl { get; set; }
    public CachedMediaType? MediaType { get; set; }
    public int? MediaDurationSeconds { get; set; }
}

public enum CachedMediaType
{
    Video,
    Audio
}
