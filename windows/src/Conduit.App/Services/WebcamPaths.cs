using System.IO;

namespace Conduit.App.Services;

/// <summary>
/// Where the native virtual-camera DLL lives once installed. It must sit in a
/// service-readable location (not the user profile) because the Windows Camera
/// Frame Server — which loads it — runs as LocalService/LocalSystem.
/// </summary>
public static class WebcamPaths
{
    /// <summary>C:\ProgramData\Conduit\ConduitCamera.dll — readable by services.</summary>
    public static string InstalledDll { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Conduit", "ConduitCamera.dll");

    /// <summary>The DLL shipped next to the app, copied to <see cref="InstalledDll"/> on install.</summary>
    public static string BundledDll { get; } = Path.Combine(
        AppContext.BaseDirectory, "ConduitCamera.dll");

    /// <summary>CLSID of the media source (must match Guids.h on the native side).</summary>
    public const string SourceClsid = "{8E14F9A2-3B7C-4D5E-A6F0-1C2B3D4E5F60}";
}
