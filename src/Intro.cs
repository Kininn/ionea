#nullable disable
// Intro: the launcher "switches on" through Ionea's eyes. Empty screen (the start screen's background,
// light or dark with the theme); her eyes flicker on with a soft glow; the light then opens out from her eye line like a screen powering on (two scan
// lines running up and down, scanlines, a short colour split) and reveals her; the name fades in under
// her; then everything hands over to the start screen. She never moves - only light changes.
//
// No sound for now (intro.wav and tools\make_intro_sound.py are kept for later).
// It also covers the audio wake-up: Program.Main calls SoundMixer.Warm before the window exists, so
// wireless headphones are streaming (silence) by the time the chime lands, and the first clicks on the
// start screen are heard. Any press, click or Esc skips it. Settings > Look and layout > Intro.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ionea;

public sealed class IntroScreen : IScreen
{
    // Timeline (ms from the first frame).
    public const double EyesOn = 280, Reveal = 1150, RevealMs = 950, OutAt = 2750, OutMs = 650;
    const double Feather = 0.16;                  // the soft edge of the opening light, as a fraction of the art's height
    const double Size = 600;                      // the art's size on the 1920x1080 stage
    const double EyeLine = 830.0 / 2048;          // her eye line, as a fraction of the art's height
    static readonly Point NearEye = new(1262.0 / 2048, 822.0 / 2048);
    Color Cyan;   // the glow: cyan on dark, a deeper blue on light (cyan washes out on a light background)

    readonly MainWindow w;
    readonly Action next;
    public readonly Grid Root = new() { Width = 1920, Height = 1080, RenderTransformOrigin = new Point(0.5, 0.5) };
    bool finished;
    UIElement[] content;   // her and the name: they leave first at the end

    public static bool Busy { get; private set; }

    public IntroScreen(MainWindow win, Action then)
    {
        w = win; next = then;
    }

