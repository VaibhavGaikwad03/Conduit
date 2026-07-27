using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Conduit.App.Services;

/// <summary>
/// Writes decoded NV12 frames into the shared block the native source reads from
/// (see native SharedFrame.h / SharedFrameIO.h). The source — running as LocalSystem
/// in the Frame Server — creates the Global\ block; this side only opens it, so no
/// elevation is needed at runtime. Layout mirrors ConduitFrameHeader exactly.
/// </summary>
public sealed class SharedFrameWriter : IDisposable
{
    private const string BlockName = @"Global\ConduitCameraFrame";

    // Must match native SharedFrame.h.
    public const int MaxWidth = 1280;
    public const int MaxHeight = 720;
    public const int FrameBytes = MaxWidth * MaxHeight * 3 / 2; // NV12
    private const int HeaderSize = 48;

    // Header field byte offsets.
    private const int OffMagic = 0, OffActiveSlot = 24, OffSequence = 28,
                      OffWidth = 16, OffHeight = 20, OffTimestamp = 32, OffWriterAlive = 40;
    private const uint Magic = 0x4D434443; // 'CDCM'

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;

    public bool IsOpen => _view is not null;

    /// <summary>Tries to open the block the source created. Returns false until it exists.</summary>
    public bool TryOpen()
    {
        if (_view is not null) return true;
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(BlockName, MemoryMappedFileRights.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, HeaderSize + 2 * FrameBytes, MemoryMappedFileAccess.ReadWrite);
            _view.Write(OffWriterAlive, 1u);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false; // source hasn't created it yet (no camera consumer active)
        }
    }

    /// <summary>Publishes one full 1280x720 NV12 frame via the inactive slot, then flips.</summary>
    public void Write(byte[] nv12, ulong timestamp100ns)
    {
        if (_view is null || nv12.Length < FrameBytes) return;

        uint active = _view.ReadUInt32(OffActiveSlot);
        uint slot = 1 - active;
        long slotOffset = HeaderSize + (long)slot * FrameBytes;

        _view.WriteArray(slotOffset, nv12, 0, FrameBytes);
        _view.Write(OffWidth, (uint)MaxWidth);
        _view.Write(OffHeight, (uint)MaxHeight);
        _view.Write(OffTimestamp, timestamp100ns);
        Thread.MemoryBarrier();
        _view.Write(OffActiveSlot, slot);
        _view.Write(OffSequence, _view.ReadUInt32(OffSequence) + 1);
        _view.Write(OffWriterAlive, 1u);
    }

    public void Dispose()
    {
        try { _view?.Write(OffWriterAlive, 0u); } catch { /* block may be gone */ }
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
    }
}
