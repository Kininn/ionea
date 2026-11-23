#nullable disable
﻿// Settings > Input: the Input card (keyboard, controller and mouse, plugged in or last seen) and a full
// screen for each one. The keyboard and controller screens draw the device in the middle with one
// console's (or one game's) buttons around it; pick one and press the key, mouse button or controller
// button to use for it. The mouse screen shows what each mouse button does and the mouse's own settings.
// Also here: the Controls card (every console), a game's Controls card (from its Esc menu) and the
// first-start setup (which device, then recommended or your own controls).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

public sealed class InputCard : MenuCard
{
    readonly Action changed;
    public static InputCard Current;   // for test scripts

    public InputCard(MainWindow w) : base(w, T("home.input"))
    {
        var s = w.Settings.Input;
        // The same rows, names and boxes as Settings > Controls.
        BeginBox(T("settings.devices"));
        foreach (var kind in new[] { "keyboard", "pad", "mouse" })
        {
            string k = kind;
            Opens(AddRow(InputDevices.Noun(k), () => Status(w, k), activate: () => DeviceScreen.For(w, k, Refresh).Open()));
        }
        AddRow(T("settings.pop_up_when_connected"), () => s.Popups ? T("common.on") : T("common.off"), _ => { s.Popups = !s.Popups; w.Settings.Save(); });
        BeginBox(T("settings.keys_and_buttons"));
        Opens(AddRow(T("settings.launcher_keys"), () => LauncherKeys.Summary(s), activate: () => new LauncherKeysCard(w).Open()));
        Opens(AddRow(T("settings.game_controls"), () => ControlsCard.Summary(w), activate: () => new ControlsCard(w, Refresh).Open()));
        EndBox();
        AddRow(T("common.done"), activate: () => Close());
        changed = () => Refresh();
    }

    public static string Status(MainWindow w, string kind)
    {
        var (name, on, seen) = w.Devs.Show(kind);
        string what = name ?? (kind == "pad" ? T("settings.none_plugged_in") : T("settings.none_found"));
        if (!on && seen != null) what = $"{name}: not connected";
        if (!w.Settings.Input.On(kind)) what = T("settings.off") + what;
        return what.Length > 40 ? what[..39] + "…" : what;
    }

    new void Refresh()
    {
        base.Refresh();
        // a device that isn't plugged in (or is turned off) is greyed
        string[] kinds = { "keyboard", "pad", "mouse" };
        for (int i = 0; i < 3 && i < Rows.Count; i++)
            Rows[i].El.Opacity = w.Devs.Connected(kinds[i]) && w.Settings.Input.On(kinds[i]) ? 1 : 0.5;
    }

    public override void Open()
    {
        Current = this;
        w.Devs.Changed += changed;
        Refresh();
        base.Open();
    }

    public override void Close(bool sound = true)
    {
        w.Devs.Changed -= changed;
        if (Current == this) Current = null;
        base.Close(sound);
    }

    public override void OnAct(Act a)
    {
        base.OnAct(a);
        if (a is Act.Left or Act.Right or Act.Accept) Refresh();
    }
}

// ---- game controls: every console ----

public sealed class ControlsCard : MenuCard
{
    readonly Action closedCb;
    readonly List<string> systems;
    InputPrefs S => w.Settings.Input;

    public ControlsCard(MainWindow w, Action closed = null) : base(w, T("settings.game_controls"))
    {
        closedCb = closed;
        systems = InputMap.ManagedSystems(w);
        AddRow(T("settings.set_up_for"), () => PadMain(w) ? T("settings.controller_2") : T("settings.keyboard_and_mouse"), _ =>
        {
            S.Main = PadMain(w) ? "keyboard" : "pad";
            w.Settings.Save();
        });
        foreach (var sys in systems)
        {
            string id = sys;
            AddRow(InputMap.SystemName(id), () => S.Changed(id) ? T("settings.your_own") : T("settings.recommended"),
                activate: () => DeviceScreen.For(w, PadMain(w) ? "pad" : "keyboard", Refresh, new ControlScope(id)).Open());
        }
        AddRow(T("settings.back_to_the_recommended_controls"), activate: () =>
        {
            S.UseRecommended();
            w.Settings.Save();
            w.InputChanged();
            Sounds.Play("favorite");
            w.Toasts.Show(T("settings.every_console_has_the_recommended_controls"), null, "info");
            Refresh();
        });
        AddRow(T("common.done"), activate: () => Close());
    }

    public static bool PadMain(MainWindow w) => w.Settings.Input.Main == "pad";

    // The Input card's row: how many consoles have their own controls.
    public static string Summary(MainWindow w)
    {
        int own = InputMap.ManagedSystems(w).Count(s => w.Settings.Input.Changed(s));
        return own == 0 ? T("settings.recommended") : own == 1 ? "1 console your own" : $"{own} consoles your own";
    }

    public override void Close(bool sound = true)
    {
        base.Close(sound);
        closedCb?.Invoke();
    }

    // One game's controls (the Home screen's quick menu, or its Esc menu while it runs).
    public static void OpenFor(MainWindow w, Game g, bool fromGameMenu) => new GameControlsCard(w, g, fromGameMenu).Open();
}

// ---- game controls: one game ----

public sealed class GameControlsCard : MenuCard
{
    readonly Game g;
    readonly ControlScope sc;
    readonly bool fromGameMenu;
    InputPrefs S => w.Settings.Input;

    public GameControlsCard(MainWindow w, Game game, bool fromGameMenu) : base(w, T("settings.controls"), 900)
    {
        g = game; this.fromGameMenu = fromGameMenu;
        sc = InputMap.ScopeFor(w, g);
        Note($"{g.Name}  ·  {sc.Name}", 26, new Thickness(4, -14, 0, 20));
        AddRow(T("settings.keyboard_and_mouse"), () => S.HasOwn(sc) ? T("settings.this_game_s") : sc.Name, activate: () => DeviceScreen.For(w, "keyboard", Refresh, sc).Open());
        AddRow(T("settings.controller_2"), () => S.HasOwn(sc) ? T("settings.this_game_s") : sc.Name, activate: () => DeviceScreen.For(w, "pad", Refresh, sc).Open());
        AddRow(T("settings.this_game_only"), () => S.HasOwn(sc) ? T("common.on") : T("common.off"), _ =>
        {
            S.SetOwn(sc, !S.HasOwn(sc));
            w.Settings.Save();
            w.InputChanged();
        });
        AddRow(T("common.done"), activate: () => Close());
        if (PadFirst()) Focus = 1;
    }

    bool PadFirst() => ControlsCard.PadMain(w) && w.Devs.Connected("pad");

    public override void Close(bool sound = true)
    {
        base.Close(sound);
        bool running = w.GameMenu.Game?.Id == g.Id;
        if (running && Ports.EmuOf(w, g) is Emulator e && EmuControls.Writes(e, sc.System))
        {
            // shadPS4 reads its input file again on its reload key, once the game runs again. The others read
            // their settings when they start: HomeMenu offers a restart (GameMenu.ControlsPending).
            if (EmuControls.LiveReload(e) && EmuControls.Write(e, S, sc))
                w.Toasts.Show(w.GameMenu.ReloadInputs() ? T("settings.controls_updated") : T("settings.the_new_controls_work_from_the"), g.Name, "info");
        }
        if (fromGameMenu && w.GameMenu.Game != null) w.GameMenu.ShowCard();
    }
}

// ---- first start: set up your controls ----

public sealed class SetupPromptCard : MenuCard
{
    bool going;

    public SetupPromptCard(MainWindow w) : base(w, T("settings.set_up_your_controls"), 900)
    {
        AddRow(T("settings.set_up_now"), activate: () => { going = true; Close(false); new SetupDeviceCard(w).Open(); });
        AddRow(T("common.later"), activate: () => Close());
    }

    public override void Close(bool sound = true)
    {
        if (!w.Settings.Input.SetupDone) { w.Settings.Input.SetupDone = true; w.Settings.Save(); }
        base.Close(sound && !going);
    }
}

// Which device you play with. Everything plugged in is listed; the controller is picked first when there is one.
public sealed class SetupDeviceCard : MenuCard
{
    readonly Action changed;
    InputPrefs S => w.Settings.Input;

    public SetupDeviceCard(MainWindow w) : base(w, T("settings.play_with"), 900)
    {
        AddRow(T("settings.keyboard_and_mouse"), () => Names(w, "keyboard", "mouse"), activate: () => Pick("keyboard"));
        AddRow(T("settings.controller_2"), () => Names(w, "pad"), activate: () => Pick("pad"));
        changed = () => Refresh();
        Focus = w.Devs.Connected("pad") ? 1 : 0;
    }

    static string Names(MainWindow w, params string[] kinds)
    {
        var names = kinds.Select(k => w.Devs.Show(k)).Where(d => d.Name != null && d.On).Select(d => d.Name).ToList();
        string v = names.Count == 0 ? (kinds[0] == "pad" ? T("settings.none_plugged_in") : T("settings.none_found")) : string.Join("  ·  ", names);
        return v.Length > 34 ? v[..33] + "…" : v;
    }

    void Pick(string kind)
    {
        S.Main = kind;
        if (kind == "pad") S.PadOn = true; else { S.KeyboardOn = true; S.MouseOn = true; }
        w.Settings.Save();
        w.InputChanged();
        Close(false);
        new SetupBindsCard(w).Open();
    }

    public override void Open() { w.Devs.Changed += changed; base.Open(); }
    public override void Close(bool sound = true) { w.Devs.Changed -= changed; base.Close(sound); }
}

// The recommended controls, or your own: each console's screen in turn.
public sealed class SetupBindsCard : MenuCard
{
    InputPrefs S => w.Settings.Input;

