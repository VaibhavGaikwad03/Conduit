// Layout of the named shared-memory block that carries one video frame from the
// C# host (writer) to the native media source (reader). Single-frame double-slot
// design: the writer fills the inactive slot, then flips `activeSlot`. Readers copy
// the active slot. A monotonically increasing `sequence` lets the reader detect new
// frames and skip duplicates. The C# side mirrors this struct exactly.
#pragma once

#include <cstdint>

#pragma pack(push, 1)

// Pixel format is always NV12 (what MF cameras and consumers expect). Width/height
// describe the current frame; both slots are sized for MaxWidth x MaxHeight.
struct ConduitFrameHeader
{
    uint32_t magic;        // 'CDCM' — sanity check that the block is initialized.
    uint32_t version;      // Layout version, currently 1.
    uint32_t maxWidth;     // Allocation dimensions (each slot's capacity).
    uint32_t maxHeight;
    uint32_t width;        // Current frame dimensions.
    uint32_t height;
    uint32_t activeSlot;   // 0 or 1 — the slot holding the latest complete frame.
    uint32_t sequence;     // Incremented each time a new frame is published.
    uint64_t timestamp100ns; // Frame time in 100-ns units (MF sample time base).
    uint32_t writerAlive;  // Host sets 1 while streaming, 0 when it stops.
    uint32_t reserved;
};

#pragma pack(pop)

// Two NV12 slots follow the header, each maxWidth*maxHeight*3/2 bytes.
constexpr uint32_t CONDUIT_FRAME_MAGIC = 0x4D434443; // 'CDCM' little-endian
constexpr uint32_t CONDUIT_FRAME_VERSION = 1;
constexpr uint32_t CONDUIT_MAX_WIDTH = 1280;
constexpr uint32_t CONDUIT_MAX_HEIGHT = 720;

inline uint32_t ConduitNv12Size(uint32_t w, uint32_t h) { return w * h * 3 / 2; }
inline uint32_t ConduitSharedSize()
{
    return sizeof(ConduitFrameHeader) + 2 * ConduitNv12Size(CONDUIT_MAX_WIDTH, CONDUIT_MAX_HEIGHT);
}
