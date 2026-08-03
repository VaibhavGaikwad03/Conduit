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

/// <summary>One entry (folder or file) in the remote file browser.</summary>
public sealed class BrowseRow
{
    public required string Token { get; init; }
    public required string Name { get; init; }
    public required bool IsDir { get; init; }
    public bool IsFile => !IsDir;
    public required string Detail { get; init; }   // "Folder" or a formatted size
    public string Icon => IsDir ? "📁" : "📄";
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ConduitNode _node;
    private readonly FeatureCoordinator _coordinator;

    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public ObservableCollection<MirroredNotification> Notifications { get; } = new();
    public ObservableCollection<TransferRow> Transfers { get; } = new();
    public ObservableCollection<SearchResultRow> SearchResults { get; } = new();
    public ObservableCollection<BrowseRow> BrowseEntries { get; } = new();
    public bool HasNotifications => Notifications.Count > 0;
    public bool HasTransfers => Transfers.Count > 0;
    public bool HasSearchResults => SearchResults.Count > 0;

    // ---- Remote file browser ----
    private bool _browseActive;
    /// <summary>True while the browser is open — drives the browse card's expanded state.</summary>
    public bool BrowseActive { get => _browseActive; private set => Set(ref _browseActive, value); }

    /// <summary>Id of the in-flight browse request, or null once closed — used to drop stale replies.</summary>
    public string? ActiveBrowseId { get; private set; }

    private string _browseStatus = "";
    public string BrowseStatus { get => _browseStatus; set => Set(ref _browseStatus, value); }

    private string _browsePath = "";
    /// <summary>Breadcrumb of the folder currently shown, e.g. "This PC / Documents / Work".</summary>
    public string BrowsePath { get => _browsePath; set => Set(ref _browsePath, value); }

    // The tokens+names of the folders we descended through; empty = at the roots.
    private readonly List<(string Token, string Name)> _browseStack = new();
    private string _browseRootName = "Files"; // the peer's name for its top level, from the roots listing
    public bool CanGoUp => _browseStack.Count > 0;

    private bool _searchActive;
    /// <summary>True from when a search starts until it is closed — drives the Close button's visibility.</summary>
    public bool SearchActive { get => _searchActive; private set => Set(ref _searchActive, value); }

    /// <summary>Id of the in-flight search, or null once it's closed — used to drop stale replies.</summary>
    public string? ActiveSearchId { get; private set; }

    private string _searchStatus = "";
    public string SearchStatus { get => _searchStatus; set => Set(ref _searchStatus, value); }

    private DeviceRow? _selected;
    public DeviceRow? Selected
    {
        get => _selected;
        set { _selected = value; OnChanged(); OnChanged(nameof(HasSelection)); OnChanged(nameof(SelectedConnected)); OnChanged(nameof(SelectedReady)); OnChanged(nameof(SelectedConnectedUnpaired)); OnChanged(nameof(SelectedOffline)); }
    }
    public bool HasSelection => _selected is not null;

    /// <summary>True when a device is selected and it's currently connected (regardless of pairing).</summary>
    public bool SelectedConnected => _selected?.Connected ?? false;

    /// <summary>Connected AND paired — the only state where the feature actions are shown.</summary>
    public bool SelectedReady => (_selected?.Connected ?? false) && (_selected?.Paired ?? false);

    /// <summary>Connected but not paired yet — show a "pair to continue" hint, not the actions.</summary>
    public bool SelectedConnectedUnpaired => (_selected?.Connected ?? false) && !(_selected?.Paired ?? false);

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
        _coordinator.DirListing += (_, d) => Dispatch(() => OnDirListing(d));
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
    public void BeginSearch(string requestId)
    {
        ActiveSearchId = requestId;
        SearchResults.Clear();
        OnChanged(nameof(HasSearchResults));
        SearchStatus = "Searching…";
        SearchActive = true;
    }

    /// <summary>Stops the search and clears the results/status; called when the user closes the search.</summary>
    public void ClearSearch()
    {
        ActiveSearchId = null;
        SearchActive = false;
        SearchResults.Clear();
        OnChanged(nameof(HasSearchResults));
        SearchStatus = "";
    }