    public SetupBindsCard(MainWindow w) : base(w, T("settings.your_controls"), 900)
    {
        AddRow(T("settings.use_the_recommended_controls"), activate: () =>
        {
            S.UseRecommended();
            w.Settings.Save();
            w.InputChanged();
            Close(false);
            Sounds.Play("favorite");
            new ControlsCard(w).Open();
        });
        AddRow(T("settings.customise"), activate: () =>
        {
            Close(false);
            Walk(w, ControlsCard.PadMain(w) ? "pad" : "keyboard", InputMap.ManagedSystems(w), 0);
        });
    }

    static void Walk(MainWindow w, string kind, List<string> systems, int i)
    {
        if (i >= systems.Count) { new ControlsCard(w).Open(); return; }
        DeviceScreen.For(w, kind, () => Ui.After(60, () => Walk(w, kind, systems, i + 1)), new ControlScope(systems[i])).Open();
    }
}

// One device on a full screen: options as chips along the top, callouts down both sides, the device
// drawn in the middle with a line from the picked callout to its key or button.
public abstract class DeviceScreen : IScreen
{
    protected readonly MainWindow w;
    protected readonly Palette P = Palette.I;
    protected readonly InputPrefs S;
    protected readonly string Kind;
    protected ControlScope Sc;                   // whose controls (keyboard / controller screens)
    readonly Action closedCb;
    readonly Grid root = new();
    protected readonly Canvas C = new() { Width = 1920, Height = 1080 };
    protected readonly Canvas Drawing = new();
    readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };    // Keyboard | Mouse | Controller, top left
    readonly StackPanel chips = new() { Orientation = Orientation.Horizontal };   // the console, device options, on/off last (top right)
    readonly StackPanel bottom = new() { Orientation = Orientation.Horizontal };  // Reset, bottom left
    readonly StackPanel[] cols = { new(), new() };
    readonly TextBlock status, message, hint;
    // the console drop-down: every console, under the console chip
    readonly Border drop = new() { Visibility = Visibility.Collapsed, MinWidth = 380 };
    readonly StackPanel dropRows = new();
    List<string> dropIds = new();
    int dropFocus = -1;                          // -1: closed
    ControlScope returnScope;                    // the mouse screen: the console to go back to on the other tabs
    readonly Line line = new() { StrokeThickness = 3, IsHitTestVisible = false, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Ellipse dot = new() { Width = 14, Height = 14, IsHitTestVisible = false };
    readonly TranslateTransform bump = new();
    readonly ScaleTransform scale = new(1, 1, 960, 540);
    bool closed;
    public static DeviceScreen Current;   // for test scripts

    protected sealed class Item
    {
        public int Col;                        // 0 tabs and chips along the top, 1 left column, 2 right column, 3 along the bottom
        public bool Selected;                  // the tab of this screen's device
        public string Id, Label, Anchor;       // Anchor: the key / button on the drawing it points at
        public Func<string> Value;
        public Action Do;
        public Func<int> Level; public int LevelMax; public Action<int> Step;   // a level row: Left / Right steps it
        public Border El, Fill; public TextBlock LabelText, ValueText;
        public Toggle Switch;                  // Callouts: shown instead of the text while the value is On / Off
        public bool NoSwitch;                  // a value that can also be something else (Mouse camera: Some)
    }
    protected readonly List<Item> Items = new();
    protected int Focus;
    protected readonly Dictionary<string, Point> Anchors = new();
    protected string Capturing;                  // the item waiting for a key or button
    protected string Assigning;                  // a drawn key / button no game button uses yet: pick the game button for it
    protected Point LastPoint = new(-1, -1);     // the pointer, as last routed to this screen (stage coordinates)

    public static DeviceScreen For(MainWindow w, string kind, Action closed, ControlScope scope = null)
    {
        scope ??= new ControlScope(InputMap.ManagedSystems(w)[0]);
        return kind switch
        {
            "keyboard" => new KeyboardScreen(w, closed, scope),
            "mouse" => new MouseScreen(w, closed),
            _ => new ControllerScreen(w, closed, scope),
        };
    }

    // The Input button on Home: the screen of the device you play with, straight away.
    public static void OpenInput(MainWindow w) =>
        For(w, ControlsCard.PadMain(w) && w.Devs.Connected("pad") ? "pad" : "keyboard", null).Open();

    protected DeviceScreen(MainWindow win, string kind, string iconKind, Action closed, ControlScope scope)
    {
        w = win; Kind = kind; S = w.Settings.Input; closedCb = closed; Sc = scope;
        root.Children.Add(new Rectangle { Fill = P.Bg });
        root.Children.Add(C);
        root.RenderTransform = scale;

        Canvas.SetLeft(tabs, 64); Canvas.SetTop(tabs, 40); C.Children.Add(tabs);
        status = Fonts.Text("", 24, P.Text2); status.MaxWidth = 900; status.TextTrimming = TextTrimming.CharacterEllipsis;
        Canvas.SetLeft(status, 74); Canvas.SetTop(status, 116); C.Children.Add(status);
        Canvas.SetRight(chips, 70); Canvas.SetTop(chips, 40); C.Children.Add(chips);
        Canvas.SetLeft(bottom, 64); Canvas.SetTop(bottom, 984); C.Children.Add(bottom);
        chips.RenderTransform = bump;
        // the device tabs (one game's controls have no mouse screen)
        foreach (var k in Sc.GameId != null ? new[] { "keyboard", "pad" } : new[] { "keyboard", "mouse", "pad" }) Tab(k);
        Canvas.SetLeft(cols[0], 64); Canvas.SetTop(cols[0], 196); cols[0].Width = 440; C.Children.Add(cols[0]);
        Canvas.SetLeft(cols[1], 1416); Canvas.SetTop(cols[1], 196); cols[1].Width = 440; C.Children.Add(cols[1]);
        C.Children.Add(Drawing);
        line.Stroke = Palette.RingBrush(); dot.Fill = Palette.RingBrush();
        C.Children.Add(line); C.Children.Add(dot);
        message = Fonts.Text("", 28, P.Accent, FontWeights.Normal, Fonts.Display);
        var mbox = new Grid { Width = 1920 }; message.HorizontalAlignment = HorizontalAlignment.Center; mbox.Children.Add(message);
        Canvas.SetTop(mbox, 930); C.Children.Add(mbox);
        hint = Fonts.Text("", 23, P.Text2);
        var hbox = new Grid { Width = 1920 }; hint.HorizontalAlignment = HorizontalAlignment.Center; hbox.Children.Add(hint);
        Canvas.SetTop(hbox, 1000); C.Children.Add(hbox);
        drop.Background = P.Card; drop.BorderBrush = P.Line; drop.BorderThickness = new Thickness(1.5);
        drop.CornerRadius = new CornerRadius(20); drop.Padding = new Thickness(8);
        drop.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0.25, Color = Colors.Black };
        drop.Child = dropRows;
        C.Children.Add(drop);   // last: over everything
        w.Devs.Changed += DevsChanged;
    }

    protected abstract string Hint { get; }
    protected virtual void Highlight() { }        // tint bound keys / buttons, glow the focused one
    protected virtual void Picked(Item it) => it.Do?.Invoke();
    protected virtual void CancelCapture() { }
    protected virtual void BuildCallouts() { }    // the console's buttons, again when the console changes

    void DevsChanged() { if (!closed) Refresh(); }

    // The console (or game) whose controls these are; the mouse screen is just "Mouse".
    public string TitleText
    {
        get
        {
            string t = Kind == "mouse" ? T("settings.mouse") : Sc.GameId != null ? Sc.GameName : Sc.Name;
            return t.Length > 30 ? t[..29] + "…" : t;
        }
    }

    // A pill along the top (col 0, right of the tabs) or along the bottom (col 3).
    protected Item Chip(string id, Func<string> value, Action act, int col = 0)
    {
        var it = new Item { Col = col, Id = id, Value = value, Do = act };
        var t = Fonts.Text("", 25, P.Text); t.VerticalAlignment = VerticalAlignment.Center;
        it.ValueText = t;
        it.El = new Border { Height = 58, CornerRadius = new CornerRadius(29), Padding = new Thickness(26, 0, 26, 0), BorderThickness = new Thickness(3), Margin = col == 0 ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0), Child = t };
        (col == 0 ? chips : bottom).Children.Add(it.El);
        Items.Add(it);
        return it;
    }

    // A device tab: its icon and name; this screen's own is filled in.
    void Tab(string kind)
    {
        bool sel = kind == Kind;
        var ink = sel ? Brushes.White : P.Text;
        var t = Fonts.Text(InputDevices.Noun(kind), 25, ink); t.VerticalAlignment = VerticalAlignment.Center;
        var icon = Art.Icon(kind == "pad" ? "controller" : kind, 32, ink); icon.VerticalAlignment = VerticalAlignment.Center; icon.Margin = new Thickness(0, 0, 12, 0);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, t } };
        var it = new Item { Col = 0, Id = "dev:" + kind, Value = () => InputDevices.Noun(kind), Selected = sel, ValueText = t };
        it.Do = () => { if (TabOff(kind)) Say(T("settings.connect_a_controller")); else SwitchTo(kind); };
        it.El = new Border { Height = 58, CornerRadius = new CornerRadius(29), Padding = new Thickness(20, 0, 26, 0), BorderThickness = new Thickness(3), Margin = new Thickness(0, 0, 10, 0), Child = sp };
        tabs.Children.Add(it.El);
        Items.Add(it);
    }

    // The Controller tab, from another screen, with no controller plugged in.
    bool TabOff(string kind) => kind == "pad" && Kind != "pad" && !w.Devs.Connected("pad");

    // Another device's screen in place of this one (same console, same way back).
    void SwitchTo(string kind)
    {
        if (kind == Kind || closed) return;
        var scope = Kind == "mouse" ? returnScope : Sc;
        var next = For(w, kind, closedCb, kind == "mouse" ? null : scope);
        next.returnScope = scope;
        next.Focus = Math.Max(0, next.Items.FindIndex(i => i.Id == "dev:" + kind));
        closed = true;
        if (Capturing != null) { Capturing = null; CancelCapture(); }
        Assigning = null;
        CloseDrop(false);
        w.Devs.Changed -= DevsChanged;
        Current = next;
        next.Refresh();
        w.CardLayer.Children.Add(next.root);
        w.Replace(this, next);
        Anim.FromTo(next.root, UIElement.OpacityProperty, 0, 1, 160);
        var old = root;
        Anim.To(old, UIElement.OpacityProperty, 0, 160, done: () => w.CardLayer.Children.Remove(old));
    }

    protected Item Callout(int col, string id, string label, string sub, Func<string> value, string anchor, Action act = null)
    {
        var it = new Item { Col = col, Id = id, Label = label, Anchor = anchor, Value = value, Do = act };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        it.LabelText = Fonts.Text(label, 23, P.Text); sp.Children.Add(it.LabelText);
        if (sub != null) { var st = Fonts.Text(sub, 16, P.Text2); sp.Children.Add(st); }
        g.Children.Add(sp);
        var v = Fonts.Text("", 23, P.Accent, FontWeights.Medium); v.VerticalAlignment = VerticalAlignment.Center; v.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(v, 1); g.Children.Add(v);
        it.ValueText = v;
        it.Switch = new Toggle(30) { Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(it.Switch, 1); g.Children.Add(it.Switch);
        it.El = new Border { Height = sub != null ? 60 : 54, CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 0, 18, 0), BorderThickness = new Thickness(2.5), Margin = new Thickness(0, 0, 0, 4), Child = g };
        cols[col - 1].Children.Add(it.El);
        Items.Add(it);
        return it;
    }

    // A row with a small level bar (1..max). Left / Right steps it; A / click goes up one and wraps round.
    protected Item LevelCallout(int col, string id, string label, Func<int> level, int max, Action<int> set, string anchor)
    {
        var it = Callout(col, id, label, null, () => level().ToString(), anchor);
        it.Level = level; it.LevelMax = max;
        it.Step = d => set(level() + d);
        it.Do = () => set(level() >= max ? 1 : level() + 1);
        var track = new Border { Width = 110, Height = 8, CornerRadius = new CornerRadius(4), Background = P.Line, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        it.Fill = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = P.Accent, HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = it.Fill;
        var g = (Grid)it.El.Child;
        g.Children.Remove(it.ValueText);
        it.ValueText.MinWidth = 30; it.ValueText.TextAlignment = TextAlignment.Right;
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(track); right.Children.Add(it.ValueText);
        Grid.SetColumn(right, 1); g.Children.Add(right);
        return it;
    }

    // Long columns (PS4: 12 buttons, 12 directions and the mouse look row) use slimmer rows so they end above the message line.
    protected void FitColumns()
    {
        if (!Items.Where(i => i.Col is 1 or 2).GroupBy(i => i.Col).Any(c => c.Count() > 12)) return;
        foreach (var it in Items.Where(i => i.Col is 1 or 2))
        {
            it.El.Height = 46; it.El.Margin = new Thickness(0, 0, 0, 3);
            it.LabelText.FontSize = 21; it.ValueText.FontSize = 21;
        }
    }

    // The callouts again, for another console (the chips stay).
    void Rebuild()
    {
        string focusId = FocusedId;
        Items.RemoveAll(i => i.Col is 1 or 2);
        cols[0].Children.Clear(); cols[1].Children.Clear();
        BuildCallouts();
        FitColumns();
        Focus = Math.Max(0, Items.FindIndex(i => i.Id == focusId));
        Refresh();
    }

    // The console chip (every console's controls) or, for one game, whether it has its own.
    protected void ScopeChip()
    {
        if (Sc.GameId != null)
        {
            Chip("own", () => T("settings.this_game_only_2") + (S.HasOwn(Sc) ? T("common.on") : T("common.off")), () =>
            {
                bool own = !S.HasOwn(Sc);
                S.SetOwn(Sc, own);
                Say(own ? T("settings.this_game_has_its_own_controls") : T("settings.this_game_uses_the_controls", Sc.Name));
                Saved();
            });
            return;
        }
        var list = InputMap.ManagedSystems(w);
        if (list.Count < 2) return;
        var it = Chip("console", () => Sc.Name, OpenDrop);
        it.ValueText.FontWeight = FontWeights.Medium;
        var chev = Fonts.Text("▾", 25, P.Accent); chev.VerticalAlignment = VerticalAlignment.Center; chev.Margin = new Thickness(12, 0, 0, 0);
        it.El.Child = null;
        it.El.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { it.ValueText, chev } };
    }

    // ---- the console drop-down ----

    public string DropList => dropFocus < 0 ? "closed" : string.Join(",", dropIds);

    void OpenDrop()
    {
        var chip = Items.FirstOrDefault(i => i.Id == "console");
        if (chip == null) return;
        dropIds = InputMap.ManagedSystems(w);
        dropRows.Children.Clear();
        foreach (var id in dropIds)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Fonts.Text(InputMap.SystemName(id), 24, id == Sc.System ? P.Accent : P.Text, id == Sc.System ? FontWeights.Medium : FontWeights.Normal);
            name.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(name);
            var own = Fonts.Text(S.Changed(id) ? T("settings.your_own") : "", 19, P.Text2); own.VerticalAlignment = VerticalAlignment.Center; own.Margin = new Thickness(24, 0, 0, 0);
            Grid.SetColumn(own, 1); g.Children.Add(own);
            dropRows.Children.Add(new Border { Height = dropIds.Count > 14 ? Math.Max(36, 840 / dropIds.Count - 4) : 56, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 0, 20, 0), BorderThickness = new Thickness(2.5), Margin = new Thickness(0, 2, 0, 2), Child = g });
        }
        dropFocus = Math.Max(0, dropIds.IndexOf(Sc.System));
        // under the chip, its right edge on the chip's
        drop.Visibility = Visibility.Visible;
        drop.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = 1850, y = 110;
        try
        {
            var at = chip.El.TransformToAncestor(C).Transform(new Point(chip.El.ActualWidth, chip.El.ActualHeight + 10));
            x = at.X; y = at.Y;
        }
        catch { /* not laid out yet: default place */ }
        Canvas.SetLeft(drop, Math.Max(64, x - Math.Max(drop.DesiredSize.Width, chip.El.ActualWidth)));
        Canvas.SetTop(drop, y);
        RefreshDrop();
        Sounds.Play("menuopen");
        Anim.FromTo(drop, UIElement.OpacityProperty, 0, 1, 160);
    }

    void RefreshDrop()
    {
        for (int i = 0; i < dropRows.Children.Count; i++)
        {
            var b = (Border)dropRows.Children[i]; bool f = i == dropFocus;
            b.Background = f ? P.RowFocus : Brushes.Transparent;
            b.BorderBrush = f ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(b, f);
        }
    }

    void CloseDrop(bool sound)
    {
        if (dropFocus < 0) return;
        dropFocus = -1;
        drop.Visibility = Visibility.Collapsed;
        if (sound) Sounds.Play("back");
    }

    void PickDrop(int i)
    {
        string id = dropIds[i];
        CloseDrop(false);
        Sounds.Play("select");
        if (id != Sc.System) { Sc = new ControlScope(id); Say(""); Rebuild(); }
    }

    void DropAct(Act a)
    {
        switch (a)
        {
            case Act.Up: case Act.Down:
                int n = dropFocus + (a == Act.Down ? 1 : -1);
                if (n < 0 || n >= dropIds.Count) { Anim.Edge(bump, a == Act.Up ? -1 : 1, true); return; }
                dropFocus = n; Sounds.Play("scroll"); RefreshDrop(); return;
            case Act.Accept: PickDrop(dropFocus); return;
            case Act.Back: CloseDrop(true); return;
        }
    }

    protected void Say(string text) { message.Text = text; if (text.Length > 0) Anim.FromTo(message, UIElement.OpacityProperty, 0.2, 1, 200); }

    public string Message => message.Text;
    public string FocusedId => Focus < Items.Count ? Items[Focus].Id : "";
    public bool IsCapturing => Capturing != null;
    public string ItemList => string.Join(",", Items.Select(i => i.Id));

    protected void Refresh()
    {
        var (name, on, seen) = w.Devs.Show(Kind);
        bool en = S.On(Kind);
        status.Text = (Kind != "mouse" && Sc.GameId != null ? $"{Sc.GameName}  ·  {Sc.Name}  ·  " : "")
                     + (name == null ? (Kind == "pad" ? T("settings.none_plugged_in") : T("settings.none_found"))
                     : on ? name
                     : $"{name}  ·  not connected" + (seen is DateTime d ? "  ·  last seen " + d.ToString("d MMM", CultureInfo.GetCultureInfo("en-GB")) : ""))
                     + (en ? "" : "  ·  turned off");
        status.Opacity = on && en ? 1 : 0.6;
        Drawing.Opacity = on && en ? 1 : 0.45;
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i]; bool f = i == Focus;
            it.ValueText.Text = it.Id == Capturing ? CaptureText
                : Assigning != null && f && CanTake(it) ? PartName(Assigning)
                : it.Value?.Invoke() ?? "";
            if (it.Switch != null)
            {
                bool? sw = it.Level == null && !it.NoSwitch && it.Id != Capturing ? Toggle.Of(it.ValueText.Text) : null;
                it.ValueText.Visibility = sw == null ? Visibility.Visible : Visibility.Collapsed;
                it.Switch.Visibility = sw == null ? Visibility.Collapsed : Visibility.Visible;
                if (sw is bool swOn) it.Switch.Set(swOn);
            }
            if (it.Level != null) { it.Fill.Width = 110.0 * Math.Clamp(it.Level(), 0, it.LevelMax) / it.LevelMax; it.Fill.Opacity = f ? 1 : 0.6; }
            it.El.Background = it.Selected ? P.Accent : f ? P.RowFocus : it.Col is 0 or 3 ? P.ButtonFill : Brushes.Transparent;
            it.El.BorderBrush = f ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(it.El, f);
            if (it.Id?.StartsWith("dev:") == true) it.El.Opacity = TabOff(it.Id[4..]) ? 0.4 : 1;
        }
        hint.Text = Hint;
        Highlight();
        root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateLine));
    }

    protected virtual string CaptureText => T("settings.press_a_key");

    void UpdateLine()
    {
        var it = Focus < Items.Count ? Items[Focus] : null;
        Point to = default; bool has = false;
        // while picking the game button for a drawn key, the line goes to that key
        if (it != null && Assigning != null && CanTake(it) && PartPoint(Assigning) is Point ap) { to = ap; has = true; }
        else if (it?.Anchor != null && Anchors.TryGetValue(it.Anchor, out var an)) { to = an; has = true; }
        if (!has || it.El.ActualWidth <= 0)
        { line.Visibility = dot.Visibility = Visibility.Hidden; return; }
        Point a;
        try
        {
            var tr = it.El.TransformToAncestor(C);
            a = it.Col == 1 ? tr.Transform(new Point(it.El.ActualWidth, it.El.ActualHeight / 2)) : tr.Transform(new Point(0, it.El.ActualHeight / 2));
        }
        catch { return; }
        line.X1 = a.X; line.Y1 = a.Y; line.X2 = to.X; line.Y2 = to.Y;
        Canvas.SetLeft(dot, to.X - 7); Canvas.SetTop(dot, to.Y - 7);
        line.Visibility = dot.Visibility = Visibility.Visible;
        Anim.FromTo(line, UIElement.OpacityProperty, 0.3, 1, 180);
    }

    public void Open()
    {
        Current = this;
        Refresh();
        w.CardLayer.Children.Add(root);
        w.Push(this);
        Sounds.Play("menuopen");
        Anim.FromTo(root, UIElement.OpacityProperty, 0, 1, 220);
        Anim.FromTo(scale, ScaleTransform.ScaleXProperty, 1.03, 1, 280);
        Anim.FromTo(scale, ScaleTransform.ScaleYProperty, 1.03, 1, 280);
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        if (Capturing != null) { Capturing = null; CancelCapture(); }
        Assigning = null;
        CloseDrop(false);
        if (Current == this) Current = null;
        w.Devs.Changed -= DevsChanged;
        Sounds.Play("back");
        w.Pop(this);
        w.InputChanged();
        closedCb?.Invoke();
        Anim.To(root, UIElement.OpacityProperty, 0, 160, done: () => w.CardLayer.Children.Remove(root));
    }

    public void Reread() => Refresh();   // the launcher keys changed (the Home menu key takes a game key away)

    protected void Saved()
    {
        w.Settings.Save();
        w.InputChanged();
        Refresh();
    }

    // Up / Down in a column (Up from the top goes to the chips), Left / Right across.
    void Move(Act a)
    {
        var cur = Items[Focus];
        int next = -1;
        List<int> Col(int c) => Enumerable.Range(0, Items.Count).Where(i => Items[i].Col == c).ToList();
        if (cur.Col is 0 or 3)
        {
            var row = Col(cur.Col); int k = row.IndexOf(Focus);
            var c1 = Col(1).Count > 0 ? Col(1) : Col(2);
            if (a == Act.Left && k > 0) next = row[k - 1];
            else if (a == Act.Right && k < row.Count - 1) next = row[k + 1];
            else if (a == Act.Down && cur.Col == 0 && c1.Count > 0) next = c1[0];
            else if (a == Act.Up && cur.Col == 3) next = c1.Count > 0 ? c1[^1] : Col(0).Count > 0 ? Col(0)[0] : -1;
        }
        else
        {
            var col = Col(cur.Col); int k = col.IndexOf(Focus);
            if (a is Act.Left or Act.Right && cur.Step != null)
            {
                int d = a == Act.Right ? 1 : -1, after = cur.Level() + d;
                if (after < 1 || after > cur.LevelMax) { Anim.Edge(bump, d, false); return; }
                cur.Step(d);
                Sounds.Play("scroll");
                Saved();
                return;
            }
            // Up from the left column's top: this device's tab; from the right column's: the last chip (on / off)
            int tab = Items.FindIndex(i => i.Selected);
            if (a == Act.Up) next = k > 0 ? col[k - 1] : Col(0).Count > 0 ? (cur.Col == 1 && tab >= 0 ? tab : Col(0)[cur.Col == 1 ? 0 : Col(0).Count - 1]) : -1;
            else if (a == Act.Down && k < col.Count - 1) next = col[k + 1];
            else if (a == Act.Down && Col(3).Count > 0) next = Col(3)[0];
            else if (a is Act.Left or Act.Right)
            {
                var other = Col(a == Act.Left ? 1 : 2);
                if (other.Count > 0 && cur.Col != (a == Act.Left ? 1 : 2)) next = other[Math.Min(k, other.Count - 1)];
            }
        }
        if (next < 0) { Anim.Edge(bump, a is Act.Left or Act.Up ? -1 : 1, a is Act.Up or Act.Down); return; }
        Focus = next;
        Sounds.Play("scroll");
        if (Assigning == null) Say("");
        Refresh();
    }

    public void OnAct(Act a)
    {
        if (closed) return;
        if (dropFocus >= 0) { DropAct(a); return; }
        if (Capturing != null)
        {
            if (a == Act.Back) { Capturing = null; CancelCapture(); Sounds.Play("back"); Say(T("settings.cancelled")); Refresh(); }
            return;
        }
        if (Assigning != null)
        {
            if (a == Act.Back) { Assigning = null; Sounds.Play("back"); Say(T("settings.cancelled")); Refresh(); return; }
            if (a == Act.Accept)
            {
                var it = Items[Focus];
                if (!CanTake(it)) { Sounds.Play("back"); return; }
                string part = Assigning;
                Assigning = null;
                Assign(it, part);
                Refresh();
                return;
            }
        }
        switch (a)
        {
            case Act.Up: case Act.Down: case Act.Left: case Act.Right: Move(a); break;
            case Act.Accept:
                Sounds.Play("select"); Say("");
                var it = Items[Focus];
                if (it.Col is 0 or 3) it.Do?.Invoke(); else Picked(it);   // tabs, chips and Reset just do their thing
                if (!closed) Refresh();
                break;
            case Act.Back: Close(); break;
        }
    }

    // Test scripts: focus an item by id.
    public bool FocusOn(string id)
    {
        int i = Items.FindIndex(it => it.Id == id);
        if (i < 0) return false;
        Focus = i; Refresh();
        return true;
    }

    // ---- the drawing: each key / button on it is a "part" ----
    protected virtual string PartAt(Point p) => null;          // the part under the mouse (stage coordinates), topmost first
    protected virtual string ActionOf(string part) => null;    // the item that uses it, or null
    protected virtual Point? PartPoint(string part) => null;   // its middle, on the drawing
    protected virtual string PartName(string part) => part;
    protected virtual string Refuse(string part) => null;      // why it can't be a game button here, or null
    protected virtual bool NoticeFor(string part) => false;    // it can't, and a card says why (the Home menu key)
    protected virtual bool PromptOnClick => true;              // a bound part starts its item's prompt (the controller one needs a controller)
    protected virtual void Assign(Item it, string part) { }    // give the item this part
    protected virtual bool TakeDrawn(string part) => false;    // a part clicked while a binding waits: use it (true), or cancel

    // A game button can be given a key / button from the drawing (not the chips or level rows).
    protected static bool CanTake(Item it) => it.Col > 0 && it.Level == null && it.Anchor != null;

    // Is the stage point on this shape as drawn (its outline, not its box)?
    protected bool Hit(Shape s, Point p)
    {
        if (s == null || !s.IsVisible) return false;
        try
        {
            var back = s.TransformToAncestor(w.Stage).Inverse;
            if (back == null) return false;
            var q = back.Transform(p);
            var g = s.RenderedGeometry;
            return g.FillContains(q) || g.StrokeContains(new Pen(Brushes.Black, s.StrokeThickness + 4), q);
        }
        catch { return false; }
    }

    // A key / button on the drawing: the prompt for the game button it does, straight away. One no game
    // button uses yet: pick the game button for it.
    void ClickDrawn(string part)
    {
        string act = ActionOf(part);
        int j = act == null ? -1 : Items.FindIndex(it => it.Id == act);
        if (j >= 0 && Items[j].Level != null) { Assigning = null; Focus = j; Sounds.Play("scroll"); Say(""); Refresh(); return; }   // a level row: Left / Right steps it
        if (j >= 0 && PromptOnClick) { Assigning = null; Focus = j; OnAct(Act.Accept); return; }
        if (NoticeFor(part)) { Assigning = null; return; }
        if (Refuse(part) is string why) { Assigning = null; Sounds.Play("back"); Say(why); Refresh(); return; }
        Assigning = part;
        if (j >= 0) Focus = j;
        else if (!CanTake(Items[Focus])) Focus = Math.Max(0, Items.FindIndex(CanTake));
        Sounds.Play("select");
        Say(T("settings.pick_the_button_for", PartName(part)));
        Refresh();
    }

    // Test scripts: where a part is on the stage, and the part waiting for its game button.
    public Point? PartOnStage(string part)
    {
        if (PartPoint(part) is not Point p) return null;
        try { return C.TransformToAncestor(w.Stage).Transform(p); } catch { return null; }
    }
    public string AssigningPart => Assigning ?? "no";
    public Point? ItemOnStage(string id)
    {
        var it = Items.FirstOrDefault(x => x.Id == id);
        if (it?.El == null || !it.El.IsVisible) return null;
        try { return it.El.TransformToAncestor(w.Stage).Transform(new Point(it.El.ActualWidth / 2, it.El.ActualHeight / 2)); } catch { return null; }
    }

    public bool OnMouse(MouseAct m, Point p)
    {
        if (closed) return true;
        LastPoint = p;
        if (dropFocus >= 0)
        {
            // the drop-down: point = select, click = pick, click outside = close
            int r = Enumerable.Range(0, dropRows.Children.Count).FirstOrDefault(j => Pointer.Over((FrameworkElement)dropRows.Children[j], w.Stage, p), -1);
            switch (m)
            {
                case MouseAct.Move: if (r >= 0 && r != dropFocus) { dropFocus = r; Sounds.Play("scroll"); RefreshDrop(); } return true;
                case MouseAct.Down: if (r >= 0) PickDrop(r); else CloseDrop(true); return true;
                // the release of the click that opened it: picking and closing happen on Down
                case MouseAct.Up: return true;
                case MouseAct.WheelUp: DropAct(Act.Up); return true;
                case MouseAct.WheelDown: DropAct(Act.Down); return true;
            }
            CloseDrop(true);
            return true;
        }
        int i = Items.FindIndex(it => Pointer.Over(it.El, w.Stage, p));
        switch (m)
        {
            case MouseAct.Move:
                if (Capturing == null && i >= 0 && i != Focus) { Focus = i; Sounds.Play("scroll"); Refresh(); }
                return i >= 0 || PartAt(p) != null;
            case MouseAct.Down:
                if (Capturing != null)
                {
                    // a part on the drawing is the answer; anywhere else cancels
                    if (!(PartAt(p) is string cp && TakeDrawn(cp))) OnAct(Act.Back);
                    return true;
                }
                if (i >= 0) { Focus = i; OnAct(Act.Accept); return true; }
                if (PartAt(p) is string part) ClickDrawn(part);
                else if (Assigning != null) OnAct(Act.Back);
                return true;
            case MouseAct.WheelUp: OnAct(Act.Up); return true;
            case MouseAct.WheelDown: OnAct(Act.Down); return true;
        }
        return false;   // right click: back
    }

    // The keyboard's on/off chip: it can only go off while a controller can take over.
    protected void ToggleKeyboard()
    {
        if (S.KeyboardOn && !(S.PadOn && w.Devs.Connected("pad")))
        { Say(T("settings.plug_in_a_controller_first_without")); return; }
        S.KeyboardOn = !S.KeyboardOn;
        Saved();
    }

    protected Item PopupChip() => Chip("popups", () => "Pop-ups: " + (S.PopupsFor(Kind) ? T("common.on") : T("common.off")), () =>
    {
        bool on = !S.PopupsFor(Kind);
        if (on) S.Popups = true;
        switch (Kind) { case "keyboard": S.KeyboardPopups = on; break; case "mouse": S.MousePopups = on; break; default: S.PadPopups = on; break; }
        Saved();
    });
}

