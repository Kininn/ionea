#nullable disable
// Look: v5/v8 colours (light + dark), animation helpers, own drawn art, sounds.
// All art here is drawn in code from our own v8 SVGs (no Nintendo or ES-DE assets).
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static Ionea.Loc;

namespace Ionea;

// A colour preset (Settings > Look and layout > Colours, per profile): the accent palette for the launcher's
// effects and highlights. A, B, C: the ring gradient (light end, middle, deep end), also the running light
// and the launch effects. Glow: the soft halo. F1, F2: fills that carry white text or icons, dark
// enough for white on them. Disc: the start of the "+" disc and toast discs.
// AccL / AccD: the accent for text, lines, bars and marks on the light / dark card (4.5:1 or better).
// Gradient presets also run their bars from A to C; their glass rim goes A (top) to C (bottom).
public sealed record ColourPreset(string Id, string NameId, string A, string B, string C, string Glow, string F1, string F2, string Disc,
    string AccL, string AccD, bool Gradient, bool TintRim)
{
    public string Name => T(NameId);   // read when shown: these tables are made before the language is set
    public static readonly ColourPreset[] All =
    {
        // Whole palettes only: the one-colour presets (Red, Orange, Yellow / Gold, Green, Blue,
        // Purple) are gone, every preset is a set of colours that belong together. Blossom, Meadow and
        // Twilight bring pink, lime and peach / orchid, which no preset had.
        new("ionea",    "settings.default_2",  "16D0F5", "2D86FF", "6A5CFF", "38B6FF", "2D86FF", "6A5CFF", "16D0F5", "0A62D0", "4DA8FF", false, false),
        new("aurora",   "settings.aurora",   "2EE59D", "2D86FF", "9B5CFF", "4FB8E8", "16967A", "6A3FE0", "2EC98E", "0F6E8C", "5FD4E0", true, true),
        new("sunset",   "settings.sunset",   "F04848", "FF8A1F", "FFD23F", "FF8A3D", "D93A3A", "D9650F", "F04848", "C2410C", "FF9466", true, true),
        new("blossom",  "settings.blossom",  "FFA3C7", "F2547D", "B8327A", "FF7FA8", "D6336C", "A3266A", "F2547D", "C2255C", "FF8FB0", true, true),
        new("meadow",   "settings.meadow",   "D4F06A", "5CCF73", "1E9E8A", "8FE08A", "2A8F48", "157A6E", "4CBF66", "1F7A3D", "8FE08A", true, true),
        new("twilight", "settings.twilight", "FFB38A", "D66BA0", "6A4BC4", "E58AB0", "B8457E", "5B3FB0", "E07A8F", "A63A72", "F0A0C4", true, true),
    };
    // A profile saved with a one-colour preset gets the whole palette nearest to it.
    static readonly Dictionary<string, string> moved = new() { ["red"] = "sunset", ["orange"] = "sunset", ["gold"] = "sunset", ["green"] = "meadow", ["blue"] = "ionea", ["purple"] = "twilight" };
    static string Moved(string id) => id != null && moved.TryGetValue(id, out var to) ? to : id;
    public static ColourPreset Of(string id) => All.FirstOrDefault(p => p.Id == Moved(id)) ?? All[0];
    public static int IndexOf(string id) => Math.Max(0, Array.FindIndex(All, p => p.Id == Moved(id)));
}

public sealed class Palette
{
    public static readonly Palette I = new();
    // Each bound colour is worked out from (dark, preset), so a theme or preset change animates every one.
    readonly List<(Animatable obj, DependencyProperty prop, Func<bool, ColourPreset, Color> pick)> binds = new();
    public bool Dark { get; private set; }
    public ColourPreset Preset { get; private set; } = ColourPreset.All[0];

    public readonly SolidColorBrush Bg, Text, Text2, Accent, Line, ButtonFill, IconInk, TileFallback, TileEdge, Card, Scrim, RowFocus, Error;
    public readonly GradientStop StartA, StartB, GlowA, GlowA0, GlowB, GlowB0, SoftA, SoftB;
    // Frosted glass: the folder tile on home, and the open folder's panel over the blurred home row.
    public readonly SolidColorBrush GlassEdge, FolderScrim;
    // The dim over a sleeping launcher (Sleep.cs).
    public readonly SolidColorBrush SleepDim;
    public readonly GradientStop FolderA, FolderB, GlassA, GlassB, ShineA, ShineB;
    // Glass 2: the bright rim (top bright, bottom soft), the thin inner highlight under it,
    // and the tint over the frosted picture inside a folder tile.
    public readonly GradientStop RimA, RimB, HiA, HiB, FrostA, FrostB;
    // Colour presets: the ring gradient's stops (shared by every ring brush, so a ring on screen
    // changes with the preset), the fills under white, the bars, and the running light's and glow's inks.
    public readonly GradientStop RingA, RingB, RingC, FillA, FillB, DiscA, DiscB, BarA, BarB;
    public readonly SolidColorBrush InkA, InkB, InkC, GlowInk;
    public readonly LinearGradientBrush Bar;
    // Finish pass: the divider fades out at both ends, the option buttons are lit from above
    // with a crisp rim, and the screen's corners sink a little (barely in the light look).
    public readonly GradientStop LineA, LineB, LineC, LineD, BtnA, BtnB, BtnRimA, BtnRimB;
    public readonly LinearGradientBrush LineFade, ButtonLit, ButtonRim;
    public readonly SolidColorBrush VignetteInk;   // drawn through Art.SoftMask(edge), so it has no rings

    // The current preset's colours (fixed cyan / blue / violet / glow before the presets).
    public static Color Cyan => Hex(I.Preset.A);
    public static Color Blue => Hex(I.Preset.B);
    public static Color Violet => Hex(I.Preset.C);
    public static Color Glow => Hex(I.Preset.Glow);

    Palette()
    {
        Bg = Brush("F7F8FA", "2B2C30");
        Text = Brush("2B2D31", "F1F2F4");
        Text2 = Brush("6B7079", "A7ABB3");
        Accent = Bound(new SolidColorBrush(), SolidColorBrush.ColorProperty, (d, p) => Hex(d ? p.AccD : p.AccL));
        Line = Brush("CDD0D6", "45474E");
        ButtonFill = Brush("FFFFFF", "3A3C42");
        IconInk = Brush("2E3036", "EEF0F3");
        TileFallback = Brush("DCE5F2", "2F3B4E");
        TileEdge = Brush("1F000000", "30FFFFFF");
        Card = Brush("FFFFFF", "34353A");
        Scrim = Brush("66E9ECF1", "8C141518");
        // The focused / selected row: today's light blue and grey for Ionea, a light wash of the accent for the others.
        RowFocus = Bound(new SolidColorBrush(), SolidColorBrush.ColorProperty, (d, p) => p.Id == "ionea" ? Hex(d ? "3C3F47" : "EEF4FD")
            : d ? Mix(Hex("3A3C42"), Hex(p.AccD), 0.16) : Mix(Colors.White, Hex(p.AccL), 0.08));
        Error = Brush("C4314B", "FF8A9B");
        StartA = Stop("FFFFFF", "303136", 0); StartB = Stop("EEF2F8", "1E1F23", 1);
        GlowA = Stop("597FD8FF", "382FA8FF", 0); GlowA0 = Stop("007FD8FF", "002FA8FF", 1);
        GlowB = Stop("38A88BFF", "298B6BFF", 0); GlowB0 = Stop("00A88BFF", "008B6BFF", 1);
        SoftA = Stop("FBFCFE", "3C404A", 0); SoftB = Stop("E3E8F0", "2E3139", 1);
        // Stained glass: the containers (folder tile, open folder panel, the frost over its
        // covers) are white glass in the light look and near-black glass in the dark one, each stained
        // faintly with the colour preset (its first colour at the top, its deep one below). The dark look
        // keeps its white sheen and highlight faint, so the glass reads as smoked, not as grey haze.
        GlassEdge = Brush("D0FFFFFF", "48FFFFFF");
        FolderScrim = Brush("66E9ECF1", "8C141518");
        SleepDim = Brush("A61E2128", "C70A0B0E");
        FolderA = Glass("F5FFFFFF", "E6151619", 0, 0.05, 0.10); FolderB = Glass("E6F4F5F7", "EB0D0E10", 1, 0.08, 0.14);
        GlassA = Glass("B3FFFFFF", "C2141518", 0, 0.05, 0.10); GlassB = Glass("9EFAFAFB", "CC0C0D0F", 1, 0.08, 0.14);
        ShineA = Stop("80FFFFFF", "1CFFFFFF", 0); ShineB = Stop("00FFFFFF", "00FFFFFF", 1);
        // The glass rim: white for Ionea; the others take some of the preset (gradient ones A at the top, C below).
        RimA = Stop(0, (d, p) => Rim(Hex(d ? "66FFFFFF" : "FFFFFFFF"), p, p.A, d ? 0.45 : 0.3));
        RimB = Stop(1, (d, p) => Rim(Hex(d ? "14FFFFFF" : "70FFFFFF"), p, p.Gradient ? p.C : p.B, d ? 0.6 : 0.45));
        HiA = Stop("B3FFFFFF", "2EFFFFFF", 0); HiB = Stop("00FFFFFF", "00FFFFFF", 0.35);
        FrostA = Glass("B8FFFFFF", "B0141518", 0, 0.05, 0.10); FrostB = Glass("A3F4F5F7", "BE0C0D0F", 1, 0.08, 0.14);
        // Under Reduce effects (Anim.Plain) the gradients are plain: rings in the preset's middle colour,
        // fills, discs and bars in one colour each.
        RingA = Stop(0, (d, p) => Hex(Anim.Plain ? p.B : p.A)); RingB = Stop(0.5, (d, p) => Hex(p.B)); RingC = Stop(1, (d, p) => Hex(Anim.Plain ? p.B : p.C));
        FillA = Stop(0, (d, p) => Hex(p.F1)); FillB = Stop(1, (d, p) => Hex(Anim.Plain ? p.F1 : p.F2));
        DiscA = Stop(0, (d, p) => Hex(p.Disc)); DiscB = Stop(1, (d, p) => Hex(Anim.Plain ? p.Disc : p.Id == "ionea" ? p.C : p.F2));
        // Bars (progress, sliders): the accent; gradient presets run A to C (their fill colours on the light card).
        BarA = Stop(0, (d, p) => Hex(p.Gradient && !Anim.Plain ? (d ? p.A : p.F1) : d ? p.AccD : p.AccL));
        BarB = Stop(1, (d, p) => Hex(p.Gradient && !Anim.Plain ? (d ? p.C : p.F2) : d ? p.AccD : p.AccL));
        Bar = new LinearGradientBrush(new GradientStopCollection { BarA, BarB }, 0);
        InkA = Bound(new SolidColorBrush(), SolidColorBrush.ColorProperty, (d, p) => Hex(p.A));
        InkB = Bound(new SolidColorBrush(), SolidColorBrush.ColorProperty, (d, p) => Hex(p.B));
        InkC = Bound(new SolidColorBrush(), SolidColorBrush.ColorProperty, (d, p) => Hex(p.C));
        GlowInk = Bound(new SolidColorBrush(), SolidColorBrush.ColorProperty, (d, p) => Hex(p.Glow));
        LineA = Stop("00CDD0D6", "0045474E", 0); LineB = Stop("CDD0D6", "45474E", 0.2);
        LineC = Stop("CDD0D6", "45474E", 0.8); LineD = Stop("00CDD0D6", "0045474E", 1);
        LineFade = new LinearGradientBrush(new GradientStopCollection { LineA, LineB, LineC, LineD }, 0);
        BtnA = Stop("FFFFFF", "46484F", 0); BtnB = Stop("EEF0F4", "33353A", 1);
        ButtonLit = new LinearGradientBrush(new GradientStopCollection { BtnA, BtnB }, 90);
        BtnRimA = Stop("FFFFFFFF", "40FFFFFF", 0); BtnRimB = Stop("1C1E2A40", "0AFFFFFF", 1);
        ButtonRim = new LinearGradientBrush(new GradientStopCollection { BtnRimA, BtnRimB }, 90);
        VignetteInk = Brush("101E2A40", "800A0B10");
    }

