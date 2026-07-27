#include "MediaStream.h"
#include "MediaSource.h"
#include "VideoFormat.h"
#include "Module.h"
#include "Guids.h"
#include <mferror.h>
#include <sddl.h>
#include <new>

#pragma comment(lib, "advapi32.lib")

// Opens the shared frame block, creating it (with a DACL that lets the non-elevated
// host open it for writing) if it doesn't exist yet.
static bool OpenSharedBlock(ConduitFrameReader& reader)
{
    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, FALSE };
    ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"D:(A;;GA;;;WD)", SDDL_REVISION_1, &sa.lpSecurityDescriptor, nullptr);
    bool ok = reader.OpenOrCreate(CONDUIT_CAMERA_SHARED_NAME, &sa);
    if (sa.lpSecurityDescriptor) LocalFree(sa.lpSecurityDescriptor);
    return ok;
}

ConduitMediaStream::ConduitMediaStream(ConduitMediaSource* source, IMFStreamDescriptor* sd)
    : _refCount(1), _source(source), _descriptor(sd)
{
    _source->AddRef();
    _descriptor->AddRef();
    ModuleAddRef();
}

ConduitMediaStream::~ConduitMediaStream()
{
    if (_eventQueue) { _eventQueue->Shutdown(); _eventQueue->Release(); }
    if (_descriptor) _descriptor->Release();
    if (_source) _source->Release();
    ModuleRelease();
}

ConduitMediaStream* ConduitMediaStream::Create(ConduitMediaSource* source, IMFStreamDescriptor* sd, HRESULT* hr)
{
    auto* s = new (std::nothrow) ConduitMediaStream(source, sd);
    if (!s) { if (hr) *hr = E_OUTOFMEMORY; return nullptr; }
    HRESULT h = s->Init();
    if (hr) *hr = h;
    if (FAILED(h)) { s->Release(); return nullptr; }
    return s;
}

HRESULT ConduitMediaStream::Init()
{
    return MFCreateEventQueue(&_eventQueue);
}

HRESULT ConduitMediaStream::CheckShutdown() const
{
    return _shutdown ? MF_E_SHUTDOWN : S_OK;
}

// ---- IUnknown --------------------------------------------------------------

STDMETHODIMP ConduitMediaStream::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown ||
        riid == IID_IMFMediaEventGenerator ||
        riid == IID_IMFMediaStream ||
        riid == IID_IMFMediaStream2)
    {
        *ppv = static_cast<IMFMediaStream2*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) ConduitMediaStream::AddRef() { return InterlockedIncrement(&_refCount); }

STDMETHODIMP_(ULONG) ConduitMediaStream::Release()
{
    LONG c = InterlockedDecrement(&_refCount);
    if (c == 0) delete this;
    return c;
}

// ---- IMFMediaEventGenerator ------------------------------------------------

STDMETHODIMP ConduitMediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
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

STDMETHODIMP ConduitMediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    return _eventQueue->BeginGetEvent(callback, state);
}

STDMETHODIMP ConduitMediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    return _eventQueue->EndGetEvent(result, event);
}

STDMETHODIMP ConduitMediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    return _eventQueue->QueueEventParamVar(type, extendedType, status, value);
}

// ---- IMFMediaStream --------------------------------------------------------

STDMETHODIMP ConduitMediaStream::GetMediaSource(IMFMediaSource** source)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (!source) return E_POINTER;
    return _source->QueryInterface(IID_PPV_ARGS(source));
}

STDMETHODIMP ConduitMediaStream::GetStreamDescriptor(IMFStreamDescriptor** sd)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (!sd) return E_POINTER;
    *sd = _descriptor;
    (*sd)->AddRef();
    return S_OK;
}

