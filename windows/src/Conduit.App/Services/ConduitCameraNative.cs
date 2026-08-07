using System.IO;
using System.Runtime.InteropServices;

namespace Conduit.App.Services;

/// <summary>
/// P/Invoke into the native ConduitCamera.dll control API. The DLL is resolved from
/// its installed ProgramData location (see <see cref="WebcamPaths.InstalledDll"/>)
/// rather than the app folder, so the same copy the Frame Server loads is used.
/// </summary>
internal static class ConduitCameraNative
{
    private const string Dll = "ConduitCamera.dll";

    static ConduitCameraNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(ConduitCameraNative).Assembly, (name, _, _) =>
        {
            if (name != Dll) return IntPtr.Zero;

            // Prefer the copy shipped next to the app: it always matches this build, so features
            // that call the exported C functions in-process (decode, desktop capture) never hit a
            // stale export. The virtual camera's COM activation goes through the separately
            // regsvr32-registered ProgramData copy, so this choice doesn't affect it. Fall back to
            // the installed copy if the bundled one is somehow absent.
            foreach (var path in new[] { WebcamPaths.BundledDll, WebcamPaths.InstalledDll })
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>Touch to force the static ctor (which installs the DllImport resolver) to run,
    /// so callers that only use the screen-mirror entry points still resolve the DLL correctly.</summary>
    internal static void EnsureLoaded() { }

    /// <summary>Creates and starts the "Conduit Camera" virtual camera. Returns an HRESULT.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitVCamStart();

    /// <summary>Stops and removes the virtual camera. Returns an HRESULT.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitVCamStop();

    /// <summary>Initializes the H.264 decoder pipeline. Returns an HRESULT.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitFeedStart();

    /// <summary>Decodes one Annex-B H.264 access unit and publishes the NV12 frame.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitFeedFrame(byte[] data, int len, ulong timestamp100ns);

    /// <summary>Tears down the decoder pipeline. Returns an HRESULT.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitFeedStop();
}