    static BitmapSource Pic(string file)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.UriSource = new Uri("pack://application:,,,/assets/icon/" + file);
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.EndInit();
        b.Freeze();
        return b;
    }

    // A flat colour in the exact shape of a picture (its alpha): the glows, the colour split.
    static Rectangle Shape(ImageSource pic, Color c, double opacity = 1) => new()
    {
        Width = Size, Height = Size, Fill = new SolidColorBrush(c), Opacity = opacity, IsHitTestVisible = false,
        OpacityMask = new ImageBrush(pic) { Stretch = Stretch.Fill },
    };

    public void Play()
    {
        Busy = true;
        bool dark = w.P.Dark;
        Cyan = dark ? Palette.Cyan : Palette.Blue;
        Root.Children.Add(Art.StartBackground());   // the same background the start screen has, in the current theme
        BitmapSource art, eyes;
        try { art = Pic("ionea-art-1024.png"); eyes = Pic("ionea-eyes-1024.png"); }
        catch (Exception e) { Log.Write("intro: " + e.Message); Done(); return; }

        // Everything sits on one square centred a little above the middle; the name goes under it.
        var sq = new Grid { Width = Size, Height = Size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 150, 0, 0), ClipToBounds = false };
        Root.Children.Add(sq);

        // ---- the reveal: her picture, shown through a band that opens from the eye line ----
        var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var s0 = new GradientStop(Colors.Transparent, EyeLine); var s1 = new GradientStop(Colors.Black, EyeLine);
        var s2 = new GradientStop(Colors.Black, EyeLine); var s3 = new GradientStop(Colors.Transparent, EyeLine);
        band.GradientStops = new GradientStopCollection { s0, s1, s2, s3 };
        var pic = new Image { Source = art, Width = Size, Height = Size, Opacity = 0 };
        RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);

        // as she switches on she comes into focus, and a wash of the eyes' light over her fades away
        var focus = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Quality };
        pic.Effect = focus;
        var wash = Shape(art, dark ? Cyan : Colors.White, 0);
        // the picture and its light wash show only inside the opening band
        var screen = new Grid { Width = Size, Height = Size, OpacityMask = band };
        screen.Children.Add(pic); screen.Children.Add(wash);
        sq.Children.Add(screen);
        // a slow, barely-there push-in over the whole intro (she stays still; the "camera" drifts)
        var push = new ScaleTransform(1, 1); sq.RenderTransformOrigin = new Point(0.5, 0.5); sq.RenderTransform = push;

        // before the reveal, the eyes' light spills faintly onto her face around them
        var spill = new Image
        {
            Source = art, Width = Size, Height = Size, Opacity = 0, IsHitTestVisible = false,
            OpacityMask = new RadialGradientBrush(new GradientStopCollection { new GradientStop(Colors.Black, 0), new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.45), new GradientStop(Colors.Transparent, 1) })
            { Center = NearEye, GradientOrigin = NearEye, RadiusX = 0.3, RadiusY = 0.22 },
        };
        sq.Children.Add(spill);

        // ---- the eyes: wide cyan glow, tight glow, the eye pixels, a white-hot core ----
        var glowWide = Shape(eyes, Cyan, 0); glowWide.Effect = new BlurEffect { Radius = 90, RenderingBias = RenderingBias.Performance };
        glowWide.RenderTransformOrigin = NearEye; glowWide.RenderTransform = new ScaleTransform(1.35, 1.35);
        var glowTight = Shape(eyes, Cyan, 0); glowTight.Effect = new BlurEffect { Radius = 16 };
        glowWide.CacheMode = new BitmapCache(); glowTight.CacheMode = new BitmapCache();   // blur once, not every frame
        var eyePix = new Image { Source = eyes, Width = Size, Height = Size, Opacity = 0 };
        var core = Shape(eyes, Colors.White, 0); core.Effect = new BlurEffect { Radius = 3 };
        var eyeLayer = new Grid { Width = Size, Height = Size, IsHitTestVisible = false };
        eyeLayer.Children.Add(glowWide); eyeLayer.Children.Add(glowTight); eyeLayer.Children.Add(eyePix); eyeLayer.Children.Add(core);
        sq.Children.Add(eyeLayer);

        // ---- faint scanlines on her (only her shape, only inside the opening band) ----

        // ---- the name ----
        // spaced capitals that flicker on together with her eyes, in step with them
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18, 150 + Size + 40, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5), IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 22, ShadowDepth = 0, Opacity = dark ? 0.6 : 0.18 },
        };
        foreach (char ch in "IONEA")
            name.Children.Add(new TextBlock { Text = ch.ToString(), FontSize = 50, FontFamily = Fonts.Display, FontWeight = FontWeights.Light, Foreground = dark ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF4, 0xFF)) : w.P.Text, Margin = new Thickness(0, 0, 18, 0), Opacity = 0 });
        Root.Children.Add(name);
        content = new UIElement[] { sq, name };

        w.LaunchLayer.Children.Add(Root);
        w.Push(this);
        Log.Write("intro: start");

        // 1) the eyes flicker on, settle bright, then calm to a lit glow once she's revealed
        // A simple flicker, like a light that isn't sure yet: two quick blinks, a pause, a stutter, then on.
        var f = new (double t, double v)[] { (0, 0), (40, 1), (90, 0), (150, 0), (185, 0.8), (230, 0), (420, 0), (450, 1), (490, 0.3), (520, 1), (560, 0.55), (600, 1) };
        (double, double)[] Scaled(double k, params (double t, double v)[] tail)
        {
            var list = new System.Collections.Generic.List<(double, double)>();
            foreach (var (t, v) in f) list.Add((t, v * k));
            list.AddRange(tail);
            return list.ToArray();
        }
        double settle = Reveal - EyesOn + RevealMs + 400;
        Keys(eyePix, UIElement.OpacityProperty, EyesOn, Scaled(1));
        Keys(core, UIElement.OpacityProperty, EyesOn, Scaled(dark ? 0.7 : 0.5, (900, dark ? 0.5 : 0.35), (Reveal - EyesOn + RevealMs, 0)));
        Keys(glowTight, UIElement.OpacityProperty, EyesOn, Scaled(0.9, (settle, 0.4)));
        Keys(glowWide, UIElement.OpacityProperty, EyesOn, Scaled(dark ? 0.85 : 0.6, (settle, dark ? 0.35 : 0.25)));
        Keys(spill, UIElement.OpacityProperty, EyesOn, (0, 0), (600, 0), (900, dark ? 0.14 : 0), (Reveal - EyesOn, dark ? 0.2 : 0), (Reveal - EyesOn + RevealMs, 0));

        // 2) the reveal: the band opens to the top and bottom edges, soft edges, no lines
        pic.Opacity = 1;
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut }; ease.Freeze();   // quick to open, long gentle settle
        // the soft-edged light opens from her eyes: the solid part to the edges, the feather just past them
        Grow(s1, EyeLine, 0, ease); Grow(s0, EyeLine, -Feather, ease);
        Grow(s2, EyeLine, 1, ease); Grow(s3, EyeLine, 1 + Feather, ease);
        Anim.FromTo(focus, BlurEffect.RadiusProperty, 10, 0, RevealMs + 250, Anim.Out, Reveal, () => pic.Effect = null);
        Keys(wash, UIElement.OpacityProperty, Reveal, (0, dark ? 0.35 : 0.5), (RevealMs + 300, 0));
        var drift = new SineEase { EasingMode = EasingMode.EaseInOut }; drift.Freeze();
        Anim.FromTo(push, ScaleTransform.ScaleXProperty, 1, 1.025, OutAt + OutMs, drift);
        Anim.FromTo(push, ScaleTransform.ScaleYProperty, 1, 1.025, OutAt + OutMs, drift);

        // 3) the name
        for (int i = 0; i < name.Children.Count; i++)
        {
            var letter = (UIElement)name.Children[i];
            Keys(letter, UIElement.OpacityProperty, EyesOn, f);   // the eyes' flicker, at the same moment
        }

        // 4) hand over
        Ui.After(OutAt, Finish);
    }

    void Grow(GradientStop s, double from, double to, IEasingFunction ease) =>
        s.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(RevealMs)) { BeginTime = TimeSpan.FromMilliseconds(Reveal), EasingFunction = ease });

    // Key frames as (ms after start, value) pairs; linear between them.
    static void Keys(IAnimatable o, DependencyProperty p, double start, params (double t, double v)[] keys)
    {
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(start), FillBehavior = FillBehavior.HoldEnd };
        double prev = 0;
        foreach (var (t, v) in keys)
        {
            var at = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t));
            // the flicker's quick steps stay sharp; anything slower glides (eased in and out)
            a.KeyFrames.Add(t - prev > 200 ? new EasingDoubleKeyFrame(v, at, Anim.InOut) : new LinearDoubleKeyFrame(v, at));
            prev = t;
        }
        o.BeginAnimation(p, a);
    }

    void Finish() => Finish(OutMs);
    void Finish(double ms)
    {
        if (finished) return;
        finished = true;
        Log.Write("intro: done");
        // The next screen is built underneath first, then the intro lifts off it.
        w.Pop(this);
        Busy = false;
        // She and the name drift up and dissolve first; the start screen rises in under them a beat later,
        // and only then does the intro's background (the same picture) fade, so there is no visible seam.
        foreach (var c in content ?? Array.Empty<UIElement>())
        {
            var t = new TranslateTransform();
            c.RenderTransform = c.RenderTransform is Transform old && !old.Value.IsIdentity ? new TransformGroup { Children = { old, t } } : t;
            Anim.To(t, TranslateTransform.YProperty, -18, ms, Anim.InOut);
            Anim.To(c, UIElement.OpacityProperty, 0, ms * 0.8, Anim.InOut);
        }
        Ui.After(ms * 0.35, () =>
        {
            next?.Invoke();
            Anim.To(Root, UIElement.OpacityProperty, 0, 520, Anim.InOut, ms * 0.3, Done);
        });
    }

    void Done()
    {
        Busy = false;
        w.LaunchLayer.Children.Remove(Root);
        if (!finished) { finished = true; w.Pop(this); next?.Invoke(); }
    }

    // Any press skips it.
    public void OnAct(Act a) { if (a is Act.Accept or Act.Back) Finish(260); }
    public bool OnMouse(MouseAct m, Point p) { if (m == MouseAct.Down) { Finish(260); return true; } return false; }
}
