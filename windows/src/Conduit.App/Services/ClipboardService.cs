using Conduit.Core.Logging;
using Serilog;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace Conduit.App.Services;

/// <summary>
/// Two-way clipboard sync. Reads/writes the Windows text clipboard and reports local
/// changes (so they can be pushed to the phone) while suppressing echo of values we
/// ourselves just set from a remote update.
///
/// The WM_CLIPBOARDUPDATE hook lives in MainWindow, which calls <see cref="OnClipboardChanged"/>.
/// </summary>
public sealed class ClipboardService
{
    private readonly ILogger _log = ConduitLog.For("Clipboard");
    private string? _lastValue;

    /// <summary>Raised when the local clipboard text changes (to be sent to peers).</summary>
    public event EventHandler<string>? LocalClipboardChanged;

    /// <summary>Apply text received from a peer to the local clipboard.</summary>
    public void SetFromRemote(string text)
    {
        try
        {
            _lastValue = text; // suppress echo
            Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(text));
            _log.Information("Clipboard updated from remote ({Len} chars)", text.Length);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to set clipboard from remote");
        }
    }

    /// <summary>Called by MainWindow when the OS signals a clipboard change.</summary>
    public void OnClipboardChanged()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text) || text == _lastValue) return;

            _lastValue = text;
            _log.Information("Local clipboard changed ({Len} chars)", text.Length);
            LocalClipboardChanged?.Invoke(this, text);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to read clipboard");
        }
    }
}
