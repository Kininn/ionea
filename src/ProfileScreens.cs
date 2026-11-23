#nullable disable
// Profile screens: making a profile (first start and "Add a profile"), "Who's playing?", the profile
// card with play stats, and removing a profile.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static Ionea.Loc;

namespace Ionea;

// A name (the only rule: no other profile uses it) and, if wanted, a picture.
// First start: full screen, can't be skipped. Later: over the home screen, B cancels.
public sealed class NewProfileScreen : IScreen
{
    readonly MainWindow w;
    readonly bool first;
    readonly Action<Profile> done;
    readonly Grid root = new();
    readonly TextBox box;
    readonly TextBlock hint, picLabel;
    readonly Grid preview = new() { Width = 150, Height = 150 };
    readonly Border picButton;
    readonly TranslateTransform shake = new(), picBump = new();
    readonly int hue;
    BitmapSource picture;
    int focus;   // 0 = the name, 1 = the picture button
    public bool WantsText => focus == 0;
    public TextBox Box => box;
    const string Ready = "";

    public NewProfileScreen(MainWindow win, bool firstStart, Action<Profile> onDone = null)
    {
        w = win; first = firstStart; done = onDone;
        var P = w.P;
        hue = w.Profiles.NextHue();
        if (first) root.Children.Add(Art.StartBackground());
        else root.Children.Add(new Rectangle { Fill = P.Bg, Opacity = 0.97 });
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 40) };
        preview.Margin = new Thickness(0, 0, 0, 30); preview.HorizontalAlignment = HorizontalAlignment.Center;
        col.Children.Add(preview);
        col.Children.Add(Center(Fonts.Text(first ? T("settings.welcome") : T("settings.new_profile"), 76, P.Text, FontWeights.Light, Fonts.Display), first ? null : new Thickness(0, 0, 0, 48)));
        if (first) col.Children.Add(Center(Fonts.Text(T("settings.who_s_playing"), 34, P.Text2), new Thickness(0, 6, 0, 48)));
        box = new TextBox
        {
            Width = 900, FontSize = 58, FontFamily = Fonts.Display, FontWeight = FontWeights.Light, MaxLength = Profiles.MaxName,
            Foreground = P.Text, CaretBrush = P.Accent, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, SelectionBrush = P.Accent,
        };
        col.Children.Add(box);
        col.Children.Add(new Border { Width = 900, Height = 3, CornerRadius = new CornerRadius(1.5), Background = P.Accent, Margin = new Thickness(0, 6, 0, 0), RenderTransform = shake });
        hint = Center(Fonts.Text(Ready, 28, P.Text2), new Thickness(0, 26, 0, 34));
        col.Children.Add(hint);
        picLabel = Fonts.Text(T("settings.add_a_picture"), 30, P.Text);
        picButton = new Border
        {
            CornerRadius = new CornerRadius(38), Height = 76, Padding = new Thickness(40, 0, 40, 0), BorderThickness = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Center, RenderTransform = picBump, Background = P.RowFocus, BorderBrush = Brushes.Transparent,
            Child = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { picLabel } },
        };
        col.Children.Add(picButton);
        col.Children.Add(Center(Fonts.Text(first ? T("settings.down_picture_enter_done_more_people") : T("settings.down_picture_enter_done_esc_cancel"), 24, P.Text2), new Thickness(0, 28, 0, 0)));
        root.Children.Add(col);
        box.TextChanged += (s, e) => { ShowPreview(); CheckLive(); };
        ShowPreview();
        ShowFocus();
    }

    static TextBlock Center(TextBlock t, Thickness? m = null) { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = m ?? new Thickness(0); return t; }

    public void Open()
    {
        (first ? w.ScreenLayer : w.CardLayer).Children.Add(root);
        w.Push(this);
        root.Opacity = 0;
        Anim.To(root, UIElement.OpacityProperty, 1, first ? 500 : 220);
        if (!first) Sounds.Play("menuopen");
        FocusBox();
    }

    void FocusBox()
    {
        root.Dispatcher.BeginInvoke(() => { box.Focus(); System.Windows.Input.Keyboard.Focus(box); box.CaretIndex = box.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void ShowPreview()
    {
        preview.Children.Clear();
        preview.Children.Add(Art.AvatarPreview(picture, box?.Text, hue, 150));
    }

    void CheckLive()
    {
        string err = Profiles.Clean(box.Text).Length == 0 ? null : w.Profiles.CheckName(box.Text);
        hint.Text = err ?? Ready;
        hint.Foreground = err != null ? w.P.Error : w.P.Text2;
    }

    void ShowFocus()
    {
        picButton.BorderBrush = focus == 1 ? Palette.RingBrush() : Brushes.Transparent;
        Art.Lit(picButton, focus == 1);
        picLabel.Foreground = focus == 1 ? w.P.Accent : w.P.Text;
        picLabel.Text = picture == null ? T("settings.add_a_picture_optional") : T("settings.change_the_picture");
    }

    void SetFocus(int f)
    {
        if (f == focus) return;
        focus = f;
        Sounds.Play("scroll");
        ShowFocus();
        if (f == 0) FocusBox();
        else System.Windows.Input.Keyboard.Focus(w);   // keys stop typing into the name
    }

    void ChoosePicture()
    {
        Picker.File(w, T("settings.choose_a_profile_picture"), Picker.ImageFilter, Picker.Images, null, file => { SetPicture(file); focus = 1; SetFocus(0); });
        if (w.TopScreen == this) { focus = 1; SetFocus(0); }   // Windows' dialog, closed
    }

    public void SetPicture(string file)
    {
        var pic = Profiles.PictureFrom(file);
        if (pic == null) { hint.Text = T("settings.that_picture_can_t_be_read"); hint.Foreground = w.P.Error; Anim.Bump(picBump, 1); return; }
        picture = pic;
        ShowPreview(); ShowFocus(); CheckLive();
        Log.Write("new profile: picture chosen");
    }

    public bool OnMouse(MouseAct m, Point p)
    {
        bool overPic = Pointer.Over(picButton, w.Stage, p) || Pointer.Over(preview, w.Stage, p);
        if (m == MouseAct.Move) { if (Pointer.Over(picButton, w.Stage, p)) SetFocus(1); return overPic; }
        if (m == MouseAct.Down)
        {
            if (overPic) { Sounds.Play("select"); ChoosePicture(); return true; }
            if (Pointer.Over(box, w.Stage, p)) { SetFocus(0); return true; }
        }
        if (m == MouseAct.Right && !first) { OnAct(Act.Back); return true; }
        return true;
    }

    public void OnAct(Act a)
    {
        switch (a)
        {
            case Act.Down: if (focus == 0) SetFocus(1); else Anim.Edge(picBump, 1, true); break;
            case Act.Up: if (focus == 1) SetFocus(0); break;
            case Act.Back:
                if (first) { if (focus == 1) SetFocus(0); break; }
                Close(); Sounds.Play("back");
                break;
            case Act.Accept:
                if (focus == 1) { Sounds.Play("select"); ChoosePicture(); break; }
                Create();
                break;
        }
    }

    void Create()
    {
        string err = w.Profiles.CheckName(box.Text);
        if (err != null) { hint.Text = err; hint.Foreground = w.P.Error; Anim.Refuse(shake); return; }
        Profile p;
        try { p = w.Profiles.Create(box.Text, picture, w.CoreSaveDirs(), w.Lib); }
        catch (Exception e) { Log.Write("new profile: " + e); hint.Text = "Couldn't make the profile: " + e.Message; hint.Foreground = w.P.Error; return; }
        Sounds.Play("select");
        if (first)
        {
            w.Profiles.Switch(p);
            Anim.To(root, UIElement.OpacityProperty, 0, 380, done: () => w.ScreenLayer.Children.Remove(root));
            var start = new StartScreen(w);
            w.Replace(this, start);
            start.FadeIn();
            return;
        }
        Close();
        w.Toasts.Show(T("settings.profile_made"), p.Name, "plus");
        done?.Invoke(p);
    }

    void Close()
    {
        w.Pop(this);
        System.Windows.Input.Keyboard.Focus(w);
        Anim.To(root, UIElement.OpacityProperty, 0, 180, done: () => w.CardLayer.Children.Remove(root));
    }
}

// "Who's playing?": every profile as a circle, and one to add a new one.
public sealed class WhoCard : Card
{
    readonly List<(Profile p, FrameworkElement el, Grid ring, TextBlock name)> items = new();
    readonly Action<Profile> picked;
    readonly TranslateTransform bump = new();
    int focus;

    public WhoCard(MainWindow w, Action<Profile> onPicked) : base(w, T("settings.who_s_playing"), 1500)
    {
        picked = onPicked;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, RenderTransform = bump, Margin = new Thickness(0, 10, 0, 10) };
        var list = w.Profiles.List;
        foreach (var p in list.Append(null))
        {
            var cell = new StackPanel { Width = 230, Margin = new Thickness(8, 0, 8, 0) };
            var g = new Grid { Width = 170, Height = 170, HorizontalAlignment = HorizontalAlignment.Center };
            var ring = Art.CircleRing(130, out var glow); ring.Opacity = 0; ring.HorizontalAlignment = HorizontalAlignment.Center; ring.VerticalAlignment = VerticalAlignment.Center;
            Anim.Glow(glow);
            g.Children.Add(ring);
            if (p != null) g.Children.Add(Art.Avatar(w.Profiles, p, 130));
            else
            {
                var add = new Grid { Width = 130, Height = 130 };
                add.Children.Add(new Rectangle { RadiusX = Art.DiscRad(130), RadiusY = Art.DiscRad(130), Fill = P.RowFocus, Stroke = P.Line, StrokeThickness = 2 });   // the avatars' shape (Icon shape)
                add.Children.Add(Art.Icon("plus", 56, P.IconInk));
                g.Children.Add(add);
            }
            cell.Children.Add(g);
            var t = Fonts.Text(p?.Name ?? T("settings.add_profile"), 30, P.Text);
            t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 10, 0, 0); t.MaxWidth = 220;
            cell.Children.Add(t);
            row.Children.Add(cell);
            items.Add((p, cell, ring, t));
        }
        Body.Children.Add(row);
        focus = Math.Max(0, list.FindIndex(p => p.Id == w.Profiles.Active?.Id));
        Show();
    }

    public int Count => items.Count;
    public string FocusName => items[focus].p?.Name ?? "+";

    void Show()
    {
        for (int i = 0; i < items.Count; i++)
        {
            bool f = i == focus;
            items[i].ring.Opacity = f ? 1 : 0;
            items[i].name.Foreground = f ? P.Accent : P.Text;
        }
    }

    void Move(int d)
    {
        int n = focus + d;
        if (n < 0 || n >= items.Count) { Anim.Edge(bump, d); return; }
        focus = n; Sounds.Play("scroll"); Show();
    }

    public override bool OnMouse(MouseAct m, Point p)
    {
        int i = items.FindIndex(it => Pointer.Over(it.el, w.Stage, p));
        if (m == MouseAct.Move) { if (i >= 0 && i != focus) { focus = i; Sounds.Play("scroll"); Show(); } return i >= 0; }
        if (m == MouseAct.Down && i >= 0) { focus = i; Show(); OnAct(Act.Accept); return true; }
        if (m is MouseAct.WheelUp or MouseAct.WheelDown) { Move(m == MouseAct.WheelUp ? -1 : 1); return true; }
        return base.OnMouse(m, p);
    }

    public override void OnAct(Act a)
    {
        switch (a)
        {
            case Act.Left: case Act.Up: Move(-1); break;
            case Act.Right: case Act.Down: Move(1); break;
            case Act.Back: Close(); break;
            case Act.Accept:
            {
                var p = items[focus].p;
                Sounds.Play("select");
                Close(false);
                if (p != null) { picked(p); break; }
                new NewProfileScreen(w, false, made => picked(made)).Open();
                break;
            }
        }
    }
}

