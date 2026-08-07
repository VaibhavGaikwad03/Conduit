// Native primary-desktop capture + H.264 encode for the "view/control the PC from the phone"
// feature. This is the mirror image of ScreenDecoder.cpp (which decodes the phone's screen): here
// we grab the PC's primary display with DXGI Desktop Duplication, composite the mouse cursor onto
// it (duplication delivers the pointer separately), convert BGRA -> NV12, run it through the
// Microsoft H.264 encoder MFT, and hand each Annex-B access unit to a managed callback. The C# side
// streams those bytes to the phone, which decodes them into a full-screen surface. Kept in C++ to
// avoid IMFTransform / D3D interop in C#, mirroring the decode path.
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <atomic>
#include <thread>
#include <vector>
#include <cstdint>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "wmcodecdspuuid.lib")
#pragma comment(lib, "strmiids.lib")

// Delivered per encoded frame: one Annex-B H.264 access unit (start-code delimited NAL units).
typedef void(*ConduitDesktopFrameCb)(const uint8_t* data, int len);

namespace
{
    constexpr int kFps = 30;
    constexpr UINT64 kFrameDur100ns = 10'000'000 / kFps; // 333333
    constexpr UINT32 kBitrate = 8'000'000;

    // ---- D3D / duplication ----
    ID3D11Device* g_device = nullptr;
    ID3D11DeviceContext* g_ctx = nullptr;
    IDXGIOutputDuplication* g_dup = nullptr;
    ID3D11Texture2D* g_staging = nullptr;   // CPU-readable copy target
    UINT g_w = 0, g_h = 0;                   // capture (and coded display) size, even

    // Last full desktop image (tightly packed BGRA, g_w*g_h*4). Reused on cursor-only updates.
    std::vector<uint8_t> g_desktop;
    std::vector<uint8_t> g_frame;   // desktop + cursor composite scratch
    std::vector<uint8_t> g_nv12;    // NV12 scratch fed to the encoder
    bool g_haveDesktop = false;

    // ---- Cursor state (Desktop Duplication delivers the pointer out-of-band) ----
    std::vector<uint8_t> g_curShape;
    DXGI_OUTDUPL_POINTER_SHAPE_INFO g_curInfo{};
    int g_curX = 0, g_curY = 0;
    bool g_curVisible = false;

    // ---- Encoder ----
    IMFTransform* g_enc = nullptr;
    std::vector<uint8_t> g_seqHeader;   // SPS/PPS (Annex-B) to prepend to each IDR
    std::vector<uint8_t> g_encOut;      // reusable delivery buffer
    UINT64 g_sampleTime = 0;

    ConduitDesktopFrameCb g_cb = nullptr;
    std::atomic<bool> g_run{false};
    std::thread g_thread;
    HANDLE g_initDone = nullptr;      // signalled once the thread finishes init
    std::atomic<HRESULT> g_initHr{S_OK};

    inline uint8_t Clip(int v) { return static_cast<uint8_t>(v < 0 ? 0 : (v > 255 ? 255 : v)); }

    void Teardown(); // defined below; CaptureLoop unwinds through it

    // ---------------------------------------------------------------- D3D setup

    HRESULT InitDuplication()
    {
        D3D_FEATURE_LEVEL fl;
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            &g_device, &fl, &g_ctx);
        if (FAILED(hr)) return hr;

        IDXGIDevice* dxgiDev = nullptr;
        hr = g_device->QueryInterface(IID_PPV_ARGS(&dxgiDev));
        if (FAILED(hr)) return hr;
        IDXGIAdapter* adapter = nullptr;
        hr = dxgiDev->GetAdapter(&adapter);
        dxgiDev->Release();
        if (FAILED(hr)) return hr;

        // Output 0 is the primary display.
        IDXGIOutput* output = nullptr;
        hr = adapter->EnumOutputs(0, &output);
        adapter->Release();
        if (FAILED(hr)) return hr;

        IDXGIOutput1* output1 = nullptr;
        hr = output->QueryInterface(IID_PPV_ARGS(&output1));
        output->Release();
        if (FAILED(hr)) return hr;

        hr = output1->DuplicateOutput(g_device, &g_dup);
        output1->Release();
        if (FAILED(hr)) return hr;

        DXGI_OUTDUPL_DESC desc{};
        g_dup->GetDesc(&desc);
        g_w = desc.ModeDesc.Width & ~1u;    // H.264 needs even dimensions
        g_h = desc.ModeDesc.Height & ~1u;
        if (g_w == 0 || g_h == 0) return E_FAIL;

