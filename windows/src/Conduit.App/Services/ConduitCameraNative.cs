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
            if (name == Dll && File.Exists(WebcamPaths.InstalledDll))
            {
                if (NativeLibrary.TryLoad(WebcamPaths.InstalledDll, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

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