// ---- keyboard (and the mouse buttons, which can be game buttons too) ----

public sealed class KeyboardScreen : DeviceScreen, IScreen
{
    const double U = 44, Gap = 4;
    readonly Dictionary<string, Shape> keys = new();       // bind name ("K", "LeftShift", "MouseLeft") -> drawn key / mouse button
    readonly Dictionary<string, TextBlock> keyText = new();
    readonly List<string> drawOrder = new();                 // bind names as drawn: the last one is on top
    Path mouseBody; Point mouseAt;                           // the drawn mouse: the mouse look row points at it
    readonly List<Line> ridges = new();                      // the wheel's grip lines

    static readonly (string label, Key key, double wu)[][] Layout =
    {
        new[] { ("`", Key.Oem3, 1.0), ("1", Key.D1, 1), ("2", Key.D2, 1), ("3", Key.D3, 1), ("4", Key.D4, 1), ("5", Key.D5, 1), ("6", Key.D6, 1), ("7", Key.D7, 1), ("8", Key.D8, 1), ("9", Key.D9, 1), ("0", Key.D0, 1), ("-", Key.OemMinus, 1), ("=", Key.OemPlus, 1), ("Backspace", Key.Back, 2) },
        new[] { ("Tab", Key.Tab, 1.5), ("Q", Key.Q, 1), ("W", Key.W, 1), ("E", Key.E, 1), ("R", Key.R, 1), ("T", Key.T, 1), ("Y", Key.Y, 1), ("U", Key.U, 1), ("I", Key.I, 1), ("O", Key.O, 1), ("P", Key.P, 1), ("[", Key.OemOpenBrackets, 1), ("]", Key.Oem6, 1), ("\\", Key.Oem5, 1.5) },
        new[] { ("Caps", Key.CapsLock, 1.75), ("A", Key.A, 1), ("S", Key.S, 1), ("D", Key.D, 1), ("F", Key.F, 1), ("G", Key.G, 1), ("H", Key.H, 1), ("J", Key.J, 1), ("K", Key.K, 1), ("L", Key.L, 1), (";", Key.Oem1, 1), ("'", Key.OemQuotes, 1), ("Enter", Key.Return, 2.25) },
        new[] { ("Shift", Key.LeftShift, 2.25), ("Z", Key.Z, 1), ("X", Key.X, 1), ("C", Key.C, 1), ("V", Key.V, 1), ("B", Key.B, 1), ("N", Key.N, 1), ("M", Key.M, 1), (",", Key.OemComma, 1), (".", Key.OemPeriod, 1), ("/", Key.OemQuestion, 1), ("Shift", Key.RightShift, 2.75) },
        new[] { ("Ctrl", Key.LeftCtrl, 1.25), ("Win", Key.LWin, 1.25), ("Alt", Key.LeftAlt, 1.25), ("", Key.Space, 6.25), ("Alt", Key.RightAlt, 1.25), ("Fn", Key.None, 1.25), ("Menu", Key.Apps, 1.25), ("Ctrl", Key.RightCtrl, 1.25) },
    };

