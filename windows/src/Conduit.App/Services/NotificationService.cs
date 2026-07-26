using System.Windows.Forms;
using Conduit.Core.Logging;
using Conduit.Core.Protocol;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Surfaces mirrored Android notifications on Windows via the tray balloon, and tracks
/// them so the user can dismiss/reply (the reply is sent back with notification-action).
/// </summary>
public sealed class NotificationService
{
    private readonly ILogger _log = ConduitLog.For("Notifications");
    private readonly NotifyIcon _tray;

    /// <summary>The most recent mirrored notifications (key → summary), newest first.</summary>
    public List<MirroredNotification> Recent { get; } = new();

    public event EventHandler? NotificationsChanged;

    public NotificationService(NotifyIcon tray) => _tray = tray;

    public void Show(Packet packet)
    {
        var n = new MirroredNotification
        {
            Key = packet.GetString("key") ?? Guid.NewGuid().ToString("N"),
            AppName = packet.GetString("appName") ?? "Phone",
            Title = packet.GetString("title") ?? "",
            Text = packet.GetString("text") ?? "",
            CanReply = packet.GetBool("canReply")
        };

        _log.Information("Notification from {App}: {Title}", n.AppName, n.Title);
        Recent.Insert(0, n);
        if (Recent.Count > 50) Recent.RemoveRange(50, Recent.Count - 50);
        NotificationsChanged?.Invoke(this, EventArgs.Empty);

        _tray.BalloonTipTitle = $"{n.AppName}: {n.Title}";
        _tray.BalloonTipText = n.Text;
        _tray.ShowBalloonTip(5000);
    }

    public void Remove(string key)
    {
        Recent.RemoveAll(n => n.Key == key);
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class MirroredNotification
{
    public required string Key { get; init; }
    public required string AppName { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public bool CanReply { get; init; }
    public DateTimeOffset ReceivedAt { get; } = DateTimeOffset.Now;
}
