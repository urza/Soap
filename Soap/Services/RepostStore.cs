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
    private readonly ConcurrentDictionary<string, RepostEntry> _byId = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private CancellationTokenSource? _saveDebounceCts;
    private readonly object _importLock = new();
    private long _nextRepostOrder;

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
                {
                    // Older galleries have no source order. Retain their previous
                    // ordering until a newest-first import supplies it.
                    if (entry.RepostOrder == 0) entry.RepostOrder = entry.AddedAt.Ticks;
                    _nextRepostOrder = Math.Max(_nextRepostOrder, entry.RepostOrder);
                    _entries[entry.Url] = entry;
                    _byId[entry.Id] = entry;
                }
            }
            _logger.LogInformation("Loaded {Count} repost entries from {Path}", _entries.Count, FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load reposts from {Path}", FilePath);
        }
    }

    public RepostEntry? Get(string url) => _entries.TryGetValue(url, out var e) ? e : null;

    public RepostEntry? GetById(string id) => _byId.TryGetValue(id, out var e) ? e : null;

    public IReadOnlyList<RepostEntry> GetAll() =>
        _entries.Values.OrderByDescending(e => e.RepostOrder)
            .ThenByDescending(e => e.AddedAt).ThenBy(e => e.Url, StringComparer.Ordinal).ToList();

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
    /// Import a newest-first source list. Reimporting updates its order without
    /// resetting existing statuses, files, or import dates. Returns only new URLs.
    /// Separate batches appear newest first; duplicate URLs keep their first position.
    /// </summary>
    public IReadOnlyList<string> ImportNewestFirst(IEnumerable<string> urls, string? sourceProfile)
    {
        var ordered = urls.Distinct(StringComparer.Ordinal).ToList();
        if (ordered.Count == 0) return [];
        var added = new List<string>();
        lock (_importLock)
        {
            _nextRepostOrder = checked(_nextRepostOrder + ordered.Count);
            var order = _nextRepostOrder;
            var importedAt = DateTime.UtcNow;
            foreach (var url in ordered)
            {
                if (_entries.TryGetValue(url, out var existing))
                {
                    existing.RepostOrder = order--;
                    continue;
                }
                var entry = new RepostEntry
                {
                    Url = url,
                    SourceProfile = sourceProfile,
                    Status = RepostStatus.Queued,
                    AddedAt = importedAt,
                    RepostOrder = order--
                };
                _entries[url] = entry;
                _byId[entry.Id] = entry;
                added.Add(url);
            }
        }
        ScheduleSave();
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
            var list = GetAll();
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
