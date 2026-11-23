// Sleep: while the launcher is not the active window and no game is open, the screen dims
// under a calm moon and "Sleeping", fading in; the looping effects stop only once the dim has covered them,
// so nothing freezes mid-move in view. Selecting the launcher again fades it away as the effects resume.
// The moon and its halo are still pictures (no animation), so a sleeping launcher does not redraw.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static Ionea.Loc;

namespace Ionea;

public sealed class SleepLayer
{
    readonly MainWindow w;
    public readonly Grid Layer = new() { Width = 1920, Height = 1080, Opacity = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly TranslateTransform rise = new();
    bool asleep;
    int gen;

    public bool Asleep => asleep;

    public SleepLayer(MainWindow win)
    {
        w = win;
        var P = Palette.I;
        Layer.Children.Add(new Rectangle { Fill = P.SleepDim });
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = rise };
        // the moon: a crescent in a soft halo and a faint ring
        var moon = new Grid { Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Center };
        var halo = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(70, 255, 255, 255), 0), new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.55), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
        });
        halo.Freeze();
        moon.Children.Add(new Ellipse { Fill = halo });
        moon.Children.Add(new Ellipse { Width = 132, Height = 132, Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), StrokeThickness = 2 });
        var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(40, 40), 40, 40), new EllipseGeometry(new Point(62, 26), 34, 34));
        crescent.Freeze();
        moon.Children.Add(new Path { Data = crescent, Fill = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), Width = 80, Height = 80, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        col.Children.Add(moon);
        var title = Fonts.Text(T("home.sleeping"), 60, Brushes.White, FontWeights.Light, Fonts.Display);
        title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 8, 0, 0);
        col.Children.Add(title);
        var hint = Fonts.Text(T("home.select_the_launcher_to_wake_it"), 30, new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), FontWeights.Light, Fonts.Display);
        hint.HorizontalAlignment = HorizontalAlignment.Center; hint.Margin = new Thickness(0, 10, 0, 0);
        col.Children.Add(hint);
        Layer.Children.Add(col);
    }

    // Into sleep: the dim fades in (the content rises a little), then the looping effects pause.
    // Out of it: the dim fades away; the caller resumes the effects first, so they are moving as it clears.
    public void Set(bool sleep)
    {
        if (sleep == asleep) return;
        asleep = sleep;
        int my = ++gen;
        if (sleep)
        {
            Log.Write("sleep: in");
            Layer.Visibility = Visibility.Visible;
            Anim.To(Layer, UIElement.OpacityProperty, 1, 700, Anim.InOut, done: () =>
            {
                if (my != gen) return;
                Anim.PauseLoops(true);
                Log.Write("sleep: effects paused");
            });
            Anim.FromTo(rise, TranslateTransform.YProperty, 18, 0, 900, Anim.Out);
        }
        else
        {
            Log.Write("sleep: out");
            Anim.To(Layer, UIElement.OpacityProperty, 0, 420, Anim.Out, done: () => { if (my == gen) Layer.Visibility = Visibility.Collapsed; });
            Anim.To(rise, TranslateTransform.YProperty, -10, 420, Anim.Out);
        }
    }
}
