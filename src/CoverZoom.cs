#nullable disable
// Cover zoom and position: which part of a game's cover a tile shows, kept per game relative
// to the picture (Game.CoverZoom / CoverX / CoverY), so it is the same at every tile size and icon shape.
// The cover file itself is never changed. Zoom can go below 1 to show more of the picture. CoverView does the maths and makes the brush every cover uses;
// CoverAdjustCard is Game info > Adjust cover (slider, + / -, Reset; wheel and drag on the preview;
// triggers / LB RB zoom and the right stick pans on a pad; + / - and Shift + arrows on the keyboard).
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

public static class CoverView
{
    public const double Max = 4;      // the closest zoom
    public const double Step = 0.25;  // + / -, LB / RB, the wheel

    public const double Floor = 0.2;  // the farthest zoom out any picture can have

    public static double Zoom(Game g) => Math.Clamp(g?.CoverZoom ?? 1, Floor, Max);
    public static bool Plain(Game g) => g == null || (g.CoverZoom == null && g.CoverX == null && g.CoverY == null);

    public static double Aspect(ImageSource s) =>
        s is BitmapSource b && b.PixelWidth > 0 && b.PixelHeight > 0 ? (double)b.PixelWidth / b.PixelHeight
        : s != null && s.Height > 0 ? s.Width / s.Height : 1;

    // The zoom where the whole picture just fits the tile, and the farthest out (a margin round it).
    public static double Whole(double aspect) => Math.Min(aspect, 1 / aspect);
    public static double Min(double aspect) => Math.Max(Floor, Whole(aspect) * 0.8);

    // The part of the picture a square tile shows, in the picture's own 0..1 units. Zoom 1 is the "cover" fit
    // (the picture fills the tile, the long side cropped evenly). Above 1 the box stays inside the picture;
    // below 1 (zoomed out) the box is larger than the picture on a side, the picture stays inside
    // the tile, and the rest of the tile shows a soft copy of it (Brush).
    public static Rect Box(double aspect, double zoom, double cx, double cy)
    {
        zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 1, Min(aspect), Max);
        double bw = aspect > 1 ? 1 / aspect : 1, bh = aspect < 1 ? aspect : 1;
        double w = bw / zoom, h = bh / zoom;
        double x = Math.Clamp((double.IsFinite(cx) ? cx : 0.5) - w / 2, Math.Min(0, 1 - w), Math.Max(0, 1 - w));
        double y = Math.Clamp((double.IsFinite(cy) ? cy : 0.5) - h / 2, Math.Min(0, 1 - h), Math.Max(0, 1 - h));
        return new Rect(x, y, w, h);
    }

    public static Rect Box(ImageSource src, Game g) => Box(Aspect(src), Zoom(g), g?.CoverX ?? 0.5, g?.CoverY ?? 0.5);

    // The brush for a game's cover on any tile or header: today's fill when the game has no view set.
    public static Brush Brush(ImageSource src, Game g) => Plain(g) ? Art.Fill(src) : Brush(src, Box(src, g));

    // The picture shown through a box (Box). A box past the picture's edge: the picture over a soft blurred
    // copy of itself that fills the tile. soft: that copy, made once by the caller (Soft); null makes it here.
    public static Brush Brush(ImageSource src, Rect box, ImageSource soft = null)
    {
        if (box.Width <= 1.0005 && box.Height <= 1.0005)
        {
            var b = new ImageBrush(src) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = box };
            RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
            return b;
        }
        var unit = new Rect(0, 0, 1, 1);
        static Rect Place(Rect v) => new(-v.X / v.Width, -v.Y / v.Height, 1 / v.Width, 1 / v.Height);   // the picture's place on the tile
        var dg = new DrawingGroup { ClipGeometry = new RectangleGeometry(unit) };
        RenderOptions.SetBitmapScalingMode(dg, BitmapScalingMode.HighQuality);
        dg.Children.Add(new ImageDrawing(soft ?? Soft(src) ?? src, Place(Box(Aspect(src), 1, 0.5, 0.5))));
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, new RectangleGeometry(unit)));
        var at = Place(box);
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new RectangleGeometry(new Rect(at.X + 0.005, at.Y + 0.01, at.Width, at.Height))));
        dg.Children.Add(new ImageDrawing(src, at));
        dg.Freeze();
        return new DrawingBrush(dg) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = unit };
    }

    // A tiny copy of the picture; stretched over the tile it is a soft blur of its colours.
    public static ImageSource Soft(ImageSource src)
    {
        if (src is not BitmapSource b || b.PixelWidth <= 0) return null;
        var t = new TransformedBitmap(b, new ScaleTransform(12.0 / b.PixelWidth, 12.0 / b.PixelWidth));
        t.Freeze();
        return t;
    }

    public static string Describe(Game g) => g == null ? "none" : FormattableString.Invariant($"{Zoom(g):0.00}|{g.CoverX ?? 0.5:0.00}|{g.CoverY ?? 0.5:0.00}");
}

