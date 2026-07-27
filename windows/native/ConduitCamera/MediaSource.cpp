#include "MediaSource.h"
#include "Module.h"
#include <mferror.h>
#include <new>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")

ConduitMediaSource::ConduitMediaSource() : _refCount(1) { ModuleAddRef(); }

ConduitMediaSource::~ConduitMediaSource()
{
    if (_eventQueue) { _eventQueue->Shutdown(); _eventQueue->Release(); }
    if (_attributes) _attributes->Release();
    MFShutdown();
    ModuleRelease();
}

ConduitMediaSource* ConduitMediaSource::Create(HRESULT* hr)
{
    auto* source = new (std::nothrow) ConduitMediaSource();
    if (!source) { if (hr) *hr = E_OUTOFMEMORY; return nullptr; }
    HRESULT h = source->Init();
    if (hr) *hr = h;
    if (FAILED(h)) { source->Release(); return nullptr; }
    return source;
}

HRESULT ConduitMediaSource::Init()
{
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;
    hr = MFCreateEventQueue(&_eventQueue);
    if (FAILED(hr)) return hr;
    hr = MFCreateAttributes(&_attributes, 1);
    return hr;
}

HRESULT ConduitMediaSource::CheckShutdown() const
{
    return _shutdown ? MF_E_SHUTDOWN : S_OK;
}

// ---- IUnknown --------------------------------------------------------------

STDMETHODIMP ConduitMediaSource::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown ||
        riid == IID_IMFMediaEventGenerator ||
        riid == IID_IMFMediaSource ||
        riid == IID_IMFMediaSourceEx)
    {
        *ppv = static_cast<IMFMediaSourceEx*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) ConduitMediaSource::AddRef() { return InterlockedIncrement(&_refCount); }

STDMETHODIMP_(ULONG) ConduitMediaSource::Release()
{
    LONG c = InterlockedDecrement(&_refCount);
    if (c == 0) delete this;
    return c;
}

// ---- IMFMediaEventGenerator (delegate to the event queue) ------------------

STDMETHODIMP ConduitMediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    IMFMediaEventQueue* queue = nullptr;
    {
        std::lock_guard<std::mutex> guard(_lock);
        HRESULT hr = CheckShutdown();
        if (FAILED(hr)) return hr;
        queue = _eventQueue;
        queue->AddRef();
    }
    HRESULT hr = queue->GetEvent(flags, event);
    queue->Release();
    return hr;
}

STDMETHODIMP ConduitMediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    return _eventQueue->BeginGetEvent(callback, state);
}

STDMETHODIMP ConduitMediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    return _eventQueue->EndGetEvent(result, event);
}

STDMETHODIMP ConduitMediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    return _eventQueue->QueueEventParamVar(type, extendedType, status, value);
}

// ---- IMFMediaSource (skeleton — filled in next) ----------------------------

STDMETHODIMP ConduitMediaSource::GetCharacteristics(DWORD* characteristics)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (!characteristics) return E_POINTER;
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

STDMETHODIMP ConduitMediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor**)
{
    return E_NOTIMPL;
}

STDMETHODIMP ConduitMediaSource::Start(IMFPresentationDescriptor*, const GUID*, const PROPVARIANT*)
{
    return E_NOTIMPL;
}

STDMETHODIMP ConduitMediaSource::Stop() { return E_NOTIMPL; }
STDMETHODIMP ConduitMediaSource::Pause() { return MF_E_INVALID_STATE_TRANSITION; }

STDMETHODIMP ConduitMediaSource::Shutdown()
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    _shutdown = true;
    if (_eventQueue) _eventQueue->Shutdown();
    return S_OK;
}

// ---- IMFMediaSourceEx ------------------------------------------------------

STDMETHODIMP ConduitMediaSource::GetSourceAttributes(IMFAttributes** attributes)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (!attributes) return E_POINTER;
    *attributes = _attributes;
    (*attributes)->AddRef();
    return S_OK;
}

STDMETHODIMP ConduitMediaSource::GetStreamAttributes(DWORD, IMFAttributes**)
{
    return E_NOTIMPL;
}

STDMETHODIMP ConduitMediaSource::SetD3DManager(IUnknown*)
{
    return E_NOTIMPL;
}
