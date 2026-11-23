#nullable disable
// Mouse in the launcher: our own cursor (a white pointer with the selection ring's cyan-to-violet edge,
// filled with the gradient over anything that can be clicked), a ring ripple on clicks, hidden after a
// few idle seconds or as soon as the keyboard or a controller is used. Moving over tiles, buttons and
// rows selects them (with the scroll sound), a left click is A, holding it on a game is holding A,
// a right click on a game opens its quick menu and anywhere else goes back, the wheel scrolls.
// The cursor is a real Windows cursor built in memory, so it never lags behind the mouse.
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ionea;

public enum MouseAct { Move, Down, Up, Right, WheelUp, WheelDown }

public sealed class Pointer
{
    readonly MainWindow w;
    public readonly Canvas Layer = new() { IsHitTestVisible = false };
    Cursor normal, hover;
    int builtSize = -1;
    double builtDpi;
    Point lastScreen = new(double.NaN, double.NaN);
    long lastMove;
    bool overClickable, hidden = true;
    public bool Held;                       // left button down (counts as A held)
    public readonly TextTip Tips;           // the whole text over text cut short with "…" (TextTip.cs)
    public static string LastRoute;         // for test scripts

    public Pointer(MainWindow win)
    {
        w = win;
        Tips = new TextTip(w.Stage, Layer);
        w.PreviewMouseMove += (s, e) => Move(e);
        w.PreviewMouseDown += (s, e) => Down(e);
        w.PreviewMouseUp += (s, e) => { if (!Scripted && e.ChangedButton == MouseButton.Left && Held) { Held = false; Route(MouseAct.Up, e.GetPosition(w.Stage)); } };
        w.PreviewMouseWheel += (s, e) => Wheel(e);
        w.Deactivated += (s, e) => Held = false;
        w.MouseLeave += (s, e) => { Held = false; Tips.Hide(); };
        w.Clock.Tick += (s, e) => { if (!hidden && Environment.TickCount64 - lastMove > 3000 && !Held) Hide(); };
        Apply();
    }

    bool On => w.Settings.Input.MouseOn;
    bool Scripted => !string.IsNullOrEmpty(w.Opt.Script);   // a test run: only the script moves the mouse, a real one passing over is ignored

    // The keyboard or a controller was used: the cursor steps aside until the mouse moves again.
    public void Hide()
    {
        Tips.Hide();
        if (hidden) return;
        hidden = true;
        Apply();
    }

    public void Apply()
    {
        if (!On) { w.Cursor = null; return; }   // turned off: the plain Windows arrow, and clicks do nothing
        Build();
        w.Cursor = hidden ? Cursors.None : overClickable ? hover : normal;
    }

    // In a game the cursor stays hidden, except where the mouse is the touch screen (DS, 3DS). Its buttons
    // still reach the game (read by the core itself).
    bool GameHides => w.TopScreen is CoreScreen c ? !c.ShowsCursor : w.TopScreen is EmbedScreen;

    void Move(MouseEventArgs e)
    {
        if (!On || Scripted) return;
        if (GameHides) { Hide(); return; }
        // WPF also sends moves when things slide under a still mouse: only real movement counts.
        Point sp;
        try { sp = w.PointToScreen(e.GetPosition(w)); } catch { return; }
        if (!double.IsNaN(lastScreen.X) && Math.Abs(sp.X - lastScreen.X) + Math.Abs(sp.Y - lastScreen.Y) < 3) return;
        lastScreen = sp;
        lastMove = Environment.TickCount64;
        bool was = hidden;
        hidden = false;
        if (!HoverAllowed(sp)) { Tips.Hide(); if (was) Apply(); return; }   // the wheel is scrolling: hover doesn't fight it
        bool click = Route(MouseAct.Move, e.GetPosition(w.Stage));
        Tips.At(e.GetPosition(w.Stage));   // after the route: what it opened or scrolled is in place
        if (click != overClickable || was) { overClickable = click; Apply(); }
    }

