using System.Diagnostics;
using System.IO;
using Conduit.Core.Logging;
using Microsoft.Win32;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Drives the "phone as PC webcam" feature on the Windows side: installs and registers
/// the native virtual-camera DLL (one-time, elevated), creates/tears down the camera,
/// and pumps decoded NV12 frames into the shared block the camera reads from.
///
/// Frames come from <see cref="WriteFrame"/> (the H.264 decoder feeds these later); a
/// built-in test pattern (<see cref="StartTestPattern"/>) exercises the path without a
/// phone. The writer only opens once something actually consumes the camera, so the
/// pump retries until then.
/// </summary>
public sealed class WebcamService : IDisposable
{
    private readonly ILogger _log = ConduitLog.For("Webcam");
    private readonly SharedFrameWriter _writer = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _testCts;
    private VideoStreamReceiver? _receiver;
    private uint _frameCounter;
    public bool IsRunning { get; private set; }

    /// <summary>True once the DLL is registered under HKLM at the installed path.</summary>
    public bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Classes\CLSID\{WebcamPaths.SourceClsid}\InprocServer32");
        var path = key?.GetValue(null) as string;
        return path is not null &&
               string.Equals(path, WebcamPaths.InstalledDll, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path);
    }

    /// <summary>
    /// Copies the DLL to its service-readable home and registers it (HKLM) — one elevated
    /// step, so the user sees a single UAC prompt. Returns false if declined or missing.
    /// </summary>
    public bool EnsureInstalled()
    {
        if (!File.Exists(WebcamPaths.BundledDll))
        {
            // Nothing to deploy/update from — fall back to whatever is already registered.
            if (IsInstalled()) return true;
            _log.Error("Bundled ConduitCamera.dll not found at {Path}", WebcamPaths.BundledDll);
            return false;
        }
        // Re-deploy not just when unregistered, but also when the bundled DLL differs from the
        // installed one — otherwise a rebuilt native DLL (e.g. the low-latency decoder) would
        // never reach the ProgramData copy the Frame Server actually loads.
        if (IsInstalled() && FilesEqual(WebcamPaths.BundledDll, WebcamPaths.InstalledDll))
            return true;

        var dir = Path.GetDirectoryName(WebcamPaths.InstalledDll)!;
        // A single elevated shell: make the folder, copy the DLL, register it.
        var cmd = $"mkdir \"{dir}\" 2>nul & copy /Y \"{WebcamPaths.BundledDll}\" \"{WebcamPaths.InstalledDll}\" & " +
                  $"regsvr32 /s \"{WebcamPaths.InstalledDll}\"";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
            {
                UseShellExecute = true,
                Verb = "runas",           // triggers the UAC prompt
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p!.WaitForExit();
            var ok = IsInstalled();
            if (!ok) _log.Warning("Install/registration did not complete");
            return ok;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Elevated install was cancelled or failed");
            return false;
        }
    }

    /// <summary>True if both files exist and have identical contents (length + SHA-256).</summary>
    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            return sha.ComputeHash(sa).AsSpan().SequenceEqual(sha.ComputeHash(sb));
        }
        catch
        {
            return false; // if we can't compare, err toward re-deploying
        }
    }

    /// <summary>Registers (if needed) and starts the virtual camera. Returns false on failure.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (IsRunning) return true;
            if (!EnsureInstalled()) return false;

            int hr = ConduitCameraNative.ConduitVCamStart();
            if (hr < 0)
            {
                _log.Error("ConduitVCamStart failed: 0x{Hr:X8}", hr);
                return false;
            }

            // Bring up the H.264 decoder and start receiving the phone's video stream;
            // each decoded frame is published straight into the shared block by native code.
            hr = ConduitCameraNative.ConduitFeedStart();
            if (hr < 0)
            {
                _log.Error("ConduitFeedStart failed: 0x{Hr:X8}", hr);
                ConduitCameraNative.ConduitVCamStop();
                return false;
            }
            _frameCounter = 0;
            _receiver = new VideoStreamReceiver(OnEncodedFrame);
            _receiver.Start();

            IsRunning = true;
            _log.Information("Virtual camera started");
            return true;
        }
    }

    private void OnEncodedFrame(byte[] h264)
    {
        // Assign a monotonic 30 fps timestamp; the decoder publishes the NV12 result.
        ulong ts = _frameCounter++ * 333333UL;
        ConduitCameraNative.ConduitFeedFrame(h264, h264.Length, ts);
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopTestPattern();
            _receiver?.Stop();
            _receiver = null;
            _writer.Dispose();
            if (IsRunning)
            {
                ConduitCameraNative.ConduitFeedStop();
                ConduitCameraNative.ConduitVCamStop();
                IsRunning = false;
                _log.Information("Virtual camera stopped");
            }
        }
    }

    /// <summary>Feeds one decoded NV12 frame to the camera, opening the block on demand.</summary>
    public void WriteFrame(byte[] nv12, ulong timestamp100ns)
    {
        if (!_writer.IsOpen && !_writer.TryOpen()) return; // no consumer yet
        _writer.Write(nv12, timestamp100ns);
    }

    /// <summary>Pumps an animated test pattern so the camera can be verified without a phone.</summary>
    public void StartTestPattern()
    {
        StopTestPattern();
        var cts = new CancellationTokenSource();
        _testCts = cts;
        _ = Task.Run(async () =>
        {
            var frame = new byte[SharedFrameWriter.FrameBytes];
            const int w = SharedFrameWriter.MaxWidth, h = SharedFrameWriter.MaxHeight, ySize = w * h;
            for (uint f = 0; !cts.IsCancellationRequested; f++)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        frame[y * w + x] = (byte)((x + y + f * 4) & 0xFF);
                for (int i = ySize; i < frame.Length; i++) frame[i] = 128; // grayscale
                WriteFrame(frame, (ulong)f * 333333);
                try { await Task.Delay(33, cts.Token); } catch { break; }
            }
        }, cts.Token);
    }

    public void StopTestPattern()
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = null;
    }

    public void Dispose() => Stop();
}
