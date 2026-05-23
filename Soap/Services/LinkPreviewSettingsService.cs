using System.Text.Json;

namespace Soap.Services;

public class LinkPreviewSettingsService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LinkPreviewSettingsService> _logger;

    private bool _enabled;
    private bool _mediaCachingEnabled;

    public LinkPreviewSettingsService(IWebHostEnvironment env, ILogger<LinkPreviewSettingsService> logger)
    {
        _env = env;
        _logger = logger;
        LoadSettings();
    }

    private string SettingsFilePath => Path.Combine(_env.ContentRootPath, "Data", "link-preview-settings.json");

    public bool Enabled => _enabled;
    public bool MediaCachingEnabled => _mediaCachingEnabled;

    public async Task SetEnabledAsync(bool enabled)
    {
        _enabled = enabled;
        await SaveSettingsAsync();
        _logger.LogInformation("Link previews enabled set to {Enabled}", enabled);
    }

    public async Task SetMediaCachingEnabledAsync(bool enabled)
    {
        _mediaCachingEnabled = enabled;
        await SaveSettingsAsync();
        _logger.LogInformation("Media caching enabled set to {Enabled}", enabled);
    }


    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<LinkPreviewSettings>(json);
                if (settings != null)
                {
                    _enabled = settings.Enabled;
                    _mediaCachingEnabled = settings.MediaCachingEnabled;
                }
                _logger.LogInformation("Loaded link preview settings: Enabled={Enabled}, MediaCaching={MediaCaching}",
                    _enabled, _mediaCachingEnabled);
            }
            else
            {
                // Default: enabled
                _enabled = true;
                _mediaCachingEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load link preview settings from {Path}", SettingsFilePath);
            _enabled = true;
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            var settings = new LinkPreviewSettings
            {
                Enabled = _enabled,
                MediaCachingEnabled = _mediaCachingEnabled
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save link preview settings to {Path}", SettingsFilePath);
        }
    }

    private class LinkPreviewSettings
    {
        public bool Enabled { get; set; } = true;
        public bool MediaCachingEnabled { get; set; } = true;
    }
}
