#nullable disable
// Outro: the intro in reverse, short (~0.5 s). When the launcher closes by any normal route (Quit, Alt+F4,
// the window's close button), the screen dims into the start screen's background (light or dark, the colour
// preset's glows), her art and the IONEA title come up on it, then she dissolves upward and her eyes go out
// last, and the window closes. UI sound fades out with it (SoundMixer.FadeOut), so nothing clicks at the end.
// With a game open in the Home menu, MainWindow.BeginQuit first closes it behind its closing screen, then
// this plays over that screen. Every press while it plays is eaten: nothing cancels the close.
// Windows shutdown / logoff skips it. Settings > Look and layout > Outro when closing.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ionea;

public sealed class OutroScreen : IScreen
{
    public const double Ms = 500;                 // the whole outro
    const double InMs = 170, OutAt = 210, OutMs = 230, EyesOutAt = 330, EyesOutMs = 150;
    const double Size = 600;                      // her art on the 1920x1080 stage, where the intro has it

    readonly MainWindow w;
    readonly Grid root = new() { Width = 1920, Height = 1080, Background = Brushes.Transparent };   // catches the mouse
    bool shown;

    public static bool Playing { get; private set; }   // test scripts: "expect outro"

    public OutroScreen(MainWindow win) { w = win; }

    // From the moment the close is asked: in front, eating every press (a game may close first).
    public void Hold()
    {
        w.LaunchLayer.Children.Add(root);   // above the home screen, cards and any closing screen already up
        w.Push(this);
        w.Mouse?.Hide();
    }

    // The visible part, then done (the window closes).
    public void Play(Action done)
    {
        if (shown) return;
        shown = true; Playing = true;
        // over a closing screen that came up after Hold()
        w.LaunchLayer.Children.Remove(root); w.LaunchLayer.Children.Add(root);
        bool dark = w.P.Dark;
        Color glow = dark ? Palette.Cyan : Palette.Blue;

        var bg = Art.StartBackground(); bg.Opacity = 0;
        root.Children.Add(bg);
        BitmapSource art = Pic("ionea-art-1024.png"), eyes = Pic("ionea-eyes-1024.png");

        var sq = new Grid { Width = Size, Height = Size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 150, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5), Opacity = 0, IsHitTestVisible = false };
        var her = new Grid { Width = Size, Height = Size };
        if (art != null)
        {
            var pic = new Image { Source = art, Width = Size, Height = Size };
            RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);
            her.Children.Add(pic);
        }
        sq.Children.Add(her);
        var eyeLayer = new Grid { Width = Size, Height = Size, IsHitTestVisible = false };
        if (eyes != null)
        {
            var g = new Rectangle { Width = Size, Height = Size, Fill = new SolidColorBrush(glow), Opacity = dark ? 0.7 : 0.45, OpacityMask = new ImageBrush(eyes) { Stretch = Stretch.Fill }, Effect = new BlurEffect { Radius = 16 } };
            g.CacheMode = new BitmapCache();   // blur once, not every frame
            eyeLayer.Children.Add(g);
            eyeLayer.Children.Add(new Image { Source = eyes, Width = Size, Height = Size });
        }
        sq.Children.Add(eyeLayer);
        var scale = new ScaleTransform(1.03, 1.03); var drift = new TranslateTransform();
        sq.RenderTransform = new TransformGroup { Children = { scale, drift } };
        root.Children.Add(sq);

        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18, 150 + Size + 40, 0, 0), IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = glow, BlurRadius = 22, ShadowDepth = 0, Opacity = dark ? 0.6 : 0.18 },
        };
        foreach (char ch in "IONEA")
            name.Children.Add(new TextBlock { Text = ch.ToString(), FontSize = 50, FontFamily = Fonts.Display, FontWeight = FontWeights.Light, Foreground = dark ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF4, 0xFF)) : w.P.Text, Margin = new Thickness(0, 0, 18, 0) });
        var nameDrift = new TranslateTransform(); name.RenderTransform = nameDrift;
        var nameIn = new Grid { Opacity = 0, IsHitTestVisible = false };   // fades in; the name inside fades out (one animation per property)
        nameIn.Children.Add(name);
        root.Children.Add(nameIn);

        Log.Write("outro: start");
        SoundMixer.FadeOut((int)(Ms - 80));   // silent a little before the window goes

        // 1) the screen dims into her background; she and the name settle in
        Anim.To(bg, UIElement.OpacityProperty, 1, InMs, Anim.Out);
        Anim.To(w.ToastLayer, UIElement.OpacityProperty, 0, InMs, Anim.Out);   // toasts sit above every layer: they go too
        Anim.To(sq, UIElement.OpacityProperty, 1, InMs, Anim.Out);
        Anim.To(nameIn, UIElement.OpacityProperty, 1, InMs, Anim.Out);
        Anim.To(scale, ScaleTransform.ScaleXProperty, 1, OutAt + OutMs, Anim.Out);
        Anim.To(scale, ScaleTransform.ScaleYProperty, 1, OutAt + OutMs, Anim.Out);
        // 2) she and the name drift up and dissolve; her eyes stay lit a moment longer, then go out
        Anim.To(her, UIElement.OpacityProperty, 0, OutMs, Anim.InOut, OutAt);
        Anim.To(drift, TranslateTransform.YProperty, -14, OutMs + 60, Anim.InOut, OutAt);
        Anim.To(name, UIElement.OpacityProperty, 0, OutMs - 40, Anim.InOut, OutAt);
        Anim.To(nameDrift, TranslateTransform.YProperty, -14, OutMs, Anim.InOut, OutAt);
        Anim.To(eyeLayer, UIElement.OpacityProperty, 0, EyesOutMs, Anim.InOut, EyesOutAt);
        Ui.After(Ms, () => done?.Invoke());
    }

    static BitmapSource Pic(string file)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri("pack://application:,,,/assets/icon/" + file);
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception e) { Log.Write("outro: " + e.Message); return null; }
    }

    // Nothing stops it.
    public void OnAct(Act a) { }
    public bool TakeKey(Key k) => true;
    public bool TakeMouse(MouseButton b) => true;
    public bool OnMouse(MouseAct m, Point p) => true;
}