    GradientStop Glass(string light, string dark, double offset, double kl, double kd) =>
        Stop(offset, (d, p) => Stain(Hex(d ? dark : light), offset == 0 ? p.A : p.Gradient ? p.C : p.B, d ? kd : kl));

    static Color Stain(Color glass, string tint, double k)
    {
        var c = Mix(glass, Hex(tint), k);
        return Color.FromArgb(glass.A, c.R, c.G, c.B);
    }

    static Color Rim(Color white, ColourPreset p, string tint, double k)
    {
        if (!p.TintRim) return white;
        var c = Mix(white, Hex(tint), k);
        return Color.FromArgb(white.A, c.R, c.G, c.B);
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb((byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static Color Hex(string s)
    {
        uint v = Convert.ToUInt32(s, 16);
        return s.Length == 8 ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
                             : Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    T Bound<T>(T obj, DependencyProperty prop, Func<bool, ColourPreset, Color> pick) where T : Animatable
    {
        obj.SetValue(prop, pick(false, Preset));
        binds.Add((obj, prop, pick));
        return obj;
    }

    SolidColorBrush Brush(string light, string dark)
    {
        Color l = Hex(light), d = Hex(dark);
        return Bound(new SolidColorBrush(), SolidColorBrush.ColorProperty, (dk, _) => dk ? d : l);
    }

    GradientStop Stop(string light, string dark, double offset)
    {
        Color l = Hex(light), d = Hex(dark);
        return Bound(new GradientStop { Offset = offset }, GradientStop.ColorProperty, (dk, _) => dk ? d : l);
    }

    GradientStop Stop(double offset, Func<bool, ColourPreset, Color> pick) => Bound(new GradientStop { Offset = offset }, GradientStop.ColorProperty, pick);

    public void Set(bool dark, bool animate) => Apply(dark, Preset, animate);

    // Settings > Colours: every accent, ring, glow, bar and rim takes the preset's colours (animated when live).
    public void SetPreset(string id, bool animate) => Apply(Dark, ColourPreset.Of(id), animate);

    void Apply(bool dark, ColourPreset preset, bool animate)
    {
        Dark = dark; Preset = preset;
        foreach (var (obj, prop, pick) in binds)
        {
            Color c = pick(dark, preset);
            if (animate) obj.BeginAnimation(prop, new ColorAnimation(c, TimeSpan.FromMilliseconds(380)) { EasingFunction = Anim.InOut });
            else { obj.BeginAnimation(prop, null); obj.SetValue(prop, c); }
        }
    }

    // The ring gradient on the shared stops, so it follows a preset change while it is on screen.
    public static LinearGradientBrush RingBrush() => RingBrush(new Point(0, 0), new Point(1, 1));
    public static LinearGradientBrush RingBrush(Point from, Point to) => new(new GradientStopCollection { I.RingA, I.RingB, I.RingC }, from, to);
}

public static class Fonts
{
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");

    public static TextBlock Text(string s, double size, Brush fg, FontWeight? w = null, FontFamily f = null) => new()
    {
        Text = s, FontSize = size, Foreground = fg, FontFamily = f ?? Body, FontWeight = w ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}

public static class Anim
{
    public static readonly IEasingFunction Out = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction InOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Back = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
    static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

    // Reduce effects (Settings > Sound > Effects, per profile): every animation here is about
    // half as long, the press has no shake and the edge bump is smaller. The effects themselves (breathing
    // glows, running lights, the ring's shine, the start screen's pulse) are not shown at all, rather than
    // held still, and the ring, bar and disc gradients are plain colours (Plain, Palette). Other never-ending
    // animations stand still at a calm frame (the loading spinner keeps turning: it says "busy").
    public static bool Reduced;
    public static bool Plain => Reduced;
    static readonly List<WeakReference<UIElement>> effects = new();

    // An effect: shown normally, hidden (not just stopped) under Reduce effects.
    public static T Effect<T>(T e) where T : UIElement
    {
        if (e == null) return e;
        if (effects.Count > 512) effects.RemoveAll(x => !x.TryGetTarget(out _));
        effects.Add(new WeakReference<UIElement>(e));
        e.Visibility = Reduced ? Visibility.Hidden : Visibility.Visible;
        return e;
    }

    // A focus ring's breathing glow (an effect).
    public static void Glow(UIElement glow) => Forever(Effect(glow), UIElement.OpacityProperty, 0.45, 1, 1100);

    // How many effects are hidden / shown now (tests).
    public static string EffectsForTest()
    {
        effects.RemoveAll(x => !x.TryGetTarget(out _));
        int hid = effects.Count(x => x.TryGetTarget(out var e) && e is FrameworkElement f && f.IsLoaded && e.Visibility != Visibility.Visible);
        int shown = effects.Count(x => x.TryGetTarget(out var e) && e is FrameworkElement f && f.IsLoaded && e.Visibility == Visibility.Visible);
        return shown > 0 ? "shown" : hid > 0 ? "hidden" : "none";
    }
    const double ReducedTime = 0.55;
    static double T(double ms) => Reduced ? ms * ReducedTime : ms;

    public static AnimationTimeline To(IAnimatable o, DependencyProperty p, double to, double ms, IEasingFunction ease = null, double delay = 0, Action done = null)
    {
        ms = T(ms); delay = T(delay);
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Out, BeginTime = TimeSpan.FromMilliseconds(delay) };
        if (done != null) a.Completed += (s, e) => done();
        o.BeginAnimation(p, a);
        return a;
    }

    public static void FromTo(IAnimatable o, DependencyProperty p, double from, double to, double ms, IEasingFunction ease = null, double delay = 0, Action done = null)
    {
        ms = T(ms); delay = T(delay);
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Out, BeginTime = TimeSpan.FromMilliseconds(delay) };
        if (done != null) a.Completed += (s, e) => done();
        o.BeginAnimation(p, a);
    }

    public static void Set(IAnimatable o, DependencyProperty p, double v)
    {
        o.BeginAnimation(p, null);
        ((DependencyObject)o).SetValue(p, v);
    }

    public static void Forever(IAnimatable o, DependencyProperty p, double from, double to, double ms, bool reverse = true, IEasingFunction ease = null, bool still = false)
    {
        Loop(o, p, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        { AutoReverse = reverse, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease ?? InOut },
            // where it rests under Reduce effects: at its start when "still", else half way, a fading pulse at its end (gone)
            TimeSpan.FromMilliseconds(still ? 0 : reverse ? ms / 2 : ms * 0.999), p == RotateTransform.AngleProperty);
    }

    // Every never-ending animation (ring runner, breathing glows, start-screen pulse, spinner) runs through
    // here, so they can all be paused while the launcher can't be seen. A running one makes WPF redraw the
    // whole window every frame, which kept the GPU busy (fans on) with the launcher behind other windows.
    // Under Reduce effects they stand still too (at their Rest frame), except the Busy ones (the spinner).
    sealed record LoopRef(WeakReference<AnimationClock> Clock, TimeSpan? Rest, bool Busy);
    static readonly List<LoopRef> loops = new();
    static bool loopsPaused;

    public static void Loop(IAnimatable o, DependencyProperty p, AnimationTimeline a, TimeSpan? rest = null, bool busy = false)
    {
        var clock = a.CreateClock();
        o.ApplyAnimationClock(p, clock);   // same SnapshotAndReplace as BeginAnimation; BeginAnimation(p, null) still stops it
        if (loops.Count > 256) loops.RemoveAll(w => !w.Clock.TryGetTarget(out _));
        var l = new LoopRef(new WeakReference<AnimationClock>(clock), rest, busy);
        loops.Add(l);
        if (loopsPaused || Reduced) Apply(l, clock);
    }

    static void Apply(LoopRef l, AnimationClock c)
    {
        if (c.Controller == null) return;
        if (loopsPaused || (Reduced && !l.Busy))
        {
            if (Reduced && !l.Busy && l.Rest is TimeSpan r) c.Controller.Seek(r, TimeSeekOrigin.BeginTime);
            c.Controller.Pause();
        }
        else c.Controller.Resume();
    }

    static void ApplyAll()
    {
        loops.RemoveAll(w => !w.Clock.TryGetTarget(out _));
        foreach (var l in loops) if (l.Clock.TryGetTarget(out var c)) Apply(l, c);
    }

    public static void PauseLoops(bool pause)
    {
        if (pause == loopsPaused) return;
        loopsPaused = pause;
        ApplyAll();
    }

    // Turn Reduce effects on or off (MainWindow, from the profile's sound settings).
    public static void SetReduced(bool on)
    {
        if (on == Reduced) return;
        Reduced = on;
        ApplyAll();
        effects.RemoveAll(x => !x.TryGetTarget(out _));
        foreach (var x in effects) if (x.TryGetTarget(out var e)) e.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        Palette.I.Set(Palette.I.Dark, false);   // the gradients plain, or back
    }

    // "Pressed" (A on a game or an options button): a quick squash, a spring back just past full size,
    // a light flash and a small sideways shake. The caller holds input for PressMs so it always finishes.
    public static double PressMs => Reduced ? 170 : 340;
    public static void Press(ScaleTransform s, TranslateTransform shake, UIElement flash, double amp)
    {
        if (Reduced)
        {
            // Reduce effects: one small dip and back, a short flash, no shake.
            var q = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(160) };
            q.KeyFrames.Add(new EasingDoubleKeyFrame(0.97, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50)), Out));
            q.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), InOut));
            s.BeginAnimation(ScaleTransform.ScaleXProperty, q);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, q);
            shake.BeginAnimation(TranslateTransform.XProperty, null);
            if (flash != null)
            {
                var g = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(160) };
                g.KeyFrames.Add(new EasingDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40)), Out));
                g.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), Out));
                flash.BeginAnimation(UIElement.OpacityProperty, g);
            }
            return;
        }
        var sc = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        sc.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60)), Out));
        sc.KeyFrames.Add(new EasingDoubleKeyFrame(1.045, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), Out));
        sc.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), InOut));
        s.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
        var sh = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        foreach (var (ms, k) in new[] { (100, 0.0), (135, 1.0), (180, -0.8), (225, 0.45), (270, -0.2), (320, 0.0) })
            sh.KeyFrames.Add(new EasingDoubleKeyFrame(k * amp, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), InOut));
        shake.BeginAnimation(TranslateTransform.XProperty, sh);
        if (flash != null)
        {
            var f = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(PressMs) };
            f.KeyFrames.Add(new EasingDoubleKeyFrame(0.38, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), Out));
            f.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(PressMs)), Out));
            flash.BeginAnimation(UIElement.OpacityProperty, f);
        }
    }

    // Moving past the end of a row or list: the bump plus a soft "thunk". Holding the direction
    // against the edge repeats the bump but not the sound (at most one every 220 ms).
    static long edgeAt;
    public static void Edge(TranslateTransform t, int dir, bool vertical = false)
    {
        long now = Environment.TickCount64;
        if (now - edgeAt > 220) Sounds.Play("edge");
        edgeAt = now;
        Bump(t, dir, vertical);
    }

    // Refused (a name taken, a field left empty, nothing ticked): the shake plus the muted "error" sound.
    public static void Refuse(TranslateTransform t)
    {
        Sounds.Play("error");
        Bump(t, 1);
    }

    // Small sideways shake: "nothing further this way".
    public static void Bump(TranslateTransform t, int dir, bool vertical = false)
    {
        double far = Reduced ? 4 : 9;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(Reduced ? 160 : 260) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(far * dir, KeyTime.FromPercent(0.3), Out));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-far / 3 * dir, KeyTime.FromPercent(0.65), InOut));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), InOut));
        t.BeginAnimation(vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty, a);
    }

    // A value just changed (a settings row's value): it swells a little and settles, so the eye catches
    // what changed. Render transform only. Under Reduce effects: nothing.
    public static void Pulse(FrameworkElement e, double amount = 0.08)
    {
        if (e == null || Reduced) return;
        if (e.RenderTransform is not ScaleTransform s || s.IsFrozen)
        {
            if (e.RenderTransform != null && e.RenderTransform != Transform.Identity) return;   // has a transform of its own: leave it
            e.RenderTransform = s = new ScaleTransform(1, 1);
            e.RenderTransformOrigin = new Point(e.HorizontalAlignment == HorizontalAlignment.Left ? 0 : e.HorizontalAlignment == HorizontalAlignment.Center ? 0.5 : 1, 0.5);
        }
        var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(240) };
        k.KeyFrames.Add(new EasingDoubleKeyFrame(1 + amount, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), Out));
        k.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), InOut));
        s.BeginAnimation(ScaleTransform.ScaleXProperty, k);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, k);
    }
}

