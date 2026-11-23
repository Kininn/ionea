#nullable disable
// The on-screen keyboard: typing with a controller. It opens over the bottom of the screen when a controller's
// A is pressed on a text box (a name, the search, a container), and on its own when a text box gets the focus
// while the controller is what was used last. Only controller input goes to it; a real keyboard still types into the box.
//   A: the focused key   B: delete (on an empty box: close)   X: space   Y: shift   LB / RB: caret left / right
//   Start or the Done key: close and confirm, as Enter would   Up from the top row / Down from the bottom row: close
//   and move on (Down: to the next control of the screen)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Ionea.Loc;

namespace Ionea;

public sealed class Osk
{
    readonly MainWindow w;
    readonly Grid layer = new() { VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    readonly StackPanel rowsHost = new() { HorizontalAlignment = HorizontalAlignment.Center };
    readonly List<List<(string key, Border el, TextBlock label)>> rows = new();
    TextBox box;
    TextBox closedOn;      // closed by moving off it: it doesn't open again on its own for this box
    int r, c;
    bool shift;

    public const string Shift = "⇧", Back = "⌫", Space = "Space", Done = "Done", Left = "◀", Right = "▶";
    static readonly string[][] Layout =
    {
        new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-" },
        new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "'" },
        new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", ":", "!" },
        new[] { Shift, "z", "x", "c", "v", "b", "n", "m", ",", ".", "?" },
        new[] { Left, Right, Space, Back, Done },
    };

    public bool IsOpen => box != null;
    public TextBox Box => box;
    public string FocusedKey => IsOpen ? rows[r][c].key : null;