    public KeyboardScreen(MainWindow w, Action closed, ControlScope scope) : base(w, "keyboard", "keyboard", closed, scope)
    {
        ScopeChip();
        Chip("on", () => T("settings.keyboard") + (S.KeyboardOn ? T("common.on") : T("common.off")), ToggleKeyboard);
        Chip("reset", () => T("settings.reset_keys"), () => new ResetBindsCard(w, () => { S.ResetKeys(Sc); Say(T("settings.back_to_the_recommended_keys")); Saved(); }).Open(), col: 3);
        BuildCallouts();
        FitColumns();
        DrawKeyboard();
        DrawMouse();
        Focus = Items.FindIndex(i => i.Col == 1);
    }

    // Left: the console's buttons. Right: its d-pad and sticks.
    protected override void BuildCallouts()
    {
        var L = Sc.Layout;
        foreach (var (id, label) in L.Buttons) Callout(1, id, label, null, () => InputMap.BindName(S.KeyFor(Sc, id)), "k:" + id);
        // Switch: with the mouse camera on, the right stick is the mouse only (one source per stick there)
        bool MouseStick(string a) => a.StartsWith("rs_") && Sc.System is "switch" or "switch2" && InputMap.MouseCamOn(S, Sc);
        foreach (var id in L.Directions) { string a = id; Callout(2, a, L.Label(a), null, () => MouseStick(a) ? T("settings.mouse") : InputMap.BindName(S.KeyFor(Sc, a)), "k:" + a); }
        // PS4 / Switch: the mouse moves the camera (right stick), and how fast
        if (InputMap.MouseCamSystem(Sc.System) && L.RightStick)
        {
            Callout(2, "mousecam", T("settings.mouse_camera"), null, () => S.MouseCamera(Sc) ? T("common.on") : T("common.off"), null, () => { S.SetMouseCamera(Sc, !S.MouseCamera(Sc)); Saved(); });
            LevelCallout(2, "mousespeed", T("settings.mouse_look_sensitivity"), () => S.MouseSpeed(Sc), InputMap.MouseSpeedMax, v => S.SetMouseSpeed(Sc, v), "k:mouse");
        }
    }

