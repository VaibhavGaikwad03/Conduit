#pragma once
#include <windows.h>
#include <unknwn.h>

// Standard COM class factory that hands out ConduitMediaSource instances.
class ConduitClassFactory : public IClassFactory
{
public:
    ConduitClassFactory();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IClassFactory
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override;
    STDMETHODIMP LockServer(BOOL lock) override;

private:
    ~ConduitClassFactory();
    LONG _refCount;
};
