// COM in-proc server glue for ConduitCamera.dll: module lifetime, the class-object
// entry point Windows uses to instantiate our media source, and self-registration
// of the CLSID under HKCU (so no elevation is needed for a per-user install).
#include <windows.h>
#include <combaseapi.h>
#include <atomic>
#include <string>

#include <initguid.h>  // Emits the CLSID storage for the DLL (once).
#include "Guids.h"
#include "Module.h"
#include "ClassFactory.h"

static std::atomic<long> g_lockCount{0};
static HMODULE g_module = nullptr;

void ModuleAddRef() { g_lockCount.fetch_add(1); }
void ModuleRelease() { g_lockCount.fetch_sub(1); }
long ModuleLockCount() { return g_lockCount.load(); }

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != CLSID_ConduitCameraSource)
        return CLASS_E_CLASSNOTAVAILABLE;

    auto factory = new (std::nothrow) ConduitClassFactory();
    if (!factory)
        return E_OUTOFMEMORY;

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return ModuleLockCount() == 0 ? S_OK : S_FALSE;
}

// ---- Self-registration -----------------------------------------------------
// Registers under HKLM so the Windows Camera Frame Server (which runs as
// LocalService / LocalSystem) can find and load the source. Writing HKLM
// requires an elevated regsvr32; this is the feature's one-time admin step.

static std::wstring GuidToString(REFGUID guid)
{
    wchar_t buf[64] = {};
    StringFromGUID2(guid, buf, ARRAYSIZE(buf));
    return buf;
}

static LONG SetKeyValue(HKEY root, const std::wstring& subkey, const wchar_t* name, const std::wstring& value)
{
    HKEY key;
    LONG rc = RegCreateKeyExW(root, subkey.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (rc != ERROR_SUCCESS) return rc;
    rc = RegSetValueExW(key, name, 0, REG_SZ,
                        reinterpret_cast<const BYTE*>(value.c_str()),
                        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return rc;
}

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH] = {};
    if (GetModuleFileNameW(g_module, path, ARRAYSIZE(path)) == 0)
        return HRESULT_FROM_WIN32(GetLastError());

    const std::wstring clsid = GuidToString(CLSID_ConduitCameraSource);
    const std::wstring base = L"Software\\Classes\\CLSID\\" + clsid;

    LONG rc = SetKeyValue(HKEY_LOCAL_MACHINE, base, nullptr, CONDUIT_CAMERA_FRIENDLY_NAME);
    if (rc != ERROR_SUCCESS) return HRESULT_FROM_WIN32(rc);

    rc = SetKeyValue(HKEY_LOCAL_MACHINE, base + L"\\InprocServer32", nullptr, path);
    if (rc != ERROR_SUCCESS) return HRESULT_FROM_WIN32(rc);

    rc = SetKeyValue(HKEY_LOCAL_MACHINE, base + L"\\InprocServer32", L"ThreadingModel", L"Both");
    if (rc != ERROR_SUCCESS) return HRESULT_FROM_WIN32(rc);

    return S_OK;
}

STDAPI DllUnregisterServer()
{
    const std::wstring clsid = GuidToString(CLSID_ConduitCameraSource);
    const std::wstring base = L"Software\\Classes\\CLSID\\" + clsid;
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, base.c_str());
    return S_OK;
}
