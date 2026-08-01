// Native H.264 -> NV12 decode, exposed as a C API for the C# host. The host receives
// the phone's Annex-B H.264 over TCP and hands each frame to ConduitFeedFrame; we run
// it through the Media Foundation H.264 decoder MFT and publish the NV12 result into
// the shared block the virtual camera reads. Keeping MF in C++ avoids painful IMFTransform
// interop in C#. Assumes the phone encodes at 1280x720 (the camera's fixed format).
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h> // CODECAPI_AVLowLatencyMode; ICodecAPI comes from strmif.h (already included)
#include <vector>

#include "Guids.h"
#include "VideoFormat.h"
#include "SharedFrameIO.h"

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "wmcodecdspuuid.lib")
#pragma comment(lib, "strmiids.lib")

namespace
{
    IMFTransform* g_decoder = nullptr;
    ConduitFrameWriter g_writer;
    std::vector<BYTE> g_repack; // tight NV12 scratch
    bool g_started = false;

    // The Microsoft H.264 decoder buffers several frames before emitting output (it fills a
    // reorder/lookahead pipeline), which shows up as visible webcam lag. Low-latency mode makes
    // it emit one output per input immediately. Set it both ways for robustness across builds.
    void EnableLowLatency()
    {
        IMFAttributes* attrs = nullptr;
        if (SUCCEEDED(g_decoder->GetAttributes(&attrs)) && attrs)
        {
            attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
            attrs->Release();
        }
        ICodecAPI* codec = nullptr;
        if (SUCCEEDED(g_decoder->QueryInterface(IID_PPV_ARGS(&codec))) && codec)
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
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(t, MF_MT_FRAME_SIZE, CONDUIT_CAM_WIDTH, CONDUIT_CAM_HEIGHT);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t, MF_MT_FRAME_RATE, CONDUIT_CAM_FPS_NUM, CONDUIT_CAM_FPS_DEN);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = g_decoder->SetInputType(0, t, 0);
        if (t) t->Release();
        return hr;
    }

    // Selects the decoder's NV12 output type (called initially and after a stream change).
    HRESULT SelectOutputType()
    {
        for (DWORD i = 0; ; ++i)
        {
            IMFMediaType* t = nullptr;
            HRESULT hr = g_decoder->GetOutputAvailableType(0, i, &t);
            if (FAILED(hr)) return hr;
            GUID sub{};
            t->GetGUID(MF_MT_SUBTYPE, &sub);
            if (sub == MFVideoFormat_NV12)
            {
                hr = g_decoder->SetOutputType(0, t, 0);
                t->Release();
                return hr;
            }
            t->Release();
        }
    }

    // Repacks a possibly-strided NV12 buffer into a tight 1280x720 frame and publishes it.
    void PublishNV12(IMFSample* sample)
    {
        IMFMediaBuffer* buf = nullptr;
        if (FAILED(sample->ConvertToContiguousBuffer(&buf))) return;

        const UINT32 need = ConduitNv12Size(CONDUIT_CAM_WIDTH, CONDUIT_CAM_HEIGHT);
        BYTE* data = nullptr; DWORD len = 0;
        if (SUCCEEDED(buf->Lock(&data, nullptr, &len)) && len >= need)
        {
            if (g_repack.size() < need) g_repack.resize(need);
            memcpy(g_repack.data(), data, need); // decoder emits contiguous NV12 at frame size
            buf->Unlock();

            if (!g_writer.IsOpen()) g_writer.Open(CONDUIT_CAMERA_SHARED_NAME);
            if (g_writer.IsOpen())
            {
                LONGLONG ts = 0; sample->GetSampleTime(&ts);
                g_writer.WriteFrame(g_repack.data(), static_cast<UINT64>(ts));
            }
        }
        else if (data) buf->Unlock();
        buf->Release();
    }

    void DrainOutput()
    {
        for (;;)
        {
            MFT_OUTPUT_STREAM_INFO info{};
            g_decoder->GetOutputStreamInfo(0, &info);
            const bool mftAllocates = (info.dwFlags &
                (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;

            IMFSample* outSample = nullptr;
            if (!mftAllocates)
            {
                IMFMediaBuffer* b = nullptr;
                if (FAILED(MFCreateMemoryBuffer(info.cbSize ? info.cbSize :
                        ConduitNv12Size(CONDUIT_CAM_WIDTH, CONDUIT_CAM_HEIGHT), &b))) return;
                MFCreateSample(&outSample);
                outSample->AddBuffer(b);
                b->Release();
            }

            MFT_OUTPUT_DATA_BUFFER out{};
            out.pSample = outSample;
            DWORD status = 0;
            HRESULT hr = g_decoder->ProcessOutput(0, 1, &out, &status);

            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) { if (outSample) outSample->Release(); break; }
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (outSample) outSample->Release();
                if (FAILED(SelectOutputType())) break;
                continue;
            }
            if (FAILED(hr)) { if (outSample) outSample->Release(); break; }

            if (out.pSample)
            {
                PublishNV12(out.pSample);
                out.pSample->Release();
            }
            if (out.pEvents) out.pEvents->Release();
        }
    }
}

extern "C" HRESULT ConduitFeedStart()
{
    if (g_started) return S_OK;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;

    hr = CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&g_decoder));
    if (SUCCEEDED(hr)) EnableLowLatency();
    if (SUCCEEDED(hr)) hr = SetInputType();
    if (SUCCEEDED(hr)) hr = SelectOutputType();
    if (SUCCEEDED(hr)) hr = g_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    if (SUCCEEDED(hr)) hr = g_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

    if (FAILED(hr))
    {
        if (g_decoder) { g_decoder->Release(); g_decoder = nullptr; }
        MFShutdown();
        return hr;
    }
    g_started = true;
    return S_OK;
}

// data = one Annex-B H.264 access unit; timestamp100ns = presentation time.
extern "C" HRESULT ConduitFeedFrame(const BYTE* data, int len, UINT64 timestamp100ns)
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

    hr = g_decoder->ProcessInput(0, sample, 0);
    sample->Release();
    if (SUCCEEDED(hr)) DrainOutput();
    return hr;
}

extern "C" HRESULT ConduitFeedStop()
{
    if (g_decoder)
    {
        g_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        g_decoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
        g_decoder->Release();
        g_decoder = nullptr;
    }
    g_writer.Close();
    if (g_started) { MFShutdown(); g_started = false; }
    return S_OK;
}
