// Manual test harness (not shipped). Creates the "Conduit Camera" virtual camera
// backed by our registered COM source and holds it open until you press Enter, so
// the test pattern can be verified in the Windows Camera app / any video app.
//
// Prereq: register the DLL first (no admin needed):
//   regsvr32 /s ConduitCamera.dll
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <combaseapi.h>
#include <ks.h>
#include <ksproxy.h>
#include <cstdio>

#include <initguid.h>  // Emits the CLSID storage for this exe (once).
#include "Guids.h"

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfsensorgroup.lib")
#pragma comment(lib, "ole32.lib")

int wmain()
{
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) { wprintf(L"MFStartup failed: 0x%08X\n", hr); return 1; }

    wchar_t clsid[64] = {};
    StringFromGUID2(CLSID_ConduitCameraSource, clsid, ARRAYSIZE(clsid));

    // Diagnostic: instantiate our source in-process and probe the interfaces the
    // Frame Server requires, so we can tell "bad source" from "stale cached DLL".
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IUnknown* unk = nullptr;
    HRESULT chr = CoCreateInstance(CLSID_ConduitCameraSource, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&unk));
    wprintf(L"CoCreateInstance(source) = 0x%08X\n", chr);
    if (SUCCEEDED(chr))
    {
        void* p = nullptr;
        wprintf(L"  IMFMediaSourceEx      = 0x%08X\n", unk->QueryInterface(IID_IMFMediaSourceEx, &p)); if (p) { ((IUnknown*)p)->Release(); p = nullptr; }
        wprintf(L"  IMFGetService         = 0x%08X\n", unk->QueryInterface(IID_IMFGetService, &p)); if (p) { ((IUnknown*)p)->Release(); p = nullptr; }
        wprintf(L"  IKsControl            = 0x%08X\n", unk->QueryInterface(__uuidof(IKsControl), &p)); if (p) { ((IUnknown*)p)->Release(); p = nullptr; }
        wprintf(L"  IMFSampleAllocCtrl    = 0x%08X\n", unk->QueryInterface(IID_IMFSampleAllocatorControl, &p)); if (p) { ((IUnknown*)p)->Release(); p = nullptr; }
        unk->Release();
    }

    IMFVirtualCamera* vcam = nullptr;
    hr = MFCreateVirtualCamera(
        MFVirtualCameraType_SoftwareCameraSource,
        MFVirtualCameraLifetime_Session,
        MFVirtualCameraAccess_CurrentUser,
        CONDUIT_CAMERA_FRIENDLY_NAME,
        clsid,
        nullptr, 0,
        &vcam);
    if (FAILED(hr)) { wprintf(L"MFCreateVirtualCamera failed: 0x%08X\n", hr); MFShutdown(); return 1; }

    hr = vcam->Start(nullptr);
    if (FAILED(hr)) { wprintf(L"Start failed: 0x%08X\n", hr); vcam->Remove(); vcam->Release(); MFShutdown(); return 1; }

    // Prove Windows now enumerates our camera among the video capture devices.
    IMFAttributes* attrs = nullptr;
    if (SUCCEEDED(MFCreateAttributes(&attrs, 1)))
    {
        attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                       MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
        IMFActivate** devices = nullptr;
        UINT32 count = 0;
        if (SUCCEEDED(MFEnumDeviceSources(attrs, &devices, &count)))
        {
            wprintf(L"Video capture devices Windows sees (%u):\n", count);
            for (UINT32 i = 0; i < count; ++i)
            {
                wchar_t* name = nullptr; UINT32 len = 0;
                if (SUCCEEDED(devices[i]->GetAllocatedString(
                        MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len)))
                {
                    wprintf(L"  - %s\n", name);
                    CoTaskMemFree(name);
                }
                devices[i]->Release();
            }
            CoTaskMemFree(devices);
        }
        attrs->Release();
    }

    wprintf(L"Conduit Camera is live. Open the Windows Camera app and pick it.\n");
    wprintf(L"Press Enter to remove the camera and exit...\n");
    getwchar();

    vcam->Stop();
    vcam->Remove();
    vcam->Release();
    MFShutdown();
    return 0;
}
