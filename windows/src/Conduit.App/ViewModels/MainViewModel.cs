using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Conduit.App.Services;
using Conduit.Core.Models;
using Conduit.Core.Networking;
using Application = System.Windows.Application;

namespace Conduit.App.ViewModels;

public sealed class DeviceRow : INotifyPropertyChanged
{
    public required string DeviceId { get; init; }
    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
    private bool _connected;
    public bool Connected { get => _connected; set => Set(ref _connected, value); }
    private bool _paired;
    public bool Paired { get => _paired; set => Set(ref _paired, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ConduitNode _node;
    private readonly FeatureCoordinator _coordinator;

    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public ObservableCollection<MirroredNotification> Notifications { get; } = new();

    private DeviceRow? _selected;
    public DeviceRow? Selected
    {
        get => _selected;
        set { _selected = value; OnChanged(); OnChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => _selected is not null;

    private string _phoneStatus = "No device connected";
    public string PhoneStatus { get => _phoneStatus; set => Set(ref _phoneStatus, value); }

    private string _selfName = "";
    public string SelfName { get => _selfName; set => Set(ref _selfName, value); }

    public MainViewModel(ConduitNode node, FeatureCoordinator coordinator, NotificationService notifications)
    {
        _node = node;
        _coordinator = coordinator;
        SelfName = $"This PC: {node.Self.Name}";

        _node.DevicesChanged += (_, _) => Dispatch(RefreshDevices);
        _node.PeerConnected += (_, _) => Dispatch(RefreshDevices);
        _node.PeerDisconnected += (_, _) => Dispatch(RefreshDevices);
        _coordinator.StatusChanged += (_, _) => Dispatch(UpdateStatus);
        notifications.NotificationsChanged += (_, _) => Dispatch(() => RefreshNotifications(notifications));

        RefreshDevices();
    }

    private void RefreshDevices()
    {
        foreach (var dev in _node.KnownDevices)
        {
            var row = Devices.FirstOrDefault(d => d.DeviceId == dev.DeviceId);
            bool connected = _node.IsConnected(dev.DeviceId);
            string status = connected ? "Connected" : dev.IsPaired ? "Paired (offline)" : "Discovered";
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
    }

    private void UpdateStatus()
    {
        var s = _coordinator.Status;
        PhoneStatus =
            $"Battery: {s.BatteryLevel}%{(s.Charging ? " ⚡" : "")}   " +
            $"WiFi: {(string.IsNullOrEmpty(s.Ssid) ? "—" : s.Ssid)}   " +
            $"Ringer: {(string.IsNullOrEmpty(s.RingerMode) ? "—" : s.RingerMode)}   " +
            $"{(string.IsNullOrWhiteSpace(s.NowPlaying) ? "" : "♪ " + s.NowPlaying)}";
    }

    private void RefreshNotifications(NotificationService svc)
    {
        Notifications.Clear();
        foreach (var n in svc.Recent) Notifications.Add(n);
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