// The active profile: its play stats on the left, what can be done with it on the right.
public sealed class ProfileCard : MenuCard
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    readonly Profile p;

    public ProfileCard(MainWindow w, int focus = 0) : base(w, null, 1560)
    {
        var pr = w.Profiles;
        p = pr.Active;
        if (p == null) { Note(T("settings.no_profile_yet_2"), 28); AddRow(T("common.close"), activate: () => Close()); return; }
        var s = pr.Summarize(w.Lib);

        // Header: picture, name, since when.
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 30) };
        var av = Art.Avatar(pr, p, 120); av.Margin = new Thickness(0, 0, 30, 0);
        head.Children.Add(av);
        var hn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hn.Children.Add(Fonts.Text(p.Name, 52, P.Text, FontWeights.Normal, Fonts.Display));
        hn.Children.Add(Fonts.Text(Loc.N("settings.playing_since", pr.Data.Profiles.Count, p.Created.ToString("d MMMM yyyy", En)), 26, P.Text2));
        head.Children.Add(hn);
        Body.Children.Add(head);

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(700) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        var stats = new StackPanel();
        cols.Children.Add(stats);
        var rows = new StackPanel(); Grid.SetColumn(rows, 2);
        cols.Children.Add(rows);
        Body.Children.Add(cols);

        void Stat(string label, string value, string sub = null)
        {
            var b = new Border { Background = P.RowFocus, CornerRadius = new CornerRadius(18), Padding = new Thickness(26, 14, 26, 14), Margin = new Thickness(0, 0, 0, 10) };
            var sp = new StackPanel();
            sp.Children.Add(Fonts.Text(label, 22, P.Text2));
            sp.Children.Add(Fonts.Text(value, 32, P.Text, FontWeights.Medium));
            if (sub != null) sp.Children.Add(Fonts.Text(sub, 22, P.Text2));
            b.Child = sp;
            stats.Children.Add(b);
        }
        var ps = PlayStats.Of(w);   // the Play stats page's numbers (ProfileStats.cs): days with a minute of play or more
        Stat(T("settings.total_play_time"), Profiles.Hours(s.Total), ps.DaysPlayed > 0 ? Loc.N("settings.over_days", ps.DaysPlayed) : T("settings.start_a_game_to_begin_counting"));
        Stat(T("settings.most_played"), s.Most?.Name ?? T("settings.nothing_yet"), s.Most != null ? Profiles.Hours(s.MostSeconds) : null);
        Stat(T("settings.past_month"), s.MonthCount == 0 ? T("settings.no_games") : s.MonthCount == 1 ? "1 game" : $"{s.MonthCount} games",
            s.MonthCount > 0 ? string.Join(", ", s.Month.Take(3).Select(g => g.Name)) + (s.MonthCount > 3 ? $" and {s.MonthCount - 3} more" : "") : null);
        Stat(T("settings.last_played"), s.Last?.Name ?? T("settings.nothing_yet"), s.LastAt is DateTime at ? Ago(at) : null);
        // the streak (the rest on Play stats)
        if (ps.Streak > 0 || ps.BestStreak > 0)
            stats.Children.Add(Fonts.Text(Loc.N("settings.streak", ps.BestStreak, ps.Streak), 22, P.Text2));

        RowHost = rows;
        Opens(WithIcon(AddRow(T("settings.play_stats"), activate: () => new ProfileStatsCard(w).Open()), "gauge"));
        AddRow(T("settings.switch_profile"), () => pr.Data.Profiles.Count > 1 ? Loc.N("settings.other_profiles", pr.Data.Profiles.Count - 1) : "", activate: () =>
        {
            if (!w.CanSwitchProfile(out string why)) { new MessageCard(w, T("home.can_t_switch_now"), why + T("settings.every_profile_s_saves_stay_their")).Open(); return; }
            Close(false);
            new WhoCard(w, np => { w.SwitchProfile(np); w.Toasts.Show(T("settings.now_playing"), np.Name, "info"); }).Open();
        });
        AddRow(T("settings.picture"), () => p.Picture ? T("home.change") : T("common.add"), activate: () =>
        {
            Picker.File(w, T("settings.choose_a_profile_picture"), Picker.ImageFilter, Picker.Images, null, SetPicture);
        });
        if (p.Picture)
            AddRow(T("settings.remove_the_picture"), activate: () => { pr.SetPicture(p, null); w.ProfileChanged(); Reopen(); });
        AddRow(T("common.rename"), () => p.Name, activate: () =>
            new TextCard(w, T("settings.rename_this_profile"), p.Name, Profiles.MaxName, n => { pr.Rename(p, n); w.ProfileChanged(); Reopen(); }, n => pr.CheckName(n, p.Id)).Open());
        AddRow(T("settings.add_a_profile"), activate: () =>
        {
            Close(false);
            new NewProfileScreen(w, false, np =>
            {
                if (w.CanSwitchProfile(out _)) { w.SwitchProfile(np); w.Toasts.Show(T("settings.now_playing"), np.Name, "info"); }
            }).Open();
        });
        if (pr.Data.Profiles.Count > 1)
            AddRow(T("settings.remove_a_profile"), activate: () => { Close(false); new RemoveProfileCard(w).Open(); });
        AddRow(T("common.close"), activate: () => Close());
        Focus = Math.Clamp(focus, 0, Rows.Count - 1);
    }

    public void SetPicture(string file)
    {
        var pic = Profiles.PictureFrom(file);
        if (pic == null) { new MessageCard(w, T("settings.can_t_use_that_picture"), T("settings.the_file_can_t_be_read")).Open(); return; }
        w.Profiles.SetPicture(p, pic);
        w.ProfileChanged();
        Reopen();
    }

    void Reopen() { int f = Focus; Close(false); new ProfileCard(w, f).Open(); }

    static string Ago(DateTime at)
    {
        int days = (DateTime.Today - at.Date).Days;
        return days <= 0 ? T("settings.today") : days == 1 ? T("settings.yesterday") : days < 30 ? $"{days} days ago" : at.ToString("d MMMM yyyy", En);
    }
}

