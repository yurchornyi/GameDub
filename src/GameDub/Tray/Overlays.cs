using System;
using System.Drawing;
using System.Windows.Forms;
using GameDub.Core;
using GameDub.Pipeline;

namespace GameDub.Tray;

/// <summary>Topmost window that never takes focus from the game.</summary>
public class NoActivateForm : Form
{
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    public NoActivateForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }
}

/// <summary>Short status toast at the top of the game window.</summary>
public sealed class Osd : NoActivateForm
{
    readonly System.Windows.Forms.Timer _hide = new() { Interval = 2500 };
    string _text = "";

    public Osd()
    {
        BackColor = Color.FromArgb(24, 26, 28);
        Opacity = 0.92;
        Font = new Font("Segoe UI Semibold", 15f);
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
        CreateHandle();
    }

    public void Flash(string text, IntPtr gameWindow)
    {
        _text = text;
        var area = gameWindow != IntPtr.Zero ? Native.GetClientScreenRect(gameWindow) : Screen.PrimaryScreen!.Bounds;
        var size = TextRenderer.MeasureText(text, Font);
        Bounds = new Rectangle(area.Left + (area.Width - size.Width - 48) / 2, area.Top + area.Height / 20, size.Width + 48, size.Height + 24);
        Invalidate();
        if (!Visible) Show();
        _hide.Stop();
        _hide.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var accent = new SolidBrush(Color.FromArgb(16, 124, 16));
        e.Graphics.FillRectangle(accent, 0, 0, 6, Height);
        TextRenderer.DrawText(e.Graphics, _text, Font, new Rectangle(14, 0, Width - 14, Height), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }
}

/// <summary>Subtitle-region editor driven by the gamepad while the game keeps focus.</summary>
public sealed class RegionEditor : NoActivateForm
{
    const string Help = "D-pad ↑↓ рух · ←→ ширина · LB/RB висота · Y авто · A зберегти · B скасувати";
    readonly DubPipeline _pipe;
    readonly Action<string> _osd;
    readonly RelRect _r;
    readonly Gamepad _pad = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    readonly Font _font = new("Segoe UI Semibold", 12f);

    public RegionEditor(DubPipeline pipe, Action<string> osd)
    {
        _pipe = pipe; _osd = osd;
        var cur = pipe.Region;
        _r = new RelRect { X = cur.X, Y = cur.Y, W = cur.W, H = cur.H };
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        _pipe.Editing = true;
        _pad.Poll(); // swallow the press that opened the editor
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Place();
    }

    void Tick()
    {
        ushort b = _pad.Poll();
        if (b == 0) { Place(); return; }
        if ((b & Gamepad.A) != 0) { Save(); return; }
        if ((b & Gamepad.B) != 0) { _osd("GameDub: зміни скасовано"); Close(); return; }
        if ((b & Gamepad.Y) != 0) { _pipe.RestartDetection(); _osd("GameDub: шукаю субтитри…"); Close(); return; }
        if ((b & Gamepad.Up) != 0) _r.Y -= 0.01;
        if ((b & Gamepad.Down) != 0) _r.Y += 0.01;
        if ((b & Gamepad.Left) != 0) { _r.X += 0.01; _r.W -= 0.02; }
        if ((b & Gamepad.Right) != 0) { _r.X -= 0.01; _r.W += 0.02; }
        if ((b & Gamepad.LB) != 0) _r.H -= 0.01;
        if ((b & Gamepad.RB) != 0) { _r.H += 0.01; _r.Y -= 0.01; }
        _r.Clamp();
        Place();
    }

    void Place()
    {
        var hwnd = _pipe.Window;
        if (hwnd == IntPtr.Zero) { Close(); return; }
        var rect = _r.ToScreen(Native.GetClientScreenRect(hwnd));
        if (Bounds != rect) { Bounds = rect; Invalidate(); }
    }

    public void Save()
    {
        _pipe.SetRegion(_r);
        _osd("GameDub: область збережено");
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(Color.Gold, 4);
        e.Graphics.DrawRectangle(pen, 2, 2, Width - 4, Height - 4);
        var size = TextRenderer.MeasureText(Help, _font);
        var box = new Rectangle(6, 6, Math.Min(Width - 12, size.Width + 16), size.Height + 8);
        using var bg = new SolidBrush(Color.FromArgb(24, 26, 28));
        e.Graphics.FillRectangle(bg, box);
        TextRenderer.DrawText(e.Graphics, Help, _font, box, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _pipe.Editing = false;
        base.OnFormClosed(e);
    }
}
