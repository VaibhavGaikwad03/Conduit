#pragma once
#include <windows.h>
#include <mfidl.h>
#include <mfapi.h>
#include <mutex>

class ConduitMediaSource;

// The single video stream exposed by ConduitMediaSource. Delivers NV12 samples on
// demand (RequestSample). For milestone 1 it synthesizes an animated test pattern;
// later it copies live frames out of the shared-memory block.
class ConduitMediaStream : public IMFMediaStream2
{
public:
    static ConduitMediaStream* Create(ConduitMediaSource* source, IMFStreamDescriptor* sd, HRESULT* hr);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** sd) override;
    STDMETHODIMP RequestSample(IUnknown* token) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

    // Called by the source.
    HRESULT Start();
    HRESULT Stop();
    HRESULT Shutdown();

private:
    ConduitMediaStream(ConduitMediaSource* source, IMFStreamDescriptor* sd);
    ~ConduitMediaStream();
    HRESULT Init();
    HRESULT CheckShutdown() const;
    HRESULT CreateTestSample(IMFSample** sample);

    LONG _refCount;
    mutable std::mutex _lock;
    bool _shutdown = false;
    MF_STREAM_STATE _state = MF_STREAM_STATE_STOPPED;

    ConduitMediaSource* _source = nullptr;      // AddRef'd; released on Shutdown.
    IMFStreamDescriptor* _descriptor = nullptr;
    IMFMediaEventQueue* _eventQueue = nullptr;

    LONGLONG _nextTime = 0;   // 100-ns sample time, monotonically increasing.
    LONGLONG _frameDuration = 333333; // 30 fps.
    DWORD _frameIndex = 0;
};