// Pick a profile to remove (not the one in use), then confirm. Its saves go to the Recycle Bin.
public sealed class RemoveProfileCard : MenuCard
{
    public RemoveProfileCard(MainWindow w) : base(w, T("settings.remove_a_profile"), 900)
    {
        var others = w.Profiles.List.Where(p => p.Id != w.Profiles.Active?.Id).ToList();
        foreach (var p in others)
            AddRow(p.Name, activate: () => { Close(false); new ConfirmRemoveCard(w, p).Open(); });
        AddRow(T("common.cancel"), activate: () => Close());
    }
}

public sealed class ConfirmRemoveCard : MenuCard
{
    public ConfirmRemoveCard(MainWindow w, Profile p) : base(w, T("settings.remove", p.Name), 900)
    {
        Note(T("settings.saves_save_states_and_play_time"), 28, new Thickness(4, 0, 0, 24));
        AddRow(T("settings.keep_the_profile"), activate: () => Close());
        AddRow(T("settings.remove_2", p.Name), activate: () =>
        {
            Close(false);
            if (!w.CanSwitchProfile(out string why)) { new MessageCard(w, T("settings.can_t_remove_it_now"), why + ".").Open(); return; }
            string err = w.Profiles.Remove(p, w.Lib);
            if (err != null) { new MessageCard(w, T("settings.couldn_t_remove_the_profile"), err).Open(); return; }
            w.ProfileChanged();
            w.Toasts.Show(T("settings.profile_removed"), p.Name + T("settings.in_the_recycle_bin"), "info");
        });
    }
}