    void DrawKeyboard()
    {
        double totalU = 15 + 0.4 + 3;
        double x0 = 960 - totalU * U / 2, y0 = 340;
        var plate = new Border { Width = totalU * U + 28, Height = 5 * U + 28, CornerRadius = new CornerRadius(22), Background = P.Card, BorderBrush = P.Line, BorderThickness = new Thickness(1.5) };
        Canvas.SetLeft(plate, x0 - 14); Canvas.SetTop(plate, y0 - 14); Drawing.Children.Add(plate);
        for (int r = 0; r < Layout.Length; r++)
        {
            double x = x0;
            foreach (var (label, key, wu) in Layout[r])
            {
                AddKey(label, key, x, y0 + r * U, wu * U);
                x += wu * U;
            }
        }
        // arrow cluster, right of the 60% block
        double ax = x0 + 15.4 * U;
        AddKey("↑", Key.Up, ax + U, y0 + 3 * U, U);
        AddKey("←", Key.Left, ax, y0 + 4 * U, U);
        AddKey("↓", Key.Down, ax + U, y0 + 4 * U, U);
        AddKey("→", Key.Right, ax + 2 * U, y0 + 4 * U, U);
    }

    void AddKey(string label, Key key, double x, double y, double wpx)
    {
        var r = new Rectangle { Width = wpx - Gap, Height = U - Gap, RadiusX = 8, RadiusY = 8, StrokeThickness = 1.5 };
        Canvas.SetLeft(r, x + Gap / 2); Canvas.SetTop(r, y + Gap / 2);
        Drawing.Children.Add(r);
        var t = Fonts.Text(label, label.Length > 2 ? 14 : 18, P.Text2); t.IsHitTestVisible = false;
        t.Width = r.Width; t.TextAlignment = TextAlignment.Center;
        Canvas.SetLeft(t, x + Gap / 2); Canvas.SetTop(t, y + Gap / 2 + (r.Height - (label.Length > 2 ? 19 : 24)) / 2);
        Drawing.Children.Add(t);
        string name = key.ToString();
        if (key != Key.None && !keys.ContainsKey(name)) { keys[name] = r; keyText[name] = t; drawOrder.Add(name); }
    }

