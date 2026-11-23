#nullable disable
// Start screen: who's playing (top left), big clock, date, "press to continue" pulse.
// The first start makes a profile first (ProfileScreens.cs).
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static Ionea.Loc;

namespace Ionea;

public sealed class StartScreen : IScreen
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    readonly MainWindow w;
    public readonly Grid Root = new() { RenderTransformOrigin = new Point(0.5, 0.5) };
    readonly ScaleTransform scale = new();
    readonly TextBlock time, date, name;
    readonly Grid avatarHost = new() { Width = 64, Height = 64 };
    readonly Grid power = new() { Width = 76, Height = 76 };   // top right: Quit Ionea without going through Settings
    readonly Ellipse powerRing;

    public StartScreen(MainWindow win)
    {
        w = win;
        w.StartScr = this;
        var P = w.P;
        Root.RenderTransform = scale;
        Root.Children.Add(Art.StartBackground());
        var c = new Canvas();
        Root.Children.Add(c);

        Canvas.SetLeft(avatarHost, 64); Canvas.SetTop(avatarHost, 44); c.Children.Add(avatarHost);
        name = Fonts.Text("", 32, P.Text, FontWeights.Medium); Canvas.SetLeft(name, 146); Canvas.SetTop(name, 54); name.MaxWidth = 900; c.Children.Add(name);
        RefreshProfile();

        power.Children.Add(new Ellipse { Fill = P.ButtonFill });
        var pic = Art.Icon("power", 36, P.IconInk); pic.HorizontalAlignment = HorizontalAlignment.Center; pic.VerticalAlignment = VerticalAlignment.Center;
        power.Children.Add(pic);
        powerRing = new Ellipse { Stroke = P.Accent, StrokeThickness = 3, Opacity = 0 };
        power.Children.Add(powerRing);
        Canvas.SetLeft(power, 1920 - 64 - 76); Canvas.SetTop(power, 38); c.Children.Add(power);

        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 250, 0, 0) };
        time = Fonts.Text("", 230, P.Text, FontWeights.Light, Fonts.Display); time.HorizontalAlignment = HorizontalAlignment.Center;
        date = Fonts.Text("", 46, P.Text2, FontWeights.Light, Fonts.Display); date.HorizontalAlignment = HorizontalAlignment.Center; date.Margin = new Thickness(0, -14, 0, 0);
        col.Children.Add(time); col.Children.Add(date);
        Root.Children.Add(col);

        // "Press to continue": accent disc with a play mark, a slow breathing scale and a ring that pulses out.
        var cue = new Grid { Width = 200, Height = 200, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 690, 0, 0) };
        var pulse = new Ellipse { Width = 84, Height = 84, Stroke = P.Accent, StrokeThickness = 3, RenderTransformOrigin = new Point(0.5, 0.5) };
        var ps = new ScaleTransform(); pulse.RenderTransform = ps;
        Anim.Forever(ps, ScaleTransform.ScaleXProperty, 1, 1.75, 1700, false, Anim.Out);
        Anim.Forever(ps, ScaleTransform.ScaleYProperty, 1, 1.75, 1700, false, Anim.Out);
        Anim.Forever(Anim.Effect(pulse), UIElement.OpacityProperty, 0.75, 0, 1700, false, Anim.Out);   // not shown under Reduce effects
        var halo = new Ellipse { Width = 98, Height = 98, Stroke = P.Accent, StrokeThickness = 2, Opacity = 0.35 };
        var disc = new Grid { Width = 84, Height = 84, RenderTransformOrigin = new Point(0.5, 0.5) };
        disc.Children.Add(new Ellipse { Fill = P.Accent });
        disc.Children.Add(new Path { Data = Geometry.Parse("M0,0 L22,13 L0,26 Z"), Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) });
        var ds = new ScaleTransform(); disc.RenderTransform = ds;
        Anim.Forever(ds, ScaleTransform.ScaleXProperty, 1, 1.06, 850, still: true);   // full size, still, under Reduce effects
        Anim.Forever(ds, ScaleTransform.ScaleYProperty, 1, 1.06, 850, still: true);
        cue.Children.Add(pulse); cue.Children.Add(halo); cue.Children.Add(disc);
        Root.Children.Add(cue);
        var press = Fonts.Text(T("home.press_a_or_enter"), 32, P.Text2, FontWeights.Light, Fonts.Display);
        press.HorizontalAlignment = HorizontalAlignment.Center; press.VerticalAlignment = VerticalAlignment.Top; press.Margin = new Thickness(0, 900, 0, 0);
        Root.Children.Add(press);
        // the version, small in the bottom right corner (Updater.cs)
        var ver = Fonts.Text(AppVersion.Short, 22, P.Text2);
        ver.HorizontalAlignment = HorizontalAlignment.Right; ver.VerticalAlignment = VerticalAlignment.Bottom; ver.Margin = new Thickness(0, 0, 64, 40); ver.Opacity = 0.7;
        Root.Children.Add(ver);

        Tick();
        w.Clock.Tick += (s, e) => Tick();
        w.ScreenLayer.Children.Add(Root);
    }

    void Tick()
    {
        var now = DateTime.Now;
        time.Text = now.ToString("HH:mm", En);
        date.Text = now.ToString("dddd d MMMM", En);
    }

    public void RefreshProfile()
    {
        var p = w.Profiles.Active;
        name.Text = p?.Name ?? "";
        avatarHost.Children.Clear();
        avatarHost.Children.Add(Art.Avatar(w.Profiles, p, 64));
    }

    public void FadeIn()
    {
        Root.Visibility = Visibility.Visible;
        Anim.FromTo(Root, UIElement.OpacityProperty, 0, 1, 500);
        Anim.FromTo(scale, ScaleTransform.ScaleXProperty, 1.04, 1, 500);
        Anim.FromTo(scale, ScaleTransform.ScaleYProperty, 1.04, 1, 500);
    }

    // A click anywhere continues (the play disc counts as something to click); the power button asks to quit.
    public bool OnMouse(MouseAct m, Point p)
    {
        bool onPower = Pointer.Over(power, w.Stage, p);
        if (m == MouseAct.Move)
        {
            double o = onPower ? 1 : 0;
            if (powerRing.Opacity != o) { powerRing.Opacity = o; if (onPower) Sounds.Play("scroll"); }
            return true;
        }
        if (m == MouseAct.Down && onPower) { Sounds.Play("select"); new QuitCard(w).Open(); return true; }
        if (m == MouseAct.Down) { OnAct(Act.Accept); return true; }
        return false;
    }

    public void OnAct(Act a)
    {
        if (a != Act.Accept) return;
        // More than one profile: ask who's playing first.
        if (w.Profiles.Data.Profiles.Count > 1)
        {
            Sounds.Play("select");
            new WhoCard(w, p => { w.SwitchProfile(p); Continue(); }).Open();
            return;
        }
        Continue();
    }

    void Continue()
    {
        if (w.TopScreen != this) return;
        Sounds.Play("systembrowse");
        Anim.To(Root, UIElement.OpacityProperty, 0, 340, Anim.InOut, done: () => { if (w.TopScreen != this) Root.Visibility = Visibility.Hidden; });
        Anim.To(scale, ScaleTransform.ScaleXProperty, 1.05, 340, Anim.InOut);
        Anim.To(scale, ScaleTransform.ScaleYProperty, 1.05, 340, Anim.InOut);
        w.ShowHome(true);
    }
}
