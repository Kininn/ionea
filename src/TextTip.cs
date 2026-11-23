// Text cut short with "…" (a row's name or value, a heading, a path): with the mouse on it, its whole text shows
// just above it, as plain text with no box round it (a soft halo in the card's colour keeps it readable over what
// is under it). It goes when the mouse leaves the text, the text goes away, the keyboard or a controller is used,
// the wheel turns or a click lands. Works for every screen: it looks at the text under the pointer, so nothing
// has to opt in. Text the mouse can't reach (IsHitTestVisible off, e.g. home tile names) gets none.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Ionea;

public sealed class TextTip
{
    const double Gap = 4, Edge = 8, MaxSize = 24, MinSize = 18;
    readonly FrameworkElement stage;
    readonly TextBlock tip;
    TextBlock? over;

    public string? Shown => over == null ? null : tip.Text;   // for test scripts

    public TextTip(FrameworkElement stage, Panel layer)
    {
        this.stage = stage;
        tip = new TextBlock
        {
            IsHitTestVisible = false, Visibility = Visibility.Collapsed, FontFamily = Fonts.Body,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, MaxWidth = 1920 - 2 * Edge,
        };
        layer.Children.Add(tip);
    }

    // The pointer is at p (stage coordinates).
    public void At(Point p)
    {
        var t = TrimmedAt(p);
        if (t == over) { if (t != null) Place(t); return; }
        Hide();
        if (t != null) Show(t);
    }

    public void Hide()
    {
        if (over == null) return;
        over.IsVisibleChanged -= Gone;
        over = null;
        tip.Visibility = Visibility.Collapsed;
    }

    void Gone(object s, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Hide(); }

    TextBlock? TrimmedAt(Point p)
    {
        if (p.X < 0 || p.Y < 0 || p.X > stage.ActualWidth || p.Y > stage.ActualHeight) return null;
        var hit = stage.InputHitTest(p) as DependencyObject;
        var t = hit as TextBlock ?? (hit as TextElement)?.Parent as TextBlock;   // a Run in a TextBlock
        return t != null && IsTrimmed(t) ? t : null;
    }

    // One line, trimmed, and its text is wider than the room it has.
    public static bool IsTrimmed(TextBlock t)
    {
        if (t.TextTrimming == TextTrimming.None || t.TextWrapping != TextWrapping.NoWrap || string.IsNullOrEmpty(t.Text) || t.ActualWidth <= 0) return false;
        var ft = new FormattedText(t.Text, CultureInfo.CurrentUICulture, t.FlowDirection,
            new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(t).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace > t.ActualWidth - t.Padding.Left - t.Padding.Right + 0.5;
    }

    void Show(TextBlock t)
    {
        over = t;
        t.IsVisibleChanged += Gone;
        var P = Palette.I;
        tip.Text = t.Text;
        tip.FontWeight = t.FontWeight;
        tip.FontSize = Math.Clamp(t.FontSize * 0.8, MinSize, MaxSize);
        tip.Foreground = P.Text;
        tip.Effect = Anim.Reduced ? null : new DropShadowEffect { Color = P.Card.Color, ShadowDepth = 0, BlurRadius = 10, Opacity = 1 };
        tip.Visibility = Visibility.Visible;
        Place(t);
        Anim.FromTo(tip, UIElement.OpacityProperty, 0, 1, 120);
    }

    // Just above the text, from its left edge (its right edge for text on the right of a row), kept on the stage.
    void Place(TextBlock t)
    {
        Rect b;
        try { b = t.TransformToAncestor(stage).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight)); }
        catch (InvalidOperationException) { Hide(); return; }
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = tip.DesiredSize;
        bool right = t.HorizontalAlignment == HorizontalAlignment.Right || t.TextAlignment == TextAlignment.Right;
        double x = right ? b.Right - size.Width : b.Left;
        x = Math.Max(Edge, Math.Min(x, stage.ActualWidth - Edge - size.Width));
        double y = b.Top - size.Height - Gap;
        if (y < Edge) y = b.Bottom + Gap;   // no room above: under it
        Canvas.SetLeft(tip, x); Canvas.SetTop(tip, y);
    }
}
