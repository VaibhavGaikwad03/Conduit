// Self-contained check of the shared-memory frame transport: write a known NV12
// frame, read it back through the reader, and confirm the bytes match. No special
// privileges (uses a session-local name), so it runs in CI / a plain shell.
#include <windows.h>
#include <vector>
#include <cstdio>
#include "SharedFrameIO.h"

int wmain()
{
    const wchar_t* name = L"Local\\ConduitCamSelfTest";
    const UINT32 size = ConduitNv12Size(CONDUIT_MAX_WIDTH, CONDUIT_MAX_HEIGHT);

    std::vector<BYTE> src(size), dst(size, 0);
    for (UINT32 i = 0; i < size; ++i) src[i] = static_cast<BYTE>((i * 7 + 3) & 0xFF);

    ConduitFrameWriter writer;
    if (!writer.Create(name)) { wprintf(L"FAIL: writer create (err %lu)\n", GetLastError()); return 1; }
    writer.WriteFrame(src.data(), 123456);

    ConduitFrameReader reader;
    if (!reader.Open(name)) { wprintf(L"FAIL: reader open (err %lu)\n", GetLastError()); return 1; }
    if (!reader.HasFrame()) { wprintf(L"FAIL: no frame published\n"); return 1; }
    if (!reader.ReadLatest(dst.data(), size)) { wprintf(L"FAIL: read\n"); return 1; }

    bool equal = memcmp(src.data(), dst.data(), size) == 0;
    wprintf(L"Shared-frame self-test: %s (%u bytes round-tripped)\n", equal ? L"PASS" : L"FAIL", size);
    return equal ? 0 : 2;
}
