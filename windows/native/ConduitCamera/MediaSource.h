#pragma once
#include <windows.h>
#include <mfidl.h>
#include <mfapi.h>
#include <ks.h>
#include <ksproxy.h>
#include <mutex>

class ConduitMediaStream;

// The Media Foundation media source Windows instantiates for "Conduit Camera".
// A virtual-camera source must expose IMFMediaSourceEx plus the camera-control
// surfaces the Frame Server probes at start time (IMFGetService, IKsControl).
class ConduitMediaSource : public IMFMediaSourceEx, public IMFGetService,
                           public IKsControl, public IMFSampleAllocatorControl,
                           public IMFActivate
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

    // IMFGetService
    STDMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* ppv) override;

    // IKsControl (camera control — we expose no controls, but the surface must exist)
    STDMETHODIMP KsProperty(PKSPROPERTY prop, ULONG propLen, LPVOID data, ULONG dataLen, ULONG* bytesReturned) override;
    STDMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLen, LPVOID data, ULONG dataLen, ULONG* bytesReturned) override;
    STDMETHODIMP KsEvent(PKSEVENT evt, ULONG evtLen, LPVOID data, ULONG dataLen, ULONG* bytesReturned) override;

    // IMFSampleAllocatorControl (we allocate our own samples, so no allocator needed)
    STDMETHODIMP SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) override;
    STDMETHODIMP GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) override;

    // IMFActivate — the Frame Server activates the source through this. Implemented
    // in SourceActivate.cpp; IMFAttributes methods delegate to _attributes.
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    STDMETHODIMP ShutdownObject() override;
    STDMETHODIMP DetachObject() override;

    // IMFAttributes (delegated to _attributes)
    STDMETHODIMP GetItem(REFGUID key, PROPVARIANT* value) override;
    STDMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) override;
    STDMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT value, BOOL* result) override;
    STDMETHODIMP Compare(IMFAttributes* other, MF_ATTRIBUTES_MATCH_TYPE type, BOOL* result) override;
    STDMETHODIMP GetUINT32(REFGUID key, UINT32* value) override;
    STDMETHODIMP GetUINT64(REFGUID key, UINT64* value) override;
    STDMETHODIMP GetDouble(REFGUID key, double* value) override;
    STDMETHODIMP GetGUID(REFGUID key, GUID* value) override;
    STDMETHODIMP GetStringLength(REFGUID key, UINT32* length) override;
    STDMETHODIMP GetString(REFGUID key, LPWSTR value, UINT32 size, UINT32* length) override;
    STDMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* value, UINT32* length) override;
    STDMETHODIMP GetBlobSize(REFGUID key, UINT32* size) override;
    STDMETHODIMP GetBlob(REFGUID key, UINT8* buf, UINT32 bufSize, UINT32* blobSize) override;
    STDMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** buf, UINT32* size) override;
    STDMETHODIMP GetUnknown(REFGUID key, REFIID riid, LPVOID* ppv) override;
    STDMETHODIMP SetItem(REFGUID key, REFPROPVARIANT value) override;
    STDMETHODIMP DeleteItem(REFGUID key) override;
    STDMETHODIMP DeleteAllItems() override;
    STDMETHODIMP SetUINT32(REFGUID key, UINT32 value) override;
    STDMETHODIMP SetUINT64(REFGUID key, UINT64 value) override;
    STDMETHODIMP SetDouble(REFGUID key, double value) override;
    STDMETHODIMP SetGUID(REFGUID key, REFGUID value) override;
    STDMETHODIMP SetString(REFGUID key, LPCWSTR value) override;
    STDMETHODIMP SetBlob(REFGUID key, const UINT8* buf, UINT32 size) override;
    STDMETHODIMP SetUnknown(REFGUID key, IUnknown* unknown) override;
    STDMETHODIMP LockStore() override;
    STDMETHODIMP UnlockStore() override;
    STDMETHODIMP GetCount(UINT32* count) override;
    STDMETHODIMP GetItemByIndex(UINT32 index, GUID* key, PROPVARIANT* value) override;
    STDMETHODIMP CopyAllItems(IMFAttributes* dest) override;

private:
    ConduitMediaSource();
    ~ConduitMediaSource();
    HRESULT Init();
    HRESULT CreateDescriptors();
    HRESULT CheckShutdown() const;

    LONG _refCount;
    mutable std::mutex _lock;
    bool _shutdown = false;
    IMFMediaEventQueue* _eventQueue = nullptr;
    IMFAttributes* _attributes = nullptr;
    IMFStreamDescriptor* _streamDescriptor = nullptr;
    IMFPresentationDescriptor* _presentationDescriptor = nullptr;
    ConduitMediaStream* _stream = nullptr;
};
