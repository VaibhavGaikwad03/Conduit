// C-exported control surface so the C# host can create/tear down the virtual camera
// with a plain P/Invoke instead of hand-marshalling the IMFVirtualCamera COM vtable.
// The camera is Session-lifetime: it lives as long as this process holds it, so the
// C# app calls ConduitVCamStart when the user enables the webcam and ConduitVCamStop
// (or exits) to remove it.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <combaseapi.h>

#include "Guids.h"  // CLSID_ConduitCameraSource is defined (storage) in dllmain.cpp.

static IMFVirtualCamera* g_vcam = nullptr;
static bool g_mfStarted = false;

extern "C" HRESULT ConduitVCamStart()
{
    if (g_vcam) return S_OK; // already running

    // Ensure COM is up on this thread; ignore "already initialized in another mode".
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;
    g_mfStarted = true;

    wchar_t clsid[64] = {};
    StringFromGUID2(CLSID_ConduitCameraSource, clsid, ARRAYSIZE(clsid));

    hr = MFCreateVirtualCamera(
        MFVirtualCameraType_SoftwareCameraSource,
        MFVirtualCameraLifetime_Session,
        MFVirtualCameraAccess_CurrentUser,
        CONDUIT_CAMERA_FRIENDLY_NAME,
        clsid, nullptr, 0, &g_vcam);
    if (SUCCEEDED(hr)) hr = g_vcam->Start(nullptr);

    if (FAILED(hr))
    {
        if (g_vcam) { g_vcam->Remove(); g_vcam->Release(); g_vcam = nullptr; }
        MFShutdown();
        g_mfStarted = false;
    }
    return hr;
}

extern "C" HRESULT ConduitVCamStop()
{
    if (g_vcam)
    {
        g_vcam->Stop();
        g_vcam->Remove();
        g_vcam->Release();
        g_vcam = nullptr;
    }
    if (g_mfStarted) { MFShutdown(); g_mfStarted = false; }
    return S_OK;
}
