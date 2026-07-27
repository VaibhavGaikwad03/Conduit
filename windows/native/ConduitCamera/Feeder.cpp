// Manual test feeder (not shipped). Publishes an animated NV12 pattern into the
// shared frame block so the virtual camera shows a *live* feed distinct from its
// built-in test bar — proving the shared-memory path end to end before the phone
// stream exists. Writes to the Global block by default, so run it ELEVATED (only
// Administrators/services hold SeCreateGlobalPrivilege). Optional arg: block name.
#include <windows.h>
#include <sddl.h>
#include <vector>
#include <cstdio>
#include "SharedFrameIO.h"
#include "Guids.h"

#pragma comment(lib, "advapi32.lib")

int wmain(int argc, wchar_t** argv)
{
    const wchar_t* name = (argc > 1) ? argv[1] : CONDUIT_CAMERA_SHARED_NAME;

    // Permissive DACL so the session-0 Frame Server (LocalService) can open it.
    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, FALSE };
    ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"D:(A;;GA;;;WD)", SDDL_REVISION_1, &sa.lpSecurityDescriptor, nullptr);

    ConduitFrameWriter writer;
    if (!writer.Create(name, &sa))
    {
        wprintf(L"Create('%s') failed: err %lu (need elevation for Global\\?)\n", name, GetLastError());
        return 1;
    }
    wprintf(L"Feeding animated frames into '%s'. Ctrl+C to stop.\n", name);

    const UINT32 w = CONDUIT_MAX_WIDTH, h = CONDUIT_MAX_HEIGHT;
    const UINT32 ySize = w * h;
    std::vector<BYTE> frame(ConduitNv12Size(w, h));

    for (UINT32 f = 0; ; ++f)
    {
        // Y: moving diagonal gradient; UV: slow color wash — clearly not the test bar.
        for (UINT32 y = 0; y < h; ++y)
            for (UINT32 x = 0; x < w; ++x)
                frame[y * w + x] = static_cast<BYTE>((x + y + f * 4) & 0xFF);
        BYTE u = static_cast<BYTE>(128 + 80 * ((f / 30) & 1 ? 1 : -1));
        BYTE v = static_cast<BYTE>(128 - 40);
        for (UINT32 i = 0; i < ySize / 2; i += 2) { frame[ySize + i] = u; frame[ySize + i + 1] = v; }

        writer.WriteFrame(frame.data(), static_cast<UINT64>(f) * 333333);
        Sleep(33);
    }
}
