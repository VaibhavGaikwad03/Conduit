using System.Text.Json;
using Conduit.Core.Logging;
using Conduit.Core.Models;
using Serilog;

namespace Conduit.Core.Storage;

/// <summary>Persisted app settings + identity, saved to %LOCALAPPDATA%\Conduit\config.json.</summary>
public sealed class AppConfig
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    /// <summary>Base64 PKCS#8 private key for this device's stable identity.</summary>
    public string? PrivateKey { get; set; }
    public string LogLevel { get; set; } = "Information";
    public bool AutoAcceptFromPaired { get; set; } = true;
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Conduit");
    public List<PairedDevice> PairedDevices { get; set; } = [];
}

/// <summary>Loads and saves <see cref="AppConfig"/> and the trusted-device list.</summary>
public sealed class AppStore
{
    private static readonly ILogger Log = ConduitLog.For("Storage");
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Conduit", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();

    public AppConfig Config { get; private set; } = new();

    public AppStore Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
                Log.Information("Loaded config; {Count} paired device(s)", Config.PairedDevices.Count);
            }
            else
            {
                Save();
                Log.Information("Created new config at {Path}", ConfigPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load config; using defaults");
            Config = new AppConfig();
        }
        return this;
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOpts));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save config");
            }
        }
    }

    public bool IsPaired(string deviceId) => Config.PairedDevices.Any(d => d.DeviceId == deviceId);

    public PairedDevice? GetPaired(string deviceId) =>
        Config.PairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);

    public void AddPaired(PairedDevice device)
    {
        lock (_lock)
        {
            Config.PairedDevices.RemoveAll(d => d.DeviceId == device.DeviceId);
            Config.PairedDevices.Add(device);
        }
        Save();
        Log.Information("Paired with {Name} ({Id})", device.Name, device.DeviceId);
    }

    public void RemovePaired(string deviceId)
    {
        lock (_lock) Config.PairedDevices.RemoveAll(d => d.DeviceId == deviceId);
        Save();
        Log.Information("Unpaired {Id}", deviceId);
    }
}
