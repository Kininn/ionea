#nullable disable
// "+" on Home opens Add: Game (the old choices), Emulator (the Add emulator card) or Container, which asks for a name and then
// which games go in it. A container is the same thing as a folder in the library (Game.Folder).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

public sealed class AddMenuCard : MenuCard
{
    public AddMenuCard(MainWindow w) : base(w, T("common.add"), 760)
    {
        Opens(WithIcon(AddRow(T("home.game"), activate: () => { Close(false); new AddChoiceCard(w).Open(); }), "plus"));
        Opens(WithIcon(AddRow(T("home.emulator"), activate: () => { Close(false); new AddEmulatorCard(w).Open(); }), "chip"));
        Opens(WithIcon(AddRow(T("home.container"), activate: () => { Close(false); ContainerPickCard.Start(w); }), "folder"));
        Opens(WithIcon(AddRow(T("settings.import_a_folder"), () => T("settings.games_and_emulators_in_it"), activate: () => { Close(false); ScanCard.PickFolder(w); }), "search"));
        AddRow(T("common.cancel"), activate: () => Close());
    }
}

// Add container, step 2: tick the games that go in it (typing narrows the list), then "Make container".
// Games already in another container can be ticked too: they move.
public sealed class ContainerPickCard : Card, IScreen
{
    const int Visible = 6;
    const double RowH = 96;

    readonly string name;
    readonly List<Game> all;
    List<Game> shown = new();
    readonly HashSet<string> picked = new();
    readonly TextBox box;
    readonly TextBlock hint, doneText;
    readonly StackPanel list = new();
    readonly Border done;
    readonly TranslateTransform scroll = new(), shake = new();
    readonly Dictionary<string, Border> checks = new();
    int focus, top;   // focus == shown.Count: the "Make container" button
    public bool WantsText => true;
    public int Count => shown.Count;
    public int Picked => picked.Count;
    public string FocusName => focus < shown.Count ? shown[focus].Name : "done";

    // Step 1: the name. A name already used by a container is refused.
    public static void Start(MainWindow w) =>
        new TextCard(w, T("home.name_the_container"), "", 28, n => new ContainerPickCard(w, n).Open(),
            n => w.Lib.Folders().Any(f => string.Equals(f, n, StringComparison.CurrentCultureIgnoreCase))
                ? T("home.there_is_already_a_container_called") : null).Open();

    public ContainerPickCard(MainWindow w, string containerName) : base(w, T("home.games_for", containerName), 1300)
    {
        name = containerName;
        all = w.Lib.Data.Games.Where(g => !g.Removed).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var icon = Art.Icon("search", 40, P.Accent); icon.Margin = new Thickness(4, 0, 18, 0);
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        box = new TextBox
        {
            FontSize = 36, FontFamily = Fonts.Display, MaxLength = 40, Foreground = P.Text, CaretBrush = P.Accent,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionBrush = P.Accent, VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(box);
        Body.Children.Add(row);
        Body.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = P.Accent, Margin = new Thickness(0, 0, 0, 18), RenderTransform = shake });

        var view = new Grid { Height = Visible * RowH, ClipToBounds = true };
        list.RenderTransform = scroll;
        list.VerticalAlignment = VerticalAlignment.Top;
        view.Children.Add(list);
        Body.Children.Add(view);
        hint = Note("", 22, new Thickness(4, 14, 0, 14));

        doneText = Fonts.Text("", 30, P.Text, FontWeights.SemiBold);
        doneText.HorizontalAlignment = HorizontalAlignment.Center; doneText.VerticalAlignment = VerticalAlignment.Center;
        done = new Border { Height = 76, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(3), Child = doneText };
        Body.Children.Add(done);

