// Toggle: the pill switch shown for an On/Off setting instead of the words.
// A rounded track (grey when off, the accent when on) with a white knob that slides across.
// Rows still store "On" / "Off" as their value; the menus swap the text for this switch when they see it.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Ionea;

public sealed class Toggle : Grid
{
    readonly Border on;
    readonly TranslateTransform knobAt = new();
    readonly double travel;
    bool? state;

    static Palette P => Palette.I;

    public Toggle(double height)
    {
        double width = height * 1.7, pad = height * 0.09, knob = height - pad * 2;
        travel = width - pad * 2 - knob;
        Width = width; Height = height;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;   // a click lands on the row, which toggles it
        Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = P.Text2, Opacity = 0.35 });   // the off track: grey that shows on light and dark boxes
        on = new Border { CornerRadius = new CornerRadius(height / 2), Background = P.Accent, Opacity = 0 };
        Children.Add(on);
        Children.Add(new Border
        {
            Width = knob, Height = knob, CornerRadius = new CornerRadius(knob / 2), Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(pad, 0, 0, 0),
            RenderTransform = knobAt,
            Effect = new DropShadowEffect { BlurRadius = height * 0.18, ShadowDepth = height * 0.04, Direction = 270, Opacity = 0.3 },
        });
    }

    // Shows the state; slides to it if it changed while on screen, jumps there the first time.
    public void Set(bool value)
    {
        if (state == value) return;
        bool first = state == null;
        state = value;
        double x = value ? travel : 0, o = value ? 1 : 0;
        if (first)
        {
            knobAt.BeginAnimation(TranslateTransform.XProperty, null); knobAt.X = x;
            on.BeginAnimation(OpacityProperty, null); on.Opacity = o;
            return;
        }
        Anim.To(knobAt, TranslateTransform.XProperty, x, 220, Anim.InOut);
        Anim.To(on, OpacityProperty, o, 180, Anim.InOut);
    }

    // "On" / "Off" (a row's value) → the state; anything else is not a switch.
    public static bool? Of(string value) => value == "On" ? true : value == "Off" ? false : null;
}
