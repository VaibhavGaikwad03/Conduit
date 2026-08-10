using System.Runtime.InteropServices;

namespace Conduit.Helper;

/// <summary>
/// P/Invoke into the native primary-desktop capturer/encoder in ConduitCamera.dll (the same module
/// the app uses), loaded from next to this exe. The helper runs it on whatever desktop the agent
/// launched it onto, so the encoded frames reflect that desktop.
/// </summary>
internal static class NativeDesktop
{
    private const string Dll = "ConduitCamera.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FrameCb(IntPtr data, int len);

    private static FrameCb? _cb; // pinned by holding the reference while capture runs

    static NativeDesktop()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeDesktop).Assembly, (name, _, _) =>
        {
            if (name == Dll)
            {
                var path = Path.Combine(AppContext.BaseDirectory, Dll);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
            }
            return IntPtr.Zero;
        });
    }

    [DllImport(Dll, PreserveSig = true)] private static extern int ConduitDesktopStart(FrameCb cb);
    [DllImport(Dll, PreserveSig = true)] private static extern int ConduitDesktopStop();

    public static bool Start(FrameCb cb)
    {
        _cb = cb;
        return ConduitDesktopStart(cb) >= 0;
    }

    public static void Stop()
    {
        ConduitDesktopStop();
        _cb = null;
    }
}