    public Osk(MainWindow win, Grid host)
    {
        w = win;
        var P = w.P;
        layer.Children.Add(new Border
        {
            Background = P.Card, CornerRadius = new CornerRadius(30, 30, 0, 0), Padding = new Thickness(40, 30, 40, 34),
            HorizontalAlignment = HorizontalAlignment.Center, Width = 1500, Child = rowsHost,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 50, ShadowDepth = 0, Opacity = 0.18, Color = Colors.Black },
        });
        foreach (var line in Layout)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 5) };
            var list = new List<(string, Border, TextBlock)>();
            foreach (string k in line)
            {
                double width = k switch { Space => 520, Done => 220, Back => 150, Shift => 110, Left or Right => 110, _ => 110 };
                var t = Fonts.Text(k == Space ? T("settings.space") : k == Done ? T("common.done") : k, 38, P.Text);   // key ids stay English
                t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
                var b = new Border { Width = width, Height = 84, Margin = new Thickness(5, 0, 5, 0), CornerRadius = new CornerRadius(18), Background = P.ButtonFill, BorderThickness = new Thickness(3), Child = t };
                panel.Children.Add(b);
                list.Add((k, b, t));
            }
            rows.Add(list);
            rowsHost.Children.Add(panel);
        }
        host.Children.Add(layer);
        Keyboard.AddGotKeyboardFocusHandler(w, (s, e) =>
        {
            // a text box of the screen in front got the focus with a controller plugged in: open over it
            if (e.NewFocus is TextBox tb && !IsOpen && tb != closedOn && w.PadLast && w.Devs.Connected("pad") && w.Settings.Input.PadOn && w.TopScreen is IScreen top && top.WantsText)
                w.Dispatcher.BeginInvoke(() => { if (Keyboard.FocusedElement == tb && !IsOpen) Open(tb); }, System.Windows.Threading.DispatcherPriority.Input);
        });
        w.StackChanged += () => { if (IsOpen && !(w.TopScreen is IScreen s && s.WantsText)) Close(); };
    }

    public void Open(TextBox tb)
    {
        if (tb == null) return;
        box = tb; closedOn = null;
        r = 1; c = 0; shift = false;
        Labels(); Show();
        layer.Visibility = Visibility.Visible;
        Sounds.Play("menuopen");
        Log.Write("keyboard: open");
    }

    public void Close()
    {
        if (!IsOpen) return;
        box = null;
        layer.Visibility = Visibility.Collapsed;
        Log.Write("keyboard: closed");
    }

    // A controller's action. True = the keyboard used it (nothing else gets it).
    public bool Handle(Act a)
    {
        if (!IsOpen)
        {
            // A on a text box opens the keyboard (instead of confirming an empty or half-typed name)
            if (a == Act.Accept && w.TopScreen is IScreen s && s.WantsText && Keyboard.FocusedElement is TextBox tb && tb.IsVisible) { Open(tb); return true; }
            return false;
        }
        if (!box.IsVisible || !(w.TopScreen is IScreen top && top.WantsText)) { Close(); return false; }
        switch (a)
        {
            case Act.Left: Move(0, -1); return true;
            case Act.Right: Move(0, 1); return true;
            case Act.Up:
                if (r == 0) { Leave(); return false; }   // the screen gets the Up
                Move(-1, 0); return true;
            case Act.Down:
                if (r == rows.Count - 1) { Leave(); return false; }   // the screen gets the Down (the next control)
                Move(1, 0); return true;
            case Act.Accept: Press(rows[r][c].key); return true;
            case Act.Back:
                if (box.Text.Length == 0) { Leave(); Sounds.Play("back"); return true; }
                Press(Back); return true;
            case Act.Option: Press(Space); return true;
            case Act.Menu: Press(Done); return true;
            case Act.PageLeft: Press(Left); return true;
            case Act.PageRight: Press(Right); return true;
        }
        return true;
    }

    void Leave() { closedOn = box; Close(); }

    void Move(int dr, int dc)
    {
        int nr = Math.Clamp(r + dr, 0, rows.Count - 1);
        if (nr != r)
        {
            // keep roughly the same place across rows of different widths
            double at = (c + 0.5) / rows[r].Count;
            r = nr; c = Math.Clamp((int)(at * rows[r].Count), 0, rows[r].Count - 1);
        }
        else c = (c + dc + rows[r].Count) % rows[r].Count;
        Sounds.Play("scroll");
        Show();
    }

    public void Press(string key)
    {
        if (!IsOpen) return;
        int at = Math.Clamp(box.SelectionStart, 0, box.Text.Length), len = box.SelectionLength;
        string text = box.Text;
        switch (key)
        {
            case Shift: shift = !shift; Labels(); Sounds.Play("scroll"); return;
            case Left: box.SelectionLength = 0; box.CaretIndex = Math.Max(0, at - 1); Sounds.Play("scroll"); return;
            case Right: box.SelectionLength = 0; box.CaretIndex = Math.Min(text.Length, at + len + 1); Sounds.Play("scroll"); return;
            case Done:
            {
                var tb = box;
                Close();
                Sounds.Play("select");
                if (Keyboard.FocusedElement != tb) Keyboard.Focus(tb);
                w.Dispatch(Act.Accept);   // as Enter on the box
                return;
            }
            case Back:
                if (len > 0) Put(text.Remove(at, len), at);
                else if (at > 0) Put(text.Remove(at - 1, 1), at - 1);
                Sounds.Play("back");
                return;
            case Space: Type(" "); return;
            default: Type(shift ? key.ToUpperInvariant() : key); if (shift) { shift = false; Labels(); } return;
        }
    }

    void Type(string s)
    {
        int at = Math.Clamp(box.SelectionStart, 0, box.Text.Length), len = box.SelectionLength;
        string text = box.Text.Remove(at, len);
        if (box.MaxLength > 0 && text.Length + s.Length > box.MaxLength) { Sounds.Play("error"); return; }
        // the first letter of an empty box, and after a space, starts upper case (names)
        if (s.Length == 1 && char.IsLetter(s[0]) && (at == 0 || text[at - 1] == ' ')) s = s.ToUpperInvariant();
        Put(text.Insert(at, s), at + s.Length);
        Sounds.Play("scroll");
    }

    void Put(string text, int caret)
    {
        box.Text = text;
        box.SelectionLength = 0;
        box.CaretIndex = Math.Clamp(caret, 0, text.Length);
    }

    void Labels()
    {
        foreach (var row in rows)
            foreach (var (k, _, t) in row)
                if (k.Length == 1 && char.IsLetter(k[0])) t.Text = shift ? k.ToUpperInvariant() : k;
        foreach (var row in rows) foreach (var (k, el, _) in row) if (k == Shift) el.Background = shift ? w.P.RowFocus : w.P.ButtonFill;
    }

    void Show()
    {
        var P = w.P;
        for (int i = 0; i < rows.Count; i++)
            for (int j = 0; j < rows[i].Count; j++)
            {
                var (k, el, t) = rows[i][j];
                bool on = i == r && j == c;
                el.BorderBrush = on ? Palette.RingBrush() : Brushes.Transparent;
                t.Foreground = on ? P.Accent : P.Text;
                if (k != Shift || !shift) el.Background = on ? P.RowFocus : P.ButtonFill;
            }
    }

    // For tests: move the focus to a key (by its label) and press it.
    public bool PressKey(string key)
    {
        for (int i = 0; i < rows.Count; i++)
            for (int j = 0; j < rows[i].Count; j++)
                if (string.Equals(rows[i][j].key, key, StringComparison.OrdinalIgnoreCase)) { r = i; c = j; Show(); Press(rows[i][j].key); return true; }
        return false;
    }
}
