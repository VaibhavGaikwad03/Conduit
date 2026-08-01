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
        var (down, up) = button switch
        {
            "right"  => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _        => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        SendMouse(0, 0, 0, down);
        SendMouse(0, 0, 0, up);
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
        ushort vk = name switch
        {
            "enter"     => 0x0D,
            "backspace" => 0x08,
            "tab"       => 0x09,
            "escape"    => 0x1B,
            "up"        => 0x26,
            "down"      => 0x28,
            "left"      => 0x25,
            "right"     => 0x27,
            "home"      => 0x24,
            "end"       => 0x23,
            _           => 0,
        };
        if (vk == 0) { _log.Warning("Unknown key {Key}", name); return; }
        SendKey(vk, false);
        SendKey(vk, true);
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