public static class Art
{
    static Palette P => Palette.I;

    // Icon shape (Settings > Look and layout > Icon shape, per profile): "round" (the v8 look: tiles with
    // soft corners, round buttons and pictures), "rounded" (rounded squares) or "square" (near-sharp).
    public static string IconShape = "round";
    public static readonly string[] Shapes = { "round", "rounded", "square" };
    public static string[] ShapeNames => new[] { T("settings.round"), T("settings.rounded_square"), T("settings.square") };
    public static string ShapeName(string shape) => ShapeNames[Math.Max(0, Array.IndexOf(Shapes, shape))];
    // A tile's corner for the corner it has in the round look (game and container tiles, their rings).
    public static double Corner(double round) => IconShape switch { "rounded" => round * 0.5, "square" => round * 0.12, _ => round };
    // The corner of what is a circle in the round look (option buttons, profile pictures), d across.
    public static double DiscRad(double d) => IconShape switch { "rounded" => d * 0.26, "square" => d * 0.07, _ => d / 2 };
    // A "disc" of the current shape, d across (a circle in the round look).
    public static Rectangle Disc(double d, Brush fill = null) => new() { Width = d, Height = d, RadiusX = DiscRad(d), RadiusY = DiscRad(d), Fill = fill };

    // v8 switch-user.svg: white rim, grey disc, white head and shoulders.
    public static FrameworkElement UserCircle(double size)
    {
        var c = new Canvas { Width = 128, Height = 128 };
        c.Children.Add(Place(Disc(126, Brushes.White), 1, 1));
        var disc = Disc(120, new LinearGradientBrush(Palette.Hex("D9DCE2"), Palette.Hex("C3C7CF"), 90));
        c.Children.Add(Place(disc, 4, 4));
        var person = new Canvas { Width = 128, Height = 128, Clip = new RectangleGeometry(new Rect(4, 4, 120, 120), DiscRad(120), DiscRad(120)) };
        person.Children.Add(Place(new Ellipse { Width = 42, Height = 42, Fill = Brushes.White }, 43, 29));
        person.Children.Add(Place(new Ellipse { Width = 80, Height = 76, Fill = Brushes.White }, 24, 80));
        c.Children.Add(person);
        return new Viewbox { Width = size, Height = size, Child = c };
    }

