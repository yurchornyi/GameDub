using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using GameDub.Core;

namespace GameDub.Lights;

/// <summary>
/// The gamepad, read in the background: the left trigger (the brake) and any press. Windows gives XInput and
/// GameInput state only to the app in front; a background app reads zeros (16.11 test on the ROG Xbox Ally X: three
/// minutes of racing, the left trigger 0 all the time). Raw Input with RIDEV_INPUTSINK still gets the pad's HID reports.
/// The HID view of an Xbox-style pad (its device path has "IG_") has both triggers on one Z axis: the left one moves it
/// up from the middle, the right one down; both at once cancel out (a known limit of that view).
/// It listens only while asked (a race on, or waiting for a touch): no messages at all otherwise.
/// </summary>
sealed class PadInput : IDisposable
{
    const int WM_INPUT = 0x00FF, WM_LISTEN = 0x8001, WM_QUIT_PAD = 0x8002;
    const uint RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100, RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007;
    const int HidP_Input = 0, HIDP_STATUS_SUCCESS = 0x110000;
    const ushort PageDesktop = 1, PageButton = 9, UsageJoystick = 4, UsageGamepad = 5, UsageX = 0x30, UsageY = 0x31, UsageZ = 0x32, UsageRx = 0x33, UsageRy = 0x34, UsageHat = 0x39;

