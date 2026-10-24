using System;

namespace GameDub.Core;

/// <summary>XInput polling with edge detection and D-pad auto-repeat. Left stick acts as D-pad.</summary>
public sealed class Gamepad
{
    public const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Start = 0x10, View = 0x20,
        LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    const ushort Dirs = Up | Down | Left | Right;
    const short StickDeadzone = 16000;

    ushort _prev;
    long _holdStart, _lastRepeat, _lastScan;
    int _index = -1;

    /// <summary>Returns buttons pressed since the last call (plus repeated directions while held).</summary>
    public ushort Poll()
    {
        ushort cur = Read();
        long now = Environment.TickCount64;
        ushort pressed = (ushort)(cur & ~_prev);
        ushort dirs = (ushort)(cur & Dirs);
        if (dirs != 0 && dirs == (_prev & Dirs))
        {
            if (now - _holdStart > 400 && now - _lastRepeat > 110) { pressed |= dirs; _lastRepeat = now; }
        }
        else if (dirs != 0) _holdStart = now;
        _prev = cur;
        return pressed;
    }

    ushort Read()
    {
        try
        {
            if (_index >= 0 && Native.XInputGetState(_index, out var s) == 0) return Map(s.Gamepad);
            _index = -1;
            // Scanning disconnected slots is slow, so do it at most once per second.
            long now = Environment.TickCount64;
            if (now - _lastScan < 1000) return 0;
            _lastScan = now;
            for (int i = 0; i < 4; i++)
                if (Native.XInputGetState(i, out var st) == 0) { _index = i; return Map(st.Gamepad); }
        }
        catch (DllNotFoundException) { }
        return 0;
    }

    static ushort Map(Native.XINPUT_GAMEPAD g)
    {
        ushort b = g.wButtons;
        if (g.sThumbLY > StickDeadzone) b |= Up;
        if (g.sThumbLY < -StickDeadzone) b |= Down;
        if (g.sThumbLX < -StickDeadzone) b |= Left;
        if (g.sThumbLX > StickDeadzone) b |= Right;
        return b;
    }
}
