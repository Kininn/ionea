#nullable disable
// Cards: modal panels over the home screen (settings, controllers, search placeholder, messages, text entry).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

public abstract class Card : IScreen
{
    protected readonly MainWindow w;
    protected readonly Palette P = Palette.I;
    protected readonly Grid Root = new();
    protected readonly Border Box;
    protected readonly StackPanel Body = new();
    public const double LightRoom = 10;    // a focused row's running light reaches ~4 px outside it
    readonly ScaleTransform s = new();
    bool closed;
    readonly StackPanel corner;            // top right: ← (back) and X (close every menu)
    readonly Border backBtn, closeBtn;
    protected readonly StackPanel Pinned = new();   // under the scroll area, always in view

    protected Card(MainWindow win, string title, double width = 940)
    {
        w = win;
        Root.Children.Add(new Rectangle { Fill = P.Scrim });
        Box = new Border
        {
            Width = width, Background = P.Card, CornerRadius = new CornerRadius(30), Padding = new Thickness(56, 46, 56, 44),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = s, RenderTransformOrigin = new Point(0.5, 0.5),
            Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 12, Direction = 270, Opacity = 0.16, Color = Colors.Black },
        };
        // A card taller than the screen scrolls (MenuCard keeps the focused row in view).
        // Room round the rows so a focused row's running light isn't cut off at the scroller's edge.
        var scroll = new ScrollViewer
        {
            Content = Body, Focusable = false, PanningMode = PanningMode.None, Margin = new Thickness(-LightRoom),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Body.Margin = new Thickness(LightRoom);
        backBtn = CornerButton(Art.Icon("back", 30, P.Text2));
        closeBtn = CornerButton(new Path { Data = Geometry.Parse("M15 15 L33 33 M33 15 L15 33"), Stroke = P.Text2, StrokeThickness = 4.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 48, Height = 48 });
        corner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Children = { backBtn, closeBtn } };
        // Under the scroll area: a bar that stays put however far the list scrolls (Save on settings cards).
        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition());
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(Pinned, 1); Grid.SetRowSpan(corner, 2);
        inner.Children.Add(scroll); inner.Children.Add(Pinned); inner.Children.Add(corner);
        Box.Child = inner;
        Box.MaxHeight = 1040;
        if (title != null)
        {
            var t = Fonts.Text(title, 44, P.Text, FontWeights.Normal, Fonts.Display);
            t.Margin = new Thickness(0, 0, 0, 26);
            Body.Children.Add(t);
        }
        Root.Children.Add(Box);
    }

    Border CornerButton(FrameworkElement pic)
    {
        pic.HorizontalAlignment = HorizontalAlignment.Center; pic.VerticalAlignment = VerticalAlignment.Center;
        if (pic is Path) pic.LayoutTransform = new ScaleTransform(30 / 48.0, 30 / 48.0);
        return new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = Brushes.Transparent, Margin = new Thickness(6, 0, 0, 0), Child = pic };
    }

    // The corner buttons, before the card's own mouse handling: ← is Back, X closes every menu.
    public bool CornerMouse(MouseAct m, Point p)
    {
        if (corner.Visibility != Visibility.Visible) return false;
        bool onBack = Pointer.Over(backBtn, w.Stage, p), onClose = Pointer.Over(closeBtn, w.Stage, p);
        if (m == MouseAct.Move)
        {
            Brush Fill(bool on) => on ? P.RowFocus : Brushes.Transparent;
            if (backBtn.Background != Fill(onBack) || closeBtn.Background != Fill(onClose))
            {
                if (onBack || onClose) Sounds.Play("scroll");
                backBtn.Background = Fill(onBack); closeBtn.Background = Fill(onClose);
            }
            return onBack || onClose;
        }
        if (m != MouseAct.Down) return false;
        if (onBack) { OnAct(Act.Back); return true; }
        if (onClose) { CloseAll(); return true; }
        return false;
    }

    // Back on every menu in turn until none is left. It stops at a screen that isn't a card, a card that
    // stays open, or a card that Back opened (a question like "Apply changes?" is answered by the user).
    // Settings closes for good in one step: Back on a Settings page only goes to its rail.
    public void CloseAll()
    {
        var before = w.Stack.ToList();
        for (int i = 0; i < 20; i++)
        {
            var top = w.TopScreen;
            if (top is not Card c) break;
            if (c is SettingsCard sc) { sc.Close(); break; }
            c.OnAct(Act.Back);
            if (w.TopScreen == top || (w.TopScreen != null && !before.Contains(w.TopScreen))) break;
        }
    }

    // For tests: the middle of the X corner button on the stage; the card body's height; how many texts hold a phrase.
    public FrameworkElement CloseButtonElement => closeBtn;   // the tutorial points at it
    public FrameworkElement BoxElement => Box;                // the tutorial's bubble goes beside it
    public Point CloseButtonForTest => closeBtn.TranslatePoint(new Point(closeBtn.ActualWidth / 2, closeBtn.ActualHeight / 2), w.Stage);
    public double BodyHeightForTest => Body.ActualHeight;
    public int TextCountForTest(string part)
    {
        int n = 0;
        void Walk(object o)
        {
            if (o is TextBlock t && t.Text.Contains(part, StringComparison.OrdinalIgnoreCase)) n++;
            if (o is DependencyObject d) foreach (var c in LogicalTreeHelper.GetChildren(d)) Walk(c);
        }
        Walk(Box);
        return n;
    }

    public bool QuietOpen;   // the opener played the sound already (the Home menu over a game, at the key press)

    public virtual void Open()
    {
        corner.Margin = new Thickness(0, -Box.Padding.Top + 10, -Box.Padding.Right + 10, 0);
        w.CardLayer.Children.Add(Root);
        w.Push(this);
        if (!QuietOpen) Sounds.Play("menuopen");
        Anim.FromTo(Root, UIElement.OpacityProperty, 0, 1, 200);
        Anim.FromTo(s, ScaleTransform.ScaleXProperty, 0.95, 1, 260);
        Anim.FromTo(s, ScaleTransform.ScaleYProperty, 0.95, 1, 260);
    }

    public virtual void Close(bool sound = true)
    {
        if (closed) return;
        closed = true;
        if (sound) Sounds.Play("back");
        w.Pop(this);
        Anim.To(Root, UIElement.OpacityProperty, 0, 160, done: () => w.CardLayer.Children.Remove(Root));
        Anim.To(s, ScaleTransform.ScaleXProperty, 0.97, 160);
        Anim.To(s, ScaleTransform.ScaleYProperty, 0.97, 160);
    }

    public abstract void OnAct(Act a);

    // Mouse: a click outside the card (or a right click) goes back, the wheel moves like Up / Down.
    public virtual bool OnMouse(MouseAct m, Point p)
    {
        switch (m)
        {
            case MouseAct.Down: if (!Pointer.Over(Box, w.Stage, p)) { OnAct(Act.Back); return true; } return false;
            case MouseAct.WheelUp: OnAct(Act.Up); return true;
            case MouseAct.WheelDown: OnAct(Act.Down); return true;
        }
        return false;
    }

    // host: where the note goes (the body unless given; Settings gives its page, which is cleared on each page change).
    protected TextBlock Note(string text, double size = 26, Thickness? margin = null, Panel host = null)
    {
        var t = Fonts.Text(text, size, P.Text2);
        t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
        t.Margin = margin ?? new Thickness(4, 0, 0, 10);
        (host ?? Body).Children.Add(t);
        return t;
    }
}

// A card with a focusable list of rows: Up/Down move, Left/Right change, A activates, B closes.
public class MenuCard : Card
{
    protected sealed class Row
    {
        public Func<string> Value; public Action<int> Change; public Action Activate;
        public Func<string> Label;     // a label that follows the state (Refresh sets it); null = the text it was made with
        public Border El; public TextBlock ValueText;
        public Toggle Switch;          // shown instead of the text while the value is On / Off (IsSwitch)
        public bool Opens;             // opens another card (a chevron): its value is a summary, never a switch
        public bool IsSwitch(string v) => Level == null && !Opens && !HasList && Toggle.Of(v) != null;
        public bool Silent;            // Left / Right makes no "scroll" sound (a level: it ticks, or plays itself)
        public bool Speaks;            // a level whose setter plays its own preview (the sound levels): no tick
        public Func<int> Level;        // a level row (0-100): the bar shows it, a click on the bar sets it
        public Action<int> SetLevel;
        public FrameworkElement Track; public Border Fill;
        public Func<bool> Dim;         // shown faded (e.g. a sound level while all sounds are off)
        public Func<IList<string>> Names; public Func<int> Current; public Action<int> Pick;   // a list of choices (WithList)
        public bool HasList => Names != null && Names().Count > 2;
    }
    const double TrackW = 220;
    protected readonly List<Row> Rows = new();
    protected int Focus;
    protected Panel RowHost;   // where rows go (the body unless a card lays them out elsewhere)
    protected bool RowsActive = true;   // false: the focus is elsewhere on the card (Settings' rail), no row is lit
    readonly TranslateTransform bump = new();
    readonly ScaleTransform land = new();
    int landed = -1;

    protected MenuCard(MainWindow w, string title, double width = 940) : base(w, title, width) { }

    // A list that needs slow work first (drives, shares): a spinner shows until it is done. Quick
    // work (under 150 ms) builds straight away so the spinner doesn't flash.
    public bool Loading { get; private set; }
    bool gone;

    protected void LoadThen<T>(Func<T> work, Action<T> build)
    {
        var t = System.Threading.Tasks.Task.Run(() => { try { return work(); } catch (Exception e) { Log.Write($"{GetType().Name}: loading failed: {e.Message}"); return default; } });
        if (t.Wait(150)) { build(t.Result); return; }
        Loading = true;
        var spin = LaunchFx.Spinner(60);
        spin.HorizontalAlignment = HorizontalAlignment.Center;
        spin.Margin = new Thickness(0, 24, 0, 34);
        Body.Children.Add(spin);
        long start = Environment.TickCount64;
        t.ContinueWith(_ => w.Dispatcher.BeginInvoke(() => Ui.After(Math.Max(1, 450 - (Environment.TickCount64 - start)), () =>
        {
            if (spin.Children[1] is Shape arc && arc.RenderTransform is RotateTransform rot) rot.BeginAnimation(RotateTransform.AngleProperty, null);
            Body.Children.Remove(spin);
            Loading = false;
            if (gone) return;
            build(t.Result);
            if (Rows.Count > 9) CompactRows();
            Refresh();
        })));
    }

    public override void Close(bool sound = true) { gone = true; CloseList(false); base.Close(sound); }

    // A value row added from outside (the tweaks card fills its More page this way).
    internal void AddRowFor(string label, Func<string> value, Action<int> change) => AddRow(label, value, change);
    internal void AddRowFor(string label, Func<string> value, Action<int> change, Func<IList<string>> names, Func<int> current, Action<int> pick)
        => WithList(AddRow(label, value, change), names, current, pick);

    // A setting with three or more choices: A or a click opens the list of them (Left / Right still step).
    protected Row WithList(Row r, Func<IList<string>> names, Func<int> current, Action<int> pick)
    {
        r.Names = names; r.Current = current; r.Pick = pick;
        return r;
    }