    void Down(MouseButtonEventArgs e)
    {
        if (!On || Scripted) return;
        if (GameHides) { Hide(); return; }
        lastMove = Environment.TickCount64;
        Tips.Hide();
        if (hidden) { hidden = false; Apply(); }
        wheelLock = false;   // a click is deliberate: hover follows the mouse again
        // binding a mouse button to a game button (KeyboardScreen): any button is taken as it is
        if (w.TopScreen is IScreen bs && bs.TakeMouse(e.ChangedButton)) { e.Handled = true; return; }
        var p = e.GetPosition(w.Stage);
        if (e.ChangedButton == MouseButton.Left)
        {
            if (w.TopScreen is IScreen s && s.WantsText && e.OriginalSource is DependencyObject d && InTextBox(d)) return;   // place the caret
            Ripple(p);
            Held = true;
            if (Route(MouseAct.Down, p)) e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            Ripple(p);
            if (!Route(MouseAct.Right, p)) w.Dispatch(Act.Back);
            e.Handled = true;
        }
    }

    void Wheel(MouseWheelEventArgs e)
    {
        if (!On || Scripted) return;
        Tips.Hide();
        try { var sp = w.PointToScreen(e.GetPosition(w)); NoteWheel(sp); lastScreen = sp; } catch { /* window not shown yet */ }
        var m = e.Delta > 0 ? MouseAct.WheelUp : MouseAct.WheelDown;
        if (!Route(m, e.GetPosition(w.Stage))) w.Dispatch(e.Delta > 0 ? Act.Up : Act.Down);
        e.Handled = true;
    }

    // The wheel wins over hover (every list: home, cards, menu lists, the Home menu and core settings in a
    // game). While the wheel turns and for WheelQuietMs after the last notch, mouse moves don't change the
    // selection, and the spot the mouse rests at is followed. After that, hover only takes over once the mouse
    // has moved DeliberatePx from that spot, so a few pixels of jitter never count. A click ends it at once.
    public const int WheelQuietMs = 400, DeliberatePx = 12;
    long wheelAt;
    bool wheelLock;
    Point wheelAnchor;

    public void NoteWheel(Point screen)
    {
        wheelAt = Environment.TickCount64;
        wheelAnchor = screen;
        wheelLock = true;
    }

    public bool HoverAllowed(Point screen)
    {
        if (!wheelLock) return true;
        if (Environment.TickCount64 - wheelAt < WheelQuietMs) { wheelAnchor = screen; return false; }
        if ((screen - wheelAnchor).Length < DeliberatePx) return false;
        wheelLock = false;
        return true;
    }

    public void EndWheelLock() => wheelLock = false;

