#nullable disable
// "+ Add game" first step (From Steam / Program or shortcut) and the Steam picker: a searchable,
// scrollable list of installed Steam games with their covers. Arrows / Enter / Esc and a pad work.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

public sealed class AddChoiceCard : MenuCard
{
    public AddChoiceCard(MainWindow w) : base(w, T("home.add_game"), 860)
    {
        int fresh = NewRoms.Find(w).Count;
        AddRow(T("settings.new_game_files"), () => fresh == 0 ? T("settings.none_waiting") : fresh == 1 ? T("settings.1_ready_to_add") : T("settings.ready_to_add", fresh), activate: () => { Close(false); new NewRomsCard(w).Open(); });
        AddRow(T("settings.from_steam"), () => T("settings.installed_games"), activate: () => { Close(false); new SteamPickCard(w).Open(); });
        AddRow(T("settings.program_or_shortcut"), () => T("settings.exe_or_lnk"), activate: () => { Close(false); new AddGameFlow(w).Start(); });
        AddRow(T("settings.game_file"), () => w.Emus.Data.Emulators.Count == 0 ? T("settings.add_an_emulator_first_2") : T("settings.plays_in_an_emulator"), activate: () => { Close(false); new AddRomFlow(w).Start(); });
        AddRow(T("common.cancel"), activate: () => Close());
    }
}

public sealed class SteamPickCard : Card, IScreen
{
    const int Visible = 7;
    const double RowH = 104;
    static readonly Dictionary<string, BitmapSource> thumbs = new();

    readonly List<SteamGame> all;
    List<SteamGame> shown = new();
    readonly TextBox box;
    readonly TextBlock hint;
    readonly StackPanel list = new();
    readonly TranslateTransform scroll = new(), shake = new();
    readonly Dictionary<string, Border> thumbEls = new();
    int focus, top;
    public bool WantsText => true;
    public int Count => shown.Count;
    public int Marked => shown.Count(s => SteamLib.Existing(w.Lib, s) != null);
    public string FocusName => focus < shown.Count ? shown[focus].Name : null;

    public SteamPickCard(MainWindow w) : base(w, T("settings.from_steam"), 1300)
    {
        string root = SteamLib.Root();
        all = root == null ? new List<SteamGame>() : SteamLib.Installed(root);
        Log.Write($"steam picker: {(root ?? "no Steam")}, {all.Count} installed game(s)");

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
        hint = Note("", 22, new Thickness(4, 16, 0, 0));
        box.TextChanged += (s, e) => Filter();
        Filter();
    }

    void Filter()
    {
        string q = box.Text.Trim();
        shown = q.Length == 0 ? all : all.Where(s => s.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        focus = 0; top = 0;
        Build();
        Hint(null);
    }

    void Hint(string error)
    {
        hint.Foreground = error != null ? P.Error : P.Text2;
        hint.Text = error ?? (all.Count == 0
            ? (SteamLib.Root() == null ? T("settings.steam_is_not_installed_on_this") : T("settings.no_installed_steam_games_were_found"))
            : (shown.Count == 0 ? T("home.no_games_match") : shown.Count == 1 ? "1 game" : shown.Count + " games"));
    }

    void Build()
    {
        list.Children.Clear(); thumbEls.Clear();
        foreach (var s in shown)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var th = new Border { Width = 80, Height = 80, CornerRadius = new CornerRadius(12), BorderBrush = P.TileEdge, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            if (thumbs.TryGetValue(s.AppId, out var bmp) && bmp != null) th.Background = Art.Fill(bmp);
            else
            {
                th.Background = P.TileFallback;
                var t = Fonts.Text(Initials(s.Name), 28, P.Text2, FontWeights.Normal, Fonts.Display);
                t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
                th.Child = t;
            }
            thumbEls[s.AppId] = th;
            g.Children.Add(th);
            var n = Fonts.Text(s.Name, 30, P.Text); n.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(n, 1); g.Children.Add(n);
            if (SteamLib.Existing(w.Lib, s) is Game have)
            {
                var m = Fonts.Text(have.Removed ? T("settings.added_removed") : T("settings.added_2"), 26, P.Accent);
                m.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(m, 2); g.Children.Add(m);
                n.Opacity = 0.6;
            }
            list.Children.Add(new Border { Height = RowH - 8, Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 0, 26, 0), BorderThickness = new Thickness(3), Child = g });
        }
        Paint(false);
    }

    static string Initials(string name) =>
        new string(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => char.IsLetterOrDigit(p[0])).Take(2).Select(p => char.ToUpperInvariant(p[0])).ToArray());

    void Paint(bool animate)
    {
        for (int i = 0; i < list.Children.Count; i++)
        {
            var b = (Border)list.Children[i]; bool f = i == focus;
            b.Background = f ? P.RowFocus : Brushes.Transparent;
            b.BorderBrush = f ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(b, f);
        }
        if (focus < top) top = focus;
        if (focus >= top + Visible) top = focus - Visible + 1;
        if (animate) Anim.To(scroll, TranslateTransform.YProperty, -top * RowH, 180, Anim.Out);
        else scroll.Y = -top * RowH;
    }

    public override void Open()
    {
        base.Open();
        Root.Dispatcher.BeginInvoke(() => { box.Focus(); System.Windows.Input.Keyboard.Focus(box); }, DispatcherPriority.Input);
        LoadThumbs();
    }

    // Covers one by one at background priority, so the list is usable at once.
    void LoadThumbs()
    {
        var todo = new Queue<SteamGame>(all.Where(s => !thumbs.ContainsKey(s.AppId)));
        void Next()
        {
            if (todo.Count == 0) return;
            var s = todo.Dequeue();
            BitmapSource b = null;
            try { b = SteamArt.Cover(s.AppId, 160); } catch (Exception e) { Log.Write($"steam thumb {s.AppId}: {e.Message}"); }
            thumbs[s.AppId] = b;
            if (b != null && thumbEls.TryGetValue(s.AppId, out var el)) { el.Child = null; el.Background = Art.Fill(b); }
            Root.Dispatcher.BeginInvoke(Next, DispatcherPriority.Background);
        }
        Root.Dispatcher.BeginInvoke(Next, DispatcherPriority.Background);
    }

    void Move(int d)
    {
        int to = Math.Clamp(focus + d, 0, Math.Max(0, shown.Count - 1));
        if (to == focus) { Anim.Edge(shake, d > 0 ? 1 : -1, true); return; }
        focus = to; Sounds.Play("scroll"); Paint(true);
    }

    public void Pick()
    {
        if (focus >= shown.Count) { Anim.Refuse(shake); return; }
        var s = shown[focus];
        string err = SteamLib.Add(w.Lib, s, out Game g, w.Home?.ViewFolder);
        if (err != null) { Log.Write($"steam add refused: {s.AppId}: {err}"); Hint(err); Anim.Refuse(shake); return; }
        Close(false);
        Sounds.Play("favorite");
        w.Home?.Rebuild(g.Id);
        w.Home?.SlideIn();
        w.Toasts.Show(T("home.game_added"), g.Name, "plus");
    }

    public override void OnAct(Act a)
    {
        switch (a)
        {
            case Act.Up: Move(-1); break;
            case Act.Down: Move(1); break;
            case Act.PageLeft: Move(-Visible); break;
            case Act.PageRight: Move(Visible); break;
            case Act.Accept: Sounds.Play("select"); Pick(); break;
            case Act.Back: Close(); break;
        }
    }
}
