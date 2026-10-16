using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class EspTarget { public RectangleF Box; public string Text; }

// Click-through, always-on-top window laid over the game's client area. Needs windowed or borderless mode
// (exclusive fullscreen draws over it). Black is the transparency key.
class Overlay : Form
{
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;

    readonly IntPtr game;
    readonly Func<int, int, List<EspTarget>> collect;
    readonly Func<bool> enabled;
    List<EspTarget> targets = new List<EspTarget>();
    readonly Pen pen = new Pen(Color.Red, 2);
    readonly Font font = new Font("Consolas", 9f, FontStyle.Bold);

    public Overlay(int pid, Func<int, int, List<EspTarget>> collect, Func<bool> enabled)
    {
        game = Process.GetProcessById(pid).MainWindowHandle;
        this.collect = collect; this.enabled = enabled;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Timer t = new Timer { Interval = 16 };
        t.Tick += delegate { Frame(); };
        t.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    void Frame()
    {
        RECT r = new RECT(); Point p = new Point(0, 0);
        bool show = enabled() && GetForegroundWindow() == game && GetClientRect(game, out r) && ClientToScreen(game, ref p);
        if (!show) { targets.Clear(); if (Visible) Hide(); return; }
        if (!Visible) Show();
        Bounds = new Rectangle(p.X, p.Y, r.Right, r.Bottom);
        try { targets = collect(r.Right, r.Bottom); } catch { targets.Clear(); }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit; // no AA fringe on the color key
        foreach (EspTarget t in targets)
        {
            e.Graphics.DrawRectangle(pen, t.Box.X, t.Box.Y, t.Box.Width, t.Box.Height);
            SizeF sz = e.Graphics.MeasureString(t.Text, font);
            e.Graphics.DrawString(t.Text, font, Brushes.Red, t.Box.X + (t.Box.Width - sz.Width) / 2, t.Box.Y - sz.Height - 1);
        }
    }
}
