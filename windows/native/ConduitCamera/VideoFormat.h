// The single fixed output format of the virtual camera: NV12, 1280x720, 30 fps.
// Keeping it fixed keeps the source simple; the phone stream is scaled to this.
#pragma once
#include <mfapi.h>
#include <mfidl.h>

constexpr UINT32 CONDUIT_CAM_WIDTH = 1280;
constexpr UINT32 CONDUIT_CAM_HEIGHT = 720;
constexpr UINT32 CONDUIT_CAM_FPS_NUM = 30;
constexpr UINT32 CONDUIT_CAM_FPS_DEN = 1;

// Builds the NV12 media type used for both the stream descriptor and negotiation.
inline HRESULT CreateConduitVideoType(IMFMediaType** out)
{
    IMFMediaType* type = nullptr;
    HRESULT hr = MFCreateMediaType(&type);
    if (FAILED(hr)) return hr;

    hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(type, MF_MT_FRAME_SIZE, CONDUIT_CAM_WIDTH, CONDUIT_CAM_HEIGHT);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type, MF_MT_FRAME_RATE, CONDUIT_CAM_FPS_NUM, CONDUIT_CAM_FPS_DEN);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_DEFAULT_STRIDE, CONDUIT_CAM_WIDTH);

    if (FAILED(hr)) { type->Release(); return hr; }
    *out = type;
    return S_OK;
}
