// Native H.264 -> BGRA decode for the screen-mirror feature. Unlike the webcam decoder (fixed
// 1280x720 NV12 into the virtual camera), the phone screen has an arbitrary, portrait resolution
// and is shown in a WPF window, so this path decodes to BGRA and hands each frame to a managed
// callback with its real dimensions. The C# side blits it into a WriteableBitmap. Kept in C++ to
// avoid IMFTransform interop in C#, mirroring Decoder.cpp.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h> // CODECAPI_AVLowLatencyMode; ICodecAPI comes from strmif.h
#include <vector>
#include <cstdint>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "wmcodecdspuuid.lib")
#pragma comment(lib, "strmiids.lib")

// Delivered per decoded frame: tightly-packed BGRA (stride = width*4), top-down.
typedef void(*ConduitScreenFrameCb)(const uint8_t* bgra, int width, int height, int stride);

namespace
{
    IMFTransform* g_dec = nullptr;
    ConduitScreenFrameCb g_cb = nullptr;
    bool g_started = false;
    std::vector<uint8_t> g_nv12;  // contiguous NV12 scratch
    std::vector<uint8_t> g_bgra;  // BGRA output scratch
    UINT32 g_w = 0, g_h = 0;      // current decoded frame size
    LONG g_stride = 0;            // NV12 row stride (>= width)

    inline uint8_t Clip(int v) { return static_cast<uint8_t>(v < 0 ? 0 : (v > 255 ? 255 : v)); }

    void EnableLowLatency()
    {
        IMFAttributes* attrs = nullptr;
        if (SUCCEEDED(g_dec->GetAttributes(&attrs)) && attrs)
        {
            attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
            attrs->Release();
        }
        ICodecAPI* codec = nullptr;
        if (SUCCEEDED(g_dec->QueryInterface(IID_PPV_ARGS(&codec))) && codec)
        {
            VARIANT v; VariantInit(&v); v.vt = VT_BOOL; v.boolVal = VARIANT_TRUE;
            codec->SetValue(&CODECAPI_AVLowLatencyMode, &v);
            codec->Release();
        }
    }

    HRESULT SetInputType()
    {
        IMFMediaType* t = nullptr;
        HRESULT hr = MFCreateMediaType(&t);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        // A nominal size; the decoder adapts to the real resolution from the bitstream (SPS) and
        // raises MF_E_TRANSFORM_STREAM_CHANGE, after which we read the true size from the output.
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(t, MF_MT_FRAME_SIZE, 1280, 720);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t, MF_MT_FRAME_RATE, 30, 1);
        if (SUCCEEDED(hr)) hr = g_dec->SetInputType(0, t, 0);
        if (t) t->Release();
        return hr;
    }

    HRESULT SelectOutputType()
    {
        for (DWORD i = 0; ; ++i)
        {
            IMFMediaType* t = nullptr;
            HRESULT hr = g_dec->GetOutputAvailableType(0, i, &t);
            if (FAILED(hr)) return hr;
            GUID sub{};
            t->GetGUID(MF_MT_SUBTYPE, &sub);
            if (sub == MFVideoFormat_NV12)
            {
                hr = g_dec->SetOutputType(0, t, 0);
                t->Release();
                return hr;
            }
            t->Release();
        }
    }

    // Reads the current output frame size + stride into g_w/g_h/g_stride.
    void RefreshOutputGeometry()
    {
        IMFMediaType* t = nullptr;
        if (FAILED(g_dec->GetOutputCurrentType(0, &t)) || !t) return;
        MFGetAttributeSize(t, MF_MT_FRAME_SIZE, &g_w, &g_h);
        UINT32 stride = 0;
        if (SUCCEEDED(t->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride)) && stride > 0)
            g_stride = static_cast<LONG>(stride);
        else
            g_stride = static_cast<LONG>(g_w);
        t->Release();
    }

    // NV12 (BT.601) -> BGRA. src is contiguous: Y plane (stride*h), then interleaved UV (stride*h/2).
    void ConvertAndDeliver(const uint8_t* src)
    {
        if (g_w == 0 || g_h == 0 || !g_cb) return;
        const LONG stride = g_stride > 0 ? g_stride : static_cast<LONG>(g_w);
        const uint8_t* yPlane = src;
        const uint8_t* uvPlane = src + static_cast<size_t>(stride) * g_h;

        const size_t need = static_cast<size_t>(g_w) * g_h * 4;
        if (g_bgra.size() < need) g_bgra.resize(need);
        uint8_t* dst = g_bgra.data();

        for (UINT32 y = 0; y < g_h; ++y)
        {
            const uint8_t* yRow = yPlane + static_cast<size_t>(y) * stride;
            const uint8_t* uvRow = uvPlane + static_cast<size_t>(y / 2) * stride;
            uint8_t* out = dst + static_cast<size_t>(y) * g_w * 4;
            for (UINT32 x = 0; x < g_w; ++x)
            {
                const int c = static_cast<int>(yRow[x]) - 16;
                const int d = static_cast<int>(uvRow[(x & ~1u)]) - 128;      // U
                const int e = static_cast<int>(uvRow[(x & ~1u) + 1]) - 128;  // V
                out[0] = Clip((298 * c + 516 * d + 128) >> 8);           // B
                out[1] = Clip((298 * c - 100 * d - 208 * e + 128) >> 8); // G
                out[2] = Clip((298 * c + 409 * e + 128) >> 8);           // R
                out[3] = 255;
                out += 4;
            }
        }
        g_cb(dst, static_cast<int>(g_w), static_cast<int>(g_h), static_cast<int>(g_w) * 4);
    }

    void DrainOutput()
    {
        for (;;)
        {
            MFT_OUTPUT_STREAM_INFO info{};
            g_dec->GetOutputStreamInfo(0, &info);
            const bool mftAllocates = (info.dwFlags &
                (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;

            IMFSample* outSample = nullptr;
            if (!mftAllocates)
            {
                const DWORD cb = info.cbSize ? info.cbSize : (1280 * 720 * 3 / 2);
                IMFMediaBuffer* b = nullptr;
                if (FAILED(MFCreateMemoryBuffer(cb, &b))) return;
                MFCreateSample(&outSample);
                outSample->AddBuffer(b);
                b->Release();
            }

            MFT_OUTPUT_DATA_BUFFER out{};
            out.pSample = outSample;
            DWORD status = 0;
            HRESULT hr = g_dec->ProcessOutput(0, 1, &out, &status);

            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) { if (outSample) outSample->Release(); break; }
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (outSample) outSample->Release();
                if (FAILED(SelectOutputType())) break;
                RefreshOutputGeometry();
                continue;
            }
            if (FAILED(hr)) { if (outSample) outSample->Release(); break; }

            if (out.pSample)
            {
                if (g_w == 0) RefreshOutputGeometry();
                IMFMediaBuffer* buf = nullptr;
                if (SUCCEEDED(out.pSample->ConvertToContiguousBuffer(&buf)) && buf)
                {
                    BYTE* data = nullptr; DWORD len = 0;
                    if (SUCCEEDED(buf->Lock(&data, nullptr, &len)))
                    {
                        ConvertAndDeliver(data);
                        buf->Unlock();
                    }
                    buf->Release();
                }
                out.pSample->Release();
            }
            if (out.pEvents) out.pEvents->Release();
        }
    }
}