    protected Row AddRow(string label, Func<string> value = null, Action<int> change = null, Action activate = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Fonts.Text(label, 30, P.Text); l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(l);
        var v = Fonts.Text("", 30, P.Accent); v.VerticalAlignment = VerticalAlignment.Center; v.MaxWidth = 520;
        Grid.SetColumn(v, 1); g.Children.Add(v);
        var sw = new Toggle(40) { Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(sw, 1); g.Children.Add(sw);
        var el = new Border { Height = 76, CornerRadius = new CornerRadius(18), Padding = new Thickness(26, 0, 26, 0), BorderThickness = new Thickness(3), Child = g, Margin = new Thickness(0, 0, 0, 6) };
        var r = new Row { Value = value, Change = change, Activate = activate, El = el, ValueText = v, Switch = sw };
        Rows.Add(r);
        (RowHost ?? Body).Children.Add(el);
        if (IconFor(label) is string kind) WithIcon(r, kind);
        return r;
    }

    // The picture for a row by its label, so every menu shows the same picture for the same thing.
    // Rows without one line up with the rows that have one (AlignIcons).
    static readonly Dictionary<string, string> RowIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Done"] = "check", ["OK"] = "check", ["Save"] = "check", ["Add"] = "plus", ["Add game"] = "plus", ["Add container"] = "folder", ["Apply on next launch"] = "check",
        ["Cancel"] = "back", ["Back"] = "back", ["Close"] = "back", ["Later"] = "back", ["Keep the profile"] = "back",
        ["Start"] = "play", ["Resume"] = "play", ["Watch the intro"] = "play", ["Watch the tutorial"] = "play",
        ["Game info"] = "info", ["Support"] = "info",
        ["Game settings"] = "gear", ["Launch"] = "play", ["Graphics tweaks"] = "gear", ["Settings for every game"] = "gear", ["More"] = "tune", ["Start options"] = "tune", ["Customize"] = "tune",
        ["Emulator"] = "chip", ["Emulators"] = "chip", ["Emulators and ports"] = "chip", ["+ Add emulator"] = "plus", ["Plays"] = "controller", ["Systems"] = "controller",
        ["Stored on"] = "drive", ["Storage"] = "drive", ["Add a place…"] = "plus",
        ["Put in a container"] = "folder", ["Move to another container"] = "folder", ["New container…"] = "plus",
        ["Remove from the list"] = "minus", ["Remove from the launcher"] = "trash", ["Removed games"] = "hidden",
        ["Uninstall"] = "trash", ["Uninstall with Steam"] = "trash",
        ["Profile and play stats"] = "person", ["Switch profile"] = "swap", ["Discord status"] = "chat", ["Group in Task Manager"] = "chip",
        ["Network connection"] = "globe", ["Language"] = "globe", ["Network activity"] = "info", ["Clear log"] = "trash",
        ["Picture"] = "picture", ["Remove the picture"] = "minus", ["Rename"] = "pen",
        ["Add a profile"] = "plus", ["Remove a profile"] = "minus",
        ["Look"] = "look", ["Theme"] = "look", ["Brightness"] = "sun", ["Display"] = "screen", ["Screen mode"] = "screen", ["Games display"] = "screen", ["Game display"] = "screen", ["Sort"] = "sort", ["Performance overlay"] = "gauge", ["Intro at start"] = "spark", ["Outro when closing"] = "spark", ["Icon shape"] = "layout", ["Colours"] = "palette", ["Layout"] = "layout", ["Order"] = "sort",
        ["Launcher keys"] = "keyboard", ["Back to the defaults"] = "restart", ["Change the Home menu key"] = "keyboard", ["Pick another key"] = "back",
        ["Sounds"] = "sound", ["Volume"] = "sound", ["Sound when pointing"] = "mouse", ["Hover sounds"] = "mouse", ["Reset sounds"] = "restart",
        ["Sound theme"] = "palette", ["Navigation"] = "move", ["Actions"] = "spark", ["Sound groups"] = "tune", ["Reduce effects"] = "look",
        ["Keyboard"] = "keyboard", ["Controller"] = "controller", ["Mouse"] = "mouse", ["Keyboard and mouse"] = "keyboard",
        ["Game controls"] = "tune", ["Set up for"] = "controller", ["Set up now"] = "controller",
        ["Pop-ups when one is plugged in"] = "bell", ["Pop-up when connected"] = "bell", ["Back to the recommended controls"] = "restart",
        ["Use the recommended controls"] = "check", ["This game only"] = "info",
        ["SteamGridDB key"] = "key", ["What went online"] = "history",
        ["New game files"] = "spark", ["From Steam"] = "library", ["Program or shortcut"] = "app", ["Game file"] = "file",
        ["Install a checked download"] = "shield", ["Download an emulator"] = "save", ["Download"] = "save",
        ["Choose a program"] = "app", ["Look for emulators"] = "search", ["Look harder"] = "search",
        ["File types"] = "file", ["Program"] = "app", ["Required files"] = "key",
        ["Ionea manages controls"] = "controller", ["Play inside Ionea"] = "screen", ["Open the emulator"] = "move",
        ["Add a file or zip"] = "file", ["Add a folder"] = "folder", ["Cover"] = "picture", ["Change cover"] = "picture",
        ["Restart now"] = "restart", ["Quit"] = "door",
        ["Version"] = "info", ["Updates from"] = "globe",
        ["Check for updates"] = "search", ["Update now"] = "save", ["Update notification"] = "bell", ["Auto update"] = "restart", ["Last checked"] = "history",
        ["Changelog"] = "file", ["Go back to the previous version"] = "restart",
    };

    static string IconFor(string label)
    {
        if (label == null) return null;
        label = Loc.ToEnglish(label);   // the picture goes by the English label (Loc.cs)
        if (RowIcons.TryGetValue(label, out var k)) return k;
        if (label.StartsWith("Take out of ")) return "home";
        if (label.StartsWith("Remove ")) return "minus";
        if (label.StartsWith("Keep ")) return "check";
        return null;
    }

    // A small grey heading over a group of rows (instead of a bare divider), so a long menu reads as bundles.
    protected void Section(string title)
    {
        var t = Fonts.Text(title, 20, P.Text2, FontWeights.SemiBold);
        t.Margin = new Thickness(28, (RowHost ?? Body).Children.Count == 0 ? 0 : 10, 0, 2);
        (RowHost ?? Body).Children.Add(t);
        sections++;
    }
    int sections;

    // A box of rows under a heading (Game settings: Display, Image quality, ...): rows added until EndBox go in it.
    // No title: a box without a heading. A new box ends the one still open.
    protected void BeginBox(string title)
    {
        EndBox();
        boxTitles.Add(title ?? "");
        if (title != null) Section(title);
        var host = new StackPanel();
        (RowHost ?? Body).Children.Add(new Border
        {
            Background = new SolidColorBrush(P.Line.Color) { Opacity = 0.28 }, CornerRadius = new CornerRadius(24),
            Padding = new Thickness(8, 8, 8, 2), Margin = new Thickness(0, 0, 0, 10), Child = host,
        });
        boxOuter = RowHost; RowHost = host; inBox = true;
    }
    protected void EndBox() { if (!inBox) return; RowHost = boxOuter; boxOuter = null; inBox = false; }