// Play stats: the active profile's numbers, a 30-day chart and the most played game with its
// cover, in the profile's look (colour preset on the bars, icon shape on their corners). Opened from the
// profile card. Everything comes from PlayStats (ProfileStats.cs).
public sealed class ProfileStatsCard : MenuCard
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    public static ProfileStatsCard Current;
    readonly Dictionary<string, string> shown = new();
    public string Stat(string label) => shown.TryGetValue(label, out var v) ? v : "none";
    public PlayStats Numbers { get; }

    public ProfileStatsCard(MainWindow w) : base(w, null, 1760)
    {
        Current = this;
        var pr = w.Profiles; var p = pr.Active;
        var s = Numbers = PlayStats.Of(w);

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
        if (p != null) { var av = Art.Avatar(pr, p, 84); av.Margin = new Thickness(0, 0, 24, 0); head.Children.Add(av); }
        var hn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hn.Children.Add(Fonts.Text(T("settings.play_stats"), 44, P.Text, FontWeights.Normal, Fonts.Display));
        hn.Children.Add(Fonts.Text(Loc.N("settings.days_with_play", s.DaysPlayed, p?.Name ?? T("settings.no_profile")), 24, P.Text2));
        head.Children.Add(hn);
        Body.Children.Add(head);

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1000) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel(); cols.Children.Add(left);
        var right = new StackPanel(); Grid.SetColumn(right, 2); cols.Children.Add(right);
        Body.Children.Add(cols);

        // numbers, three to a row
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(-5, -5, -5, 0) };
        left.Children.Add(tiles);
        void Tile(string label, string value, string sub = null)
        {
            shown[label] = value + (sub != null ? " | " + sub : "");
            var b = new Border { Background = P.RowFocus, CornerRadius = new CornerRadius(Art.Corner(18)), Padding = new Thickness(22, 10, 22, 10), Margin = new Thickness(5) };
            var sp = new StackPanel();
            sp.Children.Add(Fonts.Text(label, 21, P.Text2));
            var v = Fonts.Text(value, 30, P.Text, FontWeights.Medium); v.TextTrimming = TextTrimming.CharacterEllipsis; sp.Children.Add(v);
            var t = Fonts.Text(sub ?? " ", 20, P.Text2); t.TextTrimming = TextTrimming.CharacterEllipsis; sp.Children.Add(t);
            b.Child = sp; b.ToolTip = value + (sub != null ? " · " + sub : "");
            tiles.Children.Add(b);
        }
        static string N(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
        Tile(T("settings.total_play_time"), Profiles.Hours(s.Total), s.DaysPlayed > 0 ? "over " + N(s.DaysPlayed, "day") : T("settings.start_a_game_to_begin_counting"));
        Tile(T("settings.games_played"), s.Games.ToString(), s.Systems > 0 ? "on " + N(s.Systems, "system") : null);
        Tile(T("settings.sessions"), s.Sessions.ToString(), s.Sessions > 0 ? "about " + Profiles.Hours(s.Total / s.Sessions) + " each" : null);
        Tile(T("settings.current_streak"), N(s.Streak, "day"), s.Streak > 0 && s.Daily[29] < 60 ? T("settings.play_today_to_keep_it") : null);
        Tile(T("settings.longest_streak"), N(s.BestStreak, "day"));
        Tile(T("settings.longest_session"), s.Longest > 0 ? Profiles.Hours(s.Longest) : T("settings.none_yet"), s.LongestGame?.Name);
        double diff = s.ThisWeek - s.LastWeek;
        Tile(T("settings.this_week"), Profiles.Hours(s.ThisWeek), s.ThisWeek == 0 && s.LastWeek == 0 ? null
            : Math.Abs(diff) < 60 ? "same as last week" : (diff > 0 ? Profiles.Hours(diff) + " more than last week" : Profiles.Hours(-diff) + " less than last week"));
        Tile(T("settings.last_week"), Profiles.Hours(s.LastWeek));
        Tile(T("settings.favourite_system"), s.TopSystem ?? T("settings.none_yet"), s.TopSystem != null ? Profiles.Hours(s.TopSystemSeconds) : null);
        Tile(T("settings.last_played"), s.Last?.Name ?? T("settings.nothing_yet"), s.LastAt is DateTime la ? Ago(la) : null);
        Tile(T("settings.most_played"), s.Most?.Name ?? T("settings.nothing_yet"), s.Most != null ? Profiles.Hours(s.MostSeconds) : null);
        Tile(T("settings.first_day_with_ionea"), s.FirstDay.ToString("d MMM yyyy", En));

        // the past 30 days
        var ch = new Grid { Margin = new Thickness(0, 22, 0, 8) };
        ch.ColumnDefinitions.Add(new ColumnDefinition());
        ch.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ch.Children.Add(Caption(T("settings.past_30_days")));
        double max = s.Daily.Max(), sum30 = s.Daily.Sum();
        shown["chart"] = string.Join(",", s.Daily.Select(x => Math.Round(x / 60).ToString(CultureInfo.InvariantCulture)));
        var tot = Fonts.Text(sum30 > 0 ? Profiles.Hours(sum30) + T("settings.best_day", Profiles.Hours(max)) : T("settings.no_play"), 22, P.Text2);
        Grid.SetColumn(tot, 1); ch.Children.Add(tot);
        left.Children.Add(ch);
        const double H = 150, Gap = 8;
        double bw = (1000 - 29 * Gap) / 30;
        var bars = new Canvas { Width = 1000, Height = H };
        var ink = Palette.RingBrush(new Point(0, 1), new Point(0, 0));
        for (int i = 0; i < 30; i++)
        {
            double v = s.Daily[i], h = v <= 0 ? 4 : Math.Max(8, H * v / Math.Max(max, 1));
            double rad = Math.Min(bw / 2, Art.Corner(8));
            var r = new Rectangle { Width = bw, Height = h, RadiusX = rad, RadiusY = rad, Fill = v <= 0 ? P.Line : ink, Opacity = v <= 0 ? 0.7 : 1 };
            r.ToolTip = DateTime.Today.AddDays(i - 29).ToString("ddd d MMM", En) + "  ·  " + (v > 0 ? Profiles.Hours(v) : "no play");
            Canvas.SetLeft(r, i * (bw + Gap)); Canvas.SetTop(r, H - h);
            bars.Children.Add(r);
        }
        left.Children.Add(bars);
        var axis = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        axis.Children.Add(Fonts.Text(DateTime.Today.AddDays(-29).ToString("d MMM", En), 20, P.Text2));
        var today = Fonts.Text(T("settings.today"), 20, P.Text2); today.HorizontalAlignment = HorizontalAlignment.Right; axis.Children.Add(today);
        left.Children.Add(axis);

        // most played, with its cover
        right.Children.Add(Caption(T("settings.most_played")));
        var mp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 26) };
        var cover = new Border { Width = 190, Height = 190, CornerRadius = new CornerRadius(Art.Corner(26)), Background = P.TileFallback, ClipToBounds = true };
        if (s.Most != null && w.Lib.CoverPath(s.Most) is string cf && System.IO.File.Exists(cf) && Art.LoadCover(cf, CoverView.Zoom(s.Most) > 1.001 ? 0 : 300) is BitmapSource bmp)
            cover.Background = CoverView.Brush(bmp, s.Most);
        else if (s.Most != null)
            cover.Child = new TextBlock { Text = s.Most.Name, FontSize = 24, Foreground = P.Text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14) };
        shown["mostcover"] = s.Most == null ? "none" : cover.Background is ImageBrush ? "picture" : "name";
        mp.Children.Add(cover);
        var mt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0), Width = 380 };
        var mn = Fonts.Text(s.Most?.Name ?? T("settings.nothing_yet"), 32, P.Text, FontWeights.Medium); mn.TextWrapping = TextWrapping.Wrap; mt.Children.Add(mn);
        if (s.Most != null)
        {
            mt.Children.Add(Fonts.Text(Profiles.Hours(s.MostSeconds), 26, P.Text2));
            mt.Children.Add(Fonts.Text(T("settings.of_your_play_time", Math.Round(100 * s.MostSeconds / Math.Max(1, s.Total))), 22, P.Text2));
        }
        mp.Children.Add(mt);
        right.Children.Add(mp);

        RowHost = Pinned;
        Pinned.Margin = new Thickness(0, 18, 0, 0);
        AddRow(T("common.close"), activate: () => Close());
        Focus = 0;
    }

    public override void Close(bool sound = true) { if (Current == this) Current = null; base.Close(sound); }

    TextBlock Caption(string t) => Fonts.Text(t, 24, P.Text2, FontWeights.SemiBold);

    static string Ago(DateTime at)
    {
        int days = (DateTime.Today - at.Date).Days;
        return days <= 0 ? T("settings.today") : days == 1 ? T("settings.yesterday") : days < 30 ? $"{days} days ago" : at.ToString("d MMMM yyyy", En);
    }
}
