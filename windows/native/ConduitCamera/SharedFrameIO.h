// Reader/writer over the named shared-memory frame block (see SharedFrame.h).
// The C# host (and the test feeder) write NV12 1280x720 frames; the media source
// reads the latest one. Header-only so the DLL, feeder, and self-test all share it.
#pragma once

#include <windows.h>
#include "SharedFrame.h"

// Writer: creates/opens the block and publishes frames via a two-slot flip.
class ConduitFrameWriter
{
public:
    ~ConduitFrameWriter() { Close(); }

    bool Create(const wchar_t* name, SECURITY_ATTRIBUTES* sa = nullptr)
    {
        const DWORD size = ConduitSharedSize();
        _map = CreateFileMappingW(INVALID_HANDLE_VALUE, sa, PAGE_READWRITE, 0, size, name);
        if (!_map) return false;
        _view = static_cast<BYTE*>(MapViewOfFile(_map, FILE_MAP_ALL_ACCESS, 0, 0, size));
        if (!_view) { CloseHandle(_map); _map = nullptr; return false; }
        _hdr = reinterpret_cast<ConduitFrameHeader*>(_view);
        if (_hdr->magic != CONDUIT_FRAME_MAGIC)
        {
            ZeroMemory(_view, sizeof(ConduitFrameHeader));
            _hdr->magic = CONDUIT_FRAME_MAGIC;
            _hdr->version = CONDUIT_FRAME_VERSION;
            _hdr->maxWidth = CONDUIT_MAX_WIDTH;
            _hdr->maxHeight = CONDUIT_MAX_HEIGHT;
        }
        _hdr->writerAlive = 1;
        return true;
    }

    // nv12 must hold a full CONDUIT_MAX_WIDTH x CONDUIT_MAX_HEIGHT NV12 frame.
    void WriteFrame(const BYTE* nv12, UINT64 timestamp100ns)
    {
        if (!_hdr) return;
        const UINT32 frameBytes = ConduitNv12Size(CONDUIT_MAX_WIDTH, CONDUIT_MAX_HEIGHT);
        const UINT32 slot = 1 - _hdr->activeSlot;
        memcpy(SlotPtr(slot), nv12, frameBytes);
        _hdr->width = CONDUIT_MAX_WIDTH;
        _hdr->height = CONDUIT_MAX_HEIGHT;
        _hdr->timestamp100ns = timestamp100ns;
        MemoryBarrier();
        _hdr->activeSlot = slot;
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&_hdr->sequence));
    }

    void Close()
    {
        if (_hdr) _hdr->writerAlive = 0;
        if (_view) { UnmapViewOfFile(_view); _view = nullptr; }
        if (_map) { CloseHandle(_map); _map = nullptr; }
        _hdr = nullptr;
    }

private:
    BYTE* SlotPtr(UINT32 slot)
    {
        return _view + sizeof(ConduitFrameHeader) +
               slot * ConduitNv12Size(CONDUIT_MAX_WIDTH, CONDUIT_MAX_HEIGHT);
    }

    HANDLE _map = nullptr;
    BYTE* _view = nullptr;
    ConduitFrameHeader* _hdr = nullptr;
};

// Reader: opens an existing block and copies out the latest published frame.
class ConduitFrameReader
{
public:
    ~ConduitFrameReader() { Close(); }

    bool Open(const wchar_t* name)
    {
        _map = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
        if (!_map) return false;
        _view = static_cast<BYTE*>(MapViewOfFile(_map, FILE_MAP_READ, 0, 0, ConduitSharedSize()));
        if (!_view) { CloseHandle(_map); _map = nullptr; return false; }
        _hdr = reinterpret_cast<const ConduitFrameHeader*>(_view);
        return true;
    }

    // Opens the block if it exists, otherwise creates it. The source calls this
    // (running as LocalSystem in the Frame Server, so it holds the privilege needed
    // to create a Global\ object), letting the non-elevated host merely open it.
    bool OpenOrCreate(const wchar_t* name, SECURITY_ATTRIBUTES* sa)
    {
        if (Open(name)) return true;
        const DWORD size = ConduitSharedSize();
        _map = CreateFileMappingW(INVALID_HANDLE_VALUE, sa, PAGE_READWRITE, 0, size, name);
        if (!_map) return false;
        _view = static_cast<BYTE*>(MapViewOfFile(_map, FILE_MAP_ALL_ACCESS, 0, 0, size));
        if (!_view) { CloseHandle(_map); _map = nullptr; return false; }
        auto* h = reinterpret_cast<ConduitFrameHeader*>(_view);
        if (h->magic != CONDUIT_FRAME_MAGIC)
        {
            ZeroMemory(_view, sizeof(ConduitFrameHeader));
            h->magic = CONDUIT_FRAME_MAGIC;
            h->version = CONDUIT_FRAME_VERSION;
            h->maxWidth = CONDUIT_MAX_WIDTH;
            h->maxHeight = CONDUIT_MAX_HEIGHT;
        }
        _hdr = h;
        return true;
    }

    bool IsOpen() const { return _view != nullptr; }

    // A live writer has published at least one frame.
    bool HasFrame() const
    {
        return _hdr && _hdr->magic == CONDUIT_FRAME_MAGIC && _hdr->writerAlive && _hdr->sequence > 0;
    }

    // Copies the active slot's full NV12 frame into dst (must be >= frame size).
    bool ReadLatest(BYTE* dst, UINT32 dstSize)
    {
        if (!HasFrame()) return false;
        const UINT32 frameBytes = ConduitNv12Size(CONDUIT_MAX_WIDTH, CONDUIT_MAX_HEIGHT);
        if (dstSize < frameBytes) return false;
        const UINT32 slot = _hdr->activeSlot;
        MemoryBarrier();
        memcpy(dst, _view + sizeof(ConduitFrameHeader) + slot * frameBytes, frameBytes);
        return true;
    }

    void Close()
    {
        if (_view) { UnmapViewOfFile(_view); _view = nullptr; }
        if (_map) { CloseHandle(_map); _map = nullptr; }
        _hdr = nullptr;
    }

private:
    HANDLE _map = nullptr;
    BYTE* _view = nullptr;
    const ConduitFrameHeader* _hdr = nullptr;
};