    [StructLayout(LayoutKind.Sequential)] struct RawInputDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RawInputDevice[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint header);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, uint len);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] list, ref uint len, IntPtr pp, byte[] report, uint len2);

    /// <summary>One HID value of a pad: where it is and its range.</summary>
    readonly record struct Axis(ushort Link, ushort Usage, long Min, long Max)
    {
        public double Center(uint v) => Max > Min ? Math.Clamp((v - (Min + Max) / 2.0) / ((Max - Min) / 2.0), -1, 1) : 0;
    }

    sealed class Pad
    {
        public IntPtr Parsed;
        public bool Xbox;                                   // "IG_" in its path: triggers on Z
        public readonly List<Axis> Axes = new();
        public Axis? Z;
    }

    readonly Thread _thread;
    Sink? _sink;
    readonly ManualResetEventSlim _ready = new();
    readonly Dictionary<IntPtr, Pad?> _pads = new();
    bool _want;
    volatile float _left;
    long _activeTick;

    public PadInput()
    {
        _thread = new Thread(() =>
        {
            try { _sink = new Sink(this); }
            catch (Exception ex) { Log.Error("pad input", ex); }
            _ready.Set();
            if (_sink != null) Application.Run();
        }) { IsBackground = true, Name = "pad input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    /// <summary>The left trigger, 0..1; 0 while not listening.</summary>
    public double LeftTrigger => _left;

    /// <summary>The last press or stick move (TickCount64), 0 for none; seen only while listening.</summary>
    public long ActiveTick => Interlocked.Read(ref _activeTick);

    /// <summary>Listen or not; cheap to call every second.</summary>
    public void Listen(bool on)
    {
        if (on == _want || _sink == null) return;
        _want = on;
        PostMessage(_sink.Handle, WM_LISTEN, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
    }

    void Register(IntPtr hwnd, bool on)
    {
        var d = new[]
        {
            new RawInputDevice { Page = PageDesktop, Usage = UsageGamepad, Flags = on ? RIDEV_INPUTSINK : RIDEV_REMOVE, Target = on ? hwnd : IntPtr.Zero },
            new RawInputDevice { Page = PageDesktop, Usage = UsageJoystick, Flags = on ? RIDEV_INPUTSINK : RIDEV_REMOVE, Target = on ? hwnd : IntPtr.Zero },
        };
        if (!RegisterRawInputDevices(d, (uint)d.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            Log.Write($"pad: raw input {(on ? "on" : "off")} failed, error {Marshal.GetLastWin32Error()}");
        if (!on) _left = 0;
    }

    void OnInput(IntPtr handle)
    {
        uint size = 0, header = (uint)(8 + 2 * IntPtr.Size);
        GetRawInputData(handle, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, RID_INPUT, buf, ref size, header) != size) return;
            IntPtr device = Marshal.ReadIntPtr(buf, 8);
            int each = Marshal.ReadInt32(buf, (int)header), count = Marshal.ReadInt32(buf, (int)header + 4);
            if (each <= 0 || count <= 0) return;
            var pad = Find(device);
            if (pad == null) return;
            var report = new byte[each];
            Marshal.Copy(buf + (int)header + 8 + (count - 1) * each, report, 0, each); // the newest report
            Read(pad, report);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    void Read(Pad pad, byte[] report)
    {
        bool active = false;
        foreach (var a in pad.Axes)
        {
            if (HidP_GetUsageValue(HidP_Input, PageDesktop, a.Link, a.Usage, out uint v, pad.Parsed, report, (uint)report.Length) != HIDP_STATUS_SUCCESS) continue;
            if (a.Usage == UsageHat) { if (v >= 1 && v <= 8) active = true; continue; }
            double c = a.Center(v);
            if (a.Usage == UsageZ && pad.Xbox) _left = (float)Math.Max(0, c); // left: up to 65408, right: down to 512 (16.11)
            if (Math.Abs(c) > 0.3) active = true;
        }
        uint n = 32; var pressed = new ushort[32];
        if (HidP_GetUsages(HidP_Input, PageButton, 0, pressed, ref n, pad.Parsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS && n > 0) active = true;
        if (active) Interlocked.Exchange(ref _activeTick, Environment.TickCount64);
    }

    Pad? Find(IntPtr device)
    {
        if (_pads.TryGetValue(device, out var known)) return known;
        Pad? pad = null;
        try
        {
            uint n = 0;
            GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref n);
            IntPtr nameBuf = Marshal.AllocHGlobal((int)(n + 1) * 2);
            string name;
            try { GetRawInputDeviceInfo(device, RIDI_DEVICENAME, nameBuf, ref n); name = Marshal.PtrToStringUni(nameBuf) ?? ""; }
            finally { Marshal.FreeHGlobal(nameBuf); }

            uint size = 0;
            GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size == 0) return null;
            IntPtr parsed = Marshal.AllocHGlobal((int)size); // kept for the device's life
            GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, parsed, ref size);
            var caps = new byte[64];
            if (HidP_GetCaps(parsed, caps) != HIDP_STATUS_SUCCESS) { Marshal.FreeHGlobal(parsed); return null; }
            ushort values = BitConverter.ToUInt16(caps, 48);        // HIDP_CAPS.NumberInputValueCaps
            const int CapSize = 72;                                 // HIDP_VALUE_CAPS
            var vc = new byte[CapSize * Math.Max(1, (int)values)];
            ushort len = values;
            if (values > 0) HidP_GetValueCaps(HidP_Input, vc, ref len, parsed);
            pad = new Pad { Parsed = parsed, Xbox = name.Contains("IG_", StringComparison.OrdinalIgnoreCase) };
            for (int i = 0; i < len; i++)
            {
                int o = i * CapSize;
                ushort page = BitConverter.ToUInt16(vc, o), link = BitConverter.ToUInt16(vc, o + 6);
                bool isRange = vc[o + 12] != 0;
                int bits = BitConverter.ToUInt16(vc, o + 18);
                long min = BitConverter.ToInt32(vc, o + 40), max = BitConverter.ToInt32(vc, o + 44);
                if (max < min && bits is > 0 and < 32) { min = 0; max = (1L << bits) - 1; } // an unsigned range read as signed
                ushort u0 = BitConverter.ToUInt16(vc, o + 56), u1 = isRange ? BitConverter.ToUInt16(vc, o + 58) : u0;
                if (page != PageDesktop) continue;
                for (int u = u0; u <= u1; u++)
                    if (u is UsageX or UsageY or UsageZ or UsageRx or UsageRy or UsageHat)
                    {
                        var a = new Axis(link, (ushort)u, min, max);
                        pad.Axes.Add(a);
                        if (u == UsageZ) pad.Z = a;
                    }
            }
            Log.Write($"pad: {(pad.Xbox ? "xbox-style" : "other")} gamepad, {pad.Axes.Count} axes{(pad.Xbox && pad.Z != null ? ", triggers on Z" : "")}");
        }
        catch (Exception ex) { Log.Error("pad device", ex); }
        _pads[device] = pad;
        return pad;
    }

    public void Dispose()
    {
        if (_sink != null) PostMessage(_sink.Handle, WM_QUIT_PAD, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>A hidden window on the pad thread: raw input arrives here as WM_INPUT.</summary>
    sealed class Sink : NativeWindow
    {
        readonly PadInput _owner;
        public Sink(PadInput owner) { _owner = owner; CreateHandle(new CreateParams { Caption = "GameDub pad" }); }

        protected override void WndProc(ref Message m)
        {
            try
            {
                switch (m.Msg)
                {
                    case WM_INPUT: _owner.OnInput(m.LParam); break;
                    case WM_LISTEN: _owner.Register(Handle, m.WParam != IntPtr.Zero); return;
                    case WM_QUIT_PAD:
                        _owner.Register(Handle, false);
                        foreach (var p in _owner._pads.Values) if (p != null) Marshal.FreeHGlobal(p.Parsed);
                        _owner._pads.Clear();
                        DestroyHandle();
                        Application.ExitThread();
                        return;
                }
            }
            catch (Exception ex) { Log.Error("pad input", ex); }
            base.WndProc(ref m);
        }
    }
}
