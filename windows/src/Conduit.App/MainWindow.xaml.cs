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

    /// <summary>The device dashboard actions apply to: the first connected peer, else the selection.</summary>
    private DeviceInfo? TargetDevice()
    {
        var connected = _node.KnownDevices.FirstOrDefault(d => _node.IsConnected(d.DeviceId));
        if (connected is not null) return connected;
        if (_vm.Selected is { } row) return Find(row.DeviceId);

        MessageBox.Show("Connect a device first.", "Conduit");
        return null;
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

    private async void OnLockPhone(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d) await _coordinator.SendRemoteCommandAsync(d.DeviceId, "lock");
    }

    private async void OnRingPhone(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d) await _coordinator.SendRemoteCommandAsync(d.DeviceId, "ring");
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
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
