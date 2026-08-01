using System.Runtime.InteropServices;

namespace Conduit.App.Services;

/// <summary>
/// P/Invoke into the native screen-mirror H.264 decoder in ConduitCamera.dll. Decodes the phone's
/// screen stream to BGRA and delivers each frame to a managed callback. Shares the DllImport
/// resolver installed by <see cref="ConduitCameraNative"/> (same DLL, ProgramData location).
/// </summary>
internal static class ConduitScreenNative
{
    private const string Dll = "ConduitCamera.dll";

    static ConduitScreenNative() => ConduitCameraNative.EnsureLoaded();

    /// <summary>Called per decoded frame: a tightly-packed BGRA buffer (stride = width*4), top-down.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ScreenFrameCallback(IntPtr bgra, int width, int height, int stride);

    /// <summary>Starts the decoder pipeline; frames are delivered to <paramref name="cb"/>.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitScreenFeedStart(ScreenFrameCallback cb);

    /// <summary>Decodes one Annex-B H.264 access unit.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitScreenFeedFrame(byte[] data, int len, ulong timestamp100ns);

    /// <summary>Tears down the decoder pipeline.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitScreenFeedStop();
}