    // A settings card's Save: the last row, pinned under the list (it never scrolls away), with a line over
    // it and "Unsaved changes" on its right while there are some. Down from the last setting reaches it.
    protected Row SaveRow(Func<bool> changed, Action save)
    {
        Pinned.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = P.Line, Opacity = 0.7, Margin = new Thickness(0, 12, 0, 12) });
        var keep = RowHost; RowHost = Pinned;
        var r = AddRow(T("common.save"), () => changed() ? T("settings.unsaved_changes") : "", activate: save);
        RowHost = keep;
        r.El.Margin = new Thickness(0);
        return r;
    }
    Panel boxOuter;
    bool inBox;
    readonly List<string> boxTitles = new();
    public string BoxesForTest => string.Join(",", boxTitles);

    // Compact rows for long lists; a bit tighter when headings take room too.
    void CompactRows()
    {
        double h = sections > 0 ? 58 : 62;
        foreach (var r in Rows) { r.El.Height = h; r.El.Margin = new Thickness(0, 0, 0, 4); }
    }

    // In a list where some rows have a picture, the others' labels start where the pictured rows' do.
    void AlignIcons()
    {
        bool any = Rows.Any(HasIcon);
        foreach (var r in Rows)
            if (!HasIcon(r) && r.El.Child is Grid g && g.Children[0] is TextBlock l)
                l.Margin = new Thickness(any ? 48 : 0, 0, 0, 0);
    }

    static bool HasIcon(Row r) => r.El.Child is Grid g && g.Children.OfType<FrameworkElement>().Any(c => "rowicon".Equals(c.Tag));

    // A 0-100 level in steps of 10, with a bar. set gets the new level.
    protected Row AddSlider(string label, Func<int> get, Action<int> set)
    {
        void Set(int v) => set(Math.Clamp(v, 0, 100));
        var r = AddRow(label, () => get() <= 0 ? T("common.off") : get() + "%", d => Set((int)Math.Round((get() + d * 10) / 10.0, MidpointRounding.AwayFromZero) * 10));
        r.Silent = true; r.Level = get; r.SetLevel = Set;
        var g = (Grid)r.El.Child;
        g.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(r.ValueText, 2);
        r.ValueText.MinWidth = 150; r.ValueText.TextAlignment = TextAlignment.Right;
        var track = new Grid { Width = TrackW, Height = 44, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 6, 0) };
        track.Children.Add(new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = P.Line, VerticalAlignment = VerticalAlignment.Center });
        r.Fill = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = P.Bar, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(r.Fill);
        Grid.SetColumn(track, 1); g.Children.Add(track);
        r.Track = track;
        return r;
    }

    // A small icon left of the row's label (the label stays the grid's first child for ValueOf).
    protected Row WithIcon(Row r, string kind)
    {
        var g = (Grid)r.El.Child;
        var label = (TextBlock)g.Children[0];
        foreach (var old in g.Children.OfType<FrameworkElement>().Where(c => "rowicon".Equals(c.Tag)).ToList()) g.Children.Remove(old);
        var icon = Art.Icon(kind, 32, label.Foreground);
        icon.Tag = "rowicon";
        icon.HorizontalAlignment = HorizontalAlignment.Left; icon.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(48, 0, 0, 0);
        g.Children.Add(icon);
        return r;
    }

    // For tests: a row's value by its label.
    public virtual string FocusedLabel => Focus < Rows.Count ? ((Rows[Focus].El.Child as Grid)?.Children[0] as TextBlock)?.Text : null;
    public bool FocusRow(string label)
    {
        int i = Rows.FindIndex(r => ((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text is string t && (t == label || Loc.ToEnglish(t) == label));   // shown or English label
        if (i < 0) return false;
        Focus = i; Refresh();
        return true;
    }
    public string Labels => string.Join(" | ", Rows.Select(r => ((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text));
    public string DimLabels => string.Join(" | ", Rows.Where(r => r.Dim?.Invoke() == true).Select(r => ((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text));
    public string ValueOf(string label) => Rows.FirstOrDefault(r => ((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text == label)?.Value?.Invoke();

    // A thin line between groups of rows.
    protected void Divider()
    {
        (RowHost ?? Body).Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = P.Line, Opacity = 0.7, Margin = new Thickness(26, 10, 26, 12) });
    }

    // A row that opens a menu of its own: a › at its end (after its value, if it has one).
    protected Row Opens(Row r)
    {
        r.Opens = true;
        var g = (Grid)r.El.Child;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var c = Art.Icon("chevron", 26, P.Text2);
        c.VerticalAlignment = VerticalAlignment.Center; c.Margin = new Thickness(14, 0, -6, 0);
        Grid.SetColumn(c, g.ColumnDefinitions.Count - 1);
        g.Children.Add(c);
        return r;
    }

    // Take every row (and divider) out, for a card that swaps its list (pages, categories).
    protected void ClearRows()
    {
        EndBox();
        boxTitles.Clear();
        (RowHost ?? Body).Children.Clear();
        Rows.Clear();
        foreach (var sv in Scrollers(RowHost ?? Body)) sv.ScrollToTop();
        Focus = 0; landed = -1; sections = 0;
    }

    // Long lists get compact rows so they fit on screen.
    protected void Compact(int over = 9)
    {
        if (Rows.Count > over) CompactRows();
    }

    // Back on top after a card over it closed: its values may have changed.
    public virtual void OnTop() => Refresh();

    // The rows every Sounds list has (Settings > Sound and the Sounds card): all on or off, one volume,
    // a level per kind of sound (each plays itself at the new level), sound when pointing, reset.
    protected void SoundRows()
    {
        var s = w.Settings;
        void On(bool on) { s.Sounds = on; Sounds.On = on && !w.Opt.Mute; }
        BeginBox(T("settings.sound"));
        AddRow(T("settings.sounds"), () => s.Sounds ? T("common.on") : T("common.off"), _ => { On(!s.Sounds); s.Save(); });
        var vol = AddSlider(T("settings.volume"), () => s.Sound.Volume, v =>
        {
            s.Sound.Volume = v;
            if (v > 0 && !s.Sounds) On(true);   // turning the volume up means: I want to hear it
            s.Save(); Sounds.Play("select");
        });
        vol.Dim = () => !s.Sounds;
        vol.Speaks = true;
        // The theme: every sound in another make (both from tools\make_ui_sounds.py). A change plays a preview.
        var names = Sounds.Themes.Select(t => t.Name).ToList();
        int Cur() => Math.Max(0, Array.FindIndex(Sounds.Themes, t => t.Id == Sounds.Theme));
        void Theme(int i) { s.Sound.Theme = Sounds.Themes[(i + names.Count) % names.Count].Id; s.Save(); Sounds.Preload(); Sounds.Play("select"); }
        var th = AddRow(T("settings.sound_theme"), () => names[Cur()], d => Theme(Cur() + d));
        th.Silent = true;
        WithList(th, () => names, Cur, Theme);
        th.Dim = () => !s.Sounds;
        AddRow(T("settings.hover_sounds"), () => s.Input.HoverSounds ? T("common.on") : T("common.off"), _ => { s.Input.HoverSounds = !s.Input.HoverSounds; s.Save(); });
        // One level for each kind: Navigation (moving, choosing, menus) and Actions (things happening).
        BeginBox(T("settings.levels"));
        foreach (var k in Sounds.Kinds)
        {
            var kk = k;
            var r = AddSlider(k.Label, () => s.Sound.Kind(kk.Id), v => { s.Sound.SetKind(kk.Id, v); s.Save(); Sounds.Play(kk.Preview); });
            r.Speaks = true;
            r.Dim = () => !s.Sounds || s.Sound.Volume <= 0;
        }
        Opens(AddRow(T("settings.sound_groups"), activate: () => new SoundGroupsCard(w).Open()));
        AddRow(T("settings.reset_sounds"), () => "", activate: () =>
        {
            bool reduce = s.Sound.ReduceEffects;   // an effects choice, not a sound: it stays
            s.Sound = new SoundPrefs { ReduceEffects = reduce }; s.Input.HoverSounds = true; On(true); s.Save();
            Refresh();
            w.Toasts.Show(T("settings.sounds_reset"), T("settings.everything_at_full_volume"), "info");
        });
        EndBox();
    }

    // The fine levels, one per group of sounds (Sound groups ›), each under its kind.
    protected void SoundGroupRows()
    {
        var s = w.Settings;
        foreach (var k in Sounds.Kinds)
        {
            BeginBox(k.Label);
            foreach (var g in Sounds.Groups.Where(g => g.Kind == k.Id))
            {
                var gr = g;
                var r = AddSlider(g.Label, () => s.Sound.Group(gr.Id), v => { s.Sound.SetGroup(gr.Id, v); s.Save(); Sounds.Play(gr.Clips[0]); });
                r.Speaks = true;
                r.Dim = () => !s.Sounds || s.Sound.Volume <= 0 || s.Sound.Kind(gr.Kind) <= 0;
            }
            EndBox();
        }
    }

    // A second, quieter text on the row, right of its label (the label stays the grid's first child for ValueOf).
    protected Row Aside(Row r, string text)
    {
        var g = (Grid)r.El.Child;
        // Its own column, so a long (translated) label is cut short with "…" instead of running under it.
        foreach (UIElement c in g.Children) if (Grid.GetColumn(c) >= 1) Grid.SetColumn(c, Grid.GetColumn(c) + 1);
        g.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = GridLength.Auto });
        var t = Fonts.Text(text, 24, P.Text2);
        t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(16, 0, 28, 0);
        t.MaxWidth = 560;
        Grid.SetColumn(t, 1); g.Children.Add(t);
        return r;
    }

    // The network rows (Online.cs): the first start's Network screen and Settings > Network. The Network connection
    // switch on top (off by default, like a Wi-Fi switch); while it is off every other network row is greyed and
    // does nothing (a pop-up says why).
    protected void OnlineRows(Action connection = null)
    {
        BeginBox(T("settings.connection"));
        Aside(AddRow(T("settings.network_connection"), () => Online.Offline ? T("common.off") : T("common.on"), _ => Online.SetOffline(!Online.Offline)), T("settings.off_ionea_never_goes_online"));
        connection?.Invoke();
        BeginBox(T("settings.sources"));
        foreach (var it in Online.Switches)
        {
            var i = it;
            NetRow(Aside(AddRow(i.Label, () => Online.Switched(i.Id) ? T("common.on") : T("common.off"), _ => { if (!OfflineSaysNo()) Online.Set(i.Id, !Online.Switched(i.Id)); }), Online.HostsText(i)));
        }
        EndBox();
    }

    // A row that only matters online: greyed while the network connection is off.
    protected Row NetRow(Row r) { r.Dim = () => Online.Offline; return r; }
    protected bool OfflineSaysNo()
    {
        if (!Online.Offline) return false;
        w.Toasts.Show(T("settings.network_connection_is_off"), T("settings.switch_it_on_first"), "info");
        return true;
    }

    // Under the rows, where the rows go (in Settings that is the page, so ClearRows takes it
    // out with the page; it used to go to the card's body and pile up under every page).
    protected TextBlock Footer(string text)
    {
        var t = Fonts.Text(text, 22, P.Text2);
        t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(4, 18, 0, 0);
        (RowHost ?? Body).Children.Add(t);
        return t;
    }

    protected void Refresh()
    {
        AlignIcons();
        for (int i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i]; bool f = i == Focus && RowsActive;
            if (r.Label != null && (r.El.Child as Grid)?.Children[0] is TextBlock lt) lt.Text = r.Label();
            string v = r.Value?.Invoke() ?? "";
            if (r.IsSwitch(v))
            {
                // On / Off: the pill switch instead of the words (and no ‹ › arrows when it has the focus)
                r.ValueText.Visibility = Visibility.Collapsed; r.Switch.Visibility = Visibility.Visible;
                r.Switch.Set(v == "On");
            }
            else
            {
                r.ValueText.Visibility = Visibility.Visible; r.Switch.Visibility = Visibility.Collapsed;
                r.ValueText.Text = !f || r.Change == null ? v : r.HasList ? v + "  ▾" : "‹  " + v + "  ›";
            }
            r.El.Background = f ? P.RowFocus : Brushes.Transparent;
            r.El.BorderBrush = f ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(r.El, f);
            r.El.RenderTransform = f ? new TransformGroup { Children = { land, bump } } : null;
            r.El.RenderTransformOrigin = new Point(0.5, 0.5);
            r.El.Opacity = r.Dim?.Invoke() == true ? 0.45 : 1;
            if (r.Level != null) { r.Fill.Width = TrackW * Math.Clamp(r.Level(), 0, 100) / 100.0; r.Fill.Opacity = f ? 1 : 0.6; }
        }
        if (Focus != landed && landed >= 0)
        {
            Anim.FromTo(land, ScaleTransform.ScaleXProperty, 0.975, 1, 220, Anim.Back);
            Anim.FromTo(land, ScaleTransform.ScaleYProperty, 0.975, 1, 220, Anim.Back);
        }
        landed = Focus;
        if (RowsActive && Focus >= 0 && Focus < Rows.Count && !dragging) KeepInView(Focus);   // Focus < 0: a text box has it (Add game)
    }

    // ---- the scroll streak: a line of light just inside the card's right edge, beside a list that scrolls ----
    // It shows while the mouse is near it (brightest at the cursor, fading out above and below), while the
    // list scrolls (at the scroll position), and while it is held: dragging it scrolls the list, and the
    // row nearest the middle of the view becomes the selected one.
    Canvas fx;
    Rectangle glowR, lineR, coreR;
    LinearGradientBrush maskGlow, maskLine, maskCore;
    bool dragging, hovering;
    long lastScroll;
    double fxShown = -1;

    static LinearGradientBrush StreakMask() => new()
    {
        MappingMode = BrushMappingMode.Absolute,
        GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.White, 0.5), new GradientStop(Colors.Transparent, 1) },
    };

    void BuildStreak()
    {
        if (fx != null) return;
        fx = new Canvas { IsHitTestVisible = false, Opacity = 0 };
        var grad = Palette.RingBrush(new Point(0, 0), new Point(0, 1));
        glowR = new Rectangle { Fill = grad, RadiusX = 9, RadiusY = 9, Opacity = 0.75, Effect = new BlurEffect { Radius = 12 }, OpacityMask = maskGlow = StreakMask() };
        lineR = new Rectangle { Fill = grad, RadiusX = 2.5, RadiusY = 2.5, OpacityMask = maskLine = StreakMask() };
        coreR = new Rectangle { Fill = Brushes.White, RadiusX = 1, RadiusY = 1, Opacity = 0.9, OpacityMask = maskCore = StreakMask() };
        fx.Children.Add(glowR); fx.Children.Add(lineR); fx.Children.Add(coreR);
        Root.Children.Add(fx);
        Root.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange == 0 || dragging || e.OriginalSource is not ScrollViewer sv || sv != ActiveScroller()) return;
            lastScroll = Environment.TickCount64;
            if (Track(out var t)) Paint(t, t.Top + t.Height * (sv.VerticalOffset / Math.Max(1, sv.ScrollableHeight)), false);
            ShowStreak();
            Ui.After(750, ShowStreak);
        }));
    }

    // The list's scroll area when it holds more than fits, else null.
    ScrollViewer ActiveScroller() => Rows.Count == 0 ? null : Scrollers(Rows[0].El).FirstOrDefault(sv => sv.ScrollableHeight > 0.5);

    // Where the streak runs (in the card's coordinates): beside the scroll area, in the card's margin.
    bool Track(out Rect t)
    {
        t = Rect.Empty;
        var sv = ActiveScroller();
        if (sv == null || !sv.IsVisible) return false;
        try
        {
            var b = sv.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, sv.ActualWidth, sv.ActualHeight));
            var box = Box.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, Box.ActualWidth, Box.ActualHeight));
            double x = b.Right + Math.Min(16, (box.Right - b.Right) / 2);
            t = new Rect(x, b.Top + 8, 0, Math.Max(1, b.Height - 16));
            return true;
        }
        catch { return false; }
    }

    // Draw the light centred on y (brighter and wider while held).
    void Paint(Rect t, double y, bool held)
    {
        y = Math.Clamp(y, t.Top, t.Bottom);
        double a = y - t.Top;
        void Put(Rectangle r, LinearGradientBrush m, double width, double reach)
        {
            r.Width = width; r.Height = t.Height;
            Canvas.SetLeft(r, t.X - width / 2); Canvas.SetTop(r, t.Top);
            // Near the track's ends the light fades out before the end instead of being cut off there: its
            // brightest point stays a little inside, and its side toward the end is shorter.
            double soft = Math.Min(reach * 0.4, t.Height / 2);
            double c = Math.Clamp(a, soft, t.Height - soft);
            double up = Math.Clamp(c, soft, reach), down = Math.Clamp(t.Height - c, soft, reach);
            m.StartPoint = new Point(0, c - up); m.EndPoint = new Point(0, c + down);
            m.GradientStops[1].Offset = up / (up + down);
        }
        Put(glowR, maskGlow, held ? 24 : 16, held ? 190 : 140);
        Put(lineR, maskLine, held ? 6 : 4, held ? 210 : 160);
        Put(coreR, maskCore, 2, held ? 80 : 55);
    }

    // Fade in while pointed at, held or just scrolled; out otherwise.
    void ShowStreak()
    {
        if (fx == null) return;
        bool on = ActiveScroller() != null && (dragging || hovering || Environment.TickCount64 - lastScroll < 700);
        double o = on ? 1 : 0;
        if (o == fxShown) return;
        fxShown = o;
        Anim.To(fx, UIElement.OpacityProperty, o, on ? 140 : 320);
    }

    bool StreakMouse(MouseAct m, Point p)
    {
        if (fx == null || !Track(out var t)) { if (hovering || dragging) { hovering = dragging = false; ShowStreak(); } return false; }
        Point q;
        try { q = Root.TransformToAncestor(w.Stage).Inverse.Transform(p); } catch { return false; }
        var box = Box.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, Box.ActualWidth, Box.ActualHeight));
        bool near = q.Y >= t.Top - 20 && q.Y <= t.Bottom + 20 && q.X >= t.X - 26 && q.X <= Math.Max(t.X + 26, box.Right);
        // the button let go outside the window: the drag ends at the next move
        if (dragging && System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed) m = MouseAct.Up;
        switch (m)
        {
            case MouseAct.Move:
                if (dragging) { DragTo(t, q.Y); return true; }
                if (near != hovering) { hovering = near; ShowStreak(); }
                if (near) Paint(t, q.Y, false);
                return near;
            case MouseAct.Down:
                if (!near) return false;
                dragging = true; hovering = true;
                Sounds.Play("select");
                DragTo(t, q.Y); ShowStreak();
                return true;
            case MouseAct.Up:
                if (!dragging) return false;
                dragging = false; hovering = near;
                Paint(t, q.Y, false); ShowStreak();
                return true;
        }
        return false;
    }

    // Held and moved: the list scrolls to match (the streak's top is the list's top, its bottom the
    // list's end), and the row nearest the middle of the view is selected.
    void DragTo(Rect t, double y)
    {
        var sv = ActiveScroller();
        if (sv == null) return;
        Paint(t, y, true);
        sv.ScrollToVerticalOffset(Math.Clamp((y - t.Top) / t.Height, 0, 1) * sv.ScrollableHeight);
        sv.UpdateLayout();
        double mid = t.Top + t.Height / 2;
        int best = -1; double bd = double.MaxValue;
        for (int i = 0; i < Rows.Count; i++)
        {
            var el = Rows[i].El;
            if (!el.IsVisible) continue;
            double c;
            try { c = el.TransformToAncestor(Root).Transform(new Point(0, el.ActualHeight / 2)).Y; } catch { continue; }
            if (Math.Abs(c - mid) < bd) { bd = Math.Abs(c - mid); best = i; }
        }
        if (best >= 0 && best != Focus && RowsActive) { Focus = best; Refresh(); }
    }

    // The focused row is scrolled into view, with a row's room around it; the first row shows the
    // title above it, the last the notes under it.
    void KeepInView(int i)
    {
        var el = Rows[i].El; bool first = i == 0, last = i == Rows.Count - 1;
        w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!el.IsVisible) return;
            if (first || last) { foreach (var sv in Scrollers(el)) if (first) sv.ScrollToTop(); else sv.ScrollToBottom(); return; }
            el.BringIntoView(new Rect(0, -el.ActualHeight, el.ActualWidth, el.ActualHeight * 3));
        });
    }

    static IEnumerable<ScrollViewer> Scrollers(DependencyObject d)
    {
        for (d = VisualTreeHelper.GetParent(d); d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollViewer sv) yield return sv;
    }

    // A row scrolled out of sight can't be pointed at or clicked.
    protected bool InView(FrameworkElement el, Point p) => Scrollers(el).All(sv => Pointer.Over(sv, w.Stage, p));

    public override void Open()
    {
        BuildStreak();
        // Long lists (Settings has 12 rows) get compact rows so the whole card, footer included, fits on screen.
        if (Rows.Count > 9) CompactRows();
        Refresh();
        base.Open();
    }

    // Mouse: pointing at a row focuses it, a click is A (on a setting's ‹ › ends: Left / Right).
    public override bool OnMouse(MouseAct m, Point p)
    {
        if (StreakMouse(m, p)) return true;
        int i = Rows.FindIndex(r => Pointer.Over(r.El, w.Stage, p) && InView(r.El, p));
        if (m == MouseAct.Move)
        {
            if (i >= 0 && i != Focus) { Focus = i; Sounds.Play("scroll"); Refresh(); }
            return i >= 0;
        }
        if (m == MouseAct.Down && i >= 0)
        {
            if (i != Focus) { Focus = i; Refresh(); }
            var r = Rows[i];
            if (r.Level != null && Pointer.Over(r.Track, w.Stage, p))
            {
                // on a level's bar: that point (to the nearest step of 10)
                double x = r.Track.TransformToAncestor(w.Stage).Inverse.Transform(p).X;
                string before = r.Value?.Invoke();
                r.SetLevel((int)Math.Round(Math.Clamp(x / TrackW, 0, 1) * 10) * 10);
                Refresh();
                Changed(r, before, 0);
                return true;
            }
            if (r.HasList && r.Activate == null) { Sounds.Play("select"); OpenList(r); return true; }
            if (r.Change != null && r.Activate == null)
            {
                // on the value: its left half steps back, otherwise forward
                var vt = r.ValueText;
                bool left = Pointer.Over(vt, w.Stage, p) && p.X < vt.TransformToAncestor(w.Stage).Transform(new Point(vt.ActualWidth / 2, 0)).X;
                OnAct(left ? Act.Left : Act.Right);
                return true;
            }
            OnAct(Act.Accept);
            return true;
        }
        return base.OnMouse(m, p);
    }

    public override void OnAct(Act a)
    {
        if (Loading) { if (a == Act.Back) Close(); return; }
        switch (a)
        {
            case Act.Up: if (Focus > 0) { Focus--; Sounds.Play("scroll"); Refresh(); } else if (!ScrollText(-1)) Anim.Edge(bump, -1, true); break;
            case Act.Down: if (Focus < Rows.Count - 1) { Focus++; Sounds.Play("scroll"); Refresh(); } else if (!ScrollText(1)) Anim.Edge(bump, 1, true); break;
            case Act.Left: case Act.Right:
                if (Rows.Count == 0) break;
                var r = Rows[Focus];
                if (r.Change != null)
                {
                    string before = r.Value?.Invoke();
                    int dir = a == Act.Right ? 1 : -1;
                    r.Change(dir); Refresh();
                    Changed(r, before, dir);
                }
                break;
            case Act.Accept:
                if (Rows.Count == 0) { Close(); break; }
                var row = Rows[Focus];
                if (row.Activate == null && !row.HasList && row.Change != null)
                {
                    string before = row.Value?.Invoke();
                    row.Change(1); Refresh();
                    if (!Changed(row, before, 0)) Sounds.Play("select");
                    break;
                }
                Sounds.Play("select");
                if (row.Activate != null) row.Activate();
                else if (row.HasList) OpenList(row);
                break;
            case Act.Back: Close(); break;
        }
    }

    // Up / Down (or the wheel) past the first or last row: a card whose text is longer than fits (the
    // changelog: one row under a long text) scrolls a step instead. False: nothing more to scroll.
    bool ScrollText(int dir)
    {
        var sv = ActiveScroller();
        if (sv == null) return false;
        double to = Math.Clamp(sv.VerticalOffset + dir * 160, 0, sv.ScrollableHeight);
        if (Math.Abs(to - sv.VerticalOffset) < 0.5) return false;
        sv.ScrollToVerticalOffset(to);
        return true;
    }
    public double ScrollForTest => ActiveScroller()?.VerticalOffset ?? -1;

    // A level already full, pressed again (A, Right or a click at its end): its bar dips a step and rises
    // back once the presses stop, so the press is felt.
    long dipAt;
    void Dip(Row r)
    {
        if (r.Fill == null) return;
        long at = dipAt = Environment.TickCount64;
        Anim.To(r.Fill, FrameworkElement.WidthProperty, TrackW * 0.9, 90);
        if (!r.Speaks) Sounds.Tick(0.9);
        Ui.After(380, () =>
        {
            if (dipAt != at) return;   // pressed again: it stays down a little longer
            Anim.To(r.Fill, FrameworkElement.WidthProperty, TrackW * Math.Clamp(r.Level(), 0, 100) / 100.0, 260, Anim.Back,
                done: () => { if (dipAt == at) r.Fill.BeginAnimation(FrameworkElement.WidthProperty, null); });
        });
        DipsForTest++;
    }
    public int DipsForTest;

    // A row's value was stepped (dir -1 / +1: Left / Right; 0: A or a click on its bar). The feedback
    //: On / Off switches click up or down, levels tick at a pitch that follows the level (the
    // sound levels play themselves instead), other values tick as before, and the new value swells a
    // little. A level already at its end gives the soft edge bump instead. True: a sound was played.
    bool Changed(Row r, string before, int dir)
    {
        string after = r.Value?.Invoke();
        if (after == before)
        {
            if (r.Level != null && dir >= 0 && r.Level() >= 100) { Dip(r); return true; }
            if (r.Level != null && dir != 0) { Anim.Edge(bump, dir); return true; }
            if (dir != 0 && !r.Silent) Sounds.Play("scroll");
            return dir != 0;
        }
        if (r.Switch.Visibility != Visibility.Visible) Anim.Pulse(r.ValueText);   // a switch slides instead
        if (before == "Off" && after == "On") Sounds.Toggle(true);
        else if (before == "On" && after == "Off") Sounds.Toggle(false);
        else if (r.Level != null) { if (!r.Speaks) Sounds.Tick(r.Level() / 100.0); }
        else if (r.Silent) { }   // it played its own sound
        else if (dir != 0) Sounds.Play("scroll");
        else return false;
        return true;
    }

    // ---- the list of a setting's choices (opens under its row, or over it near the bottom) ----
    const int ListMax = 8;
    Canvas listLayer;
    Border listBox;
    readonly StackPanel listRows = new();
    TextBlock listMore;
    readonly TranslateTransform listBump = new();
    List<string> listNames = new();
    Row listRow;
    int listFocus = -1, listTop;   // -1: closed

    public bool ListOpen => listFocus >= 0;
    public string ListItems => ListOpen ? string.Join("|", listNames) : "closed";
    public string ListFocused => ListOpen ? listNames[listFocus] : "closed";
    public string ListShown => ListOpen ? string.Join("|", listNames.Skip(listTop).Take(ListMax)) + (listMore.IsVisible || listNames.Count > ListMax ? "  (" + listMore.Text + ")" : "") : "closed";

    void OpenList(Row r)
    {
        listRow = r;
        listNames = r.Names().ToList();
        listFocus = Math.Clamp(r.Current(), 0, listNames.Count - 1);
        int cur = listFocus;
        if (listLayer == null)
        {
            listLayer = new Canvas();
            listMore = Fonts.Text("", 20, P.Text2);
            listMore.Margin = new Thickness(20, 6, 0, 2);
            listBox = new Border
            {
                Background = P.Card, CornerRadius = new CornerRadius(22), Padding = new Thickness(10), BorderThickness = new Thickness(1.5), BorderBrush = P.Line,
                MinWidth = 380, RenderTransform = listBump,
                Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Direction = 270, Opacity = 0.2, Color = Colors.Black },
                Child = new StackPanel { Children = { listRows, listMore } },
            };
            listLayer.Children.Add(listBox);
            Root.Children.Add(listLayer);
        }
        listRows.Children.Clear();
        for (int i = 0; i < listNames.Count; i++)
        {
            bool on = i == cur;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = Fonts.Text(listNames[i], 26, on ? P.Accent : P.Text, on ? FontWeights.Medium : FontWeights.Normal);
            t.VerticalAlignment = VerticalAlignment.Center; t.MaxWidth = 760;
            g.Children.Add(t);
            if (on)
            {
                var c = Art.Icon("check", 26, P.Accent);
                c.VerticalAlignment = VerticalAlignment.Center; c.Margin = new Thickness(24, 0, 0, 0);
                Grid.SetColumn(c, 1); g.Children.Add(c);
            }
            listRows.Children.Add(new Border { Height = 58, CornerRadius = new CornerRadius(15), Padding = new Thickness(20, 0, 20, 0), BorderThickness = new Thickness(2.5), Margin = new Thickness(0, 2, 0, 2), Child = g });
        }
        listTop = Math.Clamp(listFocus - ListMax / 2, 0, Math.Max(0, listNames.Count - ListMax));
        listLayer.Visibility = Visibility.Visible;
        RefreshList();
        PlaceList(r);
        Sounds.Play("menuopen");
        Anim.FromTo(listBox, UIElement.OpacityProperty, 0, 1, 160);
    }

    // Under the row, its right edge on the row's; over the row when there is no room below.
    void PlaceList(Row r)
    {
        listBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = Math.Max(listBox.DesiredSize.Width, 380), bh = listBox.DesiredSize.Height;
        double rootW = Root.ActualWidth > 0 ? Root.ActualWidth : 1920, rootH = Root.ActualHeight > 0 ? Root.ActualHeight : 1080;
        Rect b = new(rootW / 2 - bw / 2, rootH / 2, bw, 0);
        try { b = r.El.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, r.El.ActualWidth, r.El.ActualHeight)); } catch { /* not in the visual tree yet */ }
        double x = Math.Clamp(b.Right - bw, 20, Math.Max(20, rootW - bw - 20));
        double y = b.Bottom + 6;
        if (y + bh > rootH - 20) y = b.Top - 6 - bh;
        if (y < 20) y = Math.Max(20, rootH - 20 - bh);
        Canvas.SetLeft(listBox, x); Canvas.SetTop(listBox, y);
    }

    void RefreshList()
    {
        if (listFocus < listTop) listTop = listFocus;
        if (listFocus >= listTop + ListMax) listTop = listFocus - ListMax + 1;
        for (int i = 0; i < listRows.Children.Count; i++)
        {
            var b = (Border)listRows.Children[i]; bool f = i == listFocus;
            b.Visibility = i >= listTop && i < listTop + ListMax ? Visibility.Visible : Visibility.Collapsed;
            b.Background = f ? P.RowFocus : Brushes.Transparent;
            b.BorderBrush = f ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(b, f);
        }
        listMore.Text = listNames.Count > ListMax ? $"{listFocus + 1} of {listNames.Count}" : "";
        listMore.Visibility = listNames.Count > ListMax ? Visibility.Visible : Visibility.Collapsed;
    }

    void CloseList(bool sound)
    {
        if (listFocus < 0) return;
        listFocus = -1;
        if (listLayer != null) listLayer.Visibility = Visibility.Collapsed;
        if (sound) Sounds.Play("back");
    }

    void PickList(int i)
    {
        var r = listRow;
        bool same = i == r.Current();
        CloseList(false);
        Sounds.Play("select");
        if (!same) r.Pick(i);
        Refresh();
        OnListPicked();
    }

    // After a choice from the list (cards that window their rows lay them out again).
    protected virtual void OnListPicked() { }

    public void ListAct(Act a)
    {
        int n = listFocus;
        switch (a)
        {
            case Act.Up: n--; break;
            case Act.Down: n++; break;
            case Act.PageLeft: n -= ListMax; break;
            case Act.PageRight: n += ListMax; break;
            case Act.Accept: PickList(listFocus); return;
            case Act.Back: CloseList(true); return;
            default: return;
        }
        if (n < 0 || n >= listNames.Count)
        {
            bool atEnd = n < 0 ? listFocus == 0 : listFocus == listNames.Count - 1;
            if (atEnd) { Anim.Edge(listBump, a is Act.Up or Act.PageLeft ? -1 : 1, true); return; }
            n = Math.Clamp(n, 0, listNames.Count - 1);
        }
        listFocus = n; Sounds.Play("scroll"); RefreshList();
    }

    // Pointing selects, a click picks, a click outside closes the list; the wheel moves.
    public bool ListMouse(MouseAct m, Point p)
    {
        int r = -1;
        for (int j = 0; j < listRows.Children.Count; j++)
            if (Pointer.Over((FrameworkElement)listRows.Children[j], w.Stage, p)) { r = j; break; }
        switch (m)
        {
            case MouseAct.Move: if (r >= 0 && r != listFocus) { listFocus = r; Sounds.Play("scroll"); RefreshList(); } return true;
            case MouseAct.Down: if (r >= 0) PickList(r); else if (!Pointer.Over(listBox, w.Stage, p)) CloseList(true); return true;
            case MouseAct.WheelUp: ListAct(Act.Up); return true;
            case MouseAct.WheelDown: ListAct(Act.Down); return true;
            default: return true;   // the release of the click that opened it, and the rest
        }
    }

    // The tutorial: the row with that label.
    public FrameworkElement RowElement(string label) => Rows.FirstOrDefault(r => ((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text is string t && (t == label || Loc.ToEnglish(t) == label))?.El;

    // Test scripts: the middle of a choice in the open list, else of the row with that label (stage coordinates).
    public Point? CentreOf(string label)
    {
        FrameworkElement e = null;
        if (ListOpen && listNames.IndexOf(label) is int i and >= 0) e = (FrameworkElement)listRows.Children[i];
        else e = Rows.FirstOrDefault(r => ((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text is string t && (t == label || Loc.ToEnglish(t) == label))?.El;
        if (e == null || !e.IsVisible) return null;
        try { return e.TransformToAncestor(w.Stage).Transform(new Point(e.ActualWidth / 2, e.ActualHeight / 2)); } catch { return null; }
    }

    // Test scripts: the choice with that name, as if clicked.
    public bool PickForTest(string name)
    {
        int i = listNames.IndexOf(name);
        if (!ListOpen || i < 0) return false;
        PickList(i);
        return true;
    }
}

// Settings: a rail on the left (the profile at the top, the categories, Quit at the bottom) and the
// chosen category's settings on the right. Moving on the rail shows each category at once; Right or A
// goes into its list; B (or Left on a row that isn't a setting) comes back to the rail.
public sealed class SettingsCard : MenuCard
{
    sealed class Item
    {
        public string Name, Icon;
        public Action Build;     // a category: fills the right side
        public Action Run;       // an action instead (Quit)
        public Border El, Mark;
    }
    readonly List<Item> items = new();
    int rail;                    // the rail item with the focus
    int shown = -1;              // the category on the right
    bool inRail = true;
    readonly StackPanel pane = new();
    readonly Grid paneWrap;
    readonly Border head = new();
    readonly Grid railList = new();
    readonly TranslateTransform paneSlide = new(), railBump = new();
    readonly ScaleTransform railLand = new();
    int railLanded = -1;
    readonly Action devChanged, updateChanged;
    const double H = 880;

    public static SettingsCard Current;   // for test scripts
    public string Category => shown >= 0 ? Loc.ToEnglish(items[shown].Name) : "none";   // by its English name, in any language
    public bool InRail => inRail;
    public FrameworkElement RailElement(string name) => items.FirstOrDefault(i => i.Name == name || Loc.ToEnglish(i.Name) == name)?.El;   // the tutorial
    public override string FocusedLabel => inRail ? items[rail].Name : base.FocusedLabel;

    public SettingsCard(MainWindow w, string category = null) : base(w, null, 1500)
    {
        Box.Height = H; Box.Padding = new Thickness(26);
        var grid = new Grid { Height = H - 52 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Body.Children.Add(grid);

        // the rail
        var dock = new DockPanel();
        grid.Children.Add(new Border { Background = P.Bg, CornerRadius = new CornerRadius(22), Padding = new Thickness(14, 26, 14, 14), Child = dock });
        var title = Fonts.Text(T("home.settings"), 44, P.Text, FontWeights.Normal, Fonts.Display);
        title.Margin = new Thickness(20, 0, 0, 22);
        DockPanel.SetDock(title, Dock.Top); dock.Children.Add(title);
        railList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        railList.RowDefinitions.Add(new RowDefinition());
        railList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        railList.RenderTransform = railBump;
        dock.Children.Add(railList);
        var top = new StackPanel(); railList.Children.Add(top);
        var bottom = new StackPanel(); Grid.SetRow(bottom, 2); railList.Children.Add(bottom);

        // the right side: a heading, then the rows
        paneWrap = new Grid { RenderTransform = paneSlide };
        paneWrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        paneWrap.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumn(paneWrap, 2); grid.Children.Add(paneWrap);
        head.Margin = new Thickness(6, 26, 0, 20);
        paneWrap.Children.Add(head);
        var paneScroll = new ScrollViewer
        {
            Content = pane, Focusable = false, PanningMode = PanningMode.None, Margin = new Thickness(-Card.LightRoom),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        pane.Margin = new Thickness(Card.LightRoom);   // room for the running light, as in Card
        Grid.SetRow(paneScroll, 1);
        paneWrap.Children.Add(paneScroll);
        RowHost = pane;

        Add(top, T("home.profile"), null, BuildProfile);
        Add(top, T("settings.look_and_layout"), "look", BuildLook);
        Add(top, T("settings.sound"), "sound", BuildSound);
        Add(top, T("settings.controls"), "controller", BuildControls);
        Add(top, T("settings.library"), "library", BuildLibrary);
        Add(top, T("settings.network"), "globe", BuildOnline);
        Add(top, T("settings.about_and_updates"), "info", BuildAbout);
        Add(bottom, T("home.quit_ionea"), "door", null, () => new QuitCard(w).Open());

        int start = category == null ? 1 : items.FindIndex(i => i.Build != null && (string.Equals(i.Name, category, StringComparison.OrdinalIgnoreCase) || string.Equals(Loc.ToEnglish(i.Name), category, StringComparison.OrdinalIgnoreCase)));
        rail = start < 0 ? 1 : start;
        Show(rail, false);
        devChanged = () => { if (Category == "Controls") Refresh(); };
        updateChanged = () => { if (Category == "About and updates") Refresh(); };
    }

    void Add(Panel host, string name, string icon, Action build, Action run = null)
    {
        var it = new Item { Name = name, Icon = icon, Build = build, Run = run };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement pic = icon == null ? Art.Avatar(w.Profiles, w.Profiles.Active, 46) : Art.Icon(icon, 34, P.Text);
        pic.HorizontalAlignment = HorizontalAlignment.Left; pic.VerticalAlignment = VerticalAlignment.Center;
        if (icon != null) pic.Margin = new Thickness(6, 0, 0, 0);
        g.Children.Add(pic);
        var label = Fonts.Text(icon == null ? w.Profiles.Active?.Name ?? T("home.profile") : name, 30, P.Text);
        label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(label, 1); g.Children.Add(label);
        // the category on the right: a small accent bar at the item's left edge
        it.Mark = new Border { Width = 5, Height = 30, CornerRadius = new CornerRadius(2.5), Background = P.Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-15, 0, 0, 0), Opacity = 0 };
        g.Children.Add(it.Mark);
        it.El = new Border { Height = 76, CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 0, 18, 0), BorderThickness = new Thickness(3), Margin = new Thickness(0, 0, 0, 6), Child = g, RenderTransformOrigin = new Point(0.5, 0.5) };
        items.Add(it);
        host.Children.Add(it.El);
    }

    // Put a category on the right.
    void Show(int i, bool animate)
    {
        if (items[i].Build == null) return;
        shown = i;
        ClearRows();
        head.Child = Heading(items[i].Name);
        items[i].Build();
        Compact(8);
        if (animate)
        {
            Anim.FromTo(paneSlide, TranslateTransform.YProperty, 18, 0, 240);
            Anim.FromTo(paneWrap, UIElement.OpacityProperty, 0, 1, 200);
        }
        RefreshAll();
    }

    FrameworkElement Heading(string text) => Fonts.Text(text, 40, P.Text, FontWeights.Normal, Fonts.Display);

    void RefreshAll()
    {
        RowsActive = !inRail;
        Refresh();
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i]; bool f = inRail && i == rail;
            it.El.Background = f || (i == shown && !inRail) ? P.RowFocus : Brushes.Transparent;
            it.El.BorderBrush = f ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(it.El, f);
            it.El.RenderTransform = f ? railLand : null;
            it.Mark.Opacity = i == shown && !f ? 1 : 0;
        }
        int now = inRail ? rail : -1;
        if (now >= 0 && now != railLanded && railLanded >= 0)
        {
            Anim.FromTo(railLand, ScaleTransform.ScaleXProperty, 0.975, 1, 220, Anim.Back);
            Anim.FromTo(railLand, ScaleTransform.ScaleYProperty, 0.975, 1, 220, Anim.Back);
        }
        railLanded = now;
    }

    // ---- the categories ----

    void BuildProfile()
    {
        var pr = w.Profiles; var p = pr.Active;
        var h = new StackPanel { Orientation = Orientation.Horizontal };
        var av = Art.Avatar(pr, p, 104); av.Margin = new Thickness(0, 0, 26, 0);
        h.Children.Add(av);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Heading(p?.Name ?? T("settings.no_profile_yet")));
        if (p != null)
        {
            int n = pr.Data.Profiles.Count;
            names.Children.Add(Fonts.Text(Loc.N("settings.played_profiles", n, Profiles.Hours(pr.Summarize(w.Lib).Total)), 26, P.Text2));
        }
        h.Children.Add(names);
        head.Child = h;
        if (p == null) return;
        BeginBox(null);
        Opens(AddRow(T("settings.profile_and_play_stats"), activate: () => { Close(false); new ProfileCard(w).Open(); }));
        if (pr.Data.Profiles.Count > 1)
            Opens(AddRow(T("settings.switch_profile"), () => Loc.N("settings.other_profiles", pr.Data.Profiles.Count - 1), activate: () =>
            {
                if (!w.CanSwitchProfile(out string why)) { new MessageCard(w, T("home.can_t_switch_now"), why + T("settings.every_profile_s_saves_stay_their")).Open(); return; }
                Close(false);
                new WhoCard(w, np => { w.SwitchProfile(np); w.Toasts.Show(T("settings.now_playing"), np.Name, "info"); }).Open();
            }));
        EndBox();
    }

    // Language (Loc.cs): Automatic follows Windows when there is a file for its language. New text shows at once;
    // what is already on screen changes when it is built again (all of it after a restart).
    void AddLanguage()
    {
        var s = w.Settings;
        List<(string Id, string Name)> All() { var l = Loc.Languages(); l.Insert(0, ("auto", T("common.automatic"))); return l; }
        int Cur() => Math.Max(0, All().FindIndex(l => l.Id.Equals(s.Language ?? "auto", StringComparison.OrdinalIgnoreCase)));
        void Set(int i)
        {
            var all = All();
            s.Language = all[(i + all.Count) % all.Count].Id; s.Save();
            Loc.Use(s.Language);
            w.Toasts.Show(T("settings.language"), T("settings.language_restart"), "info");
        }
        WithList(AddRow(T("settings.language"), () => All()[Cur()].Name, d => Set(Cur() + d)), () => All().Select(l => l.Name).ToList(), Cur, Set);
    }

    void BuildLook()
    {
        // In boxes: Appearance, Home screen, Window, In games, Intro and outro.
        var s = w.Settings;
        BeginBox(T("settings.appearance"));
        AddRow(T("settings.theme"), () => s.Dark ? T("settings.dark") : T("settings.light"), _ => w.SetDark(!s.Dark));
        AddSlider(T("settings.brightness"), () => s.Brightness, v => w.SetBrightness(v));   // 40-100 % (MainWindow.SetBrightness)
        int nc = ColourPreset.All.Length;
        WithList(AddRow(T("settings.colours"), () => ColourPreset.Of(s.Colours).Name, d => w.SetColours(ColourPreset.All[(ColourPreset.IndexOf(s.Colours) + d + nc) % nc].Id)),
            () => ColourPreset.All.Select(c => c.Name).ToList(), () => ColourPreset.IndexOf(s.Colours), i => w.SetColours(ColourPreset.All[i].Id));
        WithList(AddRow(T("settings.icon_shape"), () => Art.ShapeName(s.IconShape), d => w.SetIconShape(Art.Shapes[(Math.Max(0, Array.IndexOf(Art.Shapes, s.IconShape)) + d + 3) % 3])),
            () => Art.ShapeNames.ToList(), () => Math.Max(0, Array.IndexOf(Art.Shapes, s.IconShape)), i => w.SetIconShape(Art.Shapes[i]));
        // a look choice; kept in the sound settings, where it was first
        AddRow(T("settings.reduce_effects"), () => s.Sound.ReduceEffects ? T("common.on") : T("common.off"), _ =>
        {
            s.Sound.ReduceEffects = !s.Sound.ReduceEffects; s.Save();
            Anim.SetReduced(s.Sound.ReduceEffects);
        });
        BeginBox(T("settings.home_screen"));
        WithList(AddRow(T("settings.layout"), () => HomeScreen.Zooms[(int)s.Zoom].Name, d => w.Home?.SetZoom((Zoom)(((int)s.Zoom + d + 3) % 3))),
            () => HomeScreen.Zooms.Select(z => z.Name).ToList(), () => (int)s.Zoom, i => w.Home?.SetZoom((Zoom)i));
        AddRow(T("settings.sort"), () => s.Order == "name" ? T("settings.a_to_z") : T("settings.last_played_first"), _ =>
        {
            s.Order = s.Order == "name" ? "recent" : "name"; s.Save();
            w.Home?.Rebuild(null);
        });
        BeginBox(T("settings.window"));
        AddLanguage();
        AddRow(T("settings.screen_mode"), () => s.Fullscreen ? T("settings.full_screen") : T("settings.window"), _ => w.SetFullscreen(!s.Fullscreen));
        BeginBox(T("settings.in_games"));
        Opens(AddRow(T("settings.game_display"), () => DisplayLogic.Summary(w, null), activate: () => new DisplayCard(w, null).Open()));   // every game's default (Display.cs)
        Opens(AddRow(T("settings.performance_overlay"), () => OverlayLogic.Summary(w, null), activate: () => new OverlayCard(w, null).Open()));   // the in-game overlay's default (Overlay.cs)
        // Games played inside the launcher run in step with the display's refresh when their rate is close (DisplaySync.cs).
        Aside(AddRow(T("settings.match_the_display"), () => s.SyncToDisplay ? T("common.on") : T("common.off"), _ => { s.SyncToDisplay = !s.SyncToDisplay; s.Save(); }), T("settings.smoother_motion"));
        // Standalone emulators: the Home menu freezes the whole program only when asked (HomeMenu.cs, PauseLogic).
        Aside(AddRow(T("settings.freeze_the_emulator"), () => s.FreezeEmulator ? T("common.on") : T("common.off"), _ => { s.FreezeEmulator = !s.FreezeEmulator; s.Save(); }), T("settings.freeze_the_emulator_aside"));
        // Window capture (Discord, OBS) sees the game: emulators as child windows, hosted pictures drawn by WPF (EmbedLogic.Owned, DirectPicture.Off).
        Aside(AddRow(T("settings.streaming_mode"), () => s.StreamingMode ? T("common.on") : T("common.off"), _ => { s.StreamingMode = !s.StreamingMode; s.Save(); }), T("settings.streaming_mode_aside"));
        // Games played inside the launcher show a few frames ahead, so a press shows sooner (RunAhead.cs).
        var pp = s.Play ??= new PlayPrefs();
        int ra = RunAhead.Choices.Length;
        int RaAt() => Math.Max(0, Array.IndexOf(RunAhead.Choices, RunAhead.Clean(pp.RunAhead)));
        void Ra(int i) { pp.RunAhead = RunAhead.Choices[i]; s.Save(); }
        Aside(WithList(AddRow(T("settings.run_ahead"), () => RunAhead.Name(RunAhead.Clean(pp.RunAhead)), d => Ra((RaAt() + d + ra) % ra)),
            () => RunAhead.Choices.Select(RunAhead.Name).ToList(), RaAt, Ra), T("settings.less_input_lag_needs_a_fast"));
        Opens(AddRow(T("settings.fast_forward_and_rewind"), () => PlayCard.Summary(w), activate: () => new PlayCard(w).Open()));   // games inside the launcher (FastForward.cs, Rewind.cs)
        BeginBox(T("settings.intro_and_outro"));
        AddRow(T("settings.intro_at_start"), () => s.Intro ? T("common.on") : T("common.off"), _ => { s.Intro = !s.Intro; s.Save(); });
        AddRow(T("settings.outro_when_closing"), () => s.Outro ? T("common.on") : T("common.off"), _ => { s.Outro = !s.Outro; s.Save(); });
        AddRow(T("settings.watch_the_intro"), () => "", activate: () => { Close(false); w.ReplayIntro(); });
        EndBox();
    }

    void BuildSound() => SoundRows();

    void BuildControls()
    {
        var s = w.Settings;
        BeginBox(T("settings.devices"));
        foreach (var kind in new[] { "keyboard", "pad", "mouse" })
        {
            string k = kind;
            var r = Opens(AddRow(InputDevices.Noun(k), () => InputCard.Status(w, k), activate: () => DeviceScreen.For(w, k, RefreshAll).Open()));
            r.Dim = () => !(w.Devs.Connected(k) && s.Input.On(k));
        }
        AddRow(T("settings.pop_up_when_connected"), () => s.Input.Popups ? T("common.on") : T("common.off"), _ => { s.Input.Popups = !s.Input.Popups; s.Save(); });
        BeginBox(T("settings.keys_and_buttons"));
        Opens(AddRow(T("settings.launcher_keys"), () => LauncherKeys.Summary(s.Input), activate: () => new LauncherKeysCard(w).Open()));
        Opens(AddRow(T("settings.game_controls"), () => ControlsCard.Summary(w), activate: () => new ControlsCard(w, RefreshAll).Open()));
        EndBox();
    }

    void BuildLibrary()
    {
        BeginBox(T("settings.library"));
        Opens(AddRow(EmulatorsCard.Title, () => EmulatorsCard.Count(w), activate: () => new EmulatorsCard(w).Open()));
        Opens(AddRow(T("settings.storage"), () => { int n = Storage.Places(w).Count; return n == 1 ? T("settings.1_place") : $"{n} places"; },
            activate: () => new StorageCard(w).Open()));
        Opens(AddRow(T("settings.removed_games"), () => w.Lib.RemovedGames().Count.ToString(), activate: () => new RemovedCard(w).Open()));
        BeginBox(T("common.add"));
        Opens(AddRow(T("home.add_game"), activate: () => new AddChoiceCard(w).Open()));
        Opens(AddRow(T("settings.look_for_emulators"), () => T("settings.on_this_pc"), activate: () => ScanCard.Run(w, false, null)));
        BeginBox(T("settings.advanced"));
        // On: emulators and native ports run under Ionea's name, so Task Manager shows them in Ionea's group (from the next start). Off by default.
        AddRow(T("settings.group_in_task_manager"), () => w.Settings.TaskManagerGroup ? T("common.on") : T("common.off"), _ =>
        {
            w.Settings.TaskManagerGroup = !w.Settings.TaskManagerGroup; w.Settings.Save();
            EmbedLogic.Group = w.Settings.TaskManagerGroup;
        });
        EndBox();
        // On the page (RowHost), not the card's body: the body is never cleared, so it stacked up once per visit.
        Note(T("settings.on_emulators_show_in_ionea_s"), 22, new Thickness(28, -2, 0, 10), RowHost);
    }

    // Settings > Network (Online.cs): the network connection, the sources with their hosts, the SteamGridDB key, the Discord
    // status, and the activity log (kept on this PC only).
    void BuildOnline()
    {
        OnlineRows(() => Opens(AddRow(T("settings.network_activity"), () => Online.Entries.Count.ToString(), activate: () => new OnlineLogCard(w).Open())));
        BeginBox(T("settings.services"));
        // SteamGridDB answers only with the user's own key (steamgriddb.com, Preferences > API). Left / Right clears it.
        NetRow(AddRow(T("settings.steamgriddb_key"), () => string.IsNullOrEmpty(w.Settings.SteamGridKey) ? T("common.none") : T("settings.set"),
            _ => { if (!OfflineSaysNo() && w.Settings.SteamGridKey != null) { w.Settings.SteamGridKey = null; w.Settings.Save(); Refresh(); } },
            () => { if (!OfflineSaysNo()) new TextCard(w, T("settings.steamgriddb_key"), w.Settings.SteamGridKey ?? "", 64, v => { w.Settings.SteamGridKey = v.Trim(); w.Settings.Save(); Refresh(); }).Open(); }));
        DiscordRow();
        EndBox();
    }

    // Discord status (per profile): through the Discord app on this PC, which shows it online. Greyed offline.
    void DiscordRow() => NetRow(Aside(AddRow(T("settings.discord_status"), () => w.Settings.Discord ? T("common.on") : T("common.off"),
        _ => { if (OfflineSaysNo()) return; w.Settings.Discord = !w.Settings.Discord; w.Settings.Save(); w.RefreshDiscord(); }), T("settings.through_the_discord_app_on_this")));

    // Settings > About and updates (Updater.cs): the version, where updates come from, check, update, go back.
    void BuildAbout()
    {
        var u = w.Settings.Updates;
        BeginBox("Ionea");
        AddRow(T("settings.version"), () => AppVersion.Text);
        Opens(AddRow(T("settings.changelog"), activate: () => new ChangelogCard(w).Open()));
        AddRow(T("settings.watch_the_tutorial"), () => "", activate: () => { Close(false); w.Tutor.Begin(); });
        BeginBox(T("settings.updates"));
        // Ionea's own releases, built in (Updater.Owner/Name): shown, never changed. Goes online only when asked
        // for here (a check, Update notification, Auto update) and only with the network connection on.
        Aside(AddRow(T("settings.update_from"), () => "github.com/" + Updater.RepoName),
            Online.Offline ? T("settings.network_connection_is_off") : T("settings.network_connection_is_on"));
        AddRow(T("settings.check_for_updates"), () => Updater.Status, activate: () => _ = Updater.Check(w));
        var now = AddRow(T("settings.update_now"), () => Updater.OfferText, activate: () => Updater.UpdateNow(w));
        now.Dim = () => Updater.Offer == null;
        // Both off by default; at launch, with the network connection on (Updater.AtLaunch).
        Aside(AddRow(T("settings.update_notification"), () => u.CheckAtStart ? T("common.on") : T("common.off"), _ => { u.CheckAtStart = !u.CheckAtStart; w.Settings.Save(); Refresh(); }),
            T("settings.at_launch_says_when_a_new"));
        Aside(AddRow(T("settings.auto_update"), () => u.AutoUpdate ? T("common.on") : T("common.off"), _ => { u.AutoUpdate = !u.AutoUpdate; w.Settings.Save(); Refresh(); }),
            T("settings.at_launch_installs_a_new"));
        AddRow(T("settings.last_checked"), () => Updater.LastCheckedText);
        BeginBox(T("settings.previous_version"));
        var back = AddRow(T("settings.go_back"), () => Updater.PreviousVersion is Version v ? AppVersion.Three(v) + (v.Revision > 0 ? $" ({v.Revision})" : "") : T("common.none"),
            activate: () =>
            {
                if (Updater.PreviousVersion == null) new MessageCard(w, T("settings.no_previous_version"), T("settings.ionea_keeps_the_version_before_each")).Open();
                else new GoBackCard(w).Open();
            });
        back.Dim = () => Updater.PreviousVersion == null;
        EndBox();
    }

    // ---- input ----

    void MoveRail(int d)
    {
        int n = rail + d;
        if (n < 0 || n >= items.Count) { Anim.Edge(railBump, d, true); return; }
        rail = n;
        Sounds.Play("scroll");
        if (items[n].Build != null && n != shown) Show(n, true); else RefreshAll();
    }

    public override void OnAct(Act a)
    {
        if (inRail)
        {
            switch (a)
            {
                case Act.Up: MoveRail(-1); break;
                case Act.Down: MoveRail(1); break;
                case Act.Right: case Act.Accept:
                    var it = items[rail];
                    if (it.Run != null) { if (a == Act.Accept) { Sounds.Play("select"); it.Run(); } break; }
                    if (Rows.Count == 0) break;
                    inRail = false; Focus = 0;
                    Sounds.Play(a == Act.Accept ? "select" : "scroll");
                    RefreshAll();
                    break;
                case Act.Back: Close(); break;
            }
            return;
        }
        if (a == Act.Back || (a == Act.Left && (Rows.Count == 0 || Rows[Focus].Change == null)))
        {
            inRail = true; rail = shown;
            Sounds.Play(a == Act.Back ? "back" : "scroll");
            RefreshAll();
            return;
        }
        base.OnAct(a);
    }

    public override bool OnMouse(MouseAct m, Point p)
    {
        if (m is MouseAct.Move or MouseAct.Down)
        {
            int i = items.FindIndex(it => Pointer.Over(it.El, w.Stage, p));
            if (i >= 0)
            {
                if (i != rail || !inRail)
                {
                    rail = i; inRail = true;
                    if (m == MouseAct.Move) Sounds.Play("scroll");
                    if (items[i].Build != null && i != shown) Show(i, true); else RefreshAll();
                }
                if (m == MouseAct.Down && items[i].Run != null) { Sounds.Play("select"); items[i].Run(); }
                return true;
            }
            if (inRail && Rows.Any(r => Pointer.Over(r.El, w.Stage, p))) { inRail = false; RefreshAll(); }
        }
        return base.OnMouse(m, p);
    }

    public override void OnTop() => RefreshAll();

    public override void Open()
    {
        Current = this;
        w.Devs.Changed += devChanged;
        Updater.Changed += updateChanged;
        base.Open();
        RefreshAll();
    }

    public override void Close(bool sound = true)
    {
        w.Devs.Changed -= devChanged;
        Updater.Changed -= updateChanged;
        if (Current == this) Current = null;
        base.Close(sound);
    }
}