    static UIElement Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }

    // A profile's circle: its picture, or a coloured disc with its first letter. White rim like UserCircle.
    static readonly (string a, string b)[] avatarColors =
    {
        ("FF8A5C", "E8456B"), ("5CC8FF", "3D6BE8"), ("7BE07A", "1FA587"), ("C58BFF", "7B4BE8"),
        ("FFD15C", "F08A24"), ("FF7AC8", "C43DA0"), ("62E3D6", "2B8FB8"), ("B5BDC9", "6C7686"),
    };
    public static int AvatarHues => avatarColors.Length;
    static readonly Dictionary<string, BitmapSource> avatarPics = new();
    public static void ForgetAvatar(string id) { if (id != null) avatarPics.Remove(id); }

    public static FrameworkElement Avatar(Profiles pr, Profile p, double size)
    {
        if (p == null) return UserCircle(size);
        var c = new Canvas { Width = 128, Height = 128 };
        c.Children.Add(Place(Disc(126, Brushes.White), 1, 1));
        BitmapSource pic = null;
        if (p.Picture && !avatarPics.TryGetValue(p.Id, out pic)) { pic = LoadImage(pr.PicturePath(p), 256); avatarPics[p.Id] = pic; }
        var disc = Disc(120);
        if (pic != null) disc.Fill = Fill(pic);
        else
        {
            var (a, b) = avatarColors[((p.Hue % AvatarHues) + AvatarHues) % AvatarHues];
            disc.Fill = new LinearGradientBrush(Palette.Hex(a), Palette.Hex(b), 45);
        }
        c.Children.Add(Place(disc, 4, 4));
        if (pic == null)
        {
            string letter = string.IsNullOrEmpty(p.Name) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(p.Name, 0)).ToUpper();
            var t = new TextBlock { Text = letter, FontSize = 64, FontWeight = FontWeights.SemiBold, FontFamily = Fonts.Display, Foreground = Brushes.White, Width = 128, TextAlignment = TextAlignment.Center };
            c.Children.Add(Place(t, 0, 20));
        }
        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // A picture preview (before a profile exists).
    public static FrameworkElement AvatarPreview(BitmapSource pic, string name, int hue, double size)
    {
        var c = new Canvas { Width = 128, Height = 128 };
        c.Children.Add(Place(Disc(126, Brushes.White), 1, 1));
        var (a, b) = avatarColors[((hue % AvatarHues) + AvatarHues) % AvatarHues];
        c.Children.Add(Place(Disc(120, pic != null ? Fill(pic) : new LinearGradientBrush(Palette.Hex(a), Palette.Hex(b), 45)), 4, 4));
        if (pic == null)
        {
            string letter = string.IsNullOrWhiteSpace(name) ? "" : char.ConvertFromUtf32(char.ConvertToUtf32(name.Trim(), 0)).ToUpper();
            c.Children.Add(Place(new TextBlock { Text = letter, FontSize = 64, FontWeight = FontWeights.SemiBold, FontFamily = Fonts.Display, Foreground = Brushes.White, Width = 128, TextAlignment = TextAlignment.Center }, 0, 20));
        }
        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // 48x48 glyphs from the v8 icon SVGs, filled with the given brush.
    public static FrameworkElement Icon(string kind, double size, Brush ink)
    {
        var c = new Canvas { Width = 48, Height = 48 };
        void Rect(double x, double y, double w, double h, double r, double rot = 0)
        {
            var e = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = ink };
            if (rot != 0) { e.RenderTransform = new RotateTransform(rot, 24 - x, 24 - y); }
            c.Children.Add(Place(e, x, y));
        }
        switch (kind)
        {
            case "layout":
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Rect(6 + 13 * i, 6 + 13 * j, 10, 10, 2.6);
                break;
            case "controller":
                c.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M14 13.5 H34 A9.5 9.5 0 0 1 43.2 20.6 L46 31.6 A4.8 4.8 0 0 1 37.9 36.2 L33 31.5 H15 L10.1 36.2 A4.8 4.8 0 0 1 2 31.6 L4.8 20.6 A9.5 9.5 0 0 1 14 13.5 Z"),
                    Stroke = ink, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round,
                });
                Rect(9.5, 21, 9, 3, 1); Rect(12.5, 18, 3, 9, 1);
                c.Children.Add(Place(new Ellipse { Width = 4.2, Height = 4.2, Fill = ink }, 30.4, 18.4));
                c.Children.Add(Place(new Ellipse { Width = 4.2, Height = 4.2, Fill = ink }, 34.9, 22.9));
                break;
            case "gear":
                for (int k = 0; k < 8; k++) Rect(20.5, 3, 7, 8, 1.5, 45 * k);
                c.Children.Add(Place(new Ellipse { Width = 29, Height = 29, Stroke = ink, StrokeThickness = 7 }, 9.5, 9.5));
                break;
            case "search":
                c.Children.Add(Place(new Ellipse { Width = 25, Height = 25, Stroke = ink, StrokeThickness = 4.5 }, 7, 7));
                c.Children.Add(new Line { X1 = 29, Y1 = 29, X2 = 40, Y2 = 40, Stroke = ink, StrokeThickness = 5.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                break;
            case "folder":
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M4 13 A4 4 0 0 1 8 9 H19 L24 14 H40 A4 4 0 0 1 44 18 V37 A4 4 0 0 1 40 41 H8 A4 4 0 0 1 4 37 Z"), Fill = ink });
                break;
            case "plus":
                Rect(21.5, 9, 5, 30, 2.5); Rect(9, 21.5, 30, 5, 2.5);
                break;
            case "minus":
                c.Children.Add(Place(new Ellipse { Width = 36, Height = 36, Stroke = ink, StrokeThickness = 4 }, 6, 6));
                Rect(14, 21.75, 20, 4.5, 2.2);
                break;
            case "move":   // an arrow out of a box
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M20 8 H10 A4 4 0 0 0 6 12 V38 A4 4 0 0 0 10 42 H36 A4 4 0 0 0 40 38 V28"), Stroke = ink, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M20 28 L40 8 M28 8 H40 V20"), Stroke = ink, StrokeThickness = 4.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                break;
            case "keyboard":
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M7 13 H41 A4 4 0 0 1 45 17 V35 A4 4 0 0 1 41 39 H7 A4 4 0 0 1 3 35 V17 A4 4 0 0 1 7 13 Z"), Stroke = ink, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round });
                for (int i = 0; i < 6; i++) Rect(9 + 5.4 * i, 18.5, 3.4, 3.4, 0.9);
                for (int i = 0; i < 5; i++) Rect(11.5 + 5.4 * i, 24, 3.4, 3.4, 0.9);
                Rect(15, 30, 18, 3.4, 1.2);
                break;
            case "mouse":
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M24 5 A12 12 0 0 1 36 17 V31 A12 12 0 0 1 12 31 V17 A12 12 0 0 1 24 5 Z M12 21 H36"), Stroke = ink, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round });
                Rect(22.5, 10, 3, 6, 1.5);
                break;
            case "trash":
                Rect(7, 10, 34, 4.5, 2.2); Rect(18, 5, 12, 4, 1.8);
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M11 17 H37 L35 40 A4 4 0 0 1 31 43.5 H17 A4 4 0 0 1 13 40 Z"), Stroke = ink, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round });
                Rect(19.5, 22, 3, 15, 1.5); Rect(25.5, 22, 3, 15, 1.5);
                break;
            case "door":   // an open door: the frame, and the door swung out towards you on its left hinge
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M13 42 V9 A3 3 0 0 1 16 6 H34 A3 3 0 0 1 37 9 V42 M6 42 H42"), Stroke = ink, StrokeThickness = 3.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M13 6.5 L28 2.5 A1.5 1.5 0 0 1 30 4 V45 A1.5 1.5 0 0 1 28 46.4 L13 42 Z"), Fill = ink });
                break;
            // Settings rail and the in-game menu.
            case "look":      // a disc, half filled: light and dark
                c.Children.Add(Place(new Ellipse { Width = 34, Height = 34, Stroke = ink, StrokeThickness = 3.5 }, 7, 7));
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M24 9 A15 15 0 0 1 24 39 Z"), Fill = ink });
                break;
            case "sun":       // Brightness: a sun, a disc with eight rays
                c.Children.Add(Place(new Ellipse { Width = 16, Height = 16, Fill = ink }, 16, 16));
                c.Children.Add(Line2("M37.0 24.0 L43.0 24.0 M33.2 33.2 L37.4 37.4 M24.0 37.0 L24.0 43.0 M14.8 33.2 L10.6 37.4 M11.0 24.0 L5.0 24.0 M14.8 14.8 L10.6 10.6 M24.0 11.0 L24.0 5.0 M33.2 14.8 L37.4 10.6", 3.5));
                break;
            case "palette":   // Colours: a painter's palette with three paint spots
                c.Children.Add(Line2("M24 6 C13 6 5 14 5 24 C5 34 13 42 23 42 C27 42 27.5 39 26 36.5 C24.5 34 25.5 31 29 31 H34 C39 31 43 27 43 22 C43 13 34.5 6 24 6 Z", 3.5));
                foreach (var (x, y) in new[] { (12.5, 21.0), (18.5, 12.5), (28.5, 12.0), (35.0, 19.0) })
                    c.Children.Add(Place(new Ellipse { Width = 5.6, Height = 5.6, Fill = ink }, x, y));
                break;
            case "sound":     // a speaker with two waves
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M5 18.5 A2 2 0 0 1 7 16.5 H14 L24 8.5 V39.5 L14 31.5 H7 A2 2 0 0 1 5 29.5 Z"), Fill = ink, Stroke = ink, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round });
                c.Children.Add(Line2("M30.5 17.5 A9 9 0 0 1 30.5 30.5 M35.5 11.5 A17 17 0 0 1 35.5 36.5", 3.5));
                break;
            case "library":   // two books standing, one leaning
                Rect(6, 9, 9, 31, 2.4); Rect(18, 6, 9, 34, 2.4);
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M30.5 12.2 L38.2 10.1 L45.4 37.2 L37.7 39.3 Z"), Fill = ink, Stroke = ink, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
                Rect(4, 41, 40, 3.5, 1.5);
                break;
            case "play":
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M15 9.5 L38.5 24 L15 38.5 Z"), Fill = ink, Stroke = ink, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round });
                break;
            case "save":      // an arrow down into a tray
                c.Children.Add(Line2("M24 6 V28 M15 19.5 L24 28.5 L33 19.5 M8 30 V37 A4 4 0 0 0 12 41 H36 A4 4 0 0 0 40 37 V30", 4));
                break;
            case "load":      // an arrow up out of a tray
                c.Children.Add(Line2("M24 28.5 V6.5 M15 15 L24 6 L33 15 M8 30 V37 A4 4 0 0 0 12 41 H36 A4 4 0 0 0 40 37 V30", 4));
                break;
            case "states":    // two stacked save slots
                c.Children.Add(Line2("M9 14 A3 3 0 0 1 12 11 H36 A3 3 0 0 1 39 14 V20 A3 3 0 0 1 36 23 H12 A3 3 0 0 1 9 20 Z M9 29 A3 3 0 0 1 12 26 H36 A3 3 0 0 1 39 29 V35 A3 3 0 0 1 36 38 H12 A3 3 0 0 1 9 35 Z", 3.2));
                c.Children.Add(Place(new Ellipse { Width = 5, Height = 5, Fill = ink }, 14, 14.5));
                c.Children.Add(Place(new Ellipse { Width = 5, Height = 5, Fill = ink }, 14, 29.5));
                break;
            case "home":
                c.Children.Add(Line2("M5.5 23 L24 7.5 L42.5 23 M11 18.5 V38 A3 3 0 0 0 14 41 H34 A3 3 0 0 0 37 38 V18.5", 3.8));
                Rect(20, 28, 8, 13, 2);
                break;
            case "restart":   // a circle arrow
                c.Children.Add(Line2("M39 24 A15 15 0 1 1 34.6 13.4", 4));
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M37.5 4.5 L38.5 17 L26 16 Z"), Fill = ink, Stroke = ink, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
                break;
            case "power":     // Quit Ionea: the power symbol, a broken ring with a bar
                c.Children.Add(Line2("M15.5 12.5 A16 16 0 1 0 32.5 12.5", 4));
                c.Children.Add(Line2("M24 5 V23", 4.5));
                break;
            case "close":     // a cross in a circle
                c.Children.Add(Place(new Ellipse { Width = 36, Height = 36, Stroke = ink, StrokeThickness = 3.5 }, 6, 6));
                c.Children.Add(Line2("M17.5 17.5 L30.5 30.5 M30.5 17.5 L17.5 30.5", 4));
                break;
            case "tune":      // three sliders
                foreach (var (y, x) in new[] { (12.0, 30.0), (24.0, 15.0), (36.0, 26.0) })
                {
                    Rect(6, y - 1.75, 36, 3.5, 1.75);
                    c.Children.Add(Place(new Ellipse { Width = 11, Height = 11, Fill = ink }, x - 5.5, y - 5.5));
                }
                break;
            case "globe":     // Going online: a globe with a meridian and two latitudes
                c.Children.Add(Line2("M24 6 A18 18 0 1 1 23.99 6 Z", 3.5));
                c.Children.Add(Line2("M24 6 C15 14 15 34 24 42 C33 34 33 14 24 6 Z", 3));
                c.Children.Add(Line2("M6.5 24 L41.5 24", 3));
                c.Children.Add(Line2("M10 15 L38 15 M10 33 L38 33", 2.6));
                break;
            case "gauge":     // Performance overlay: a speed gauge with its needle
                c.Children.Add(Line2("M7 34 A17.5 17.5 0 1 1 41 34", 3.5));
                c.Children.Add(Line2("M24 30 L32.5 17.5", 3.5));
                c.Children.Add(Place(new Ellipse { Width = 7, Height = 7, Fill = ink }, 20.5, 26.5));
                break;
            case "info":      // Game info: an i in a circle
                c.Children.Add(Line2("M24 6 A18 18 0 1 1 23.99 6 Z", 3.5));
                c.Children.Add(Line2("M24 22 L24 34", 4.5));
                c.Children.Add(Place(new Ellipse { Width = 5.6, Height = 5.6, Fill = ink }, 21.2, 12.6));
                break;
            case "picture":   // Change cover: a frame with a hill and a sun
                c.Children.Add(Line2("M8 10 H40 A2 2 0 0 1 42 12 V36 A2 2 0 0 1 40 38 H8 A2 2 0 0 1 6 36 V12 A2 2 0 0 1 8 10 Z", 3.5));
                c.Children.Add(Line2("M9 33 L19 22 L27 30 L32 25 L39 32", 3.2));
                c.Children.Add(Place(new Ellipse { Width = 6, Height = 6, Fill = ink }, 29, 15));
                break;
            // Menu rows: every common row has its own picture.
            case "check":     // Done / OK
                c.Children.Add(Line2("M9 25 L19.5 35.5 L39.5 13", 5));
                break;
            case "back":      // Cancel / Back: an arrow pointing left
                c.Children.Add(Line2("M40 24 H9 M20 12 L8.5 24 L20 36", 4.5));
                break;
            case "chip":      // an emulator: a chip with pins
                c.Children.Add(Line2("M15 12 H33 A3 3 0 0 1 36 15 V33 A3 3 0 0 1 33 36 H15 A3 3 0 0 1 12 33 V15 A3 3 0 0 1 15 12 Z", 3.5));
                Rect(19, 19, 10, 10, 2);
                foreach (double p in new[] { 18.5, 24, 29.5 })
                {
                    c.Children.Add(Line2($"M{p} 5 V10 M{p} 38 V43 M5 {p} H10 M38 {p} H43", 2.8));
                }
                break;
            case "drive":     // storage: a drive with a light
                c.Children.Add(Line2("M6 28 L11 12 A3 3 0 0 1 14 10 H34 A3 3 0 0 1 37 12 L42 28 V36 A3 3 0 0 1 39 39 H9 A3 3 0 0 1 6 36 Z M6 28 H42", 3.5));
                c.Children.Add(Place(new Ellipse { Width = 5, Height = 5, Fill = ink }, 33, 31));
                break;
            case "hidden":    // an eye with a line through it
                c.Children.Add(Line2("M4 24 C11 13 17.5 10 24 10 C30.5 10 37 13 44 24 C37 35 30.5 38 24 38 C17.5 38 11 35 4 24 Z", 3.5));
                c.Children.Add(Place(new Ellipse { Width = 12, Height = 12, Fill = ink }, 18, 18));
                c.Children.Add(Line2("M9 41 L39 7", 4));
                break;
            case "person":
                c.Children.Add(Place(new Ellipse { Width = 17, Height = 17, Stroke = ink, StrokeThickness = 3.5 }, 15.5, 6));
                c.Children.Add(Line2("M8 42 C8 32 15 27 24 27 C33 27 40 32 40 42", 3.5));
                break;
            case "chat":      // a speech bubble (Discord status)
                c.Children.Add(Line2("M10 9 H38 A4 4 0 0 1 42 13 V31 A4 4 0 0 1 38 35 H22 L13 42 V35 H10 A4 4 0 0 1 6 31 V13 A4 4 0 0 1 10 9 Z", 3.5));
                foreach (double x in new[] { 15.5, 22.5, 29.5 }) c.Children.Add(Place(new Ellipse { Width = 4.5, Height = 4.5, Fill = ink }, x, 19.75));
                break;
            case "swap":      // two arrows passing each other
                c.Children.Add(Line2("M8 16 H38 M30 8 L38 16 L30 24 M40 32 H10 M18 24 L10 32 L18 40", 4));
                break;
            case "screen":    // a monitor
                c.Children.Add(Line2("M8 8 H40 A3 3 0 0 1 43 11 V31 A3 3 0 0 1 40 34 H8 A3 3 0 0 1 5 31 V11 A3 3 0 0 1 8 8 Z M24 34 V41 M15 41 H33", 3.5));
                break;
            case "spark":     // something new / the intro: a four-point star and a small one
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M20 5 C21.5 15 24 18 34 20 C24 22 21.5 25 20 35 C18.5 25 16 22 6 20 C16 18 18.5 15 20 5 Z"), Fill = ink });
                c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M36 29 C36.7 33.5 37.5 34.3 42 35 C37.5 35.7 36.7 36.5 36 41 C35.3 36.5 34.5 35.7 30 35 C34.5 34.3 35.3 33.5 36 29 Z"), Fill = ink });
                break;
            case "sort":      // lines getting shorter, with a down arrow
                c.Children.Add(Line2("M6 12 H26 M6 24 H21 M6 36 H15 M36 9 V39 M29 32 L36 39 L43 32", 3.8));
                break;
            case "bell":      // pop-ups
                c.Children.Add(Line2("M12 34 V22 A12 12 0 0 1 36 22 V34 L39 38 H9 Z M20 42 A4.5 4.5 0 0 0 28 42", 3.5));
                break;
            case "key":
                c.Children.Add(Place(new Ellipse { Width = 17, Height = 17, Stroke = ink, StrokeThickness = 3.8 }, 5, 15.5));
                c.Children.Add(Line2("M22 24 H42 M36 24 V31 M42 24 V30", 3.8));
                break;
            case "history":   // a clock with an arrow round it (what went online)
                c.Children.Add(Line2("M9.5 18 A16 16 0 1 1 8 24 M8 11 V18.5 H15.5", 3.5));
                c.Children.Add(Line2("M24 15 V24 L30 28", 3.5));
                break;
            case "file":      // a page with a folded corner
                c.Children.Add(Line2("M12 5 H28 L37 14 V40 A3 3 0 0 1 34 43 H12 A3 3 0 0 1 9 40 V8 A3 3 0 0 1 12 5 Z M28 5 V14 H37", 3.5));
                break;
            case "app":       // a program: a window
                c.Children.Add(Line2("M9 8 H39 A3 3 0 0 1 42 11 V37 A3 3 0 0 1 39 40 H9 A3 3 0 0 1 6 37 V11 A3 3 0 0 1 9 8 Z M6 16 H42", 3.5));
                c.Children.Add(Place(new Ellipse { Width = 3.6, Height = 3.6, Fill = ink }, 10.2, 10.2));
                c.Children.Add(Place(new Ellipse { Width = 3.6, Height = 3.6, Fill = ink }, 15.6, 10.2));
                break;
            case "shield":    // a checked download
                c.Children.Add(Line2("M24 5 L40 11 V22 C40 32 33 39 24 43 C15 39 8 32 8 22 V11 Z M16.5 24 L22 29.5 L32 18.5", 3.5));
                break;
            case "moon":      // a crescent
                c.Children.Add(Line2("M29 7 A17 17 0 1 0 41 31 A13 13 0 1 1 29 7 Z", 3.8));
                break;
            case "flame":     // a flame
                c.Children.Add(Line2("M24 42 C15.5 42 10 36 10 29 C10 21 17 17 18 7 C24 11 27 16 27 22 C29 20 30 18 30 15 C35 19 38 25 38 30 C38 37 32.5 42 24 42 Z", 3.8));
                break;
            case "trophy":    // a cup with handles on a stand
                c.Children.Add(Line2("M15 8 H33 V19 A9 9 0 0 1 15 19 Z M15 12 H8 C8 18 11 22 15.5 22 M33 12 H40 C40 18 37 22 32.5 22 M24 28 V36 M16 40 H32", 3.8));
                break;
            case "heart":
                c.Children.Add(Line2("M24 40 C14 33 6 26 6 17.5 A9.5 9.5 0 0 1 24 13 A9.5 9.5 0 0 1 42 17.5 C42 26 34 33 24 40 Z", 3.8));
                break;
            case "pen":       // rename
                c.Children.Add(Line2("M31 8 L40 17 L18 39 L7 41 L9 30 Z M27 12 L36 21", 3.5));
                break;
            case "chevron":   // a submenu: ›
                c.Children.Add(Line2("M18 10 L32 24 L18 38", 4.5));
                break;
        }
        // A stroked path with round ends and joins.
        System.Windows.Shapes.Path Line2(string d, double t) => new()
        {
            Data = Geometry.Parse(d), Stroke = ink, StrokeThickness = t, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // v8 switch-selector-square.svg rebuilt for a cover of the given size: soft glow, gradient ring, shine.
    // Returns the ring and its glow group (the glow "breathes").
    public static Canvas Ring(double cover, out UIElement glow)
    {
        double u = cover / 297.0, rad = Corner(16.28 * u);
        var c = new Canvas { Width = cover, Height = cover, IsHitTestVisible = false };
        var g = new Canvas();
        double[] op = { 0.06, 0.1, 0.15, 0.22, 0.3 };
        for (int i = 0; i < 5; i++)
        {
            double o = (15 - 2 * i) * u;
            g.Children.Add(Faded(Stroke(c: cover, o, rad + o, 2.6 * u, P.GlowInk), op[i]));
        }
        c.Children.Add(g);
        // The ring's inside edge sits on the cover's edge (centre line 3u out, stroke 6u): no gap between them.
        c.Children.Add(Stroke(cover, 3 * u, rad + 3 * u, 6 * u, Palette.RingBrush()));
        var shine = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(217, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.45) }, 90);
        c.Children.Add(Anim.Effect(Stroke(cover, 3 * u, rad + 3 * u, 2.4 * u, shine)));
        // The light bar that keeps running round the ring, on the same centre line as the ring stroke.
        double o2 = 3 * u, side = cover + 2 * o2, rr = rad + o2 + 3 * u;
        AddRunner(c, new RectangleGeometry(new Rect(-o2, -o2, side, side), rr, rr), 4 * (side - 2 * rr) + 2 * Math.PI * rr, 6 * u, 1700);
        glow = g;
        return c;
    }

    // A bright bar that runs clockwise round a focus ring, in the ring's own colours: white-hot head,
    // then cyan, blue and violet fading out behind it (stacked dashes of growing length, heads aligned).
    // "len" is the path's length, "t" the ring's stroke width; one lap takes lapMs.
    static void AddRunner(Panel parent, Geometry path, double len, double t, double lapMs)
    {
        path.Freeze();
        // The inks are the palette's shared brushes, so a running light changes with the colour preset.
        (double frac, double w, Brush ink, byte a)[] layers =
        {
            (0.30, t * 3.4, P.GlowInk, 60),     // soft halo
            (0.26, t * 1.25, P.InkC, 110),
            (0.17, t * 1.25, P.InkB, 185),
            (0.10, t * 1.3, P.InkA, 240),
            (0.05, t * 0.7, Brushes.White, 255),    // head
        };
        foreach (var (frac, w, ink, a) in layers)
        {
            double dash = frac * len;
            var p = new System.Windows.Shapes.Path
            {
                Data = path, Stroke = ink, Opacity = a / 255.0, StrokeThickness = w,
                StrokeDashArray = new DoubleCollection { dash / w, (len - dash) / w },
                StrokeDashCap = PenLineCap.Round, IsHitTestVisible = false,
            };
            // Dash offset is in stroke widths; the head sits at (dash - offset), so lowering it moves clockwise.
            Anim.Loop(p, Shape.StrokeDashOffsetProperty, new DoubleAnimation(dash / w, (dash - len) / w, TimeSpan.FromMilliseconds(lapMs))
                { RepeatBehavior = RepeatBehavior.Forever }, TimeSpan.Zero);
            parent.Children.Add(Anim.Effect(p));
        }
    }

    // The running light on any focused rounded box (menu rows, the Settings rail, the hold menu, buttons):
    // the same light as the game ring, running on the box's own border line. Lit(el, true) is cheap to
    // repeat; the light follows the box's size and stops when the box leaves the screen.
    static readonly DependencyProperty LitHostProperty = DependencyProperty.RegisterAttached("LitHost", typeof(Canvas), typeof(Art));

    public static void Lit(Border el, bool on)
    {
        var host = (Canvas)el.GetValue(LitHostProperty);
        if (!on) { if (host != null) Unlight(host); return; }
        if (host != null && host.Parent != el.Child)
        {
            // The box's content was replaced since: start over in the new content.
            Unlight(host);
            host = null;
        }
        if (host == null)
        {
            // The light lives in the box's own grid (its content is wrapped in one if it isn't a grid), so it
            // fades, moves and scales with the box.
            if (el.Child is not Grid panel)
            {
                var old = el.Child; el.Child = null;
                panel = new Grid();
                if (old != null) panel.Children.Add(old);
                el.Child = panel;
            }
            host = new Canvas { IsHitTestVisible = false, Width = 0, Height = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumnSpan(host, 64); Grid.SetRowSpan(host, 64);
            Panel.SetZIndex(host, 100);
            panel.Children.Add(host);
            el.SetValue(LitHostProperty, host);
            el.SizeChanged += (_, _) => { if (host.Tag is true) Light(el, host); };
            host.Unloaded += (_, _) => StopRunner(host);
            host.Loaded += (_, _) => { if (host.Tag is true && host.Children.Count == 0) Light(el, host); };
        }
        if (host.Tag is true) return;
        host.Tag = true;
        Light(el, host);
    }

    static void Light(Border el, Canvas host)
    {
        StopRunner(host);
        double bt = Math.Max(el.BorderThickness.Left, 2), wd = el.ActualWidth - bt, ht = el.ActualHeight - bt;
        if (wd <= 0 || ht <= 0) return;   // not laid out yet: SizeChanged lights it
        host.Margin = new Thickness(-(el.Padding.Left + el.BorderThickness.Left - bt / 2), -(el.Padding.Top + el.BorderThickness.Top - bt / 2), 0, 0);
        double r = Math.Clamp(el.CornerRadius.TopLeft - bt / 2, 0, Math.Min(wd, ht) / 2);
        double len = 2 * (wd - 2 * r) + 2 * (ht - 2 * r) + 2 * Math.PI * r;
        // About the game ring's speed (a 300 px tile laps in 1.7 s), within limits for tiny and huge boxes.
        AddRunner(host, new RectangleGeometry(new Rect(0, 0, wd, ht), r, r), len, bt + 0.5, Math.Clamp(len / 0.72, 1300, 3400));
    }

    static void Unlight(Canvas host) { host.Tag = false; StopRunner(host); }

    static void StopRunner(Canvas host)
    {
        foreach (var s in host.Children.OfType<Shape>()) s.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        host.Children.Clear();
    }

    // The tile's focus ring (glow, gradient ring, shine, running light) for any rounded box of w x h.
    // Same pixel sizes as a 297 px tile ring; the glow group "breathes" like the tile's.
    public static Canvas BoxRing(double w, double h, double rad, out UIElement glow)
    {
        var c = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
        var g = new Canvas();
        double[] op = { 0.06, 0.1, 0.15, 0.22, 0.3 };
        for (int i = 0; i < 5; i++)
        {
            double o = 19.5 - 2 * i;
            g.Children.Add(Faded(RectStroke(w, h, o, rad + o, 2.6, P.GlowInk), op[i]));
        }
        c.Children.Add(g);
        c.Children.Add(RectStroke(w, h, 7.5, rad + 7.5, 6, Palette.RingBrush()));
        var shine = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(217, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.45) }, 90);
        c.Children.Add(Anim.Effect(RectStroke(w, h, 7.5, rad + 7.5, 2.4, shine)));
        double o2 = 7.5, rr = rad + o2 + 3, sw = w + 2 * o2, sh = h + 2 * o2;
        AddRunner(c, new RectangleGeometry(new Rect(-o2, -o2, sw, sh), rr, rr), 2 * (sw - 2 * rr) + 2 * (sh - 2 * rr) + 2 * Math.PI * rr, 6, 2600);
        glow = g;
        return c;
    }

    static Shape Faded(Shape s, double opacity) { s.Opacity = opacity; return s; }

    static Rectangle RectStroke(double cw, double ch, double o, double r, double w, Brush b)
    {
        var e = new Rectangle { Width = cw + 2 * o + w, Height = ch + 2 * o + w, RadiusX = r + w / 2, RadiusY = r + w / 2, Stroke = b, StrokeThickness = w };
        Canvas.SetLeft(e, -o - w / 2); Canvas.SetTop(e, -o - w / 2);
        return e;
    }

    // A rounded-rect outline whose stroke is centred "o" units outside the cover edge (SVG-style).
    static Rectangle Stroke(double c, double o, double r, double w, Brush b)
    {
        var e = new Rectangle { Width = c + 2 * o + w, Height = c + 2 * o + w, RadiusX = r + w / 2, RadiusY = r + w / 2, Stroke = b, StrokeThickness = w };
        Canvas.SetLeft(e, -o - w / 2); Canvas.SetTop(e, -o - w / 2);
        return e;
    }

    // A pad face button for the hint corner: a filled disc with its letter cut out in the background colour.
    public static FrameworkElement PadGlyph(string letter, double size)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Fill = P.Text });
        g.Children.Add(new TextBlock
        {
            Text = letter, FontSize = size * 0.58, FontWeight = FontWeights.SemiBold, FontFamily = Fonts.Display, Foreground = P.Bg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, size * 0.04),
        });
        return g;
    }

    // A keyboard key for the hint corner: a rounded outline with the key's name.
    public static FrameworkElement KeyCap(string key, double height)
    {
        var t = new TextBlock
        {
            Text = key, FontSize = height * 0.5, FontWeight = FontWeights.SemiBold, FontFamily = Fonts.Body, Foreground = P.Text,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Height = height, MinWidth = height, CornerRadius = new CornerRadius(height * 0.28), BorderBrush = P.Text, BorderThickness = new Thickness(2),
            Padding = new Thickness(height * 0.26, 0, height * 0.26, 0), Child = t,
        };
    }

    // Round focus ring for the option buttons.
    public static Grid CircleRing(double d, out UIElement glow)
    {
        var gr = new Grid { Width = d + 40, Height = d + 40, IsHitTestVisible = false };
        var gl = new Grid();
        double[] op = { 0.06, 0.1, 0.16, 0.24 };
        double dr = DiscRad(d);   // the ring follows the icon shape: each outline sits "o" outside the disc's edge
        Rectangle Around(double o, double t, Brush b) => new() { Width = d + 2 * o, Height = d + 2 * o, RadiusX = dr + o, RadiusY = dr + o, Stroke = b, StrokeThickness = t };
        for (int i = 0; i < 4; i++)
            gl.Children.Add(Faded(Around(17 - 2 * i, 2.6, P.GlowInk), op[i]));
        gr.Children.Add(gl);
        gr.Children.Add(Around(9, 5, Palette.RingBrush()));
        double o2 = 6.5, side = d + 2 * o2, rr = dr + o2, at = (d + 40) / 2 - side / 2;   // the ring stroke's centre line
        AddRunner(gr, new RectangleGeometry(new Rect(at, at, side, side), rr, rr), 4 * (side - 2 * rr) + 2 * Math.PI * rr, 5, 1300);
        glow = gl;
        return gr;
    }

    static readonly Dictionary<(double, double), BitmapSource> shadows = new();

    // Soft drop shadow for a rounded square of the given size, rendered once and shared.
    // Finish pass: lit from above, as two layers: a wide, faint ambient shadow all round and
    // a tighter key shadow that falls below the tile, so it sits on the screen instead of in a grey halo.
    public static BitmapSource TileShadow(double size, double rad, out double pad)
    {
        pad = Math.Round(size * 0.09);
        if (shadows.TryGetValue((size, rad), out var bmp)) return bmp;
        double p = pad;
        int n = (int)(size + 2 * p);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var ambient = new SolidColorBrush(Color.FromArgb(2, 20, 30, 50)); ambient.Freeze();
            for (int i = 16; i >= 1; i--)
            {
                double k = i * p / 16.0;
                dc.DrawRoundedRectangle(ambient, null, new Rect(p - k, p - k + p * 0.15, size + 2 * k, size + 2 * k), rad + k, rad + k);
            }
            var key = new SolidColorBrush(Color.FromArgb(4, 16, 22, 40)); key.Freeze();
            for (int i = 10; i >= 1; i--)
            {
                double k = i * p * 0.5 / 10.0, s = p * 0.12;   // a little narrower than the tile, so the sides stay clean
                dc.DrawRoundedRectangle(key, null, new Rect(p + s - k, p - k + p * 0.42, size - 2 * s + 2 * k, size - 2 * s + 2 * k), rad + k, rad + k);
            }
        }
        var rtb = new RenderTargetBitmap(n, n, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze();
        shadows[(size, rad)] = rtb;
        return rtb;
    }

    // A soft round falloff as an alpha mask (white, opaque in the middle), with a little noise so a big,
    // faint gradient shows no rings (banding). edge = true: the other way round (clear in the middle,
    // opaque in the corners), for the vignette. Made once per size and shared.
    static readonly Dictionary<(int, int, bool), BitmapSource> masks = new();
    public static BitmapSource SoftMask(int w, int h, bool edge)
    {
        if (masks.TryGetValue((w, h, edge), out var hit)) return hit;
        var px = new byte[w * h * 4];
        var rnd = new Random(7);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = (x + 0.5) / w * 2 - 1, dy = (y + 0.5) / h * 2 - 1, d = Math.Sqrt(dx * dx + dy * dy);
                double a;
                if (edge) { double t = Math.Clamp((d - 0.55) / (1.41 - 0.55), 0, 1); a = t * t * (3 - 2 * t); }
                else { double t = Math.Clamp(d, 0, 1); a = Math.Exp(-3.2 * t * t) * (1 - t * t); }
                double v = a * 255 + (rnd.NextDouble() - rnd.NextDouble()) * 4;   // triangular noise, about +-4 levels
                byte b = (byte)Math.Clamp(Math.Round(v), 0, 255);
                int i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = b; px[i + 3] = b;   // premultiplied white
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        masks[(w, h, edge)] = bmp;
        return bmp;
    }

    // Start screen / first-run background: base gradient with two soft colour glows (v5 SVG).
    public static Canvas StartBackground()
    {
        var c = new Canvas { Width = 1920, Height = 1080, IsHitTestVisible = false };
        c.Children.Add(new Rectangle { Width = 1920, Height = 1080, Fill = new LinearGradientBrush(new GradientStopCollection { P.StartA, P.StartB }, new Point(0, 0), new Point(1, 1)) });
        c.Children.Add(Place(new Ellipse { Width = 1240, Height = 1240, Fill = new RadialGradientBrush(new GradientStopCollection { P.GlowA, P.GlowA0 }) }, 1650 - 620, 160 - 620));
        c.Children.Add(Place(new Ellipse { Width = 1120, Height = 1120, Fill = new RadialGradientBrush(new GradientStopCollection { P.GlowB, P.GlowB0 }) }, 220 - 560, 1000 - 560));
        return c;
    }

    public static BitmapSource LoadImage(string file, int decodeWidth)
    {
        try
        {
            if (file == null || !File.Exists(file)) return null;
            var b = new BitmapImage();
            using (var fs = File.OpenRead(file))
            {
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = decodeWidth;
                b.StreamSource = fs;
                b.EndInit();
            }
            b.Freeze();
            return b;
        }
        catch (Exception e) { Log.Write($"image {file}: {e.Message}"); return null; }
    }

    // A cover for a tile of size pixels: its short side at least that (a wide picture is read wider, so it
    // stays sharp when it fills the tile). 0, or a picture smaller than that: full size.
    public static BitmapSource LoadCover(string file, int size)
    {
        if (size > 0 && file != null && File.Exists(file))
            try
            {
                using var fs = File.OpenRead(file);
                var f = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                if (f.PixelHeight > 0 && f.PixelWidth > f.PixelHeight) size = (int)Math.Ceiling(size * (double)f.PixelWidth / f.PixelHeight);
                if (size >= f.PixelWidth) size = 0;
            }
            catch (Exception caught) { Log.Debug("look: image size", caught); }
        return LoadImage(file, size);
    }

    public static ImageBrush Fill(ImageSource src)
    {
        var b = new ImageBrush(src) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
        return b;
    }

    public static void SavePng(BitmapSource b, string file)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    // An element drawn once into a frozen picture (blur and all), so a costly effect such as a big blur
    // is paid once, not on every frame the element moves or scales.
    public static BitmapSource Bake(FrameworkElement el, int w, int h)
    {
        el.Measure(new Size(w, h)); el.Arrange(new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el); rtb.Freeze();
        return rtb;
    }

    // A picture blurred into a w x h picture (drawn small first, so the blur is cheap and strong).
    public static BitmapSource Frost(ImageSource src, int w, int h, double radius)
    {
        var img = new Image { Source = src, Width = w, Height = h, Stretch = Stretch.Fill, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = radius, KernelType = System.Windows.Media.Effects.KernelType.Gaussian } };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var g = new Grid { Width = w, Height = h, ClipToBounds = true };
        // the blur pulls in transparent edges; the picture drawn a little larger keeps the edges full
        img.Margin = new Thickness(-radius);
        img.Width = w + 2 * radius; img.Height = h + 2 * radius;
        g.Children.Add(img);
        return Bake(g, w, h);
    }

    // A soft shadow under a rounded panel, drawn once: a wide soft one and a closer, darker one.
    public static BitmapSource PanelShadow(double w, double h, double rad, double pad)
    {
        int n = (int)(w + 2 * pad), m = (int)(h + 2 * pad);
        var g = new Grid { Width = n, Height = m };
        // black and about as low as the menu cards' own shadow (blur 60, 12 down), so the glass sits at their height
        g.Children.Add(new Border { CornerRadius = new CornerRadius(rad), Margin = new Thickness(pad + 6, pad + 16, pad + 6, pad - 12), Background = new SolidColorBrush(Color.FromArgb(52, 0, 0, 0)), Effect = new System.Windows.Media.Effects.BlurEffect { Radius = pad * 0.8 } });
        g.Children.Add(new Border { CornerRadius = new CornerRadius(rad), Margin = new Thickness(pad + 4, pad + 6, pad + 4, pad - 4), Background = new SolidColorBrush(Color.FromArgb(26, 0, 0, 0)), Effect = new System.Windows.Media.Effects.BlurEffect { Radius = pad * 0.3 } });
        return Bake(g, n, m);
    }

    public static BitmapSource Render(int w, int h, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze();
        return rtb;
    }
}

