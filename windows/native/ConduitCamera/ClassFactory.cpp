#include "ClassFactory.h"
#include "MediaSource.h"
#include "Module.h"

ConduitClassFactory::ConduitClassFactory() : _refCount(1) { ModuleAddRef(); }
ConduitClassFactory::~ConduitClassFactory() { ModuleRelease(); }

STDMETHODIMP ConduitClassFactory::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IClassFactory)
    {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) ConduitClassFactory::AddRef() { return InterlockedIncrement(&_refCount); }

STDMETHODIMP_(ULONG) ConduitClassFactory::Release()
{
    LONG c = InterlockedDecrement(&_refCount);
    if (c == 0) delete this;
    return c;
}

STDMETHODIMP ConduitClassFactory::CreateInstance(IUnknown* outer, REFIID riid, void** ppv)
{
    if (outer) return CLASS_E_NOAGGREGATION;
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    HRESULT hr = S_OK;
    ConduitMediaSource* source = ConduitMediaSource::Create(&hr);
    if (!source) return FAILED(hr) ? hr : E_OUTOFMEMORY;

    hr = source->QueryInterface(riid, ppv);
    source->Release();
    return hr;
}

STDMETHODIMP ConduitClassFactory::LockServer(BOOL lock)
{
    if (lock) ModuleAddRef();
    else ModuleRelease();
    return S_OK;
}