// The Sounds card on its own (the same rows as Settings > Sound). Kept per profile (ProfilePrefs).
public sealed class SoundCard : MenuCard
{
    public Action Closed;

    public SoundCard(MainWindow w) : base(w, T("settings.sounds")) => SoundRows();

    public override void Close(bool sound = true)
    {
        base.Close(sound);
        Closed?.Invoke();
    }
}

// Settings > Sound > Sound groups: the level of each group of sounds, under Navigation and Actions.
public sealed class SoundGroupsCard : MenuCard
{
    public SoundGroupsCard(MainWindow w) : base(w, T("settings.sound_groups")) => SoundGroupRows();
}

// Type to filter: the home row behind the card shows the matches live. Enter keeps the results
// (B on home clears them), Esc clears and closes.
public sealed class SearchCard : Card, IScreen   // re-implement IScreen so WantsText is used
{
    readonly TextBox box;
    public bool WantsText => true;

    public SearchCard(MainWindow w, string text) : base(w, null, 1100)
    {
        Root.Children[0].Opacity = 0.35;                       // keep the results visible behind
        Box.VerticalAlignment = VerticalAlignment.Top; Box.Margin = new Thickness(0, 30, 0, 0);
        Box.Padding = new Thickness(40, 22, 40, 22);
        var row = new DockPanel();
        var icon = Art.Icon("search", 48, P.Accent); icon.Margin = new Thickness(0, 0, 22, 0);
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        box = new TextBox
        {
            Text = text, FontSize = 44, FontFamily = Fonts.Display, MaxLength = 40, Foreground = P.Text, CaretBrush = P.Accent,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionBrush = P.Accent, VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(box);
        Body.Children.Add(row);
        hint = Note("", 22, new Thickness(70, 8, 0, 0));
        box.TextChanged += (s, e) => Filter();

        // The focus ring round the search bar, the same one the selected game wears. Rebuilt when the bar
        // grows (the "3 games" line), and it moves and scales with the card.
        var ringHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = Box.VerticalAlignment, Margin = Box.Margin,
            RenderTransform = Box.RenderTransform, RenderTransformOrigin = Box.RenderTransformOrigin, IsHitTestVisible = false,
        };
        Root.Children.Add(ringHost);
        Box.SizeChanged += (s, e) =>
        {
            ringHost.Width = Box.ActualWidth; ringHost.Height = Box.ActualHeight;
            ringHost.Children.Clear();
            ringHost.Children.Add(Art.BoxRing(Box.ActualWidth, Box.ActualHeight, Box.CornerRadius.TopLeft, out var glow));
            Anim.Glow(glow);
        };
    }

