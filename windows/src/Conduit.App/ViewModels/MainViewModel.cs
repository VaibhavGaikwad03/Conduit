using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Conduit.App.Services;
using Conduit.Core.Networking;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;

namespace Conduit.App.ViewModels;

public sealed class DeviceRow : INotifyPropertyChanged
{
    public required string DeviceId { get; init; }

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _connected;
    public bool Connected { get => _connected; set { Set(ref _connected, value); OnChanged(nameof(StatusBrush)); OnChanged(nameof(ActionText)); } }

    private bool _paired;
    public bool Paired { get => _paired; set { Set(ref _paired, value); OnChanged(nameof(StatusBrush)); OnChanged(nameof(ActionText)); } }

    /// <summary>Green when connected, amber when paired-offline, grey when just discovered.</summary>
    public Brush StatusBrush => _connected
        ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
        : _paired
            ? new SolidColorBrush(Color.FromRgb(0xF5, 0xB4, 0x4C))
            : new SolidColorBrush(Color.FromRgb(0x8D, 0xA0, 0xB4));

    public string ActionText => _paired ? "Connect" : "Pair";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnChanged(name!);
    }
}

public sealed class TransferRow : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    private int _percent;
    public int Percent { get => _percent; set => Set(ref _percent, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private Brush _barBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xC6, 0xE8));
    public Brush BarBrush { get => _barBrush; set => Set(ref _barBrush, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One file found on the peer, shown in the search results list.</summary>
public sealed class SearchResultRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }   // "Folder · 1.2 MB"
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ConduitNode _node;
    private readonly FeatureCoordinator _coordinator;

    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public ObservableCollection<MirroredNotification> Notifications { get; } = new();
    public ObservableCollection<TransferRow> Transfers { get; } = new();
    public ObservableCollection<SearchResultRow> SearchResults { get; } = new();
    public bool HasNotifications => Notifications.Count > 0;
    public bool HasTransfers => Transfers.Count > 0;
    public bool HasSearchResults => SearchResults.Count > 0;

    private string _searchStatus = "";
    public string SearchStatus { get => _searchStatus; set => Set(ref _searchStatus, value); }

    private DeviceRow? _selected;
    public DeviceRow? Selected
    {
        get => _selected;
        set { _selected = value; OnChanged(); OnChanged(nameof(HasSelection)); OnChanged(nameof(SelectedConnected)); OnChanged(nameof(SelectedOffline)); }
    }
    public bool HasSelection => _selected is not null;

    /// <summary>True when a device is selected and it's currently connected — actions are only shown then.</summary>
    public bool SelectedConnected => _selected?.Connected ?? false;

    /// <summary>True when a device is selected but not connected — show a "connect first" hint.</summary>
    public bool SelectedOffline => _selected is not null && !_selected.Connected;

    private bool _drawerOpen;
    public bool DrawerOpen { get => _drawerOpen; set => Set(ref _drawerOpen, value); }

    private string _selfName = "";
    public string SelfName { get => _selfName; set => Set(ref _selfName, value); }

    private string _connectionSummary = "Waiting for devices…";
    public string ConnectionSummary { get => _connectionSummary; set => Set(ref _connectionSummary, value); }

    private bool _anyConnected;
    public bool AnyConnected { get => _anyConnected; set { Set(ref _anyConnected, value); OnChanged(nameof(StatusPillBrush)); } }

    public Brush StatusPillBrush => _anyConnected
        ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
        : new SolidColorBrush(Color.FromRgb(0x8D, 0xA0, 0xB4));

    // ---- Phone status (granular, for a proper layout) ----
    private int _batteryLevel;
    public int BatteryLevel { get => _batteryLevel; set => Set(ref _batteryLevel, value); }

    private string _batteryText = "—";
    public string BatteryText { get => _batteryText; set => Set(ref _batteryText, value); }

    private string _wifiText = "—";
    public string WifiText { get => _wifiText; set => Set(ref _wifiText, value); }

    private string _ringerText = "—";
    public string RingerText { get => _ringerText; set => Set(ref _ringerText, value); }

    private string _nowPlaying = "Nothing playing";
    public string NowPlaying { get => _nowPlaying; set => Set(ref _nowPlaying, value); }

    public bool HasDevices => Devices.Count > 0;

    public MainViewModel(ConduitNode node, FeatureCoordinator coordinator, NotificationService notifications)
    {
        _node = node;
        _coordinator = coordinator;
        SelfName = node.Self.Name;

        _node.DevicesChanged += (_, _) => Dispatch(RefreshDevices);
        _node.PeerConnected += (_, _) => Dispatch(RefreshDevices);
        _node.PeerDisconnected += (_, _) => Dispatch(RefreshDevices);
        _coordinator.StatusChanged += (_, _) => Dispatch(UpdateStatus);
        _coordinator.FileProgress += (_, p) => Dispatch(() => OnFileProgress(p));
        _coordinator.SearchResults += (_, r) => Dispatch(() => OnSearchResults(r));
        notifications.NotificationsChanged += (_, _) => Dispatch(() => RefreshNotifications(notifications));

        RefreshDevices();
    }

    private static readonly SolidColorBrush CyanBrush = new(Color.FromRgb(0x2F, 0xC6, 0xE8));
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xFF, 0x6B, 0x6B));

    private void OnFileProgress(TransferProgress p)
    {
        var row = Transfers.FirstOrDefault(t => t.Id == p.Id);
        if (row is null)
        {
            row = new TransferRow { Id = p.Id, Name = p.Name };
            Transfers.Add(row);
            OnChanged(nameof(HasTransfers));
        }

        row.Percent = p.Percent;
        row.Status = p.Failed
            ? "Failed"
            : p.Done
                ? (p.IsSending ? "Sent ✓" : "Saved to Downloads ✓")
                : $"{(p.IsSending ? "Sending" : "Receiving")} · {p.Percent}%";
        row.BarBrush = p.Failed ? RedBrush : p.Done ? GreenBrush : CyanBrush;

        if (p.Done || p.Failed)
        {
            var finished = row;
            _ = Task.Delay(4000).ContinueWith(_ => Dispatch(() =>
            {
                Transfers.Remove(finished);
                OnChanged(nameof(HasTransfers));
            }));
        }
    }

    /// <summary>Clears the results and shows a searching state; called when a search is fired.</summary>
    public void BeginSearch()
    {
        SearchResults.Clear();
        OnChanged(nameof(HasSearchResults));
        SearchStatus = "Searching…";
    }

    private void OnSearchResults(FileSearchResultsEventArgs r)
    {
        SearchResults.Clear();
        foreach (var it in r.Results)
        {
            var detail = string.IsNullOrEmpty(it.Folder) ? FormatSize(it.Size) : $"{it.Folder} · {FormatSize(it.Size)}";
            SearchResults.Add(new SearchResultRow { Id = it.Id, Name = it.Name, Detail = detail });
        }
        SearchStatus = r.Results.Count == 0
            ? "No matches"
            : $"{r.Results.Count} result{(r.Results.Count == 1 ? "" : "s")}{(r.Truncated ? " (showing first 100)" : "")}";
        OnChanged(nameof(HasSearchResults));
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:0.#} {units[i]}";
    }

    private void RefreshNotifications(NotificationService svc)
    {
        Notifications.Clear();
        foreach (var n in svc.Recent.Take(20)) Notifications.Add(n);
        OnChanged(nameof(HasNotifications));
    }

    private void RefreshDevices()
    {
        foreach (var dev in _node.KnownDevices)
        {
            var row = Devices.FirstOrDefault(d => d.DeviceId == dev.DeviceId);
            bool connected = _node.IsConnected(dev.DeviceId);
            string status = connected ? "Connected" : dev.IsPaired ? "Paired · offline" : "Discovered";
            if (row is null)
                Devices.Add(new DeviceRow { DeviceId = dev.DeviceId, Name = dev.Name, Status = status, Connected = connected, Paired = dev.IsPaired });
            else
            {
                row.Name = dev.Name;
                row.Status = status;
                row.Connected = connected;
                row.Paired = dev.IsPaired;
            }
        }

        AnyConnected = Devices.Any(d => d.Connected);
        var connectedNames = Devices.Where(d => d.Connected).Select(d => d.Name).ToList();
        ConnectionSummary = connectedNames.Count > 0
            ? $"Connected to {string.Join(", ", connectedNames)}"
            : Devices.Count > 0 ? "Device found · not connected" : "Searching for devices…";
        OnChanged(nameof(HasDevices));

        // Keep a device selected so the detail pane always reflects something useful:
        // prefer the connected one, otherwise the first device.
        if (_selected is null || Devices.All(d => d.DeviceId != _selected.DeviceId))
            Selected = Devices.FirstOrDefault(d => d.Connected) ?? Devices.FirstOrDefault();

        // The selected device's connection state may have just changed.
        OnChanged(nameof(SelectedConnected));
        OnChanged(nameof(SelectedOffline));
    }

    private void UpdateStatus()
    {
        var s = _coordinator.Status;
        BatteryLevel = s.BatteryLevel;
        BatteryText = s.BatteryLevel > 0 ? $"{s.BatteryLevel}%{(s.Charging ? "  ⚡ charging" : "")}" : "—";
        WifiText = string.IsNullOrEmpty(s.Ssid) ? "—" : s.Ssid;
        RingerText = string.IsNullOrEmpty(s.RingerMode) ? "—" : s.RingerMode;
        NowPlaying = string.IsNullOrWhiteSpace(s.NowPlaying) || s.NowPlaying.Trim() == "—"
            ? "Nothing playing" : s.NowPlaying;
    }

    private static void Dispatch(Action a) => Application.Current.Dispatcher.Invoke(a);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnChanged(name);
    }
}