    // The mouse under the keyboard: its buttons can be game buttons (the PS4 controls use them). Drawn at
    // M times its design size, with a dark outline and a soft shadow so it stands out from the background.
    const double M = 1.35;
    static Geometry G(string d) { var g = Geometry.Parse(d).Clone(); g.Transform = new ScaleTransform(M, M); return g; }

    void DrawMouse()
    {
        double ox = 960 - 60 * M, oy = 622;
        var body = new Path
        {
            Data = G("M60 0 C 96 0 120 24 120 68 L 120 126 C 120 168 92 190 60 190 C 28 190 0 168 0 126 L 0 68 C 0 24 24 0 60 0 Z"),
            Fill = P.Card, Stroke = P.Text2, StrokeThickness = 2.5,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };
        Canvas.SetLeft(body, ox); Canvas.SetTop(body, oy); Drawing.Children.Add(body);
        mouseBody = body; mouseAt = new Point(ox + 120 * M, oy + 140 * M);
        Shape Rect(double wd, double ht, double rad) => new Rectangle { Width = wd * M, Height = ht * M, RadiusX = rad * M, RadiusY = rad * M };
        var parts = new (string, Shape, double, double)[]
        {
            ("MouseLeft", new Path { Data = G("M58 2 L 58 78 L 2 78 L 2 68 C 2 26 24 2 58 2 Z") }, 0, 0),
            ("MouseRight", new Path { Data = G("M62 2 C 96 2 118 26 118 68 L 118 78 L 62 78 Z") }, 0, 0),
            ("MouseMiddle", Rect(18, 38, 9), 51, 18),
            ("MouseForward", Rect(13, 28, 6), -10, 88),
            ("MouseBack", Rect(13, 28, 6), -10, 120),
        };
        foreach (var (id, s, x, y) in parts)
        {
            s.StrokeThickness = 2;
            Canvas.SetLeft(s, ox + x * M); Canvas.SetTop(s, oy + y * M); Drawing.Children.Add(s);
            keys[id] = s; drawOrder.Add(id);
        }
        // grip lines on the wheel, so it reads as a wheel (the wheel itself takes the clicks)
        for (int i = 0; i < 3; i++)
        {
            double y = oy + (27 + i * 10) * M;
            var l = new Line { X1 = ox + 55 * M, X2 = ox + 65 * M, Y1 = y, Y2 = y, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false, Stroke = P.Text2 };
            Drawing.Children.Add(l); ridges.Add(l);
        }
    }

    static Point Mid(Shape s)
    {
        var b = s.Outline().Bounds;
        return new Point(Canvas.GetLeft(s) + b.X + b.Width / 2, Canvas.GetTop(s) + b.Y + b.Height / 2);
    }

    protected override string Hint => "";

    protected override void Highlight()
    {
        Anchors.Clear();
        var bound = Sc.Layout.Actions.ToDictionary(a => a, a => S.KeyFor(Sc, a));
        string focus = Focus < Items.Count && Items[Focus].Col is 1 or 2 ? Items[Focus].Id : null;
        // while a drawn key waits for its game button, only that key is ringed
        string focusKey = Assigning ?? (focus != null ? bound.GetValueOrDefault(focus) : null);
        var blueFill = new SolidColorBrush(Color.FromArgb(48, Palette.Blue.R, Palette.Blue.G, Palette.Blue.B));
        var blueLine = new SolidColorBrush(Color.FromArgb(150, Palette.Blue.R, Palette.Blue.G, Palette.Blue.B));
        var blueStrong = new SolidColorBrush(Color.FromArgb(210, Palette.Blue.R, Palette.Blue.G, Palette.Blue.B));
        foreach (var (k, s) in keys)
        {
            bool isBound = bound.ContainsValue(k);
            bool f = focusKey != null && focusKey == k;
            bool mouse = InputMap.IsMouse(k);   // the mouse's parts have darker edges, so it doesn't fade into the background
            s.Fill = f ? P.RowFocus : isBound ? blueFill : P.ButtonFill;
            s.Stroke = f ? Palette.RingBrush() : isBound ? (mouse ? blueStrong : blueLine) : mouse ? P.Text2 : P.Line;
            s.StrokeThickness = f ? 3 : mouse ? 2 : 1.5;
            if (keyText.TryGetValue(k, out var t)) t.Foreground = isBound || f ? P.Text : P.Text2;
        }
        foreach (var l in ridges) l.Stroke = focusKey == "MouseMiddle" ? Palette.RingBrush() : bound.ContainsValue("MouseMiddle") ? blueStrong : P.Text2;
        foreach (var (id, k) in bound)
            if (k != null && keys.TryGetValue(k, out var s)) Anchors["k:" + id] = Mid(s);
        if (mouseBody != null)
        {
            bool mf = focus == "mousespeed";
            mouseBody.Fill = mf ? P.RowFocus : P.Card;
            mouseBody.Stroke = mf ? Palette.RingBrush() : P.Text2;
            mouseBody.StrokeThickness = mf ? 3.5 : 2.5;
            Anchors["k:mouse"] = mouseAt;
        }
    }

    // Topmost first: the wheel and side buttons sit on top of the mouse's body and its left / right buttons.
    protected override string PartAt(Point p)
    {
        for (int i = drawOrder.Count - 1; i >= 0; i--)
            if (Hit(keys[drawOrder[i]], p)) return drawOrder[i];
        if (Sc.Layout.Ps4 && Hit(mouseBody, p)) return "mouse";   // the mouse look row
        return null;
    }

    protected override string ActionOf(string part) =>
        part == "mouse" ? (Sc.Layout.Ps4 ? "mousespeed" : null) : Sc.Layout.Actions.FirstOrDefault(x => S.KeyFor(Sc, x) == part);

    protected override Point? PartPoint(string part) =>
        part == "mouse" ? mouseAt : keys.TryGetValue(part, out var s) ? Mid(s) : null;

    protected override string PartName(string part) => InputMap.BindName(part);

    protected override string Refuse(string part)
    {
        string bind = InputMap.ValidBind(part);
        if (bind == null) return $"{PartName(part)} can't be used for a game button";
        if (LauncherKeys.Taker(S, bind) is string taker) return $"{PartName(part)} {LauncherKeys.Does(taker)}";
        if (bind == "MouseLeft" && Sc.Layout.Touch && S.Touch) return T("settings.left_click_is_the_touch_screen");
        return null;
    }

    protected override void Assign(Item it, string part) => Take(it.Id, InputMap.ValidBind(part));

    protected override string CaptureText => T("settings.press_a_key");

    protected override void Picked(Item it)
    {
        if (it.Col == 0) { it.Do(); return; }
        if (it.Level != null) { it.Do(); Saved(); return; }
        Capturing = it.Id;
        Say(T("settings.press_the_key_or_mouse_button", it.Label) + (LauncherKeys.IsHome(S, Key.Escape) ? "" : T("settings.hold_esc_for_esc")));
    }

    // The Home menu key (or another game key) pressed for a game button: why not, and the way to move it.
    void HomeNotice(string bind)
    {
        Capturing = null; esc.Stop();
        Sounds.Play("back");
        string taker = LauncherKeys.Taker(S, InputMap.ValidBind(bind)) ?? LauncherKeys.Home;
        Say($"{InputMap.BindName(bind)} {LauncherKeys.Does(taker)}");
        Refresh();
        new HomeKeyNoticeCard(w, LauncherKeys.Show(Enum.TryParse(bind, out Key k) ? LauncherKeys.NameOf(k) : bind), taker).Open();
    }
    readonly EscHold esc = new();
    protected override void CancelCapture() => esc.Stop();
    protected override bool NoticeFor(string part)
    {
        if (!LauncherKeys.TakesBind(S, InputMap.ValidBind(part))) return false;
        HomeNotice(part);
        return true;
    }

    void Take(string id, string bind)
    {
        Capturing = null;
        string label = Sc.Layout.Label(id);
        string swapped = bind == null ? null : Sc.Layout.Actions.FirstOrDefault(a => a != id && S.KeyFor(Sc, a) == bind);
        S.SetKey(Sc, id, bind);
        Sounds.Play("favorite");
        Say(bind == null ? T("settings.has_no_key_now", label)
            : swapped != null ? T("settings.set_took_the_old_key", InputMap.BindName(bind), Sc.Layout.Label(swapped))
            : $"{InputMap.BindName(bind)} set");
        Saved();
    }

