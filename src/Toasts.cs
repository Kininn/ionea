#nullable disable
// In-app toasts, top right: slide in, stay 2.5 s, fade out. One at a time, queued.
// Sounds: a soft bell when one appears (not when the action that caused it just made its own
// sound) and the muted "error" for kind "error" (with a small shake).
// Bursts: pop-ups that come one after another (each within BurstMs of the last one's start,
// queued ones included) make a sound only for the first of them; the rest appear quietly.
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using static Ionea.Loc;

namespace Ionea;

public sealed class Toasts
{
    readonly MainWindow w;
    readonly Queue<(string, string, string, List<(string kind, string name, bool on)>)> queue = new();
    bool showing;
    public static string Last;   // for test scripts: the last toast's title and text
    const long BurstMs = 4000;
    long lastAt = -BurstMs - 1;   // when the last toast appeared (TickCount64)
    public static int Sounded, Hushed;   // for test scripts: toasts that made their sound / stayed quiet in a burst

    public Toasts(MainWindow win) { w = win; }

    public void Show(string title, string sub, string kind)
    {
        queue.Enqueue((title, sub, kind, null));
        Last = title + (string.IsNullOrEmpty(sub) ? "" : " | " + sub);
        if (!showing) Next();
    }

    // Input devices at a game's start: one chip per device kind, greyed when it was used before but isn't plugged in.
    public void ShowDevices(string title, List<(string kind, string name, bool on)> devs)
    {
        if (devs.Count == 0) return;
        queue.Enqueue((title, null, devs.Any(d => d.on) ? devs.First(d => d.on).kind + "-on" : devs[0].kind + "-off", devs));
        Last = title + " | " + string.Join(", ", devs.Select(d => d.kind + ":" + d.name + (d.on ? "" : " (not connected)")));
        if (!showing) Next();
    }

    static string IconFor(string kind) => kind switch
    {
        "pad-on" or "pad-off" or "pad" => "controller", "keyboard-on" or "keyboard-off" or "keyboard" => "keyboard",
        "mouse-on" or "mouse-off" or "mouse" => "mouse", "plus" => "plus", "folder" => "folder", "picture" => "picture", "search" => "search",
        "error" => "info", "person" => "person", _ => "gear",
    };

    void Next()
    {
        if (queue.Count == 0) { showing = false; return; }
        showing = true;
        var (title, sub, kind, devs) = queue.Dequeue();
        var P = w.P;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        bool off = kind.EndsWith("-off"), error = kind == "error";
        var disc = Disc(kind, off, 64, new Thickness(0, 0, 20, 0));
        row.Children.Add(disc);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Fonts.Text(title, 28, P.Text, FontWeights.SemiBold));
        if (!string.IsNullOrEmpty(sub)) { var s = Fonts.Text(sub, 24, P.Text2); s.MaxWidth = 520; text.Children.Add(s); }
        if (devs != null)
            foreach (var d in devs)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Opacity = d.on ? 1 : 0.5 };
                line.Children.Add(Disc(d.kind, !d.on, 34, new Thickness(0, 0, 10, 0)));
                var n = Fonts.Text(d.name + (d.on ? "" : T("home.not_connected")), 23, d.on ? P.Text : P.Text2);
                n.MaxWidth = 520; n.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(n);
                text.Children.Add(line);
            }
        row.Children.Add(text);
        var tr = new TranslateTransform(60, 0);
        var card = new Border
        {
            Background = P.Card, CornerRadius = new CornerRadius(44), Padding = new Thickness(14, 12, 40, 12), Child = row,
            BorderBrush = P.Line, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 128, 56, 0),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Direction = 270, Opacity = 0.18 },
            RenderTransform = tr, Opacity = 0, IsHitTestVisible = false,
        };
        w.ToastLayer.Children.Add(card);
        Anim.To(card, UIElement.OpacityProperty, 1, 220);
        Anim.To(tr, TranslateTransform.XProperty, 0, 320, Anim.Back);
        long now = System.Environment.TickCount64;
        bool sound = now - lastAt > BurstMs;   // the first of a burst only
        lastAt = now;
        if (sound) Sounded++; else Hushed++;
        if (error)
        {
            if (sound) Sounds.Play("error");
            var shake = new TranslateTransform();
            row.RenderTransform = shake;
            Ui.After(Anim.Reduced ? 120 : 260, () => Anim.Bump(shake, -1));
        }
        else if (sound && Sounds.SinceLastMs > 250) Sounds.Play("toast");
        Ui.After(devs != null ? 3600 : 2700, () =>
        {
            Anim.To(card, UIElement.OpacityProperty, 0, 300, done: () => { w.ToastLayer.Children.Remove(card); Next(); });
            Anim.To(tr, TranslateTransform.XProperty, 30, 300);
        });
    }

    FrameworkElement Disc(string kind, bool off, double d, Thickness margin)
    {
        var P = w.P;
        var disc = new Grid { Width = d, Height = d, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        disc.Children.Add(new Ellipse { Fill = off ? (Brush)P.Line : new LinearGradientBrush(new GradientStopCollection { P.DiscA, P.DiscB }, 45) });
        string icon = IconFor(kind);
        disc.Children.Add(Art.Icon(icon, d * (icon is "controller" or "keyboard" ? 0.6 : icon == "mouse" ? 0.55 : 0.47), off ? P.Text2 : Brushes.White));
        return disc;
    }
}