STDMETHODIMP ConduitMediaStream::RequestSample(IUnknown* token)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (_state != MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;

    IMFSample* sample = nullptr;
    hr = CreateSample(&sample);
    if (FAILED(hr)) return hr;

    if (token) sample->SetUnknown(MFSampleExtension_Token, token);
    hr = _eventQueue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample);
    sample->Release();
    return hr;
}

// ---- IMFMediaStream2 -------------------------------------------------------

STDMETHODIMP ConduitMediaStream::SetStreamState(MF_STREAM_STATE state)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    _state = state;
    return S_OK;
}

STDMETHODIMP ConduitMediaStream::GetStreamState(MF_STREAM_STATE* state)
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    if (!state) return E_POINTER;
    *state = _state;
    return S_OK;
}

// ---- Source-driven lifecycle ----------------------------------------------

HRESULT ConduitMediaStream::Start()
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    _state = MF_STREAM_STATE_RUNNING;
    return _eventQueue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
}

HRESULT ConduitMediaStream::Stop()
{
    std::lock_guard<std::mutex> guard(_lock);
    HRESULT hr = CheckShutdown();
    if (FAILED(hr)) return hr;
    _state = MF_STREAM_STATE_STOPPED;
    return _eventQueue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
}

HRESULT ConduitMediaStream::Shutdown()
{
    std::lock_guard<std::mutex> guard(_lock);
    _shutdown = true;
    if (_eventQueue) _eventQueue->Shutdown();
    return S_OK;
}

// ---- Sample generation -----------------------------------------------------

// Sweeping bright bar on a dim background — the "no live feed" fallback.
void ConduitMediaStream::FillTestPattern(BYTE* nv12)
{
    const UINT32 w = CONDUIT_CAM_WIDTH;
    const UINT32 h = CONDUIT_CAM_HEIGHT;
    const UINT32 ySize = w * h;
    const UINT32 barX = (_frameIndex * 12) % w;
    for (UINT32 y = 0; y < h; ++y)
    {
        BYTE* row = nv12 + y * w;
        for (UINT32 x = 0; x < w; ++x)
        {
            UINT32 dist = (x >= barX) ? (x - barX) : (barX - x);
            row[x] = (dist < 40) ? 235 : 40;
        }
    }
    memset(nv12 + ySize, 128, ySize / 2); // neutral chroma → grayscale
}

HRESULT ConduitMediaStream::CreateSample(IMFSample** outSample)
{
    const UINT32 w = CONDUIT_CAM_WIDTH;
    const UINT32 h = CONDUIT_CAM_HEIGHT;
    const DWORD total = w * h * 3 / 2;

    IMFMediaBuffer* buffer = nullptr;
    HRESULT hr = MFCreateMemoryBuffer(total, &buffer);
    if (FAILED(hr)) return hr;

    BYTE* data = nullptr;
    hr = buffer->Lock(&data, nullptr, nullptr);
    if (SUCCEEDED(hr))
    {
        // Open the host's frame block lazily; serve its latest frame if a writer is
        // live, otherwise fall back to the test pattern so the camera never stalls.
        if (!_readerOpened)
        {
            OpenSharedBlock(_reader);
            _readerOpened = true;
        }
        if (!_reader.IsOpen()) OpenSharedBlock(_reader);

        if (!_reader.ReadLatest(data, total))
            FillTestPattern(data);

        buffer->Unlock();
    }
    if (SUCCEEDED(hr)) hr = buffer->SetCurrentLength(total);

    IMFSample* sample = nullptr;
    if (SUCCEEDED(hr)) hr = MFCreateSample(&sample);
    if (SUCCEEDED(hr)) hr = sample->AddBuffer(buffer);
    if (SUCCEEDED(hr)) hr = sample->SetSampleTime(_nextTime);
    if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(_frameDuration);

    if (SUCCEEDED(hr))
    {
        _nextTime += _frameDuration;
        _frameIndex++;
        *outSample = sample;
        sample->AddRef();
    }

    if (sample) sample->Release();
    buffer->Release();
    return hr;
}