// Own synthesised navigation sounds (v4), embedded. Played through SoundMixer (Sound.cs): one open
// stream that the clips are mixed into, so pressing buttons quickly never reopens the audio device.
public static class Sounds
{
    public static bool On = true;
    public static bool Quiet;   // mouse hover with hover sounds turned off: no scroll sound
    static readonly Dictionary<string, short[]> clips = new();

    // The sound themes (Settings > Sound > Sound theme, per profile). Both are made by tools\make_ui_sounds.py
    // from the same synth parts: Ionea = the wooden ticks and bell pops, Soft = a fourth lower, rounder.
    public static readonly (string Id, string Name)[] Themes = { ("ionea", "Ionea"), ("soft", "Soft") };
    public static string Theme => Prefs?.Invoke()?.Theme == "soft" ? "soft" : "ionea";

    // The groups the Sound settings show, each with its own level, under one of two kinds:
    // Navigation (moving, choosing, menus) and Actions (things happening). Id, label, kind, the clips in it.
    public sealed record Group(string Id, string LabelId, string Kind, string[] Clips) { public string Label => T(LabelId); }
    public static readonly Group[] Groups =
    {
        new("move", "settings.moving_and_typing", "nav", new[] { "scroll", "edge", "type", "tick" }),
        new("select", "settings.select_back_and_switches", "nav", new[] { "select", "back", "toggleon", "toggleoff" }),
        new("menu", "settings.menus_and_containers", "nav", new[] { "menuopen", "unfold", "fold" }),
        new("done", "settings.done_and_added", "act", new[] { "favorite" }),
        new("alert", "settings.pop_ups_and_errors", "act", new[] { "toast", "error" }),
        new("start", "settings.starting_and_ending_a_game", "act", new[] { "press", "launch", "gameclose" }),
        new("screen", "settings.start_screen_layout_and_profiles", "act", new[] { "systembrowse", "quicksysselect", "profile" }),
    };
    public sealed record Kind(string Id, string LabelId, string Preview) { public string Label => T(LabelId); }
    public static readonly Kind[] Kinds = { new("nav", "settings.navigation", "menuopen"), new("act", "settings.actions", "favorite") };
    public static Group GroupOf(string clip) => Groups.FirstOrDefault(g => g.Clips.Contains(clip));

