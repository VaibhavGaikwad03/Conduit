using System.Windows;
using System.Windows.Interop;
using Conduit.App.Services;
using Conduit.App.ViewModels;
using Conduit.Core.Logging;
using Conduit.Core.Models;
using Conduit.Core.Networking;
using Conduit.Core.Storage;
using MessageBox = System.Windows.MessageBox;

namespace Conduit.App;

public partial class MainWindow : Window
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly ConduitNode _node;
    private readonly FeatureCoordinator _coordinator;
    private readonly ClipboardService _clipboard;
    private readonly MainViewModel _vm;

    public MainWindow(ConduitNode node, AppStore store, FeatureCoordinator coordinator,
        ClipboardService clipboard, NotificationService notifications)
    {
        _node = node;
        _coordinator = coordinator;
        _clipboard = clipboard;

        InitializeComponent();
        _vm = new MainViewModel(node, coordinator, notifications);
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(hwnd);

        // Dark title bar to match the app (Windows 10 2004+ / Windows 11).
        int useDark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) _clipboard.OnClipboardChanged();
        return IntPtr.Zero;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of exiting.
        e.Cancel = true;
        Hide();
    }

    // ---- Target resolution ----------------------------------------------------

    private DeviceInfo? Find(string deviceId) =>
        _node.KnownDevices.FirstOrDefault(d => d.DeviceId == deviceId);

    /// <summary>
    /// The device the detail pane's actions apply to: the selected device (what the user is
    /// looking at), falling back to the first connected peer.
    /// </summary>
    private DeviceInfo? TargetDevice()
    {
        if (_vm.Selected is { } row && Find(row.DeviceId) is { } selected) return selected;

        var connected = _node.KnownDevices.FirstOrDefault(d => _node.IsConnected(d.DeviceId));
        if (connected is not null) return connected;

        MessageBox.Show("Select a device first.", "Conduit");
        return null;
    }

    // ---- Flyout drawer --------------------------------------------------------

    private void OnToggleDrawer(object sender, RoutedEventArgs e) => _vm.DrawerOpen = !_vm.DrawerOpen;

    private void OnCloseDrawer(object sender, RoutedEventArgs e) => _vm.DrawerOpen = false;

    private void OnCloseDrawer(object sender, System.Windows.Input.MouseButtonEventArgs e) => _vm.DrawerOpen = false;

    /// <summary>Picking a device from the drawer closes it, revealing that device's detail.</summary>
    private void OnDeviceSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_vm.Selected is not null) _vm.DrawerOpen = false;
    }

    // ---- Device row action (Pair or Connect) ----------------------------------

    private async void OnDeviceAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceRow row }) return;
        if (Find(row.DeviceId) is not { } device) return;

        try
        {
            if (device.IsPaired)
            {
                await _node.ConnectAsync(device);
            }
            else
            {
                string code = await _node.StartPairingAsync(device);
                MessageBox.Show(
                    $"Confirm this code on {device.Name}:\n\n        {code}",
                    "Pair device");
            }
        }
        catch (Exception ex)
        {
            ConduitLog.For("UI").Warning(ex, "Device action failed");
            MessageBox.Show($"Could not complete the action: {ex.Message}", "Conduit");
        }
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceRow row }) return;
        if (Find(row.DeviceId) is not { } device) return;
        await _node.DisconnectAsync(device.DeviceId);
    }

    // ---- Dashboard actions ----------------------------------------------------

    private async void OnSendClipboard(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } device) return;
        string text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
        if (string.IsNullOrEmpty(text)) { MessageBox.Show("Clipboard has no text.", "Conduit"); return; }
        await _coordinator.SendClipboardAsync(device.DeviceId, text);
    }

    private async void OnSendFile(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } device) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Send file to phone" };
        if (dlg.ShowDialog() == true)
            await _coordinator.SendFileAsync(device.DeviceId, dlg.FileName);
    }

    private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnSearchFiles(sender, e);
    }

    private void OnLinkKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnOpenLink(sender, e);
    }

    private async void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        string url = LinkBox.Text?.Trim() ?? "";
        if (url.Length == 0) { MessageBox.Show("Enter a link to open.", "Conduit"); return; }
        await _coordinator.SendOpenLinkAsync(d.DeviceId, url);
        LinkBox.Clear();
    }

    private async void OnSearchFiles(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        string query = SearchBox.Text?.Trim() ?? "";
        if (query.Length < 2)
        {
            MessageBox.Show("Type at least 2 characters to search.", "Conduit");
            return;
        }
        _vm.BeginSearch();
        await _coordinator.SendFileSearchAsync(d.DeviceId, query);
    }

    private async void OnDownloadResult(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SearchResultRow row }) return;
        if (TargetDevice() is not { } d) return;
        await _coordinator.SendFileRequestAsync(d.DeviceId, row.Id);
    }

    private async void OnLockPhone(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d) await _coordinator.SendRemoteCommandAsync(d.DeviceId, "lock");
    }

    private bool _ringing;
    private async void OnRingPhone(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        if (_ringing)
        {
            await _coordinator.SendRemoteCommandAsync(d.DeviceId, "ring-stop");
            _ringing = false;
            RingButton.Content = "🔔  Ring phone";
        }
        else
        {
            await _coordinator.SendRemoteCommandAsync(d.DeviceId, "ring");
            _ringing = true;
            RingButton.Content = "🔕  Stop ringing";
        }
    }

    private async void OnMediaPrev(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d) await _coordinator.SendMediaCommandAsync(d.DeviceId, "prev");
    }

    private async void OnMediaPlayPause(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d) await _coordinator.SendMediaCommandAsync(d.DeviceId, "pause");
    }

    private async void OnMediaNext(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d) await _coordinator.SendMediaCommandAsync(d.DeviceId, "next");
    }

    private async void OnToggleWebcam(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        var webcam = App.Instance.Webcam;

        if (!webcam.IsRunning)
        {
            // Starts (and, first time, installs+registers) the virtual camera — may prompt for UAC.
            if (!webcam.Start())
            {
                MessageBox.Show(
                    "Couldn't start the virtual camera. The one-time setup needs administrator approval.",
                    "Conduit");
                return;
            }
            await _coordinator.SendWebcamStartAsync(d.DeviceId, VideoStreamReceiver.Port);
            WebcamButton.Content = "🛑  Stop webcam";
        }
        else
        {
            await _coordinator.SendWebcamStopAsync(d.DeviceId);
            webcam.Stop();
            WebcamButton.Content = "🎥  Use phone as webcam";
        }
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
