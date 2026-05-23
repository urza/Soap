using System.Text.Json;

namespace Soap.Services;

/// <summary>
/// Persistent random-pacing settings for MediaCacheService downloads. Read at the
/// start of every download via the live properties so changes apply immediately
/// (in-flight downloads finish on the old delay; the next one uses the new one).
/// </summary>
public class PacingSettings
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PacingSettings> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public const int DefaultMinDelayMs = 2_500;
    public const int DefaultMaxDelayMs = 5_500;
    public const int HardMaxDelayMs = 120_000; // 2 minutes — sanity upper bound

    public int MinDelayMs { get; private set; } = DefaultMinDelayMs;
    public int MaxDelayMs { get; private set; } = DefaultMaxDelayMs;

    public PacingSettings(IWebHostEnvironment env, ILogger<PacingSettings> logger)
    {
        _env = env;
        _logger = logger;
        Load();
    }

    private string FilePath => Path.Combine(_env.ContentRootPath, "Data", "pacing-settings.json");

    public async Task SetAsync(int minMs, int maxMs)
    {
        if (minMs < 0) minMs = 0;
        if (maxMs < minMs) maxMs = minMs;
        if (maxMs > HardMaxDelayMs) maxMs = HardMaxDelayMs;
        if (minMs > HardMaxDelayMs) minMs = HardMaxDelayMs;

        MinDelayMs = minMs;
        MaxDelayMs = maxMs;
        await SaveAsync();
        _logger.LogInformation("Pacing updated: min={Min}ms max={Max}ms", MinDelayMs, MaxDelayMs);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<Persisted>(json);
            if (data == null) return;
            MinDelayMs = Math.Clamp(data.MinDelayMs, 0, HardMaxDelayMs);
            MaxDelayMs = Math.Clamp(data.MaxDelayMs, MinDelayMs, HardMaxDelayMs);
            _logger.LogInformation("Loaded pacing: min={Min}ms max={Max}ms", MinDelayMs, MaxDelayMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pacing settings from {Path}", FilePath);
        }
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(new Persisted
            {
                MinDelayMs = MinDelayMs,
                MaxDelayMs = MaxDelayMs
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(FilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save pacing settings to {Path}", FilePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private class Persisted
    {
        public int MinDelayMs { get; set; } = DefaultMinDelayMs;
        public int MaxDelayMs { get; set; } = DefaultMaxDelayMs;
    }
}
