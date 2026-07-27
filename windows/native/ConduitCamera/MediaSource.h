#pragma once
#include <windows.h>
#include <mfidl.h>
#include <mfapi.h>
#include <mutex>

// The Media Foundation media source Windows instantiates for "Conduit Camera".
// This first cut is a valid, instantiable COM object with a working event queue;
// presentation/streaming (CreatePresentationDescriptor, Start, sample delivery)
// is filled in on top of this skeleton.
class ConduitMediaSource : public IMFMediaSourceEx
{
public:
    static ConduitMediaSource* Create(HRESULT* hr);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* characteristics) override;
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** pd) override;
    STDMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPos) override;
    STDMETHODIMP Stop() override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
    STDMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override;
    STDMETHODIMP SetD3DManager(IUnknown* manager) override;

private:
    ConduitMediaSource();
    ~ConduitMediaSource();
    HRESULT Init();
    HRESULT CheckShutdown() const;

    LONG _refCount;
    mutable std::mutex _lock;
    bool _shutdown = false;
    IMFMediaEventQueue* _eventQueue = nullptr;
    IMFAttributes* _attributes = nullptr;
};