        box.TextChanged += (s, e) => Filter();
        Filter();
    }

    void Filter()
    {
        string q = box.Text.Trim();
        shown = q.Length == 0 ? all : all.Where(g => g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        focus = 0; top = 0;
        Build();
        Hint(null);
    }

    void Hint(string error)
    {
        hint.Foreground = error != null ? P.Error : P.Text2;
        hint.Text = error ?? (all.Count == 0 ? T("home.there_are_no_games_yet_add")
            : shown.Count == 0 ? T("home.no_games_match")
            : T("home.enter_or_a_click_ticks_a"));
    }

    void Build()
    {
        list.Children.Clear(); checks.Clear();
        foreach (var g in shown)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var th = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(14), BorderBrush = P.TileEdge, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Background = P.TileFallback };
            var letter = Fonts.Text(g.Name.Length > 0 ? g.Name[..1].ToUpperInvariant() : "?", 30, P.Text2, FontWeights.Normal, Fonts.Display);
            letter.HorizontalAlignment = HorizontalAlignment.Center; letter.VerticalAlignment = VerticalAlignment.Center;
            th.Child = letter;
            w.Home?.LoadCover(g, (bmp, _) => { th.Child = null; th.Background = CoverView.Brush(bmp, g); });
            grid.Children.Add(th);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Fonts.Text(g.Name, 30, P.Text));
            if (g.Folder != null && !string.Equals(g.Folder, name, StringComparison.CurrentCultureIgnoreCase))
                names.Children.Add(Fonts.Text(T("home.in", g.Folder), 22, P.Text2));
            Grid.SetColumn(names, 1); grid.Children.Add(names);

            var tick = Fonts.Text("✓", 28, Brushes.White, FontWeights.Bold);
            tick.HorizontalAlignment = HorizontalAlignment.Center; tick.VerticalAlignment = VerticalAlignment.Center;
            var check = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(3), VerticalAlignment = VerticalAlignment.Center, Child = tick };
            checks[g.Id] = check;
            Grid.SetColumn(check, 2); grid.Children.Add(check);

            list.Children.Add(new Border { Height = RowH - 8, Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(18), Padding = new Thickness(12, 0, 22, 0), BorderThickness = new Thickness(3), Child = grid });
        }
        foreach (var g in shown) PaintCheck(g);
        Paint(false);
    }

    void PaintCheck(Game g)
    {
        if (!checks.TryGetValue(g.Id, out var c)) return;
        bool on = picked.Contains(g.Id);
        c.Background = on ? P.Accent : Brushes.Transparent;
        c.BorderBrush = on ? P.Accent : P.Text2;
        c.Opacity = on ? 1 : 0.5;
        c.Child.Visibility = on ? Visibility.Visible : Visibility.Hidden;
    }

    void Paint(bool animate)
    {
        for (int i = 0; i < list.Children.Count; i++)
        {
            var b = (Border)list.Children[i]; bool f = i == focus;
            b.Background = f ? P.RowFocus : Brushes.Transparent;
            b.BorderBrush = f ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(b, f);
        }
        bool onDone = focus == shown.Count;
        int n = picked.Count;
        doneText.Text = n == 0 ? T("home.make_container") : N("home.make_container_with_games", n);
        doneText.Foreground = n == 0 ? P.Text2 : Brushes.White;
        done.Background = n == 0 ? P.RowFocus : P.Accent;
        done.BorderBrush = onDone ? Palette.RingBrush() : Brushes.Transparent;
        Art.Lit(done, onDone);
        if (!onDone)
        {
            if (focus < top) top = focus;
            if (focus >= top + Visible) top = focus - Visible + 1;
        }
        if (animate) Anim.To(scroll, TranslateTransform.YProperty, -top * RowH, 180, Anim.Out);
        else scroll.Y = -top * RowH;
    }

    public override void Open()
    {
        base.Open();
        Root.Dispatcher.BeginInvoke(() => { box.Focus(); System.Windows.Input.Keyboard.Focus(box); }, DispatcherPriority.Input);
    }

    void Move(int d)
    {
        int to = Math.Clamp(focus + d, 0, shown.Count);
        if (to == focus) { Anim.Edge(shake, d > 0 ? 1 : -1, true); return; }
        focus = to; Sounds.Play("scroll"); Paint(true);
    }

    void Toggle()
    {
        var g = shown[focus];
        if (!picked.Remove(g.Id)) picked.Add(g.Id);
        Sounds.Play("select");
        PaintCheck(g); Paint(false); Hint(null);
    }

    public void Finish()
    {
        if (picked.Count == 0) { Hint(T("home.tick_at_least_one_game_first")); Anim.Refuse(shake); return; }
        var games = all.Where(g => picked.Contains(g.Id)).ToList();
        foreach (var g in games) g.Folder = name;
        w.Lib.Save();
        Log.Write($"container \"{name}\" made with {games.Count} game(s): {string.Join(", ", games.Select(g => g.Name))}");
        Close(false);
        Sounds.Play("favorite");
        w.Home?.Rebuild("folder:" + name);
        w.Toasts.Show(T("home.container_made"), N("home.container_games", games.Count, name), "folder");
    }

    public override void OnAct(Act a)
    {
        switch (a)
        {
            case Act.Up: Move(-1); break;
            case Act.Down: Move(1); break;
            case Act.PageLeft: Move(-Visible); break;
            case Act.PageRight: Move(Visible); break;
            case Act.Menu: Finish(); break;
            case Act.Accept:
                if (focus == shown.Count) { Sounds.Play("select"); Finish(); }
                else Toggle();
                break;
            case Act.Back: Close(); break;
        }
    }

    // Mouse: pointing at a game or the button focuses it, a click ticks the game or makes the container.
    int RowAt(Point p)
    {
        for (int i = top; i < Math.Min(shown.Count, top + Visible); i++)
            if (Pointer.Over((FrameworkElement)list.Children[i], w.Stage, p)) return i;
        return Pointer.Over(done, w.Stage, p) ? shown.Count : -1;
    }

    public override bool OnMouse(MouseAct m, Point p)
    {
        int i = RowAt(p);
        if (m == MouseAct.Move)
        {
            if (i >= 0 && i != focus) { focus = i; Sounds.Play("scroll"); Paint(false); }
            return i >= 0;
        }
        if (m == MouseAct.Down && i >= 0) { focus = i; Paint(false); OnAct(Act.Accept); return true; }
        return base.OnMouse(m, p);
    }

    // For tests: the middle of a shown game's row (or "done") on the stage.
    public Point? CentreOf(string which)
    {
        FrameworkElement el = which == "done" ? done : int.TryParse(which, out int i) && i >= 0 && i < list.Children.Count ? (FrameworkElement)list.Children[i] : null;
        return el?.TransformToAncestor(w.Stage).Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
    }
}