    // The next key pressed, as it is (Esc cancels, Delete clears). Keys the launcher can't give a game are refused.
    bool IScreen.TakeKey(Key k)
    {
        if (Capturing == null) return false;
        if (esc.Waiting) return true;
        if (k == Key.Escape)
        {
            // a tap cancels; held, Esc is the key (unless it is the Home menu key: then a tap still cancels)
            if (LauncherKeys.IsHome(S, Key.Escape)) { OnAct(Act.Back); return true; }
            string id = Capturing;
            esc.Begin(w, () => { if (Capturing == id) Take(id, InputMap.ValidBind(Key.Escape.ToString())); }, () => { if (Capturing == id) OnAct(Act.Back); });
            return true;
        }
        if (k == Key.ImeProcessed || k == Key.None) return true;
        if (k == Key.Delete) { Take(Capturing, null); return true; }
        if (k == Key.System) return true;
        if (!InputMap.Bindable(k)) { Sounds.Play("back"); Say(T("settings.can_t_be_used_for_a", InputMap.KeyName(k))); return true; }
        if (LauncherKeys.IsHome(S, k)) { HomeNotice(k.ToString()); return true; }
        Take(Capturing, InputMap.ValidBind(k.ToString()));
        return true;
    }

    // A mouse button while waiting: that button. A left click on a key or mouse button of the drawing picks
    // that one instead. Left click stays the touch screen in DS / 3DS games.
    bool IScreen.TakeMouse(MouseButton b)
    {
        if (Capturing == null) return false;
        if (b == MouseButton.Left)
        {
            // a test run moves only the scripted pointer; a real click is where the real mouse is
            Point at = string.IsNullOrEmpty(w.Opt.Script) ? Mouse.GetPosition(w.Stage) : LastPoint;
            if (PartAt(at) is string part && part != "mouse")
            {
                if (LauncherKeys.TakesBind(S, InputMap.ValidBind(part))) { HomeNotice(part); return true; }
                if (Refuse(part) is string why) { Sounds.Play("back"); Say(why + T("settings.pick_another_key_or_esc")); return true; }
                Take(Capturing, InputMap.ValidBind(part));
                return true;
            }
        }
        string bind = InputMap.MouseBind(b);
        if (bind == null) return true;
        if (bind == "MouseLeft" && Sc.Layout.Touch && S.Touch)
        { Sounds.Play("back"); Say(T("settings.left_click_is_the_touch_screen_2")); return true; }
        Take(Capturing, bind);
        return true;
    }
}

static class ShapeData
{
    // A shape's own outline, for finding its middle on the drawing.
    public static Geometry Outline(this Shape s) => s switch
    {
        Path p => p.Data,
        _ => new RectangleGeometry(new Rect(0, 0, s.Width, s.Height)),
    };
}

// ---- controller ----

public sealed class ControllerScreen : DeviceScreen
{
    readonly Dictionary<string, Shape> parts = new();
    const double OX = 660, OY = 400;
    ushort baseline; bool baseLt, baseRt, primed;

    public ControllerScreen(MainWindow w, Action closed, ControlScope scope) : base(w, "pad", "controller", closed, scope)
    {
        ScopeChip();
        Chip("nintendo", () => T("home.layout") + (w.Settings.NintendoButtons ? T("settings.nintendo") : T("settings.as_labelled")), () =>
        {
            w.Settings.NintendoButtons = !w.Settings.NintendoButtons;
            Say(w.Settings.NintendoButtons ? T("settings.bottom_and_right_buttons_swap_places") : T("settings.buttons_work_as_labelled"));
            Saved();
        });
        Chip("on", () => T("settings.controller") + (S.PadOn ? T("common.on") : T("common.off")), () =>
        {
            if (S.PadOn && !S.KeyboardOn) { Say(T("settings.turn_the_keyboard_on_first_something")); return; }
            S.PadOn = !S.PadOn; Saved();
        });
        Chip("reset", () => T("settings.reset_buttons"), () => new ResetBindsCard(w, () => { S.ResetPad(Sc); Say(T("settings.back_to_the_recommended_buttons")); Saved(); }).Open(), col: 3);
        BuildCallouts();
        DrawPad();
        Focus = Items.FindIndex(i => i.Col == 1);
    }

    // The console's buttons, each on the side its controller button is (sticks move as they are).
    protected override void BuildCallouts()
    {
        var L = Sc.Layout;
        foreach (var id in new[] { "l2", "l1", "select", "up", "down", "left", "right", "l3" }.Where(L.Has)) Bind(1, id);
        foreach (var id in new[] { "r2", "r1", "start", "north", "west", "east", "south", "r3" }.Where(L.Has)) Bind(2, id);
    }

    void Bind(int col, string id) =>
        Callout(col, id, Sc.Layout.Label(id), null, () => InputMap.Button(S.PadFor(Sc, id))?.Label ?? "—", "p:" + id);

    void DrawPad()
    {
        var body = new Path
        {
            Data = Geometry.Parse("M150 40 C 220 22 380 22 450 40 C 540 60 584 120 597 230 C 610 340 572 402 520 397 C 470 392 444 330 404 300 L 196 300 C 156 330 130 392 80 397 C 28 402 -10 340 3 230 C 16 120 60 60 150 40 Z"),
            Fill = P.Card, Stroke = P.Line, StrokeThickness = 2,
        };
        Place(body, 0, 0);
        Part("lt", new Rectangle { Width = 80, Height = 34, RadiusX = 12, RadiusY = 12 }, 110, -34);
        Part("rt", new Rectangle { Width = 80, Height = 34, RadiusX = 12, RadiusY = 12 }, 410, -34);
        Part("lb", new Rectangle { Width = 120, Height = 22, RadiusX = 10, RadiusY = 10 }, 90, 14);
        Part("rb", new Rectangle { Width = 120, Height = 22, RadiusX = 10, RadiusY = 10 }, 390, 14);
        Part("ls", new Ellipse { Width = 76, Height = 76 }, 165 - 38, 140 - 38);
        Part("rs", new Ellipse { Width = 76, Height = 76 }, 380 - 38, 245 - 38);
        Part("dup", new Rectangle { Width = 24, Height = 28, RadiusX = 4, RadiusY = 4 }, 213, 202);
        Part("ddown", new Rectangle { Width = 24, Height = 28, RadiusX = 4, RadiusY = 4 }, 213, 254);
        Part("dleft", new Rectangle { Width = 28, Height = 24, RadiusX = 4, RadiusY = 4 }, 185, 230);
        Part("dright", new Rectangle { Width = 28, Height = 24, RadiusX = 4, RadiusY = 4 }, 237, 230);
        Part("back", new Rectangle { Width = 30, Height = 18, RadiusX = 9, RadiusY = 9 }, 245, 131);
        Part("start", new Rectangle { Width = 30, Height = 18, RadiusX = 9, RadiusY = 9 }, 325, 131);
        Part("y", new Ellipse { Width = 38, Height = 38 }, 435 - 19, 98 - 19);
        Part("a", new Ellipse { Width = 38, Height = 38 }, 435 - 19, 182 - 19);
        Part("x", new Ellipse { Width = 38, Height = 38 }, 393 - 19, 140 - 19);
        Part("b", new Ellipse { Width = 38, Height = 38 }, 477 - 19, 140 - 19);
        foreach (var (id, txt) in new[] { ("y", "Y"), ("a", "A"), ("x", "X"), ("b", "B"), ("lb", "LB"), ("rb", "RB"), ("lt", "LT"), ("rt", "RT") })
        {
            var s = parts[id];
            var t = Fonts.Text(txt, 16, P.Text2, FontWeights.Medium); t.IsHitTestVisible = false;
            t.Width = s.Width; t.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(t, Canvas.GetLeft(s)); Canvas.SetTop(t, Canvas.GetTop(s) + s.Height / 2 - 11);
            Drawing.Children.Add(t);
        }
    }