extern "C" HRESULT ConduitScreenFeedStart(ConduitScreenFrameCb cb)
{
    if (g_started) return S_OK;
    g_cb = cb;
    g_w = g_h = 0; g_stride = 0;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;

    hr = CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&g_dec));
    if (SUCCEEDED(hr)) EnableLowLatency();
    if (SUCCEEDED(hr)) hr = SetInputType();
    if (SUCCEEDED(hr)) hr = SelectOutputType();
    if (SUCCEEDED(hr)) hr = g_dec->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    if (SUCCEEDED(hr)) hr = g_dec->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

    if (FAILED(hr))
    {
        if (g_dec) { g_dec->Release(); g_dec = nullptr; }
        MFShutdown();
        return hr;
    }
    g_started = true;
    return S_OK;
}

// data = one Annex-B H.264 access unit; timestamp100ns = presentation time.
extern "C" HRESULT ConduitScreenFeedFrame(const BYTE* data, int len, UINT64 timestamp100ns)
{
    if (!g_started || !data || len <= 0) return E_FAIL;

    IMFMediaBuffer* buf = nullptr;
    HRESULT hr = MFCreateMemoryBuffer(len, &buf);
    if (FAILED(hr)) return hr;
    BYTE* dst = nullptr;
    if (SUCCEEDED(buf->Lock(&dst, nullptr, nullptr))) { memcpy(dst, data, len); buf->Unlock(); }
    buf->SetCurrentLength(len);

    IMFSample* sample = nullptr;
    hr = MFCreateSample(&sample);
    if (SUCCEEDED(hr)) hr = sample->AddBuffer(buf);
    if (SUCCEEDED(hr)) hr = sample->SetSampleTime(static_cast<LONGLONG>(timestamp100ns));
    if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(333333);
    buf->Release();
    if (FAILED(hr)) { if (sample) sample->Release(); return hr; }

    hr = g_dec->ProcessInput(0, sample, 0);
    sample->Release();
    if (SUCCEEDED(hr)) DrainOutput();
    return hr;
}

extern "C" HRESULT ConduitScreenFeedStop()
{
    if (g_dec)
    {
        g_dec->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        g_dec->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
        g_dec->Release();
        g_dec = nullptr;
    }
    g_cb = nullptr;
    if (g_started) { MFShutdown(); g_started = false; }
    return S_OK;
}
