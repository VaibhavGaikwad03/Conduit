using System.Runtime.InteropServices;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Controls whatever media app is currently playing on Windows by synthesizing the
/// hardware media keys (play/pause, next, prev, volume). This works with Spotify,
/// browsers, the Media Player, etc. without any per-app integration.
/// </summary>
public sealed class MediaService
{
    private readonly ILogger _log = ConduitLog.For("Media");

    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_MUTE = 0xAD;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public void Execute(string command, double? value = null)
    {
        _log.Information("Media command: {Command} {Value}", command, value);
        switch (command)
        {
            case "play":
            case "pause":
                Tap(VK_MEDIA_PLAY_PAUSE);
                break;
            case "next":
                Tap(VK_MEDIA_NEXT_TRACK);
                break;
            case "prev":
                Tap(VK_MEDIA_PREV_TRACK);
                break;
            case "volume":
                // value 0..1 → number of relative steps (each step ≈ 2%).
                AdjustVolume(value ?? 0.5);
                break;
            case "mute":
                Tap(VK_VOLUME_MUTE);
                break;
            default:
                _log.Warning("Unknown media command {Command}", command);
                break;
        }
    }

    private void AdjustVolume(double target)
    {
        // Simple relative nudge: below 0.5 lowers, above raises. Fine-grained level
        // control needs the Core Audio API; this keeps the phone remote responsive.
        int steps = (int)Math.Round(Math.Abs(target - 0.5) * 10);
        byte key = target >= 0.5 ? VK_VOLUME_UP : VK_VOLUME_DOWN;
        for (int i = 0; i < Math.Max(1, steps); i++) Tap(key);
    }

    private void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