    void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, OX + x); Canvas.SetTop(e, OY + y); Drawing.Children.Add(e); }
    void Part(string id, Shape s, double x, double y) { s.StrokeThickness = 2; Place(s, x, y); parts[id] = s; }

    protected override string Hint => Capturing != null || w.Devs.Connected("pad") ? "" : T("settings.no_controller_plugged_in");

    IEnumerable<string> Buttons => Sc.Layout.Actions.Where(a => !InputMap.Get(a).Stick);

    protected override void Highlight()
    {
        Anchors.Clear();
        string focusBtn = Assigning ?? (Focus < Items.Count && Items[Focus].Col is 1 or 2 ? S.PadFor(Sc, Items[Focus].Id) : null);
        var bound = new HashSet<string>(Buttons.Select(a => S.PadFor(Sc, a)));
        foreach (var (id, s) in parts)
        {
            bool f = id == focusBtn, b = bound.Contains(id);
            s.Fill = f ? P.RowFocus : P.ButtonFill;
            s.Stroke = f ? Palette.RingBrush() : b ? new SolidColorBrush(Color.FromArgb(150, Palette.Blue.R, Palette.Blue.G, Palette.Blue.B)) : P.Line;
            s.StrokeThickness = f ? 3.5 : 2;
            s.Opacity = b || f ? 1 : 0.55;
        }
        foreach (var a in Buttons)
            if (S.PadFor(Sc, a) is string pb && parts.TryGetValue(pb, out var s))
                Anchors["p:" + a] = new Point(Canvas.GetLeft(s) + s.Width / 2, Canvas.GetTop(s) + s.Height / 2);
    }

    protected override string PartAt(Point p) => parts.Reverse().FirstOrDefault(kv => Hit(kv.Value, p)).Key;
    protected override string ActionOf(string part) => Buttons.FirstOrDefault(x => S.PadFor(Sc, x) == part);
    protected override Point? PartPoint(string part) =>
        parts.TryGetValue(part, out var s) ? new Point(Canvas.GetLeft(s) + s.Width / 2, Canvas.GetTop(s) + s.Height / 2) : null;
    protected override string PartName(string part) => InputMap.Button(part)?.Label ?? part;

    // With no controller to press, a button on the drawing asks which game button it is for.
    protected override bool PromptOnClick => w.Devs.Connected("pad") && S.PadOn;

    protected override void Assign(Item it, string part)
    {
        string swapped = Buttons.FirstOrDefault(a => a != it.Id && S.PadFor(Sc, a) == part);
        S.SetPad(Sc, it.Id, part);
        Sounds.Play("favorite");
        Say(swapped != null ? T("settings.set_took_the_old_button", PartName(part), Sc.Layout.Label(swapped)) : $"{PartName(part)} set");
        Saved();
    }

    // While waiting for a controller button: a button clicked on the drawing is the answer.
    protected override bool TakeDrawn(string part)
    {
        var it = Items.FirstOrDefault(i => i.Id == Capturing);
        if (it == null) return false;
        Capturing = null;
        w.Input.Capture = null;
        Assign(it, part);
        return true;
    }

    protected override string CaptureText => T("settings.press_a_button");

    protected override void Picked(Item it)
    {
        if (it.Col == 0) { it.Do(); return; }
        if (!w.Devs.Connected("pad") || !S.PadOn)
        {
            // no controller to press: step to the next button
            var list = InputMap.PadButtons;
            int i = Array.FindIndex(list, b => b.Id == S.PadFor(Sc, it.Id));
            var next = list[(i + 1) % list.Length];
            S.SetPad(Sc, it.Id, next.Id);
            Say(T("settings.is_now", it.Label, next.Label));
            Saved();
            return;
        }
        Capturing = it.Id; primed = false;
        Say(T("settings.press_the_controller_button_for", it.Label));
        w.Input.Capture = OnPad;
    }

    protected override void CancelCapture() => w.Input.Capture = null;

    // From the controller poll: the first button pressed after the one that started this.
    void OnPad(ushort b, byte lt, byte rt)
    {
        bool l = lt > 100, r = rt > 100;
        if (!primed) { baseline = b; baseLt = l; baseRt = r; primed = true; return; }
        baseline &= b; baseLt &= l; baseRt &= r;   // released buttons can count again
        string hit = null;
        foreach (var pb in InputMap.PadButtons)
        {
            bool down = pb.Id == "lt" ? l && !baseLt : pb.Id == "rt" ? r && !baseRt : pb.Mask != 0 && (b & pb.Mask) != 0 && (baseline & pb.Mask) == 0;
            if (down) { hit = pb.Id; break; }
        }
        if (hit == null || Capturing == null) return;
        string id = Capturing;
        Capturing = null;
        w.Input.Capture = null;
        S.SetPad(Sc, id, hit);
        Sounds.Play("favorite");
        Say($"{InputMap.Button(hit).Label} set");
        Saved();
    }
}

// ---- mouse ----

public sealed class MouseScreen : DeviceScreen
{
    readonly Dictionary<string, Shape> parts = new();
    static ControlScope[] CamScopes => InputMap.MouseCamSystems.Select(s => new ControlScope(s)).ToArray();   // from the definitions (systems.json "mouseCamera")
    static string CamNames => string.Join(", ", CamScopes.Select(c => c.Name));
    string CamValue => CamScopes.All(c => S.MouseCamera(c)) ? T("common.on") : CamScopes.Any(c => S.MouseCamera(c)) ? T("settings.some") : T("common.off");
    int CamSpeed => CamScopes.Select(c => S.MouseSpeed(c)).GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;

    public MouseScreen(MainWindow w, Action closed) : base(w, "mouse", "mouse", closed, new ControlScope(""))
    {
        Chip("on", () => T("settings.mouse_2") + (S.MouseOn ? T("common.on") : T("common.off")), () => { S.MouseOn = !S.MouseOn; Saved(); });
        Callout(1, "left", T("settings.left_button"), T("settings.hold_it_on_a_game_its"), () => T("settings.select"), "m:left");
        Callout(1, "right", T("settings.right_button"), T("settings.on_a_game_its_quick_menu"), () => T("common.back"), "m:right");
        Callout(1, "wheel", T("settings.wheel"), T("settings.lists_and_the_game_row"), () => T("settings.scroll"), "m:wheel");
        Callout(2, "size", T("settings.cursor_size"), null, () => S.CursorSize switch { 0 => T("home.small"), 2 => T("home.large"), _ => T("settings.normal") }, null,
            () => { S.CursorSize = (S.CursorSize + 1) % 3; Saved(); });
        Callout(2, "hover", T("settings.sound_when_pointing"), T("settings.the_scroll_sound_as_the_cursor"), () => S.HoverSounds ? T("common.on") : T("common.off"), null,
            () => { S.HoverSounds = !S.HoverSounds; Saved(); });
        Callout(2, "touch", "DS / 3DS touch screen", T("settings.click_and_drag_on_the_bottom"), () => S.Touch ? T("common.on") : T("common.off"), null,
            () => { S.Touch = !S.Touch; Saved(); });
        // every mouse camera console at once; each console's own keyboard screen sets just that one
        var cam = Callout(2, "mousecam", T("settings.mouse_camera"), CamNames, () => CamValue, null, () =>
        {
            bool on = CamValue != "On";
            foreach (var c in CamScopes) S.SetMouseCamera(c, on);
            Saved();
        });
        cam.NoSwitch = true;   // it can also say Some, so it stays as words
        LevelCallout(2, "mousespeed", T("settings.mouse_look_sensitivity"), () => CamSpeed, InputMap.MouseSpeedMax,
            v => { foreach (var c in CamScopes) S.SetMouseSpeed(c, v); }, null);
        DrawMouse();
        Focus = Items.FindIndex(i => i.Col == 2);
    }

    void DrawMouse()
    {
        double ox = 960 - 150, oy = 300;
        var body = new Path
        {
            Data = Geometry.Parse("M150 0 C 240 0 300 60 300 170 L 300 330 C 300 440 230 500 150 500 C 70 500 0 440 0 330 L 0 170 C 0 60 60 0 150 0 Z"),
            Fill = P.Card, Stroke = P.Text2, StrokeThickness = 3,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };
        Canvas.SetLeft(body, ox); Canvas.SetTop(body, oy); Drawing.Children.Add(body);
        var left = new Path { Data = Geometry.Parse("M146 4 L 146 196 L 2 196 L 2 170 C 2 64 62 4 146 4 Z") };
        var right = new Path { Data = Geometry.Parse("M154 4 C 238 4 298 64 298 170 L 298 196 L 154 196 Z") };
        var wheel = new Rectangle { Width = 30, Height = 74, RadiusX = 15, RadiusY = 15 };
        foreach (var (id, s, x, y) in new (string, Shape, double, double)[] { ("left", left, 0, 0), ("right", right, 0, 0), ("wheel", wheel, 135, 48) })
        {
            Canvas.SetLeft(s, ox + x); Canvas.SetTop(s, oy + y); s.StrokeThickness = 2; Drawing.Children.Add(s); parts[id] = s;
        }
        for (int i = 0; i < 4; i++)
        {
            double y = oy + 64 + i * 14;
            var l = new Line { X1 = ox + 142, X2 = ox + 158, Y1 = y, Y2 = y, StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false, Stroke = P.Text2 };
            Drawing.Children.Add(l);
        }
        // the cursor itself, as drawn on screen
        var img = new Image { Source = Pointer.Picture(64, false), Width = 64, Height = 64 };
        Canvas.SetLeft(img, ox + 118); Canvas.SetTop(img, oy + 300); Drawing.Children.Add(img);
        var img2 = new Image { Source = Pointer.Picture(64, true), Width = 64, Height = 64 };
        Canvas.SetLeft(img2, ox + 172); Canvas.SetTop(img2, oy + 300); Drawing.Children.Add(img2);
        var cap = Fonts.Text(T("settings.your_cursor_and_over_something_to"), 17, P.Text2) ;
        cap.Width = 300; cap.TextAlignment = TextAlignment.Center; cap.TextWrapping = TextWrapping.Wrap;
        Canvas.SetLeft(cap, ox); Canvas.SetTop(cap, oy + 372); Drawing.Children.Add(cap);
        Anchors["m:left"] = new Point(ox + 75, oy + 110);
        Anchors["m:right"] = new Point(ox + 225, oy + 110);
        Anchors["m:wheel"] = new Point(ox + 150, oy + 85);
    }

    protected override string Hint => "";

    protected override void Highlight()
    {
        string f = Focus < Items.Count ? Items[Focus].Id : null;
        foreach (var (id, s) in parts)
        {
            s.Fill = id == f ? P.RowFocus : P.ButtonFill;
            s.Stroke = id == f ? Palette.RingBrush() : P.Text2;
            s.StrokeThickness = id == f ? 3.5 : 2;
        }
    }

    // Topmost first: the wheel sits on top of the left and right buttons.
    protected override string PartAt(Point p) => parts.Reverse().FirstOrDefault(kv => Hit(kv.Value, p)).Key;
    protected override string ActionOf(string part) => parts.ContainsKey(part) ? part : null;
    protected override Point? PartPoint(string part) => Anchors.TryGetValue("m:" + part, out var a) ? a : null;

    protected override void Picked(Item it)
    {
        if (it.Level != null) { it.Do(); Saved(); return; }
        if (it.Do != null) { it.Do(); return; }
        Say(it.Id switch { "left" => T("settings.left_click_selects_hold_it_on"), "right" => T("settings.right_click_goes_back_or_opens"), _ => T("settings.the_wheel_scrolls_lists_and_the") });
    }
}

// Reset keys / Reset buttons ask first. No is focused, so a stray press keeps the binds.
public sealed class ResetBindsCard : MenuCard
{
    public ResetBindsCard(MainWindow w, Action reset) : base(w, T("settings.reset_binds_to_default"), 720)
    {
        AddRow(T("common.yes"), activate: () => { Close(false); reset(); });
        AddRow(T("common.no"), activate: () => Close());
        FocusRow(T("common.no"));
    }
}
