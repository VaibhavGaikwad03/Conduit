using System.IO;
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
    private readonly AppStore _store;
    private readonly FeatureCoordinator _coordinator;
    private readonly ClipboardService _clipboard;
    private readonly NotificationService _notifications;
    private readonly MainViewModel _vm;

    public MainWindow(ConduitNode node, AppStore store, FeatureCoordinator coordinator,
        ClipboardService clipboard, NotificationService notifications)
    {
        _node = node;
        _store = store;
        _coordinator = coordinator;
        _clipboard = clipboard;
        _notifications = notifications;

        InitializeComponent();
        _vm = new MainViewModel(node, coordinator, notifications);
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            _clipboard.OnClipboardChanged();
        return IntPtr.Zero;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of exiting.
        e.Cancel = true;
        Hide();
    }

    // ---- Helpers --------------------------------------------------------------

    private DeviceInfo? SelectedDevice()
    {
        var row = _vm.Selected;
        if (row is null)
        {
            MessageBox.Show("Select a device first.", "Conduit");
            return null;
        }
        return _node.KnownDevices.FirstOrDefault(d => d.DeviceId == row.DeviceId);
    }

    // ---- Device actions -------------------------------------------------------

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is { } device)
            await _node.ConnectAsync(device);
    }

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is not { } device) return;
        try
        {
            string code = await _node.StartPairingAsync(device);
            MessageBox.Show($"Confirm this code on your phone:\n\n    {code}", "Pair with " + device.Name);
        }
        catch (Exception ex)
        {
            ConduitLog.For("UI").Warning(ex, "Pairing failed");
            MessageBox.Show($"Could not start pairing: {ex.Message}", "Conduit");
        }
    }

    // ---- Feature actions ------------------------------------------------------

    private async void OnSendClipboard(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is not { } device) return;
        string text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
        if (string.IsNullOrEmpty(text)) { MessageBox.Show("Clipboard has no text.", "Conduit"); return; }
        await _coordinator.SendClipboardAsync(device.DeviceId, text);
    }

    private async void OnSendFile(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is not { } device) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Send file to phone" };
        if (dlg.ShowDialog() == true)
            await _coordinator.SendFileAsync(device.DeviceId, dlg.FileName);
    }

    private async void OnLockPhone(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is { } d) await _coordinator.SendRemoteCommandAsync(d.DeviceId, "lock");
    }

    private async void OnRingPhone(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is { } d) await _coordinator.SendRemoteCommandAsync(d.DeviceId, "ring");
    }

    private async void OnMediaPrev(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is { } d) await _coordinator.SendMediaCommandAsync(d.DeviceId, "prev");
    }

    private async void OnMediaPlayPause(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is { } d) await _coordinator.SendMediaCommandAsync(d.DeviceId, "pause");
    }

    private async void OnMediaNext(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is { } d) await _coordinator.SendMediaCommandAsync(d.DeviceId, "next");
    }

    // ---- Logs -----------------------------------------------------------------

    private void OnRefreshLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var latest = new DirectoryInfo(ConduitLog.LogDirectory)
                .GetFiles("conduit-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null) { LogBox.Text = "(no log file yet)"; return; }

            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var lines = reader.ReadToEnd().Split('\n');
            LogBox.Text = string.Join('\n', lines.TakeLast(400));
            LogBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogBox.Text = $"Failed to read logs: {ex.Message}";
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start("explorer.exe", ConduitLog.LogDirectory);
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);
}
