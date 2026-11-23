#nullable disable
// The first-start tutorial: add an emulator, add a game, play it, open the Home menu.
// It points at the real buttons (a ring and a soft dim around them, a short line under them) and moves
// on when the user has done the step themselves. It is offered once per PC after the controls setup
// (MainWindow.ShowHome); Settings > Look and layout > Watch the tutorial plays it again.
// Back on Home asks "Leave the tutorial?"; a click on Skip in the bubble leaves it too.
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

public sealed class Tutorial
{
    public enum Step { Off, Emulator, Game, Play, InGame }

    readonly MainWindow w;
    readonly Palette P = Palette.I;
    readonly Canvas layer;
    readonly Path dim;
    readonly Border ring, bubble;
    readonly TextBlock count, line, skip;
    readonly DispatcherTimer tick;
    Step step = Step.Off;
    int emusBefore;
    bool hadEmus, sawList;
    HashSet<string> gamesBefore = new();
    string gameId, shownKey;

    public bool Active => step != Step.Off;
    public string StepName => step.ToString().ToLowerInvariant();   // test scripts
    public string Target { get; private set; } = "none";             // test scripts: what the ring is on

    public Tutorial(MainWindow win, Canvas coachLayer)
    {
        w = win; layer = coachLayer;
        dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)) };
        ring = new Border { CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(5), BorderBrush = Palette.RingBrush() };
        count = Fonts.Text("", 22, P.Text2);
        line = Fonts.Text("", 30, P.Text);
        line.TextWrapping = TextWrapping.Wrap; line.TextTrimming = TextTrimming.None; line.Margin = new Thickness(0, 4, 0, 0);
        skip = Fonts.Text(T("home.skip"), 22, P.Accent);
        var top = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(skip, Dock.Right);
        top.Children.Add(count); top.Children.Add(skip);
        bubble = new Border
        {
            MaxWidth = 620, Background = P.Card, CornerRadius = new CornerRadius(22), Padding = new Thickness(30, 20, 30, 24),
            Child = new StackPanel { Children = { top, line } },
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Direction = 270, Opacity = 0.2, Color = Colors.Black },
        };
        layer.Children.Add(dim); layer.Children.Add(ring); layer.Children.Add(bubble);
        layer.Visibility = Visibility.Collapsed;
        tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        tick.Tick += (s, e) => Update();
    }

    // First start: once Home is in front with nothing over it, the welcome card. Once per PC.
    public void OfferWhenHome()
    {
        bool waiting = false;
        void Check()
        {
            if (waiting || w.TopScreen != w.Home) return;
            waiting = true;
            Ui.After(500, () =>
            {
                waiting = false;
                if (w.Settings.TutorialAsked || Active) { w.StackChanged -= Check; return; }
                if (w.TopScreen != w.Home || w.Launch.Busy || w.Launch.Running) return;   // still busy: the next change looks again
                w.StackChanged -= Check;
                new TutorialCard(w).Open();
            });
        }
        w.StackChanged += Check;
        Check();
    }

    public void Begin()
    {
        Asked();
        emusBefore = w.Emus.Data.Emulators.Count;
        hadEmus = emusBefore > 0;
        sawList = false;
        gameId = null; shownKey = null;
        step = Step.Emulator;
        var pal = Palette.I;   // the theme may have changed since
        bubble.Background = pal.Card; line.Foreground = pal.Text; count.Foreground = pal.Text2; skip.Foreground = pal.Accent;
        ring.BorderBrush = Palette.RingBrush();
        layer.Visibility = Visibility.Visible;
        Anim.Forever(ring, UIElement.OpacityProperty, 1, 0.45, 700);
        tick.Start();
        Log.Write("tutorial: started" + (hadEmus ? $" ({emusBefore} emulator(s) already)" : ""));
        Update();
    }

    public void Stop(string why)
    {
        if (!Active) return;
        step = Step.Off;
        tick.Stop();
        ring.BeginAnimation(UIElement.OpacityProperty, null);
        layer.Visibility = Visibility.Collapsed;
        Target = "none";
        Log.Write("tutorial: " + why);
    }

    void Asked() { if (!w.Settings.TutorialAsked) { w.Settings.TutorialAsked = true; w.Settings.Save(); } }

    // Back on Home (with no folder or search open) while the tutorial runs.
    public void AskLeave() => new TutorialLeaveCard(w).Open();

    // A click on Skip in the bubble (Pointer.Route asks first).
    public bool SkipHit(MouseAct m, Point p)
    {
        if (!Active || m != MouseAct.Down || layer.Visibility != Visibility.Visible || bubble.Visibility != Visibility.Visible) return false;
        if (!Pointer.Over(skip, w.Stage, p)) return false;
        Sounds.Play("back");
        AskLeave();
        return true;
    }

    // ---- what to point at ----

    string Key(string id) => w.PadLast ? id switch { "accept" => "A", "menu" => "Start", "home" => "View + Start", _ => "" } : LauncherKeys.First(w.Settings.Input, id);
    static string Press(string key, string what) => key == "" ? what : $"{what} ({key})";

    public void Update()
    {
        if (!Active) return;
        var top = w.TopScreen;
        int emus = w.Emus.Data.Emulators.Count;
        var games = w.Lib.Data.Games.Where(g => !g.Removed).ToList();

        // the steps move on by themselves
        if (step == Step.Emulator && (emus > emusBefore || (hadEmus && sawList && (top == w.Home || top is SettingsCard))))
        { step = Step.Game; gamesBefore = games.Select(g => g.Id).ToHashSet(); Sounds.Play("select"); Log.Write("tutorial: emulator step done"); }
        if (step == Step.Game && games.FirstOrDefault(g => !gamesBefore.Contains(g.Id)) is Game added)
        { step = Step.Play; gameId = added.Id; Sounds.Play("select"); Log.Write($"tutorial: game step done ({added.Name})"); }
        if (step == Step.Play && (w.Launch.Busy || w.Launch.Running)) { step = Step.InGame; Log.Write("tutorial: playing"); }
        if (step == Step.InGame && top == w.Home && !w.Launch.Busy)
        {
            Stop("done");
            Ui.After(400, () => { if (w.TopScreen == w.Home) new TutorialDoneCard(w).Open(); });
            return;
        }
        if (step == Step.Play && games.All(g => g.Id != gameId)) { gameId = null; step = Step.Game; gamesBefore = games.Select(g => g.Id).ToHashSet(); }

        var (target, el, text) = Pick(top);
        // hidden while a game is starting or in front (a core draws on the stage under this layer's place)
        bool hide = w.Launch.Busy || (w.Launch.Running && top is not Card) || top is TutorialCard or TutorialLeaveCard or TutorialDoneCard;
        layer.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        Target = hide ? "hidden" : target;
        if (hide) return;
        Show(el == null ? Rect.Empty : StageRect(el), text, (top as Card)?.BoxElement is FrameworkElement box ? StageRect(box) : Rect.Empty);
    }

    (string target, FrameworkElement el, string text) Pick(IScreen top)
    {
        var mc = top as MenuCard;
        FrameworkElement Row(string label) => mc?.RowElement(label);
        (string, FrameworkElement, string) BackHome() => top is Card c ? ("close", c.CloseButtonElement, T("home.back_to_home")) : ("none", null, T("home.back_to_home"));
        bool settingsOpen = w.Stack.Any(s => s is SettingsCard or EmulatorsCard);
        if (top is CoreOfferCard oc) return ("row:" + oc.FirstLabel, Row(oc.FirstLabel), T("home.recommended_with_the_core_the_home"));
        if (top is DownloadCard) return ("none", null, T("home.download_it_or_cancel"));

        switch (step)
        {
            case Step.Emulator:
                if (top == w.Home) return ("gear", w.Home.TutorialSpot("gear"), Press(Key("menu"), "First, an emulator. Open Settings"));
                if (top is SettingsCard sc)
                    return sc.Category == "Library" ? ("row:" + EmulatorsCard.Title, Row(EmulatorsCard.Title), T("home.open_2") + EmulatorsCard.Title)
                        : ("rail:Library", sc.RailElement("Library"), T("home.go_to_library"));
                if (top is EmulatorsCard)
                {
                    sawList = true;
                    if (hadEmus)
                    {
                        var names = w.Emus.Data.Emulators.Select(e => e.Name).ToList();
                        string have = names.Count == 1 ? names[0] : $"{names.Count} emulators";
                        return ("row:+ Add emulator", Row("+ Add emulator"), T("home.you_have_more_are_added_here", have));
                    }
                    return ("row:+ Add emulator", Row("+ Add emulator"), T("home.add_an_emulator"));
                }
                if (top is AddEmulatorCard) return ("none", null, T("home.download_one_or_choose_one_on"));
                if (w.Stack.Any(s => s is AddEmulatorCard)) return ("none", null, T("home.add_an_emulator"));
                return BackHome();

            case Step.Game:
                if (top == w.Home)
                    return w.Home.View == "home" ? ("add", w.Home.TutorialSpot("add"), T("home.now_a_game_pick")) : ("none", null, T("home.back_to_home"));
                if (top is AddMenuCard) return ("row:Game", Row("Game"), T("home.add_a_game"));
                if (top is AddChoiceCard) return ("row:Game file", Row("Game file"), T("home.game_file_then_pick_the_file"));
                if (top is AddRomCard) return ("row:Add", Row("Add"), T("home.ionea_matched_the_emulator_add_it"));
                if (top is EmuChoiceCard) return ("none", null, T("home.pick_the_emulator"));
                if (settingsOpen) return BackHome();
                return ("none", null, T("home.pick_a_game_file"));

            case Step.Play:
                if (top == w.Home && w.Home.TutorialSpot(gameId) is FrameworkElement t)
                    return ("game:" + w.Lib.Data.Games.First(g => g.Id == gameId).Name, t,
                        $"{Press(Key("accept"), "Press to play")}. In the game, {Key("home")} opens the Home menu");
                return BackHome();

            default:   // in the game: the Home menu
                if (top is HomeMenuCard) return ("none", null, T("home.the_home_menu_resume_or_close"));
                return ("none", null, $"{Key("home")} opens the Home menu");
        }
    }

    Rect StageRect(FrameworkElement e)
    {
        if (e == null || !e.IsVisible || e.ActualWidth <= 0) return Rect.Empty;
        try
        {
            var r = e.TransformToAncestor(w.Stage).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            return r.IntersectsWith(new Rect(0, 0, 1920, 1080)) ? r : Rect.Empty;
        }
        catch { return Rect.Empty; }
    }

    void Show(Rect r, string text, Rect card)
    {
        if (text != line.Text)
        {
            line.Text = text;
            count.Text = step switch { Step.Emulator => "1 of 3", Step.Game => "2 of 3", _ => "3 of 3" };
            Anim.FromTo(bubble, UIElement.OpacityProperty, 0, 1, 220);
        }
        string key = text + r + card;
        if (key == shownKey) return;
        shownKey = key;
        bool has = !r.IsEmpty;
        ring.Visibility = dim.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has)
        {
            var hole = Rect.Inflate(r, 12, 12);
            dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, 1920, 1080)), new RectangleGeometry(hole, 26, 26));
            ring.Width = hole.Width; ring.Height = hole.Height;
            Canvas.SetLeft(ring, hole.X); Canvas.SetTop(ring, hole.Y);
        }
        // beside the card in front when there is room (level with the ring), else under / over the ring, else low in the middle
        bool side = !card.IsEmpty && 1920 - card.Right >= 400;
        bubble.MaxWidth = side ? Math.Min(620, 1920 - card.Right - 70) : 620;
        bubble.Measure(new Size(bubble.MaxWidth, double.PositiveInfinity));
        var b = bubble.DesiredSize;
        double x, y;
        if (side) { x = card.Right + 36; y = Math.Clamp((has ? r.Y + r.Height / 2 : card.Y + card.Height / 2) - b.Height / 2, 30, 1050 - b.Height); }
        else if (!has) { x = 960 - b.Width / 2; y = 1080 - b.Height - 90; }
        else
        {
            x = Math.Clamp(r.X + r.Width / 2 - b.Width / 2, 40, 1880 - b.Width);
            y = r.Bottom + 30 + b.Height < 1050 ? r.Bottom + 30 : r.Top - 30 - b.Height;
            if (y < 30) { y = Math.Clamp(r.Y + r.Height / 2 - b.Height / 2, 30, 1050 - b.Height); x = r.Right + 30 + b.Width < 1890 ? r.Right + 30 : Math.Max(30, r.Left - 30 - b.Width); }
        }
        Canvas.SetLeft(bubble, x); Canvas.SetTop(bubble, y);
    }
}

// "Let's add your first game" after the first start's controls setup.
public sealed class TutorialCard : MenuCard
{
    public TutorialCard(MainWindow w) : base(w, T("home.let_s_add_your_first_game"), 860)
    {
        Note(T("home.an_emulator_then_a_game_about"), 30, new Thickness(4, -6, 0, 26));
        AddRow(T("home.start"), activate: () => { Close(false); w.Tutor.Begin(); });
        AddRow(T("home.skip"), activate: () => Close());
        w.Settings.TutorialAsked = true; w.Settings.Save();
    }
}

public sealed class TutorialLeaveCard : MenuCard
{
    public TutorialLeaveCard(MainWindow w) : base(w, T("home.leave_the_tutorial"), 760)
    {
        AddRow(T("home.keep_going"), activate: () => Close());
        AddRow(T("home.leave"), activate: () => { Close(); w.Tutor.Stop("left"); });
    }
}

public sealed class TutorialDoneCard : MenuCard
{
    public TutorialDoneCard(MainWindow w) : base(w, T("home.you_re_set"), 860)
    {
        Note(T("home.add_more_games_and_emulators_with"), 30, new Thickness(4, -6, 0, 26));
        AddRow(T("common.done"), activate: () => Close());
    }
}
