using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LimLoToolkit.Tools;

/// <summary>
/// Sends keyboard input to this process's FFXIVGAME window, for the Keybound Brawler tool.
///
/// <para><b>How the game reads a keyboard</b> (ffxiv_dx11.exe, 2026-10-20 build, reversed
/// 2026-10-28 — see <c>docs/keybound-brawler.md</c>): the main loop runs PeekMessage →
/// TranslateMessage → DispatchMessage. The window procedure's <c>WM_KEYDOWN</c> handler skips
/// auto-repeats, maps the VK to an internal key code and pushes a key event into a 64-entry ring;
/// the text in an input bar arrives separately, as <c>WM_CHAR</c>. Measured: a bare WM_CHAR adds
/// one ring entry, a real key adds two.</para>
///
/// <para>Typing with bare WM_CHAR got the player expelled from the duty for "extended
/// inactivity" twice, about two minutes in, while words were going in fine.
/// <see cref="SendTypedChar"/> does what a physical key does — WM_KEYDOWN (correct case via a
/// temporary keyboard state) plus WM_CHAR, then WM_KEYUP on a later frame — and a full
/// eight-minute match ran with no kick.</para>
/// </summary>
internal sealed class KeyInjector
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const int VK_BACK = 0x08;
    private const int VK_RETURN = 0x0D;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int KeyHoldMs = 50;

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint parent, nint childAfter, string? cls, string? window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    private nint _hwnd;
    private uint _windowThread;
    private readonly List<(int Vk, nint Scan, long ReleaseAt, int Frames)> _held = new();
    private readonly byte[] _saved = new byte[256];
    private readonly byte[] _temp = new byte[256];

    /// <summary>Bare WM_CHAR — text only, no key event. Kept for characters no key produces.</summary>
    public bool SendChar(char c)
    {
        var h = Window();
        return h != nint.Zero && PostMessageW(h, WM_CHAR, c, 1);
    }

    /// <summary>
    /// Types one character the way a physical key does. Must run on the window's own thread
    /// (the framework thread is): SetKeyboardState only affects the calling thread, and
    /// SendMessage to a same-thread window calls the window procedure directly — so the
    /// temporary Shift state is in place exactly while the game's ToAscii runs, and no
    /// TranslateMessage pass can add a second WM_CHAR. Returns false (caller falls back to
    /// <see cref="SendChar"/>) when that is not possible.
    /// </summary>
    public bool SendTypedChar(char c, out string? fallbackReason)
    {
        fallbackReason = null;
        var h = Window();
        if (h == nint.Zero) { fallbackReason = "no game window"; return false; }
        if (_windowThread != GetCurrentThreadId()) { fallbackReason = "not on the game window's thread"; return false; }

        short scanResult = VkKeyScanW(c);
        if (scanResult == -1) { fallbackReason = $"no key produces '{c}' on this keyboard layout"; return false; }
        int vk = scanResult & 0xFF;
        int mods = (scanResult >> 8) & 0xFF;
        if ((mods & ~1) != 0) { fallbackReason = $"'{c}' needs Ctrl/Alt"; return false; }
        bool shift = (mods & 1) != 0;

        nint scan = (nint)(MapVirtualKeyW((uint)vk, 0) << 16);
        if (!GetKeyboardState(_saved)) { fallbackReason = "GetKeyboardState failed"; return false; }
        Buffer.BlockCopy(_saved, 0, _temp, 0, 256);
        byte s = shift ? (byte)0x80 : (byte)0;
        _temp[VK_SHIFT] = s; _temp[VK_LSHIFT] = s; _temp[VK_RSHIFT] = 0;
        _temp[VK_CONTROL] = 0; _temp[VK_MENU] = 0;

        try
        {
            SetKeyboardState(_temp);
            SendMessageW(h, WM_KEYDOWN, vk, 1 | scan);   // bit 30 clear: a fresh press, not a repeat
            SendMessageW(h, WM_CHAR, c, 1 | scan);
        }
        finally
        {
            SetKeyboardState(_saved);
        }
        _held.Add((vk, scan, Environment.TickCount64 + KeyHoldMs, 0));
        return true;
    }

    /// <summary>
    /// Backspace. Sent as WM_CHAR 0x08 first; <paramref name="viaKeyDown"/> switches to a
    /// real VK_BACK down/up pair for a field that ignores the character form.
    /// </summary>
    public bool SendBackspace(bool viaKeyDown) =>
        viaKeyDown ? SendKey(VK_BACK) : SendChar('\b');

    /// <summary>Enter as a VK_RETURN down/up pair; <paramref name="viaChar"/> sends WM_CHAR '\r' instead.</summary>
    public bool SendEnter(bool viaChar) => viaChar ? SendChar('\r') : SendKey(VK_RETURN);

    /// <summary>LControl press — resets the game's client-side input timers. Does nothing in game.</summary>
    public bool TapControl() => SendKey(VK_LCONTROL);

    /// <summary>
    /// Key down now, key up on a LATER frame (<see cref="Pump"/>), so the press spans at least
    /// one of the game's per-frame key samples.
    /// </summary>
    private bool SendKey(int vk)
    {
        var h = Window();
        if (h == nint.Zero) return false;
        nint scan = (nint)(MapVirtualKeyW((uint)vk, 0) << 16);
        if (!PostMessageW(h, WM_KEYDOWN, vk, 1 | scan)) return false;
        _held.Add((vk, scan, Environment.TickCount64 + KeyHoldMs, 0));
        return true;
    }

    /// <summary>True while a key is down and waiting for its release frame.</summary>
    public bool AnyHeld => _held.Count > 0;

    /// <summary>Call every framework tick. Releases keys held for at least KeyHoldMs and 2 frames.</summary>
    public void Pump()
    {
        if (_held.Count == 0) return;
        long now = Environment.TickCount64;
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            var (vk, scan, releaseAt, frames) = _held[i];
            if (now < releaseAt || frames < 2)
            {
                _held[i] = (vk, scan, releaseAt, frames + 1);
                continue;
            }
            var h = Window();
            if (h != nint.Zero) PostMessageW(h, WM_KEYUP, vk, 1 | scan | unchecked((nint)0xC0000000));
            _held.RemoveAt(i);
        }
    }

    /// <summary>Releases everything still held — on disarm and unload, so no key is left stuck down.</summary>
    public void ReleaseAll()
    {
        var h = Window();
        foreach (var (vk, scan, _, _) in _held)
            if (h != nint.Zero) PostMessageW(h, WM_KEYUP, vk, 1 | scan | unchecked((nint)0xC0000000));
        _held.Clear();
    }

    private nint Window()
    {
        if (_hwnd != nint.Zero) return _hwnd;
        int myPid = Environment.ProcessId;
        nint h = nint.Zero;
        while ((h = FindWindowExW(nint.Zero, h, "FFXIVGAME", null)) != nint.Zero)
        {
            uint tid = GetWindowThreadProcessId(h, out int pid);
            if (pid == myPid) { _windowThread = tid; return _hwnd = h; }
        }
        return nint.Zero;
    }
}
