// IMFActivate + IMFAttributes surface for ConduitMediaSource. The Frame Server
// CoCreateInstances our CLSID, QIs IMFActivate, and calls ActivateObject to get
// the media source — so the source doubles as its own activation object. The
// IMFAttributes methods just delegate to the internal _attributes store.
#include "MediaSource.h"

// ---- IMFActivate -----------------------------------------------------------

STDMETHODIMP ConduitMediaSource::ActivateObject(REFIID riid, void** ppv)
{
    // The activated object is this media source itself.
    return QueryInterface(riid, ppv);
}

STDMETHODIMP ConduitMediaSource::ShutdownObject()
{
    return S_OK; // Real teardown happens via IMFMediaSource::Shutdown.
}

STDMETHODIMP ConduitMediaSource::DetachObject()
{
    return E_NOTIMPL;
}

// ---- IMFAttributes (delegate to _attributes) -------------------------------

STDMETHODIMP ConduitMediaSource::GetItem(REFGUID key, PROPVARIANT* value) { return _attributes->GetItem(key, value); }
STDMETHODIMP ConduitMediaSource::GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) { return _attributes->GetItemType(key, type); }
STDMETHODIMP ConduitMediaSource::CompareItem(REFGUID key, REFPROPVARIANT value, BOOL* result) { return _attributes->CompareItem(key, value, result); }
STDMETHODIMP ConduitMediaSource::Compare(IMFAttributes* other, MF_ATTRIBUTES_MATCH_TYPE type, BOOL* result) { return _attributes->Compare(other, type, result); }
STDMETHODIMP ConduitMediaSource::GetUINT32(REFGUID key, UINT32* value) { return _attributes->GetUINT32(key, value); }
STDMETHODIMP ConduitMediaSource::GetUINT64(REFGUID key, UINT64* value) { return _attributes->GetUINT64(key, value); }
STDMETHODIMP ConduitMediaSource::GetDouble(REFGUID key, double* value) { return _attributes->GetDouble(key, value); }
STDMETHODIMP ConduitMediaSource::GetGUID(REFGUID key, GUID* value) { return _attributes->GetGUID(key, value); }
STDMETHODIMP ConduitMediaSource::GetStringLength(REFGUID key, UINT32* length) { return _attributes->GetStringLength(key, length); }
STDMETHODIMP ConduitMediaSource::GetString(REFGUID key, LPWSTR value, UINT32 size, UINT32* length) { return _attributes->GetString(key, value, size, length); }
STDMETHODIMP ConduitMediaSource::GetAllocatedString(REFGUID key, LPWSTR* value, UINT32* length) { return _attributes->GetAllocatedString(key, value, length); }
STDMETHODIMP ConduitMediaSource::GetBlobSize(REFGUID key, UINT32* size) { return _attributes->GetBlobSize(key, size); }
STDMETHODIMP ConduitMediaSource::GetBlob(REFGUID key, UINT8* buf, UINT32 bufSize, UINT32* blobSize) { return _attributes->GetBlob(key, buf, bufSize, blobSize); }
STDMETHODIMP ConduitMediaSource::GetAllocatedBlob(REFGUID key, UINT8** buf, UINT32* size) { return _attributes->GetAllocatedBlob(key, buf, size); }
STDMETHODIMP ConduitMediaSource::GetUnknown(REFGUID key, REFIID riid, LPVOID* ppv) { return _attributes->GetUnknown(key, riid, ppv); }
STDMETHODIMP ConduitMediaSource::SetItem(REFGUID key, REFPROPVARIANT value) { return _attributes->SetItem(key, value); }
STDMETHODIMP ConduitMediaSource::DeleteItem(REFGUID key) { return _attributes->DeleteItem(key); }
STDMETHODIMP ConduitMediaSource::DeleteAllItems() { return _attributes->DeleteAllItems(); }
STDMETHODIMP ConduitMediaSource::SetUINT32(REFGUID key, UINT32 value) { return _attributes->SetUINT32(key, value); }
STDMETHODIMP ConduitMediaSource::SetUINT64(REFGUID key, UINT64 value) { return _attributes->SetUINT64(key, value); }
STDMETHODIMP ConduitMediaSource::SetDouble(REFGUID key, double value) { return _attributes->SetDouble(key, value); }
STDMETHODIMP ConduitMediaSource::SetGUID(REFGUID key, REFGUID value) { return _attributes->SetGUID(key, value); }
STDMETHODIMP ConduitMediaSource::SetString(REFGUID key, LPCWSTR value) { return _attributes->SetString(key, value); }
STDMETHODIMP ConduitMediaSource::SetBlob(REFGUID key, const UINT8* buf, UINT32 size) { return _attributes->SetBlob(key, buf, size); }
STDMETHODIMP ConduitMediaSource::SetUnknown(REFGUID key, IUnknown* unknown) { return _attributes->SetUnknown(key, unknown); }
STDMETHODIMP ConduitMediaSource::LockStore() { return _attributes->LockStore(); }
STDMETHODIMP ConduitMediaSource::UnlockStore() { return _attributes->UnlockStore(); }
STDMETHODIMP ConduitMediaSource::GetCount(UINT32* count) { return _attributes->GetCount(count); }
STDMETHODIMP ConduitMediaSource::GetItemByIndex(UINT32 index, GUID* key, PROPVARIANT* value) { return _attributes->GetItemByIndex(index, key, value); }
STDMETHODIMP ConduitMediaSource::CopyAllItems(IMFAttributes* dest) { return _attributes->CopyAllItems(dest); }