        D3D11_TEXTURE2D_DESC td{};
        td.Width = g_w; td.Height = g_h; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_STAGING;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        return g_device->CreateTexture2D(&td, nullptr, &g_staging);
    }

    // ---------------------------------------------------------------- Encoder setup

    void ConfigureRateControl()
    {
        ICodecAPI* codec = nullptr;
        if (SUCCEEDED(g_enc->QueryInterface(IID_PPV_ARGS(&codec))) && codec)
        {
            VARIANT v; VariantInit(&v);
            v.vt = VT_UI4; v.ulVal = eAVEncCommonRateControlMode_CBR;
            codec->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v);
            v.vt = VT_UI4; v.ulVal = kBitrate;
            codec->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);
            v.vt = VT_UI4; v.ulVal = kFps * 2;   // keyframe every ~2s
            codec->SetValue(&CODECAPI_AVEncMPVGOPSize, &v);
            v.vt = VT_BOOL; v.boolVal = VARIANT_TRUE;
            codec->SetValue(&CODECAPI_AVLowLatencyMode, &v);
            codec->Release();
        }
        IMFAttributes* attrs = nullptr;
        if (SUCCEEDED(g_enc->GetAttributes(&attrs)) && attrs)
        {
            attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
            attrs->Release();
        }
    }

    HRESULT SetEncoderTypes()
    {
        // Encoders want the OUTPUT type set before the input.
        IMFMediaType* outT = nullptr;
        HRESULT hr = MFCreateMediaType(&outT);
        if (SUCCEEDED(hr)) hr = outT->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = outT->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        if (SUCCEEDED(hr)) hr = outT->SetUINT32(MF_MT_AVG_BITRATE, kBitrate);
        if (SUCCEEDED(hr)) hr = outT->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = outT->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(outT, MF_MT_FRAME_SIZE, g_w, g_h);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(outT, MF_MT_FRAME_RATE, kFps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(outT, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(hr)) hr = g_enc->SetOutputType(0, outT, 0);
        if (outT) outT->Release();
        if (FAILED(hr)) return hr;

        IMFMediaType* inT = nullptr;
        hr = MFCreateMediaType(&inT);
        if (SUCCEEDED(hr)) hr = inT->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = inT->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        if (SUCCEEDED(hr)) hr = inT->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(inT, MF_MT_FRAME_SIZE, g_w, g_h);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(inT, MF_MT_FRAME_RATE, kFps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(inT, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(hr)) hr = g_enc->SetInputType(0, inT, 0);
        if (inT) inT->Release();
        return hr;
    }

    // Cache the SPS/PPS the encoder advertises, so we can guarantee every IDR carries them.
    void CacheSequenceHeader()
    {
        IMFMediaType* t = nullptr;
        if (FAILED(g_enc->GetOutputCurrentType(0, &t)) || !t) return;
        UINT32 size = 0;
        if (SUCCEEDED(t->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &size)) && size > 0)
        {
            g_seqHeader.resize(size);
            t->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, g_seqHeader.data(), size, nullptr);
        }
        t->Release();
    }

    HRESULT InitEncoder()
    {
        HRESULT hr = CoCreateInstance(CLSID_CMSH264EncoderMFT, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&g_enc));
        if (FAILED(hr)) return hr;
        ConfigureRateControl();
        hr = SetEncoderTypes();
        if (SUCCEEDED(hr)) hr = g_enc->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        if (SUCCEEDED(hr)) hr = g_enc->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        if (SUCCEEDED(hr)) CacheSequenceHeader();
        return hr;
    }

    // ---------------------------------------------------------------- Cursor compositing

    // Blends one BGRA source pixel over the destination using straight alpha.
    inline void BlendPixel(uint8_t* d, const uint8_t* s)
    {
        const int a = s[3];
        d[0] = static_cast<uint8_t>((s[0] * a + d[0] * (255 - a)) / 255);
        d[1] = static_cast<uint8_t>((s[1] * a + d[1] * (255 - a)) / 255);
        d[2] = static_cast<uint8_t>((s[2] * a + d[2] * (255 - a)) / 255);
    }

    void CompositeCursor()
    {
        if (!g_curVisible || g_curShape.empty()) return;
        const int cw = static_cast<int>(g_curInfo.Width);
        const int pitch = static_cast<int>(g_curInfo.Pitch);
        // Monochrome shapes stack an AND mask over an XOR mask, so the real height is halved.
        const bool mono = g_curInfo.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME;
        const int ch = mono ? static_cast<int>(g_curInfo.Height) / 2 : static_cast<int>(g_curInfo.Height);
        uint8_t* frame = g_frame.data();

        for (int y = 0; y < ch; ++y)
        {
            const int fy = g_curY + y;
            if (fy < 0 || fy >= static_cast<int>(g_h)) continue;
            for (int x = 0; x < cw; ++x)
            {
                const int fx = g_curX + x;
                if (fx < 0 || fx >= static_cast<int>(g_w)) continue;
                uint8_t* d = frame + (static_cast<size_t>(fy) * g_w + fx) * 4;

                if (g_curInfo.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR)
                {
                    const uint8_t* s = g_curShape.data() + static_cast<size_t>(y) * pitch + x * 4;
                    BlendPixel(d, s);
                }
                else if (g_curInfo.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR)
                {
                    // A byte 0xFF => XOR with the screen; 0x00 => opaque copy.
                    const uint8_t* s = g_curShape.data() + static_cast<size_t>(y) * pitch + x * 4;
                    if (s[3] == 0xFF) { d[0] ^= s[0]; d[1] ^= s[1]; d[2] ^= s[2]; }
                    else { d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; }
                }
                else // MONOCHROME: 1bpp AND mask then 1bpp XOR mask
                {
                    const uint8_t* andRow = g_curShape.data() + static_cast<size_t>(y) * pitch;
                    const uint8_t* xorRow = g_curShape.data() + static_cast<size_t>(ch + y) * pitch;
                    const int bit = 7 - (x & 7);
                    const int andBit = (andRow[x >> 3] >> bit) & 1;
                    const int xorBit = (xorRow[x >> 3] >> bit) & 1;
                    if (andBit == 0 && xorBit == 0) { d[0] = d[1] = d[2] = 0; }        // black
                    else if (andBit == 0 && xorBit == 1) { d[0] = d[1] = d[2] = 255; } // white
                    else if (andBit == 1 && xorBit == 1) { d[0] = ~d[0]; d[1] = ~d[1]; d[2] = ~d[2]; } // invert
                    // andBit==1, xorBit==0 => transparent (leave dest)
                }
            }
        }
    }

    // ---------------------------------------------------------------- BGRA -> NV12

    void ToNv12()
    {
        const size_t ySize = static_cast<size_t>(g_w) * g_h;
        const size_t need = ySize + ySize / 2;
        if (g_nv12.size() < need) g_nv12.resize(need);
        uint8_t* Y = g_nv12.data();
        uint8_t* UV = Y + ySize;
        const uint8_t* src = g_frame.data();

        for (UINT y = 0; y < g_h; ++y)
        {
            const uint8_t* row = src + static_cast<size_t>(y) * g_w * 4;
            uint8_t* yout = Y + static_cast<size_t>(y) * g_w;
            for (UINT x = 0; x < g_w; ++x)
            {
                const int b = row[x * 4 + 0], g = row[x * 4 + 1], r = row[x * 4 + 2];
                yout[x] = Clip(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            }
        }
        // 2x2-averaged chroma, interleaved U,V.
        for (UINT y = 0; y < g_h; y += 2)
        {
            uint8_t* uvout = UV + static_cast<size_t>(y / 2) * g_w;
            for (UINT x = 0; x < g_w; x += 2)
            {
                int sb = 0, sg = 0, sr = 0;
                for (int dy = 0; dy < 2; ++dy)
                    for (int dx = 0; dx < 2; ++dx)
                    {
                        const uint8_t* p = src + (static_cast<size_t>(y + dy) * g_w + (x + dx)) * 4;
                        sb += p[0]; sg += p[1]; sr += p[2];
                    }
                sb >>= 2; sg >>= 2; sr >>= 2;
                uvout[x + 0] = Clip(((-38 * sr - 74 * sg + 112 * sb + 128) >> 8) + 128); // U
                uvout[x + 1] = Clip(((112 * sr - 94 * sg - 18 * sb + 128) >> 8) + 128);  // V
            }
        }
    }

    // ---------------------------------------------------------------- Encode + deliver

    void Deliver(IMFSample* sample)
    {
        IMFMediaBuffer* buf = nullptr;
        if (FAILED(sample->ConvertToContiguousBuffer(&buf)) || !buf) return;
        BYTE* data = nullptr; DWORD len = 0;
        if (SUCCEEDED(buf->Lock(&data, nullptr, &len)))
        {
            UINT32 clean = 0;
            sample->GetUINT32(MFSampleExtension_CleanPoint, &clean); // GUID key by reference
            // Guarantee SPS/PPS ahead of every IDR (unless the encoder already inlined them).
            const bool startsWithSps = len >= 5 && data[0] == 0 && data[1] == 0 &&
                data[2] == 0 && data[3] == 1 && (data[4] & 0x1F) == 7;
            g_encOut.clear();
            if (clean && !startsWithSps && !g_seqHeader.empty())
                g_encOut.insert(g_encOut.end(), g_seqHeader.begin(), g_seqHeader.end());
            g_encOut.insert(g_encOut.end(), data, data + len);
            if (g_cb) g_cb(g_encOut.data(), static_cast<int>(g_encOut.size()));
            buf->Unlock();
        }
        buf->Release();
    }

    void DrainEncoder()
    {
        for (;;)
        {
            MFT_OUTPUT_STREAM_INFO info{};
            g_enc->GetOutputStreamInfo(0, &info);
            const bool mftAllocates = (info.dwFlags &
                (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;

            IMFSample* outSample = nullptr;
            if (!mftAllocates)
            {
                const DWORD cb = info.cbSize ? info.cbSize : (g_w * g_h * 3 / 2);
                IMFMediaBuffer* b = nullptr;
                if (FAILED(MFCreateMemoryBuffer(cb, &b))) return;
                MFCreateSample(&outSample);
                outSample->AddBuffer(b);
                b->Release();
            }

            MFT_OUTPUT_DATA_BUFFER out{};
            out.pSample = outSample;
            DWORD status = 0;
            HRESULT hr = g_enc->ProcessOutput(0, 1, &out, &status);
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) { if (outSample) outSample->Release(); break; }
            if (FAILED(hr)) { if (outSample) outSample->Release(); break; }
            if (out.pSample) { Deliver(out.pSample); out.pSample->Release(); }
            if (out.pEvents) out.pEvents->Release();
        }
    }

    void EncodeFrame()
    {
        ToNv12();
        const DWORD len = static_cast<DWORD>(static_cast<size_t>(g_w) * g_h * 3 / 2);
        IMFMediaBuffer* buf = nullptr;
        if (FAILED(MFCreateMemoryBuffer(len, &buf))) return;
        BYTE* dst = nullptr;
        if (SUCCEEDED(buf->Lock(&dst, nullptr, nullptr))) { memcpy(dst, g_nv12.data(), len); buf->Unlock(); }
        buf->SetCurrentLength(len);

        IMFSample* sample = nullptr;
        if (SUCCEEDED(MFCreateSample(&sample)))
        {
            sample->AddBuffer(buf);
            sample->SetSampleTime(static_cast<LONGLONG>(g_sampleTime));
            sample->SetSampleDuration(kFrameDur100ns);
            g_sampleTime += kFrameDur100ns;
            if (SUCCEEDED(g_enc->ProcessInput(0, sample, 0))) DrainEncoder();
            sample->Release();
        }
        buf->Release();
    }

    // ---------------------------------------------------------------- Capture loop

    // Pull the just-acquired desktop texture into g_desktop (tightly packed BGRA).
    void ReadDesktop(ID3D11Texture2D* tex)
    {
        g_ctx->CopyResource(g_staging, tex);
        D3D11_MAPPED_SUBRESOURCE map{};
        if (FAILED(g_ctx->Map(g_staging, 0, D3D11_MAP_READ, 0, &map))) return;
        if (g_desktop.size() != static_cast<size_t>(g_w) * g_h * 4)
            g_desktop.resize(static_cast<size_t>(g_w) * g_h * 4);
        const uint8_t* src = static_cast<const uint8_t*>(map.pData);
        for (UINT y = 0; y < g_h; ++y)
            memcpy(g_desktop.data() + static_cast<size_t>(y) * g_w * 4,
                   src + static_cast<size_t>(y) * map.RowPitch, static_cast<size_t>(g_w) * 4);
        g_ctx->Unmap(g_staging, 0);
        g_haveDesktop = true;
    }

    void UpdateCursor(const DXGI_OUTDUPL_FRAME_INFO& fi)
    {
        if (fi.LastMouseUpdateTime.QuadPart != 0)
        {
            g_curVisible = fi.PointerPosition.Visible != FALSE;
            g_curX = fi.PointerPosition.Position.x;
            g_curY = fi.PointerPosition.Position.y;
        }
        if (fi.PointerShapeBufferSize != 0)
        {
            g_curShape.resize(fi.PointerShapeBufferSize);
            UINT required = 0;
            g_dup->GetFramePointerShape(fi.PointerShapeBufferSize, g_curShape.data(),
                &required, &g_curInfo);
        }
    }

    void CaptureLoop()
    {
        // Own the whole COM/D3D/MFT lifetime on this one thread so nothing is created in an
        // apartment that gets torn down under us. Init failures are reported back through
        // g_initHr, which ConduitDesktopStart waits on.
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        HRESULT hr = InitDuplication();
        if (SUCCEEDED(hr)) hr = InitEncoder();
        g_initHr.store(hr);
        if (FAILED(hr))
        {
            Teardown();
            CoUninitialize();
            g_run.store(false);
            SetEvent(g_initDone);
            return;
        }
        SetEvent(g_initDone);

        LARGE_INTEGER freq; QueryPerformanceFrequency(&freq);
        LARGE_INTEGER last{}; QueryPerformanceCounter(&last);

        while (g_run.load())
        {
            IDXGIResource* res = nullptr;
            DXGI_OUTDUPL_FRAME_INFO fi{};
            HRESULT hr = g_dup->AcquireNextFrame(15, &fi, &res);

            if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            {
                // Nothing changed; keep the stream alive at a slow heartbeat so a late-joining
                // decoder still gets frames, but don't busy-spin.
                Sleep(5);
                continue;
            }
            if (hr == DXGI_ERROR_ACCESS_LOST) break; // resolution/mode change; caller restarts
            if (FAILED(hr)) { Sleep(5); continue; }

            bool desktopChanged = fi.LastPresentTime.QuadPart != 0;
            bool cursorChanged = fi.LastMouseUpdateTime.QuadPart != 0;

            if (desktopChanged)
            {
                ID3D11Texture2D* tex = nullptr;
                if (SUCCEEDED(res->QueryInterface(IID_PPV_ARGS(&tex))))
                {
                    ReadDesktop(tex);
                    tex->Release();
                }
            }
            UpdateCursor(fi);
            g_dup->ReleaseFrame();
            res->Release();

            if (!g_haveDesktop) continue;
            if (!desktopChanged && !cursorChanged) continue;

            // Throttle to ~kFps regardless of how fast updates arrive.
            LARGE_INTEGER now; QueryPerformanceCounter(&now);
            const double elapsedMs = (now.QuadPart - last.QuadPart) * 1000.0 / freq.QuadPart;
            if (elapsedMs < 1000.0 / kFps) { Sleep(1); continue; }
            last = now;

            g_frame = g_desktop; // fresh copy to composite the cursor onto
            CompositeCursor();
            EncodeFrame();
        }

        // Release D3D/MFT on the same thread that created them, before leaving the apartment.
        Teardown();
        CoUninitialize();
    }

    void Teardown()
    {
        if (g_enc)
        {
            g_enc->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            g_enc->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
            g_enc->Release(); g_enc = nullptr;
        }
        if (g_dup) { g_dup->Release(); g_dup = nullptr; }
        if (g_staging) { g_staging->Release(); g_staging = nullptr; }
        if (g_ctx) { g_ctx->Release(); g_ctx = nullptr; }
        if (g_device) { g_device->Release(); g_device = nullptr; }
        g_desktop.clear(); g_frame.clear(); g_nv12.clear();
        g_curShape.clear(); g_seqHeader.clear();
        g_haveDesktop = false; g_curVisible = false; g_sampleTime = 0;
    }
}

// Starts capturing the primary display and delivering encoded frames to cb. One capturer at a time.
extern "C" HRESULT ConduitDesktopStart(ConduitDesktopFrameCb cb)
{
    if (g_run.load()) return S_OK;
    g_cb = cb;

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;

    // All D3D/duplication/MFT objects are created and destroyed on the capture thread so they
    // never outlive their apartment. Wait for the thread to finish init and report success/failure.
    g_initDone = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_initHr.store(S_OK);
    g_run.store(true);
    g_thread = std::thread(CaptureLoop);

    WaitForSingleObject(g_initDone, INFINITE);
    CloseHandle(g_initDone);
    g_initDone = nullptr;

    hr = g_initHr.load();
    if (FAILED(hr))
    {
        if (g_thread.joinable()) g_thread.join(); // it has already returned after a failed init
        MFShutdown();
        g_cb = nullptr;
        return hr;
    }
    return S_OK;
}

extern "C" HRESULT ConduitDesktopStop()
{
    if (!g_run.exchange(false)) return S_OK; // not running
    if (g_thread.joinable()) g_thread.join(); // thread tears down D3D/MFT on its way out
    MFShutdown();
    g_cb = nullptr;
    return S_OK;
}
