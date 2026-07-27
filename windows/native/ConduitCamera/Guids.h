// Stable identifiers shared between the native source DLL and the C# host.
// The CLSID identifies our in-proc COM media source so the Windows Frame Server
// can instantiate it inside consuming apps. Keep these values fixed forever —
// changing them orphans any existing registration.
#pragma once

// This header only declares the CLSID (extern). Exactly one .cpp per binary must
// #include <initguid.h> BEFORE this header to emit the actual GUID storage —
// dllmain.cpp for the DLL, TestHost.cpp for the test exe.

// {8E14F9A2-3B7C-4D5E-A6F0-1C2B3D4E5F60}
DEFINE_GUID(CLSID_ConduitCameraSource,
    0x8e14f9a2, 0x3b7c, 0x4d5e, 0xa6, 0xf0, 0x1c, 0x2b, 0x3d, 0x4e, 0x5f, 0x60);

// Friendly name shown to users when they pick the camera in other apps.
#define CONDUIT_CAMERA_FRIENDLY_NAME L"Conduit Camera"

// Named shared-memory section the C# host writes decoded NV12 frames into and
// the native source reads from. "Global\\" so it crosses sessions: the source
// runs in the session-0 Frame Server while the host writes from the user session.
#define CONDUIT_CAMERA_SHARED_NAME L"Global\\ConduitCameraFrame"
