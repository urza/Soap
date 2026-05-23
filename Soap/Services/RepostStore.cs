using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soap.Models;

namespace Soap.Services;

/// <summary>
/// JSON-backed store of TikTok repost entries. In-memory dictionary with debounced
/// writes to Data/reposts.json so the gallery survives restarts.
/// </summary>
public class RepostStore
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<RepostStore> _logger;
    private readonly ConcurrentDictionary<string, RepostEntry> _entries = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private CancellationTokenSource? _saveDebounceCts;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RepostStore(IWebHostEnvironment env, ILogger<RepostStore> logger)
    {
        _env = env;
        _logger = logger;
        Load();
    }

    private string FilePath => Path.Combine(_env.ContentRootPath, "Data", "reposts.json");

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize<List<RepostEntry>>(json, JsonOpts);
            if (list == null) return;
            foreach (var entry in list)
            {
                if (!string.IsNullOrEmpty(entry.Url))
                    _entries[entry.Url] = entry;
            }
            _logger.LogInformation("Loaded {Count} repost entries from {Path}", _entries.Count, FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load reposts from {Path}", FilePath);
        }
    }

    public RepostEntry? Get(string url) => _entries.TryGetValue(url, out var e) ? e : null;

    public IReadOnlyList<RepostEntry> GetAll() =>
        _entries.Values.OrderByDescending(e => e.AddedAt).ToList();

    public (int Queued, int Downloading, int Ready, int Failed) GetCounts()
    {
        int q = 0, d = 0, r = 0, f = 0;
        foreach (var e in _entries.Values)
        {
            switch (e.Status)
            {
                case RepostStatus.Queued: q++; break;
                case RepostStatus.Downloading: d++; break;
                case RepostStatus.Ready: r++; break;
                case RepostStatus.Failed: f++; break;
            }
        }
        return (q, d, r, f);
    }

    /// <summary>
    /// Adds a URL if not present. Returns true if it was new.
    /// </summary>
    public bool TryAdd(string url, string? sourceProfile)
    {
        var added = _entries.TryAdd(url, new RepostEntry
        {
            Url = url,
            SourceProfile = sourceProfile,
            Status = RepostStatus.Queued,
            AddedAt = DateTime.UtcNow
        });
        if (added) ScheduleSave();
        return added;
    }

    public void Update(string url, Action<RepostEntry> mutate)
    {
        if (!_entries.TryGetValue(url, out var entry)) return;
        mutate(entry);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts = new CancellationTokenSource();
        var token = _saveDebounceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                await SaveAsync();
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var list = _entries.Values.OrderByDescending(e => e.AddedAt).ToList();
            var tmp = FilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(list, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save reposts to {Path}", FilePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