    // The active profile's levels (MainWindow sets this). Null: everything at full.
    public static Func<SoundPrefs> Prefs;
    // Volume as heard: the 0-100 steps are squared so each step sounds about as big as the last.
    // Master x kind (Navigation / Actions) x group.
    public static double Gain(string clip)
    {
        var p = Prefs?.Invoke();
        if (p == null) return 1;
        var g = GroupOf(clip);
        double v = Math.Clamp(p.Volume, 0, 100) / 100.0, gr = g != null ? p.Group(g.Id) / 100.0 : 1, k = g != null ? p.Kind(g.Kind) / 100.0 : 1;
        return v * v * gr * gr * k * k;
    }
    public static string Last;    // for tests: the last clip played, its gain and pitch
    public static double LastGain, LastPitch = 1;
    public static readonly List<string> Played = new();   // for tests: the clips played, in order (the last 200 or more)

    // Natural variation: moving, typing, value ticks and edge bumps are heard many times in a
    // row. Each play takes one of five slightly re-pitched copies (never the same one twice running) and a
    // touch of level variation, so fast scrolling sounds like hands on a real thing, not a machine gun.
    // A burst also gets gently quieter (the "heat" of recent plays, down to 55 %), and one clip plays at
    // most once per MinGapMs (the rest are dropped, not queued).
    static readonly HashSet<string> Varied = new() { "scroll", "edge", "type", "tick" };
    static readonly double[] Pitches = { 0.972, 0.986, 1.0, 1.014, 1.028 };
    const double MinGapMs = 42, HeatMs = 180;
    static readonly Random rnd = new();
    static readonly Dictionary<string, (long at, double heat, int pick)> recent = new();
    public static int Dropped;    // for tests: plays skipped by the rate limit
    static long lastAt;
    // How long since any launcher sound started (a toast that follows an action's own sound stays quiet).
    public static double SinceLastMs => lastAt == 0 ? 1e9 : Environment.TickCount64 - lastAt;