public sealed class CoverAdjustCard : MenuCard, IScreen   // re-implement IScreen so TakeKey is used
{
    public static CoverAdjustCard Current;
    readonly Game g;
    readonly BitmapSource pic;
    readonly double aspect, zMin;
    readonly ImageSource soft;
    double zoom, cx, cy;
    readonly double z0, x0, y0;
    readonly Border big, small;
    readonly DispatcherTimer pad = new() { Interval = TimeSpan.FromMilliseconds(16) };
    long lastTick;
    bool drag; Point dragAt;
    public (double lt, double rt, double rx, double ry)? TestPad;   // tests: a pad's triggers and right stick

    const double BigS = 400, SmallS = 150;

    public CoverAdjustCard(MainWindow w, Game game) : base(w, T("home.adjust_cover"), 1360)
    {
        g = game;
        pic = Art.LoadImage(w.Lib.CoverPath(g), 0);
        aspect = CoverView.Aspect(pic);
        zMin = CoverView.Min(aspect);
        soft = pic == null ? null : CoverView.Soft(pic);
        zoom = z0 = Math.Max(zMin, CoverView.Zoom(g)); cx = x0 = g.CoverX ?? 0.5; cy = y0 = g.CoverY ?? 0.5;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 48, 0) };
        double R(double s) => Art.Corner(16.28 * s / 297);   // the tile's corner in today's icon shape
        big = new Border { Width = BigS, Height = BigS, CornerRadius = new CornerRadius(R(BigS)), BorderThickness = new Thickness(1.5), BorderBrush = P.TileEdge, Background = P.TileFallback };
        small = new Border { Width = SmallS, Height = SmallS, CornerRadius = new CornerRadius(R(SmallS)), BorderThickness = new Thickness(1.5), BorderBrush = P.TileEdge, Background = P.TileFallback, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 22, 0, 0) };
        left.Children.Add(big);
        var smallRow = new StackPanel { Orientation = Orientation.Horizontal };
        smallRow.Children.Add(small);
        var sn = Fonts.Text(T("home.as_a_small_tile"), 22, P.Text2); sn.VerticalAlignment = VerticalAlignment.Bottom; sn.Margin = new Thickness(20, 0, 0, 6);
        smallRow.Children.Add(sn);
        left.Children.Add(smallRow);
        grid.Children.Add(left);
        var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rows, 1); grid.Children.Add(rows);
        Body.Children.Add(grid);
        RowHost = rows;

        var zr = AddSlider(T("home.zoom"), () => (int)Math.Round((zoom - zMin) / (CoverView.Max - zMin) * 100), v => SetZoom(zMin + v / 100.0 * (CoverView.Max - zMin), 0.5, 0.5));
        zr.Value = () => FormattableString.Invariant($"{zoom:0.00}x");
        WithIcon(zr, "search");
        WithIcon(AddRow(T("home.zoom_in"), activate: () => SetZoom(zoom + CoverView.Step, 0.5, 0.5)), "plus");
        WithIcon(AddRow(T("home.zoom_out"), activate: () => SetZoom(zoom - CoverView.Step, 0.5, 0.5)), "minus");
        WithIcon(AddRow(T("home.whole_picture"), activate: () => { zoom = CoverView.Whole(aspect); cx = cy = 0.5; Show(); }), "picture");
        WithIcon(AddRow(T("home.move_sideways"), () => Where(true), d => Pan(-d * 0.1, 0)), "move");
        WithIcon(AddRow(T("home.move_up_or_down"), () => Where(false), d => Pan(0, -d * 0.1)), "move");
        WithIcon(AddRow(T("home.reset"), activate: () => { zoom = 1; cx = cy = 0.5; Show(); }), "restart");
        AddRow(T("common.done"), activate: () => Close());
        RowHost = null;
        Show();
        pad.Tick += (_, _) => Analog();
    }

    // Where the shown part sits in the room it has to move: "Centre", a percentage, or "—" when it can't move.
    string Where(bool across)
    {
        var b = CoverView.Box(aspect, zoom, cx, cy);
        double room = across ? 1 - b.Width : 1 - b.Height;   // below 0: zoomed out, the picture moves inside the tile
        if (Math.Abs(room) < 0.002) return "—";
        double at = ((across ? b.X : b.Y) - Math.Min(0, room)) / Math.Abs(room);
        return Math.Abs(at - 0.5) < 0.01 ? T("home.centre") : $"{Math.Round(at * 100)}%";
    }

    public override void Open() { base.Open(); Current = this; lastTick = Environment.TickCount64; pad.Start(); }

    public override void Close(bool sound = true)
    {
        pad.Stop();
        if (Current == this) Current = null;
        Keep();
        base.Close(sound);
    }

    // Kept as it is shown (Back and Done both keep it; Reset brings back the plain fill).
    void Keep()
    {
        bool plain = Math.Abs(zoom - 1) < 0.001 && Math.Abs(cx - 0.5) < 0.001 && Math.Abs(cy - 0.5) < 0.001;
        double? nz = plain ? null : Math.Round(zoom, 4), nx = plain ? null : Math.Round(cx, 4), ny = plain ? null : Math.Round(cy, 4);
        if (g.CoverZoom == nz && g.CoverX == nx && g.CoverY == ny) return;
        g.CoverZoom = nz; g.CoverX = nx; g.CoverY = ny;
        w.Lib.Save();
        Log.Write($"cover view: \"{g.Name}\" {CoverView.Describe(g)}");
        w.Home?.Rebuild(g.Id);
        GameInfoCard.Current?.Redraw();
    }

    // Stored as the centre of what shows, after clamping, so the saved values are always a valid view.
    void Show()
    {
        var b = CoverView.Box(aspect, zoom, cx, cy);
        cx = b.X + b.Width / 2; cy = b.Y + b.Height / 2;
        if (pic != null) { big.Background = CoverView.Brush(pic, b, soft); small.Background = CoverView.Brush(pic, b, soft); }
        Refresh();
    }

    // Zoom keeping the picture's point under (u, v) of the tile (0..1) where it is.
    void SetZoom(double z, double u, double v)
    {
        z = Math.Clamp(z, zMin, CoverView.Max);
        if (Math.Abs(z - zoom) < 1e-6) return;   // at the limit already
        var b = CoverView.Box(aspect, zoom, cx, cy);
        double px = b.X + u * b.Width, py = b.Y + v * b.Height;
        var n = CoverView.Box(aspect, z, 0.5, 0.5);
        zoom = z;
        cx = px - u * n.Width + n.Width / 2; cy = py - v * n.Height + n.Height / 2;
        Show();
    }

    // Move the picture by a part of the tile (the picture follows: right moves it right, showing more of its left).
    void Pan(double dx, double dy)
    {
        var b = CoverView.Box(aspect, zoom, cx, cy);
        cx -= dx * b.Width; cy -= dy * b.Height;
        Show();
    }

    // Pad: triggers zoom (full press doubles in a second), the right stick moves (a tile's width a second).
    void Analog()
    {
        long now = Environment.TickCount64;
        double dt = Math.Min(0.1, (now - lastTick) / 1000.0);
        lastTick = now;
        if (w.TopScreen != this) return;
        double lt = 0, rt = 0, rx = 0, ry = 0;
        if (TestPad is { } tp) { lt = tp.lt; rt = tp.rt; rx = tp.rx; ry = tp.ry; }
        else
        {
            if (!w.Input.CanRead() || !w.Input.Enabled()) return;
            for (int i = 0; i < 4; i++)
            {
                if (Native.XInputGetState(i, out var st) != 0) continue;
                var gp = st.Gamepad;
                lt = Math.Max(lt, gp.bLeftTrigger / 255.0); rt = Math.Max(rt, gp.bRightTrigger / 255.0);
                double sx = gp.sThumbRX / 32767.0, sy = gp.sThumbRY / 32767.0;
                if (Math.Abs(sx) > Math.Abs(rx)) rx = sx;
                if (Math.Abs(sy) > Math.Abs(ry)) ry = sy;
            }
        }
        static double Dead(double v, double d) => Math.Abs(v) < d ? 0 : Math.Sign(v) * (Math.Abs(v) - d) / (1 - d);
        double t = Dead(rt, 0.12) - Dead(lt, 0.12);
        rx = Dead(rx, 0.24); ry = Dead(ry, 0.24);
        if (t == 0 && rx == 0 && ry == 0) return;
        if (t != 0)
        {
            double z = Math.Clamp(zoom * Math.Pow(2, t * dt), zMin, CoverView.Max);
            if (Math.Abs(z - zoom) > 1e-6) SetZoom(z, 0.5, 0.5);
        }
        if (rx != 0 || ry != 0) Pan(-rx * dt, ry * dt);   // stick right: see more of the right side; stick up: more of the top
    }

    // A zoom step: the value tick, higher as the picture zooms in (was the move tick).
    void ZoomTick() => Sounds.Tick((zoom - zMin) / (CoverView.Max - zMin));

    public override void OnAct(Act a)
    {
        if (a is Act.PageLeft or Act.PageRight) { SetZoom(zoom + (a == Act.PageRight ? CoverView.Step : -CoverView.Step), 0.5, 0.5); ZoomTick(); return; }
        if (a is Act.Up or Act.Down or Act.Left or Act.Right && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            Pan(a == Act.Left ? 0.1 : a == Act.Right ? -0.1 : 0, a == Act.Up ? 0.1 : a == Act.Down ? -0.1 : 0);   // Shift + arrow: look that way
            return;
        }
        base.OnAct(a);
    }

    public bool TakeKey(System.Windows.Input.Key k)
    {
        switch (k)
        {
            case System.Windows.Input.Key.OemPlus: case System.Windows.Input.Key.Add: SetZoom(zoom + CoverView.Step, 0.5, 0.5); ZoomTick(); return true;
            case System.Windows.Input.Key.OemMinus: case System.Windows.Input.Key.Subtract: SetZoom(zoom - CoverView.Step, 0.5, 0.5); ZoomTick(); return true;
            case System.Windows.Input.Key.D0: case System.Windows.Input.Key.NumPad0: Sounds.Play("select"); zoom = 1; cx = cy = 0.5; Show(); return true;
        }
        return false;
    }

    // Wheel over the preview zooms around the pointer; drag on it moves the picture.
    public override bool OnMouse(MouseAct m, Point p)
    {
        bool over = pic != null && (Pointer.Over(big, w.Stage, p) || Pointer.Over(small, w.Stage, p));
        Border on = Pointer.Over(small, w.Stage, p) ? small : big;
        if (drag && System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed && !w.Mouse.Held) m = MouseAct.Up;
        switch (m)
        {
            case MouseAct.WheelUp: case MouseAct.WheelDown:
                if (!over) break;
                var q = on.TransformToAncestor(w.Stage).Inverse.Transform(p);
                SetZoom(zoom * (m == MouseAct.WheelUp ? 1.15 : 1 / 1.15), Math.Clamp(q.X / on.ActualWidth, 0, 1), Math.Clamp(q.Y / on.ActualHeight, 0, 1));
                return true;
            case MouseAct.Down:
                if (!over) break;
                drag = true; dragAt = p; Sounds.Play("select");
                return true;
            case MouseAct.Move:
                if (drag)
                {
                    double s = on == small && !Pointer.Over(big, w.Stage, p) ? SmallS : BigS;
                    Pan((p.X - dragAt.X) / s, (p.Y - dragAt.Y) / s);
                    dragAt = p;
                    return true;
                }
                if (over) return true;
                break;
            case MouseAct.Up:
                if (drag) { drag = false; return true; }
                break;
        }
        return base.OnMouse(m, p);
    }

    // For tests: the middle of the big preview on the stage.
    public Point PreviewCentre => big.TransformToAncestor(w.Stage).Transform(new Point(BigS / 2, BigS / 2));
    public string View => FormattableString.Invariant($"{zoom:0.00}|{cx:0.00}|{cy:0.00}");
}
