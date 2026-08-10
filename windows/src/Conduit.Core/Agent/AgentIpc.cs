using System.Buffers.Binary;
using System.Text;

namespace Conduit.Core.Agent;

/// <summary>
/// The tiny wire contract shared by the user app, the LocalSystem agent service, and the
/// desktop-bound helper process. It is deliberately minimal and JSON-free: the SYSTEM side only ever
/// relays opaque H.264 frames outward and a fixed-layout <see cref="InputMsg"/> inward, so nothing
/// untrusted is parsed with elevated privilege. See the locked-PC scope in PROTOCOL.md / the plan.
///
/// This is the Stage-1 plumbing that proves an app ⇄ agent ⇄ helper path on the *unlocked* desktop;
/// the secure-desktop path and PIN/grant gating build on top of it later.
/// </summary>
public static class AgentIpc
{
    /// <summary>Named pipe the app connects to; the agent hosts it with a DACL limited to the user + SYSTEM.</summary>
    public const string AppPipeName = "conduit-agent";

    /// <summary>Pipe the helper connects back to (per launch). The agent passes the exact name on the command line.</summary>
    public const string HelperPipePrefix = "conduit-helper-";

    /// <summary>Windows service name registered for the agent.</summary>
    public const string ServiceName = "ConduitAgent";
}

/// <summary>Message kinds on the app⇄agent and agent⇄helper pipes. One byte on the wire.</summary>
public enum AgentMsg : byte
{
    Hello = 1,         // handshake (either direction)
    StartCapture = 2,  // app → agent: begin capturing the current input desktop
    StopCapture = 3,   // app → agent: stop
    Input = 4,         // app → agent → helper: one InputMsg
    Frame = 5,         // helper → agent → app: one Annex-B H.264 access unit
    Status = 6,        // agent → app: text status / desktop name
}

/// <summary>
/// Length-prefixed message framing over a stream: [type:1][len:4 big-endian][payload:len].
/// Synchronous and allocation-light; both sides are single-reader/single-writer per direction.
/// </summary>
public static class AgentFrame
{
    public static void Write(Stream s, AgentMsg type, ReadOnlySpan<byte> payload)
    {
        Span<byte> head = stackalloc byte[5];
        head[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(head[1..], payload.Length);
        lock (s) // serialize concurrent writers (e.g. frames + status)
        {
            s.Write(head);
            if (payload.Length > 0) s.Write(payload);
            s.Flush();
        }
    }

    public static void WriteText(Stream s, AgentMsg type, string text) =>
        Write(s, type, Encoding.UTF8.GetBytes(text));

    /// <summary>Reads one message. Returns false at end of stream.</summary>
    public static bool Read(Stream s, out AgentMsg type, out byte[] payload)
    {
        type = default; payload = [];
        var head = new byte[5];
        if (!ReadExact(s, head, 5)) return false;
        type = (AgentMsg)head[0];
        int len = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1));
        if (len < 0 || len > 64 * 1024 * 1024) return false; // sanity bound
        payload = len == 0 ? [] : new byte[len];
        return len == 0 || ReadExact(s, payload, len);
    }

    private static bool ReadExact(Stream s, byte[] buf, int len)
    {
        int off = 0;
        while (off < len)
        {
            int n = s.Read(buf, off, len - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}

/// <summary>
/// One control action, serialized as a fixed header plus an optional trailing string. Mirrors the
/// actions the app already understands (see FeatureCoordinator.HandlePcInput) so the helper can drive
/// the desktop with the same InputService semantics — but as plain bytes, never JSON.
/// </summary>
public sealed class InputMsg
{
    public InputAction Action { get; init; }
    public MouseButton Button { get; init; }
    public int Amount { get; init; }      // scroll notches (±120)
    public int Dx { get; init; }          // relative move
    public int Dy { get; init; }
    public double X { get; init; }        // absolute normalized 0..1
    public double Y { get; init; }
    public string Text { get; init; } = ""; // for Text/Key actions (key name or typed text)

    // Layout: action(1) button(1) amount(4) dx(4) dy(4) x(8) y(8) textLen(4) text(utf8)
    private const int HeaderLen = 1 + 1 + 4 + 4 + 4 + 8 + 8 + 4;

    public byte[] ToBytes()
    {
        var text = Encoding.UTF8.GetBytes(Text);
        var buf = new byte[HeaderLen + text.Length];
        var s = buf.AsSpan();
        s[0] = (byte)Action;
        s[1] = (byte)Button;
        BinaryPrimitives.WriteInt32LittleEndian(s[2..], Amount);
        BinaryPrimitives.WriteInt32LittleEndian(s[6..], Dx);
        BinaryPrimitives.WriteInt32LittleEndian(s[10..], Dy);
        BinaryPrimitives.WriteDoubleLittleEndian(s[14..], X);
        BinaryPrimitives.WriteDoubleLittleEndian(s[22..], Y);
        BinaryPrimitives.WriteInt32LittleEndian(s[30..], text.Length);
        text.CopyTo(s[HeaderLen..]);
        return buf;
    }

    public static InputMsg FromBytes(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderLen) return new InputMsg();
        int textLen = BinaryPrimitives.ReadInt32LittleEndian(b[30..]);
        var text = "";
        if (textLen > 0 && HeaderLen + textLen <= b.Length)
            text = Encoding.UTF8.GetString(b.Slice(HeaderLen, textLen));
        return new InputMsg
        {
            Action = (InputAction)b[0],
            Button = (MouseButton)b[1],
            Amount = BinaryPrimitives.ReadInt32LittleEndian(b[2..]),
            Dx = BinaryPrimitives.ReadInt32LittleEndian(b[6..]),
            Dy = BinaryPrimitives.ReadInt32LittleEndian(b[10..]),
            X = BinaryPrimitives.ReadDoubleLittleEndian(b[14..]),
            Y = BinaryPrimitives.ReadDoubleLittleEndian(b[22..]),
            Text = text,
        };
    }
}

public enum InputAction : byte
{
    MoveAbs = 0, Down = 1, Up = 2, Tap = 3, Move = 4, Click = 5, Scroll = 6, Key = 7, Text = 8,
}

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2 }