    public static void Play(string name) => Play(name, 0);

    // pitch 0: as recorded (varied for the Varied clips); otherwise that fixed pitch (Tick).
    static void Play(string name, double pitch)
    {
        if (!On || (Quiet && name == "scroll")) return;
        double gain = Gain(name);
        if (gain <= 0) return;
        try
        {
            if (Varied.Contains(name))
            {
                long now = Environment.TickCount64;
                recent.TryGetValue(name, out var r);
                double dt = r.at == 0 ? 1e9 : now - r.at;
                if (dt < MinGapMs) { Dropped++; return; }
                double heat = r.heat * Math.Exp(-dt / HeatMs) + 1;
                int pick = r.pick;
                if (pitch == 0) { pick = rnd.Next(Pitches.Length - 1); if (pick >= r.pick) pick++; pitch = Pitches[pick]; }
                recent[name] = (now, heat, pick);
                gain *= Math.Max(0.55, 1 / (1 + 0.14 * (heat - 1))) * (0.86 + 0.14 * rnd.NextDouble());
            }
            else if (pitch == 0) pitch = 1;
            Last = name; LastGain = gain; LastPitch = pitch; lastAt = Environment.TickCount64;
            lock (Played) { Played.Add(name); if (Played.Count > 400) Played.RemoveRange(0, 200); }
            var pcm = Clip(name, pitch);
            if (pcm != null) SoundMixer.Play(name, pcm, gain);
        }
        catch (Exception e) { Log.Write("ui sound " + name + ": " + e.Message); }
    }

