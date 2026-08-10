using System.Runtime.InteropServices;
using Conduit.Core.Agent;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.Helper;

/// <summary>
/// Injects mouse/keyboard input on the helper's current desktop via SendInput, driven by the
/// fixed-layout <see cref="InputMsg"/>. Mirrors the app's InputService semantics (absolute
/// direct-touch plus the relative touchpad actions) so control feels identical whichever path
/// (in-process app or this helper) is active. Because the helper is launched onto a specific
/// desktop, its SendInput lands on that desktop — which is the whole point on the lock screen.
/// </summary>
internal static class Win32Input
{
    private static readonly ILogger Log = ConduitLog.For("HelperInput");

    // A synthesized click needs the button-down and button-up to be a discrete pair. Firing them
    // back-to-back with zero gap is sometimes missed by apps (this showed up as taps "doing nothing"
    // on the higher-latency agent path), so a click gets a short, real dwell like a physical press.
    private const int ClickDwellMs = 30;

    public static void Apply(InputMsg m)
    {
        switch (m.Action)
        {
            case InputAction.MoveAbs: MoveAbsolute(m.X, m.Y); break;
            case InputAction.Down:    Log.Information("down {Button}", m.Button); MouseButtonEvent(m.Button, down: true); break;
            case InputAction.Up:      Log.Information("up {Button}", m.Button); MouseButtonEvent(m.Button, down: false); break;
            case InputAction.Tap:     Log.Information("tap {Button} @ {X:0.000},{Y:0.000}", m.Button, m.X, m.Y); Tap(m.X, m.Y, m.Button); break;
            case InputAction.Move:    SendMouse(m.Dx, m.Dy, 0, MOUSEEVENTF_MOVE); break;
            case InputAction.Click:   Log.Information("click {Button}", m.Button); Click(m.Button); break;
            case InputAction.Scroll:  SendMouse(0, 0, m.Amount, MOUSEEVENTF_WHEEL); break;
            case InputAction.Key:     Key(m.Text); break;
            case InputAction.Text:    Type(m.Text); break;
        }
    }

    private static void Tap(double nx, double ny, MouseButton button)
    {
        MoveAbsolute(nx, ny);
        Click(button);
    }

    private static void Click(MouseButton button)
    {
        MouseButtonEvent(button, down: true);
        Thread.Sleep(ClickDwellMs);
        MouseButtonEvent(button, down: false);
    }

    private static void MoveAbsolute(double nx, double ny)
    {
        nx = Math.Clamp(nx, 0.0, 1.0);
        ny = Math.Clamp(ny, 0.0, 1.0);
        int ax = (int)Math.Round(nx * 65535.0);
        int ay = (int)Math.Round(ny * 65535.0);
        SendMouse(ax, ay, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);
    }

    private static void MouseButtonEvent(MouseButton button, bool down) => SendMouse(0, 0, 0, button switch
    {
        MouseButton.Right  => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        MouseButton.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _                  => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
    });

    private static void Type(string text)
    {
        foreach (var ch in text) { SendUnicode(ch, false); SendUnicode(ch, true); }
    }

    private static void Key(string name)
    {
        ushort vk = name switch
        {
            "enter" => 0x0D, "backspace" => 0x08, "tab" => 0x09, "escape" => 0x1B,
            "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
            "home" => 0x24, "end" => 0x23, _ => 0,
        };
        if (vk == 0) return;
        SendKey(vk, false);
        SendKey(vk, true);
    }

    // ---- SendInput plumbing (mirrors InputService.cs) ----

    private static void SendMouse(int dx, int dy, int data, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = (uint)data, dwFlags = flags } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendUnicode(char ch, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0) } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0 } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
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