    readonly TextBlock hint;

    void Filter()
    {
        string q = box.Text.Trim();
        w.Home?.SetFilter(q);
        int n = (w.Home?.Count ?? 1) - 1;
        string found = q.Length == 0 ? "" : n == 0 ? T("home.no_games_match") : n == 1 ? "1 game" : n + " games";
        hint.Text = found;
    }

    public override void Close(bool sound = true)
    {
        w.Home?.ShowViewText(true);
        base.Close(sound);
    }

    public override void Open()
    {
        base.Open();
        w.Home?.ShowViewText(false);
        Filter();
        box.CaretIndex = box.Text.Length;
        Root.Dispatcher.BeginInvoke(() => { box.Focus(); System.Windows.Input.Keyboard.Focus(box); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public override void OnAct(Act a)
    {
        if (a == Act.Back) { w.Home?.SetFilter(null); Close(); }
        else if (a == Act.Accept || a == Act.Down)
        {
            if (box.Text.Trim().Length == 0) w.Home?.SetFilter(null);
            Sounds.Play("select");
            Close(false);
        }
    }
}

// X / F2 / Delete on a game: start it, move it to or out of a folder, or take it off the list.
public sealed class GameMenuCard : MenuCard
{
    public GameMenuCard(MainWindow w, Game g) : base(w, g.Name, 860)
    {
        // Bundled: play and info first, then how it plays, then where it sits.
        AddRow(T("home.start"), activate: () => { Close(false); w.Home?.ActivateSelected(); });
        Opens(AddRow(T("settings.game_info"), activate: () => { Close(false); new GameInfoCard(w, g).Open(); }));
        bool hasSettings = GameSettings.Any(w, g);
        bool pick = g.Source == "rom" && g.Rom != null && w.Emus.ForFile(g.Rom).Count(e => e.Present) > 1;
        if (pick || hasSettings) Section(T("settings.how_it_plays"));
        if (g.Source == "rom" && g.Rom != null)
        {
            // Which emulator plays it: a core (inside the launcher) or a standalone program.
            var emus = w.Emus.ForFile(g.Rom).Where(e => e.Present).ToList();
            static string EmuName(Emulator e) => e == null ? "none" : e.IsCore ? e.Name + " (inside Ionea)" : e.Name;
            void UseEmu(Emulator e) { g.Emulator = e.Id; w.Lib.Save(); Log.Write($"\"{g.Name}\" -> emulator {e.Name}"); }
            if (emus.Count > 1)
                WithList(AddRow(T("home.emulator"), () => EmuName(w.Emus.Get(g.Emulator)), d =>
                {
                    int i = emus.FindIndex(e => e.Id == g.Emulator);
                    UseEmu(emus[((i < 0 ? 0 : i) + d + emus.Count) % emus.Count]);
                }), () => emus.Select(EmuName).ToList(), () => emus.FindIndex(e => e.Id == g.Emulator), i => UseEmu(emus[i]));
        }
        if (hasSettings)
            Opens(AddRow(T("settings.game_settings"), () => GameSettings.Summary(w, g),
                activate: () => { Close(false); GameSettings.Open(w, g); }));
        Section(T("settings.organise"));
        if (Storage.CanMove(g))
            AddRow(T("settings.stored_on"), () => Storage.PlaceOf(w, g.Rom)?.Name ?? System.IO.Path.GetDirectoryName(g.Rom), activate: () =>
            {
                if (w.GameMenu.Game == g) { new MessageCard(w, T("settings.the_game_is_running"), T("settings.close_it_first_then_move_it")).Open(); return; }
                if (!System.IO.File.Exists(g.Rom)) { new MessageCard(w, T("settings.can_t_move_this_game"), T("settings.the_game_file_is_missing") + g.Rom).Open(); return; }
                Close(false);
                new PlacePickCard(w, new List<Game> { g }, T("settings.move_to", g.Name)).Open();
            });
        AddRow(g.Folder == null ? T("settings.put_in_a_container") : T("settings.move_to_another_container"), activate: () => { Close(false); new FolderPickCard(w, g).Open(); });
        if (g.Folder != null)
            AddRow(T("settings.take_out_of", g.Folder), activate: () =>
            {
                g.Folder = null; w.Lib.Save(); Close(false);
                w.Home?.Rebuild(null);
                w.Toasts.Show(T("settings.moved_to_the_home_row"), g.Name, "folder");
            });
        AddRow(T("settings.remove_from_the_list"), activate: () =>
        {
            g.Removed = true; w.Lib.Save(); Close(false);
            Log.Write($"removed \"{g.Name}\" from the list");
            w.Home?.Rebuild(null);
            w.Toasts.Show(T("settings.removed_from_the_list"), g.Name, "info");
        });
    }
}

public sealed class FolderPickCard : MenuCard
{
    public FolderPickCard(MainWindow w, Game g) : base(w, g.Folder == null ? T("settings.put_in_a_container") : T("settings.move"), 860)
    {
        // A game in a container can go back out to the home row (first row), or to another container.
        if (g.Folder != null)
            AddRow(T("settings.back_to_the_home_row"), activate: () =>
            {
                Close(false);
                string old = g.Folder;
                Log.Write($"\"{g.Name}\" taken out of folder \"{old}\"");
                g.Folder = null; w.Lib.Save();
                // The last game out of the open container: it is gone, so back to the home row on that game.
                bool gone = !w.Lib.Folders().Any(f => string.Equals(f, old, StringComparison.CurrentCultureIgnoreCase));
                if (gone && w.Home?.View == "folder:" + old) w.Home.SetView(null, null, g.Id);
                else w.Home?.Rebuild(null);
                w.Toasts.Show(T("settings.moved_to_the_home_row"), g.Name, "folder");
            });
        void MoveTo(string f)
        {
            g.Folder = f; w.Lib.Save();
            Log.Write($"\"{g.Name}\" -> folder \"{f}\"");
            w.Home?.Rebuild(null);
            w.Toasts.Show(T("settings.moved_to", f), g.Name, "folder");
        }
        foreach (string f in w.Lib.Folders())
            if (!string.Equals(f, g.Folder, StringComparison.CurrentCultureIgnoreCase))
                AddRow(f, activate: () => { Close(false); MoveTo(f); });
        AddRow(T("settings.new_container"), activate: () =>
        {
            Close(false);
            new TextCard(w, T("settings.name_the_new_container"), "", 28, MoveTo).Open();
        });
        AddRow(T("common.cancel"), activate: () => Close());
    }
}

public sealed class RemovedCard : MenuCard
{
    public RemovedCard(MainWindow w) : base(w, T("settings.removed_games"), 860)
    {
        var list = w.Lib.RemovedGames();
        if (list.Count == 0) Note(T("settings.nothing_has_been_removed"), 28, new Thickness(4, 0, 0, 18));
        foreach (var g in list)
            AddRow(g.Name, () => T("settings.restore"), activate: () =>
            {
                g.Removed = false; w.Lib.Save(); Close(false);
                Log.Write($"restored \"{g.Name}\"");
                w.Home?.Rebuild(g.Folder == null ? g.Id : null);
                w.Toasts.Show(T("settings.back_on_the_list"), g.Name, "plus");
                if (w.Lib.RemovedGames().Count > 0) new RemovedCard(w).Open();
            });
        AddRow(T("common.done"), activate: () => Close());
    }
}

public sealed class MessageCard : MenuCard
{
    public MessageCard(MainWindow w, string title, string text) : base(w, title, 860)
    {
        Note(text, 30, new Thickness(4, 0, 0, 26));
        AddRow(T("common.ok"), activate: () => Close());
    }
}

// Settings > Quit launcher asks first. Resume (focused) goes back to Settings; Quit closes the launcher.
public sealed class QuitCard : MenuCard
{
    public QuitCard(MainWindow w) : base(w, T("settings.quit_ionea"), 720)
    {
        AddRow(T("home.resume"), activate: () => Close());
        WithIcon(AddRow(T("settings.quit"), activate: () => w.Close()), "door");
    }
}

// An emulator asked to close is still running after a while (GameMenu.CloseGame). It may still be writing its
// saves, so it is only ended when the user says so. Keep waiting (focused; Back too) asks again later. The card
// goes away by itself once the game has ended. While the launcher quits it sits over the closing screen.
public sealed class StillClosingCard : MenuCard
{
    readonly Action wait;
    bool answered, shut;

    public StillClosingCard(MainWindow w, string name, Action wait, Action end) : base(w, T("menu.is_still_closing", name), 900)
    {
        this.wait = wait;
        Note(T("menu.still_closing_note"), 30, new Thickness(4, 0, 0, 26));
        AddRow(T("menu.keep_waiting"), activate: () => Close());
        AddRow(T("menu.end_it_now"), activate: () => { answered = true; Close(); end(); });
    }

    public override void Open()
    {
        base.Open();
        if (w.Quitting) { w.CardLayer.Children.Remove(Root); w.LaunchLayer.Children.Add(Root); }
    }

    public override void Close(bool sound = true)
    {
        if (shut) return;
        shut = true;
        base.Close(sound);
        Ui.After(200, () => w.LaunchLayer.Children.Remove(Root));
        if (!answered) wait();
    }

    // The game ended meanwhile: nothing left to answer.
    public void Gone()
    {
        if (shut) return;
        answered = true;
        Close(false);
    }
}

// One-line text entry: Enter saves, Esc cancels.
public sealed class TextCard : Card, IScreen
{
    readonly TextBox box;
    readonly TextBlock hint;
    readonly Action<string> ok;
    readonly Func<string, string> check;   // null = fine, otherwise what's wrong
    readonly TranslateTransform shake = new();
    public bool WantsText => true;

    public TextCard(MainWindow w, string title, string value, int max, Action<string> onOk, Func<string, string> check = null) : base(w, title)
    {
        ok = onOk; this.check = check;
        box = new TextBox
        {
            Text = value ?? "", FontSize = 44, FontFamily = Fonts.Display, MaxLength = max, Foreground = P.Text, CaretBrush = P.Accent,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionBrush = P.Accent,
        };
        Body.Children.Add(box);
        Body.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = P.Accent, Margin = new Thickness(0, 4, 0, 0), RenderTransform = shake });
        hint = Note("", 24, new Thickness(4, 22, 0, 0));
        box.TextChanged += (s, e) => { hint.Text = ""; hint.Foreground = P.Text2; };
    }

    public override void Open()
    {
        base.Open();
        box.SelectAll();
        Root.Dispatcher.BeginInvoke(() => { box.Focus(); System.Windows.Input.Keyboard.Focus(box); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public override void OnAct(Act a)
    {
        if (a == Act.Back) { Close(); return; }
        if (a != Act.Accept) return;
        string v = System.Text.RegularExpressions.Regex.Replace(box.Text ?? "", @"\s+", " ").Trim();
        string err = v.Length == 0 ? T("settings.type_a_name_first") : check?.Invoke(v);
        if (err != null) { hint.Text = err; hint.Foreground = P.Error; Anim.Refuse(shake); return; }
        Sounds.Play("select");
        Close(false);
        ok(v);
    }
}
