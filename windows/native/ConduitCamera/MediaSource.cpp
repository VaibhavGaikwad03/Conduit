#include "MediaSource.h"
#include "MediaStream.h"
#include "VideoFormat.h"
#include "Module.h"
#include <mferror.h>
#include <ksmedia.h>
#include <new>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")

ConduitMediaSource::ConduitMediaSource() : _refCount(1) { ModuleAddRef(); }

ConduitMediaSource::~ConduitMediaSource()
{
    if (_stream) { _stream->Shutdown(); _stream->Release(); }
    if (_presentationDescriptor) _presentationDescriptor->Release();
    if (_streamDescriptor) _streamDescriptor->Release();
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
    if (FAILED(hr)) return hr;
    return CreateDescriptors();
}

// Builds the stream descriptor (one NV12 stream) and the presentation descriptor
// that Start() and CreatePresentationDescriptor() hand out.
HRESULT ConduitMediaSource::CreateDescriptors()
{
    IMFMediaType* type = nullptr;
    HRESULT hr = CreateConduitVideoType(&type);
    if (FAILED(hr)) return hr;

    IMFMediaType* types[1] = { type };
    hr = MFCreateStreamDescriptor(0, 1, types, &_streamDescriptor);
    if (SUCCEEDED(hr))
    {
        IMFMediaTypeHandler* handler = nullptr;
        hr = _streamDescriptor->GetMediaTypeHandler(&handler);
        if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(type);
        if (handler) handler->Release();
    }
    // The Frame Server reads these device-stream attributes off the descriptor to
    // recognize it as a color video-capture pin; without them Start fails with
    // MF_E_ATTRIBUTENOTFOUND.
    if (SUCCEEDED(hr)) hr = _streamDescriptor->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE);
    if (SUCCEEDED(hr)) hr = _streamDescriptor->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
    if (SUCCEEDED(hr)) hr = _streamDescriptor->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);
    if (SUCCEEDED(hr))
    {
        IMFStreamDescriptor* sds[1] = { _streamDescriptor };
        hr = MFCreatePresentationDescriptor(1, sds, &_presentationDescriptor);
    }
    if (SUCCEEDED(hr)) hr = _presentationDescriptor->SelectStream(0);

    type->Release();
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
    if (riid == IID_IMFGetService)
    {
        *ppv = static_cast<IMFGetService*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IKsControl))
    {
        *ppv = static_cast<IKsControl*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == IID_IMFSampleAllocatorControl)
    {
        *ppv = static_cast<IMFSampleAllocatorControl*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == IID_IMFActivate)
    {
        *ppv = static_cast<IMFActivate*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == IID_IMFAttributes)
    {
        *ppv = static_cast<IMFAttributes*>(static_cast<IMFActivate*>(this));
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

STDMETHODIMP ConduitMediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** pd)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (!pd) return E_POINTER;
    return _presentationDescriptor->Clone(pd);
}

STDMETHODIMP ConduitMediaSource::Start(IMFPresentationDescriptor*, const GUID*, const PROPVARIANT* startPos)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;

    bool firstStart = (_stream == nullptr);
    if (firstStart)
    {
        _stream = ConduitMediaStream::Create(this, _streamDescriptor, &hr);
        if (!_stream) return FAILED(hr) ? hr : E_FAIL;
        hr = _eventQueue->QueueEventParamUnk(MENewStream, GUID_NULL, S_OK,
                                             static_cast<IMFMediaStream2*>(_stream));
    }
    else
    {
        hr = _eventQueue->QueueEventParamUnk(MEUpdatedStream, GUID_NULL, S_OK,
                                             static_cast<IMFMediaStream2*>(_stream));
    }
    if (FAILED(hr)) return hr;

    hr = _stream->Start();
    if (FAILED(hr)) return hr;

    PROPVARIANT empty; PropVariantInit(&empty);
    hr = _eventQueue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK,
                                         startPos ? startPos : &empty);
    PropVariantClear(&empty);
    return hr;
}

STDMETHODIMP ConduitMediaSource::Stop()
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (_stream) _stream->Stop();
    return _eventQueue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

STDMETHODIMP ConduitMediaSource::Pause() { return MF_E_INVALID_STATE_TRANSITION; }

STDMETHODIMP ConduitMediaSource::Shutdown()
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    _shutdown = true;
    if (_stream) _stream->Shutdown();
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

STDMETHODIMP ConduitMediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (!attributes) return E_POINTER;
    if (streamId != 0) return MF_E_INVALIDSTREAMNUMBER;
    // The stream descriptor doubles as the stream's attribute store.
    return _streamDescriptor->QueryInterface(IID_PPV_ARGS(attributes));
}

STDMETHODIMP ConduitMediaSource::SetD3DManager(IUnknown*)
{
    return E_NOTIMPL;
}

// ---- IMFGetService ---------------------------------------------------------

STDMETHODIMP ConduitMediaSource::GetService(REFGUID, REFIID riid, LPVOID* ppv)
{
    // We provide no auxiliary services (allocator, rate control, etc.); the Frame
    // Server falls back to its defaults. QI for IMFGetService itself still succeeds.
    if (ppv) *ppv = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

// ---- IKsControl (no camera controls exposed) -------------------------------

STDMETHODIMP ConduitMediaSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
{
    if (bytesReturned) *bytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP ConduitMediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
{
    if (bytesReturned) *bytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP ConduitMediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
{
    if (bytesReturned) *bytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

// ---- IMFSampleAllocatorControl ---------------------------------------------

STDMETHODIMP ConduitMediaSource::SetDefaultAllocator(DWORD, IUnknown*)
{
    // We produce our own samples; the Frame Server's allocator is unused.
    return S_OK;
}

STDMETHODIMP ConduitMediaSource::GetAllocatorUsage(DWORD, DWORD* inputStreamId, MFSampleAllocatorUsage* usage)
{
    if (!inputStreamId || !usage) return E_POINTER;
    *inputStreamId = 0;
    *usage = MFSampleAllocatorUsage_DoesNotAllocate;
    return S_OK;
}
