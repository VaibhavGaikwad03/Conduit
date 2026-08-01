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

            _tray = CreateTray();
            Notifications = new NotificationService(_tray);

            var clipboard = new ClipboardService();
            var media = new MediaService();
            var power = new PowerService();
            var files = new FileTransferService(_node, _store.Config.DownloadFolder);
            files.FileReceived += (_, path) =>
            {
                _tray!.BalloonTipTitle = "File received";
                _tray.BalloonTipText = path;
                _tray.ShowBalloonTip(4000);
            };
            var fileSearch = new FileSearchService();

            Coordinator = new FeatureCoordinator(_node, clipboard, media, power, files, fileSearch, Notifications);
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
        ConduitLog.Shutdown();
        base.OnExit(e);
    }
}
