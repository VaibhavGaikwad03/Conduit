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

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ConduitNode _node;
    private readonly FeatureCoordinator _coordinator;

    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public ObservableCollection<MirroredNotification> Notifications { get; } = new();
    public bool HasNotifications => Notifications.Count > 0;

    private DeviceRow? _selected;
    public DeviceRow? Selected
    {
        get => _selected;
        set { _selected = value; OnChanged(); OnChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => _selected is not null;

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
        notifications.NotificationsChanged += (_, _) => Dispatch(() => RefreshNotifications(notifications));

        RefreshDevices();
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