    private void OnSearchResults(FileSearchResultsEventArgs r)
    {
        if (r.RequestId != ActiveSearchId) return; // stale/cancelled reply — ignore
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

    // ---- Remote file browser ---------------------------------------------------

    /// <summary>Opens the browser at the peer's roots; returns the request id to send with the (empty) list.</summary>
    public string StartBrowse()
    {
        _browseStack.Clear();
        ActiveBrowseId = Guid.NewGuid().ToString("N");
        BrowseActive = true;
        BrowseEntries.Clear();
        OnChanged(nameof(HasBrowseEntries));
        BrowseStatus = "Loading…";
        UpdateBrowsePath();
        OnChanged(nameof(CanGoUp));
        return ActiveBrowseId;
    }

    /// <summary>Descends into a folder; returns the request id to send with <paramref name="token"/>.</summary>
    public string EnterFolder(string token, string name)
    {
        _browseStack.Add((token, name));
        ActiveBrowseId = Guid.NewGuid().ToString("N");
        BrowseStatus = "Loading…";
        UpdateBrowsePath();
        OnChanged(nameof(CanGoUp));
        return ActiveBrowseId;
    }

    /// <summary>Goes up one level; returns the request id and the parent token to list ("" at roots).</summary>
    public (string RequestId, string Token) GoUp()
    {
        if (_browseStack.Count > 0) _browseStack.RemoveAt(_browseStack.Count - 1);
        ActiveBrowseId = Guid.NewGuid().ToString("N");
        BrowseStatus = "Loading…";
        UpdateBrowsePath();
        OnChanged(nameof(CanGoUp));
        return (ActiveBrowseId, _browseStack.Count > 0 ? _browseStack[^1].Token : "");
    }

    /// <summary>Closes the browser and clears its state.</summary>
    public void CloseBrowse()
    {
        ActiveBrowseId = null;
        BrowseActive = false;
        _browseStack.Clear();
        BrowseEntries.Clear();
        OnChanged(nameof(HasBrowseEntries));
        OnChanged(nameof(CanGoUp));
        BrowseStatus = "";
        BrowsePath = "";
    }

    private void OnDirListing(DirListingEventArgs d)
    {
        if (d.RequestId != ActiveBrowseId) return; // stale/superseded reply — ignore
        if (_browseStack.Count == 0 && !string.IsNullOrEmpty(d.Name))
        {
            _browseRootName = d.Name; // the peer's label for its top level
            UpdateBrowsePath();
        }
        BrowseEntries.Clear();
        if (d.Error is not null)
        {
            BrowseStatus = d.Error;
            OnChanged(nameof(HasBrowseEntries));
            return;
        }
        foreach (var en in d.Entries)
            BrowseEntries.Add(new BrowseRow
            {
                Token = en.Token,
                Name = en.Name,
                IsDir = en.IsDir,
                Detail = en.IsDir ? "Folder" : FormatSize(en.Size),
            });
        int folders = d.Entries.Count(e => e.IsDir);
        int files = d.Entries.Count - folders;
        BrowseStatus = d.Entries.Count == 0 ? "Empty folder" : $"{folders} folder{(folders == 1 ? "" : "s")}, {files} file{(files == 1 ? "" : "s")}";
        OnChanged(nameof(HasBrowseEntries));
    }

    private void UpdateBrowsePath() =>
        BrowsePath = _browseRootName + string.Concat(_browseStack.Select(s => " / " + s.Name));

    public bool HasBrowseEntries => BrowseEntries.Count > 0;

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

        // The selected device's connection/pairing state may have just changed.
        OnChanged(nameof(SelectedConnected));
        OnChanged(nameof(SelectedReady));
        OnChanged(nameof(SelectedConnectedUnpaired));
        OnChanged(nameof(SelectedOffline));
    }

    private void UpdateStatus()
    {
        var s = _coordinator.Status;
        BatteryLevel = s.BatteryLevel;
        BatteryText = s.BatteryLevel > 0 ? $"{s.BatteryLevel}%{(s.Charging ? "  ⚡ charging" : "")}" : "—";
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
