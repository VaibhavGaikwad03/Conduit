using System.Runtime.InteropServices;

namespace Conduit.App.Services;

/// <summary>
/// P/Invoke into the native primary-desktop capturer/encoder in ConduitCamera.dll. Captures the
/// PC's primary display with DXGI Desktop Duplication, encodes H.264, and delivers each Annex-B
/// access unit to a managed callback (the C# side streams it to the phone). The inverse of
/// <see cref="ConduitScreenNative"/>. Shares the DllImport resolver installed by
/// <see cref="ConduitCameraNative"/> (same DLL).
/// </summary>
internal static class ConduitDesktopNative
{
    private const string Dll = "ConduitCamera.dll";

    static ConduitDesktopNative() => ConduitCameraNative.EnsureLoaded();

    /// <summary>Called per encoded frame: one Annex-B H.264 access unit (start-code delimited).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DesktopFrameCallback(IntPtr data, int len);

    /// <summary>Starts capturing + encoding the primary display; frames go to <paramref name="cb"/>.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitDesktopStart(DesktopFrameCallback cb);

    /// <summary>Stops capture and tears down the encoder.</summary>
    [DllImport(Dll, PreserveSig = true)]
    public static extern int ConduitDesktopStop();
}
