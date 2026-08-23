using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Injects mouse and keyboard input on the PC from the phone's touchpad (the "pc-input"
/// packet). Movement is relative (dx/dy), so the phone acts like a laptop trackpad, and
/// text is typed as Unicode so any character comes through without keyboard-layout guessing.
///
/// Cursor moves are eased rather than applied as raw jumps: incoming deltas accumulate into a
/// pending target, and a ~200 Hz worker glides the cursor toward it a fraction at a time. That
/// decouples cursor motion from the (bursty, ~60 Hz) packet arrival, so it looks smooth instead
/// of steppy while total travel still matches exactly what the finger did.
/// </summary>
public sealed class InputService
{
    private readonly ILogger _log = ConduitLog.For("Input");

    // Pending relative distance still to travel (sub-pixel remainder kept in the fraction).
    private double _pendingX;
    private double _pendingY;
    private readonly object _moveLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _mover;

    // How much of the remaining distance to consume per tick, and the tick period.
    private const double EaseFactor = 0.35;
    private const int TickMs = 5;

    public void Move(int dx, int dy)
    {
        lock (_moveLock) { _pendingX += dx; _pendingY += dy; }
        EnsureMover();
        _wake.Set();
    }

    private void EnsureMover()
    {
        if (_mover is not null) return;
        lock (_moveLock)
        {
            if (_mover is not null) return;
            _mover = new Thread(MoveLoop) { IsBackground = true, Name = "conduit-cursor" };
            _mover.Start();
        }
    }

    // Eases the cursor toward the pending target; sleeps until there's something to do.
    private void MoveLoop()
    {
        while (true)
        {
            int sx, sy;
            lock (_moveLock)
            {
                sx = Step(ref _pendingX);
                sy = Step(ref _pendingY);
            }
            if (sx != 0 || sy != 0)
                SendMouse(sx, sy, 0, MOUSEEVENTF_MOVE);

            bool idle;
            lock (_moveLock) { idle = Math.Abs(_pendingX) < 1 && Math.Abs(_pendingY) < 1; }
            if (idle) _wake.WaitOne(200); else Thread.Sleep(TickMs);
        }
    }

    // Consume a fraction of the remaining distance, but at least 1px so motion never stalls.
    private static int Step(ref double pending)
    {
        var move = (int)(pending * EaseFactor);   // truncates toward zero
        if (move == 0 && Math.Abs(pending) >= 1) move = Math.Sign(pending);
        pending -= move;
        return move;
    }

    public void Click(string button)
    {
        MouseDown(button);
        MouseUp(button);
    }

    /// <summary>Jumps the cursor to an absolute point on the primary display. <paramref name="nx"/>/
    /// <paramref name="ny"/> are normalized 0..1 — the direct-touch path used while the phone views
    /// the PC desktop. Immediate (no easing), unlike the relative touchpad <see cref="Move"/>.</summary>
    public void MoveAbsolute(double nx, double ny)
    {
        nx = Math.Clamp(nx, 0.0, 1.0);
        ny = Math.Clamp(ny, 0.0, 1.0);
        // 0..65535 maps across the primary monitor (no VIRTUALDESK flag), which is exactly what we mirror.
        int ax = (int)Math.Round(nx * 65535.0);
        int ay = (int)Math.Round(ny * 65535.0);
        SendMouse(ax, ay, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);
    }

    public void MouseDown(string button) => SendMouse(0, 0, 0, button switch
    {
        "right"  => MOUSEEVENTF_RIGHTDOWN,
        "middle" => MOUSEEVENTF_MIDDLEDOWN,
        _        => MOUSEEVENTF_LEFTDOWN,
    });

    public void MouseUp(string button) => SendMouse(0, 0, 0, button switch
    {
        "right"  => MOUSEEVENTF_RIGHTUP,
        "middle" => MOUSEEVENTF_MIDDLEUP,
        _        => MOUSEEVENTF_LEFTUP,
    });

    /// <summary>Move to an absolute point and click there in one shot (a direct-touch tap).</summary>
    public void Tap(double nx, double ny, string button)
    {
        MoveAbsolute(nx, ny);
        MouseDown(button);
        MouseUp(button);
    }