    // A value step (a slider's level, the cover zoom): a light tick whose pitch rises with the value
    // (0..1 spans about a fifth in 11 steps), so the ear hears where the value is.
    public static void Tick(double frac)
    {
        int step = (int)Math.Round(Math.Clamp(frac, 0, 1) * 10);
        Play("tick", Math.Pow(2, (step - 5) * 0.7 / 12));
    }

    // Typing into any text box of the launcher (names, search, keys): the soft "type" tick, varied like the
    // moves. One class handler for every TextBox (MainWindow calls this once); text set by the code itself,
    // with no keyboard focus on the box, stays quiet.
    public static void HookTyping() => EventManager.RegisterClassHandler(typeof(TextBox), TextBoxBase.TextChangedEvent,
        new TextChangedEventHandler((s, e) => { if (s is TextBox t && t.IsKeyboardFocusWithin) Play("type"); }));

    // A two-way setting turned on or off.
    public static void Toggle(bool on) => Play(on ? "toggleon" : "toggleoff");

    static short[] Clip(string name, double pitch)
    {
        string theme = Theme, key = theme + "/" + name;
        short[] pcm;
        lock (clips)
        {
            if (!clips.TryGetValue(key, out pcm))
            {
                pcm = theme == "ionea" ? Load(null, name) : Load(theme, name) ?? Load(null, name);
                clips[key] = pcm;
            }
            if (pcm == null || Math.Abs(pitch - 1) < 1e-4) return pcm;
            string pk = key + "@" + pitch.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            if (clips.TryGetValue(pk, out var shifted)) return shifted;
            return clips[pk] = Repitch(pcm, pitch);
        }
    }

    static short[] Load(string theme, string name)
    {
        using var s = typeof(Sounds).Assembly.GetManifestResourceStream("sounds." + (theme == null ? "" : theme + ".") + name);
        if (s == null) return null;
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        var pcm = SoundMixer.Decode(bytes);
        if (pcm == null) Log.Write("ui sound: " + name + ".wav is not 16-bit 44.1 kHz PCM");
        return pcm;
    }

    // Re-pitch by resampling (stereo, linear between samples: tiny shifts of short clips).
    static short[] Repitch(short[] src, double pitch)
    {
        int frames = src.Length / 2, n = Math.Max(1, (int)(frames / pitch));
        var dst = new short[n * 2];
        for (int i = 0; i < n; i++)
        {
            double at = i * pitch;
            int a = Math.Min(frames - 1, (int)at), b = Math.Min(frames - 1, a + 1);
            double f = at - a;
            for (int c = 0; c < 2; c++) dst[i * 2 + c] = (short)Math.Round(src[a * 2 + c] * (1 - f) + src[b * 2 + c] * f);
        }
        return dst;
    }

    // Decode every clip of the theme up front (off the UI thread), so the first press of each never waits on it;
    // the varied ones get their re-pitched copies made too.
    public static void Preload() => System.Threading.Tasks.Task.Run(() =>
    {
        foreach (var g in Groups)
            foreach (var c in g.Clips)
                try
                {
                    Clip(c, 1);
                    if (Varied.Contains(c)) foreach (var p in Pitches) Clip(c, p);
                }
                catch (Exception e) { Log.Write("ui sound " + c + ": " + e.Message); }
    });
}