    static bool InTextBox(DependencyObject d)
    {
        for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBox) return true;
        return false;
    }

    // Is the stage point on this element (as drawn, with its transforms)?
    public static bool Over(FrameworkElement e, Visual stage, Point p)
    {
        if (e == null || !e.IsVisible || e.ActualWidth <= 0) return false;
        try { return e.TransformToAncestor(stage).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)).Contains(p); }
        catch { return false; }
    }

    // To the screen in front. Hover sounds can be turned off (Settings > Input > Mouse).
    public bool Route(MouseAct m, Point p)
    {
        if (w.Launch.Blocks) return true;   // a game is starting or closing
        if (w.Tutor != null && w.Tutor.SkipHit(m, p)) return true;   // Skip in the tutorial's bubble
        var s = w.TopScreen;
        if (s == null) return false;
        Sounds.Quiet = m == MouseAct.Move && !w.Settings.Input.HoverSounds;
        try
        {
            bool r = s is MenuCard mc && mc.ListOpen ? mc.ListMouse(m, p) : (s is Card cd && cd.CornerMouse(m, p)) || s.OnMouse(m, p);
            if (m != MouseAct.Move) LastRoute = $"{m} {s.GetType().Name} {(r ? "used" : "default")}";
            return r;
        }
        catch (Exception e) { Log.Write($"mouse {m}: {e}"); return true; }
        finally { Sounds.Quiet = false; }
    }

    // A ring that grows and fades where the click was.
    void Ripple(Point p)
    {
        var e = new Ellipse { Width = 20, Height = 20, Stroke = Palette.RingBrush(), StrokeThickness = 3, RenderTransform = new ScaleTransform(1, 1, 10, 10) };
        Canvas.SetLeft(e, p.X - 10); Canvas.SetTop(e, p.Y - 10);
        Layer.Children.Add(e);
        var sc = (ScaleTransform)e.RenderTransform;
        Anim.To(sc, ScaleTransform.ScaleXProperty, 3.2, 380); Anim.To(sc, ScaleTransform.ScaleYProperty, 3.2, 380);
        Anim.To(e, UIElement.OpacityProperty, 0, 380, done: () => Layer.Children.Remove(e));
    }

    // ---- the cursor ----
    // Settings > Colours changed: the pointer is drawn again in the preset's colours.
    public void Recolour() { normal = null; Apply(); }

    void Build()
    {
        int size = w.Settings.Input.CursorSize;
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        if (size == builtSize && dpi == builtDpi && normal != null) return;
        builtSize = size; builtDpi = dpi;
        int px = (int)Math.Round(32 * dpi * (size == 0 ? 0.8 : size == 2 ? 1.4 : 1.0));
        normal = Make(px, false);
        hover = Make(px, true);
    }

    public static readonly Geometry Arrow = Geometry.Parse("M4 3 L4 25.5 L9.6 20.4 L13.4 28.6 L17.4 26.9 L13.7 18.9 L21.2 18.9 Z");

    public static System.Windows.Media.Imaging.BitmapSource Picture(int px, bool click) => Art.Render(px, px, dc =>
    {
        dc.PushTransform(new ScaleTransform(px / 32.0, px / 32.0));
        var glow = Palette.Glow;
        foreach (var (wd, a) in new[] { (7.0, 0.10), (5.0, 0.16), (3.6, 0.24) })
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a * (click ? 400 : 255)), glow.R, glow.G, glow.B)), wd) { LineJoin = PenLineJoin.Round }, Arrow);
        var grad = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Palette.Cyan, 0), new GradientStop(Palette.Blue, 0.5), new GradientStop(Palette.Violet, 1) }, new Point(0, 0), new Point(0.6, 1));
        dc.DrawGeometry(click ? grad : Brushes.White, new Pen(click ? Brushes.White : grad, 1.9) { LineJoin = PenLineJoin.Round }, Arrow);
        // a small shine on the pointer's left edge
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(click ? (byte)150 : (byte)0, 255, 255, 255)), 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(6, 8), new Point(6, 18));
        dc.Pop();
    });

    // A .cur file in memory: 32-bit colour with alpha, hot spot at the pointer's tip.
    static Cursor Make(int px, bool click)
    {
        var bmp = Picture(px, click);
        int stride = px * 4;
        var pix = new byte[stride * px];
        bmp.CopyPixels(pix, stride, 0);
        for (int i = 0; i < pix.Length; i += 4)   // premultiplied -> straight alpha
        {
            int a = pix[i + 3];
            if (a == 0 || a == 255) continue;
            for (int c = 0; c < 3; c++) pix[i + c] = (byte)Math.Min(255, pix[i + c] * 255 / a);
        }
        int maskStride = (px + 31) / 32 * 4;
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((short)0); bw.Write((short)2); bw.Write((short)1);                    // ICONDIR: cursor, one image
        bw.Write((byte)(px >= 256 ? 0 : px)); bw.Write((byte)(px >= 256 ? 0 : px)); bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((short)Math.Round(4 * px / 32.0)); bw.Write((short)Math.Round(3 * px / 32.0));   // hot spot
        bw.Write(40 + stride * px + maskStride * px); bw.Write(22);
        bw.Write(40); bw.Write(px); bw.Write(px * 2); bw.Write((short)1); bw.Write((short)32);   // BITMAPINFOHEADER
        bw.Write(0); bw.Write(stride * px + maskStride * px); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = px - 1; y >= 0; y--) bw.Write(pix, y * stride, stride);
        bw.Write(new byte[maskStride * px]);
        bw.Flush();
        ms.Position = 0;
        return new Cursor(ms, false);
    }
}