    /// <summary>Positive amount scrolls up, negative down (one notch ≈ 120).</summary>
    public void Scroll(int amount) =>
        SendMouse(0, 0, amount, MOUSEEVENTF_WHEEL);

    public void Type(string text)
    {
        foreach (var ch in text)
        {
            SendUnicode(ch, false);
            SendUnicode(ch, true);
        }
    }

    public void Key(string name)
    {
        ushort vk = VkFor(name);
        if (vk == 0) { _log.Warning("Unknown key {Key}", name); return; }
        SendKey(vk, false);
        SendKey(vk, true);
    }

    /// <summary>
    /// Presses a chord like Ctrl+C or Ctrl+Alt+Del: holds each modifier in <paramref name="mods"/>
    /// (a '+'-joined list of ctrl/alt/shift/win), taps <paramref name="key"/>, then releases the
    /// modifiers in reverse. This is what the phone's on-screen PC keyboard sends when a modifier
    /// key is armed — plain typing still comes through as Unicode <see cref="Type"/>.
    /// </summary>
    public void KeyCombo(string mods, string key)
    {
        var held = (mods ?? "")
            .Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(ModVk).Where(v => v != 0).Distinct().ToList();

        foreach (var v in held) SendKey(v, false);
        ushort vk = VkFor(key);
        if (vk != 0) { SendKey(vk, false); SendKey(vk, true); }
        else _log.Warning("Combo with unknown key {Key}", key);
        // Release in reverse so nested modifiers unwind cleanly.
        for (int i = held.Count - 1; i >= 0; i--) SendKey(held[i], true);
    }

    private static ushort ModVk(string m) => m.Trim().ToLowerInvariant() switch
    {
        "ctrl" or "control" => 0x11,
        "alt"               => 0x12,
        "shift"             => 0x10,
        "win" or "meta"     => 0x5B,
        _                   => 0,
    };

    /// <summary>Maps a key name to a Win32 virtual-key code. Single characters map to their letter,
    /// digit, or (US-layout) OEM punctuation code so chords like Ctrl+- work; longer names cover the
    /// special keys (function row, arrows, editing block). Returns 0 for anything unmapped.</summary>
    private static ushort VkFor(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;

        if (name.Length == 1)
        {
            char c = char.ToLowerInvariant(name[0]);
            if (c >= 'a' && c <= 'z') return (ushort)(0x41 + (c - 'a'));
            if (c >= '0' && c <= '9') return (ushort)(0x30 + (c - '0'));
            return c switch
            {
                ' '  => 0x20,
                '-'  => 0xBD, '=' => 0xBB, '[' => 0xDB, ']' => 0xDD, '\\' => 0xDC,
                ';'  => 0xBA, '\'' => 0xDE, ',' => 0xBC, '.' => 0xBE, '/' => 0xBF, '`' => 0xC0,
                _    => 0,
            };
        }

        // Function keys F1..F12.
        if ((name[0] == 'f' || name[0] == 'F') &&
            int.TryParse(name.AsSpan(1), out var fn) && fn is >= 1 and <= 12)
            return (ushort)(0x70 + (fn - 1));

        return name.ToLowerInvariant() switch
        {
            "enter"       => 0x0D,
            "backspace"   => 0x08,
            "tab"         => 0x09,
            "escape" or "esc" => 0x1B,
            "space"       => 0x20,
            "up"          => 0x26,
            "down"        => 0x28,
            "left"        => 0x25,
            "right"       => 0x27,
            "home"        => 0x24,
            "end"         => 0x23,
            "pageup"      => 0x21,
            "pagedown"    => 0x22,
            "insert"      => 0x2D,
            "delete" or "del" => 0x2E,
            "capslock"    => 0x14,
            "printscreen" => 0x2C,
            _             => 0,
        };
    }

    // ---- Win32 SendInput plumbing ----

    private void SendMouse(int dx, int dy, int data, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = (uint)data, dwFlags = flags },
            },
        };
        Send(input);
    }

    private void SendUnicode(char ch, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                },
            },
        };
        Send(input);
    }

    private void SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0 },
            },
        };
        Send(input);
    }

    private void Send(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 0)
            _log.Warning("SendInput failed: {Err}", Marshal.GetLastWin32Error());
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData;
        public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }
}
