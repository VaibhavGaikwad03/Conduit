# ConduitCamera — native virtual-camera source

`ConduitCamera.dll` is a C++/COM Media Foundation media source that backs the
**"Conduit Camera"** Windows virtual camera. The Windows Camera Frame Server loads
it into consuming apps (Zoom, Teams, the Camera app, browsers), so it must be
native COM — not the C# app. It currently serves an animated test pattern; next it
reads live NV12 frames from shared memory written by the C# host.

## Build

```powershell
./build.ps1
```

Produces `ConduitCamera/build/Release/ConduitCamera.dll` and a manual
`ConduitCameraTestHost.exe` (creates the virtual camera and holds it open so you
can verify it in the Camera app).

## Deployment requirements (learned the hard way)

Getting a virtual camera to actually start took satisfying every one of these:

1. **Interfaces** — the source must implement `IMFMediaSourceEx`, `IMFGetService`,
   `IKsControl`, `IMFSampleAllocatorControl`, **and `IMFActivate`** (the Frame
   Server activates the CLSID through `IMFActivate::ActivateObject`).
2. **Stream attributes** — the stream descriptor must carry
   `MF_DEVICESTREAM_STREAM_CATEGORY` (= `PINNAME_VIDEO_CAPTURE`),
   `MF_DEVICESTREAM_STREAM_ID`, and `MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES`
   (= `MFFrameSourceTypes_Color`), or `Start` fails with `MF_E_ATTRIBUTENOTFOUND`.
3. **HKLM registration** — the Frame Server runs as `LocalService`/`LocalSystem`
   and cannot see an HKCU registration. Register with an **elevated** `regsvr32`
   (one-time UAC). HKCU gives `ERROR_PATH_NOT_FOUND`.
4. **Service-readable DLL location** — service accounts can't read a user's
   Desktop/profile. The DLL must live somewhere readable by services (Program
   Files / ProgramData); a user-profile path gives `ERROR_ACCESS_DENIED`.
5. **No code signing needed** — verified an unsigned DLL loads on this Win11 build.

The C# host will place the DLL in a service-readable folder, register it (elevated,
once), then create the camera via `MFCreateVirtualCamera` pointing at the CLSID.

## Register / unregister manually

```powershell
# elevated
regsvr32 /s "C:\path\to\ConduitCamera.dll"
regsvr32 /s /u "C:\path\to\ConduitCamera.dll"
```
