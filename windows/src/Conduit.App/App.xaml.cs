using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Conduit.App.Services;
using Conduit.Core.Logging;
using Conduit.Core.Networking;
using Conduit.Core.Storage;
using Application = System.Windows.Application;

namespace Conduit.App;

public partial class App : Application
{
    private NotifyIcon? _tray;
    private ConduitNode? _node;
    private AppStore? _store;
    private MainWindow? _window;

    // Only one Conduit may run per user (it binds the LAN ports). A second launch signals the
    // running instance to surface its window, then exits. Names are per-user so separate logins
    // on the same PC each get their own instance.
    private static readonly string SingleInstanceKey =
        "Conduit-SingleInstance-" + Environment.UserName;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showRequested;

    public static App Instance => (App)Current;
    public ConduitNode Node => _node!;
    public AppStore Store => _store!;
    public FeatureCoordinator Coordinator { get; private set; } = null!;
    public NotificationService Notifications { get; private set; } = null!;
    public WebcamService Webcam { get; private set; } = null!;
    public ScreenMirrorService ScreenMirror { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Enforce a single running instance. If we're not the first, wake the existing one and quit.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceKey + "-mutex", out bool isFirst);
        _showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceKey + "-show");
        if (!isFirst)
        {
            _showRequested.Set();   // ask the running instance to bring itself to the front
            Shutdown();
            return;
        }
        // We are the primary instance: listen for later launches asking us to surface.
        new Thread(() =>
        {
            while (_showRequested.WaitOne())
                Dispatcher.Invoke(ShowWindow);
        }) { IsBackground = true, Name = "SingleInstanceListener" }.Start();

        ConduitLog.Initialize();
        var log = ConduitLog.For("App");

        DispatcherUnhandledException += (_, args) =>
        {
            log.Error(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error(args.ExceptionObject as Exception, "Unhandled domain exception");

        try
        {
            _store = new AppStore().Load();
            _node = new ConduitNode(_store);
            // Incoming pair request: pop the window and ask the user to confirm the 6-digit code
            // (matching the one shown on the phone) before trusting the peer. Runs synchronously
            // so args.Accepted is set before the node sends its pair-response.
            _node.PairingRequested += (_, args) => Dispatcher.Invoke(() =>
            {
                ShowWindow();
                var result = System.Windows.MessageBox.Show(
                    $"Pair with {args.Peer.Name}?\n\nOnly accept if this code matches the one shown on {args.Peer.Name}:\n\n        {args.Code}",
                    "Conduit — pairing request",
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                args.Accepted = result == System.Windows.MessageBoxResult.Yes;
            });

            _tray = CreateTray();
            Notifications = new NotificationService(_tray);

            var clipboard = new ClipboardService();
            var media = new MediaService();
            var power = new PowerService();
            // "Find my PC" from the phone: beep (in the service) and pop the window to the front.
            power.FindMyPcRequested += () => Dispatcher.Invoke(ShowWindow);
            void OnFileReceived(object? _, string path)
            {
                _tray!.BalloonTipTitle = "File received";
                _tray.BalloonTipText = path;
                _tray.ShowBalloonTip(4000);
            }
            var files = new FileTransferService(_node, _store.Config.DownloadFolder);
            files.FileReceived += OnFileReceived;
            var fileStream = new FileStreamService(_node, _store.Config.DownloadFolder);
            fileStream.FileReceived += OnFileReceived;
            fileStream.Start();
            var fileSearch = new FileSearchService();

            Coordinator = new FeatureCoordinator(_node, clipboard, media, power, files, fileStream, fileSearch, Notifications);
            Webcam = new WebcamService();
            ScreenMirror = new ScreenMirrorService();

            _window = new MainWindow(_node, _store, Coordinator, clipboard, Notifications);
            _window.Show();

            await _node.StartAsync();
            log.Information("Conduit started successfully");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Startup failed");
            System.Windows.MessageBox.Show($"Conduit failed to start:\n{ex.Message}", "Conduit",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private NotifyIcon CreateTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Conduit", null, (_, _) => ShowWindow());
        menu.Items.Add("Open logs folder", null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", ConduitLog.LogDirectory));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        var tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Conduit",
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ShowWindow();
        return tray;
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/conduit.ico");
            using var stream = Application.GetResourceStream(uri)!.Stream;
            return new System.Drawing.Icon(stream);
        }
        catch (Exception ex)
        {
            ConduitLog.For("App").Warning(ex, "Falling back to default tray icon");
            return SystemIcons.Application;
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void ExitApp()
    {
        Webcam?.Stop();
        ScreenMirror?.Stop();
        if (_node is not null) await _node.DisposeAsync();
        _tray?.Dispose();
        ConduitLog.Shutdown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRequested?.Dispose();
        _singleInstanceMutex?.Dispose();
        ConduitLog.Shutdown();
        base.OnExit(e);
    }
}
