#nullable disable
// The scan's screens: the scan's results (ScanCard), Download an emulator (EmuInstallCard) and
// Change cover (CoverPickCard).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Ionea.Loc;

namespace Ionea;

// What the scan found, everything ticked; one confirm adds it all. Shown once at the first start (after Going
// online) and from Emulators > Add > Look for emulators.
public sealed class ScanCard : MenuCard
{
    public static ScanCard Current;
    readonly bool first;
    readonly Action then;
    ScanResult r;
    bool takeEsde = true, done;
    readonly HashSet<string> skipSystems = new(StringComparer.OrdinalIgnoreCase);   // systems whose games the user set to Skip
    public bool Ready => r != null;

    public static void Run(MainWindow w, bool first, Action then, bool deep = false) => new ScanCard(w, first, then, deep, null).Open();

    // Import a folder: everything in a folder the user chose (Scan.RunFolder), on the same results.
    public static void RunFolder(MainWindow w, string folder, Action then = null) => new ScanCard(w, false, then, true, folder).Open();

    // Asks for the folder, then imports it.
    public static void PickFolder(MainWindow w, Action then = null) =>
        Picker.Folder(w, T("settings.choose_a_folder_to_import"), null, path => RunFolder(w, path, then));

    ScanCard(MainWindow w, bool first, Action then, bool deep, string folder)
        : base(w, folder != null ? T("settings.import_a_folder") : first ? T("settings.found_on_this_pc") : T("settings.look_for_emulators"), 1240)
    {
        this.first = first; this.then = then;
        Current = this;
        var rows = new StackPanel(); Body.Children.Add(rows); RowHost = rows;   // Build clears the rows, not the title
        LoadThen(() => folder != null ? Scan.RunFolder(w, folder) : Scan.Run(w, deep), res => { r = res ?? new ScanResult { Deep = deep }; Build(); });
    }

    static string Short(string p)
    {
        var parts = p.TrimEnd('\\').Split('\\');
        return parts.Length <= 2 ? p : "…\\" + string.Join("\\", parts[^2..]);
    }

    void Build()
    {
        ClearRows();
        foreach (var e in r.Emus)
        {
            var x = e;
            Aside(AddRow(x.Def.Name, () => x.Take ? T("common.add") : T("home.skip"), _ => { x.Take = !x.Take; Refresh(); }, () => { x.Take = !x.Take; Refresh(); }),
                x.Missing > 0 ? Loc.N("settings.files_missing", x.Missing, x.Where) : x.Where);
        }
        if (r.Esde != null)
            Aside(AddRow(T("settings.es_de"), () => takeEsde ? T("settings.use") : T("home.skip"), _ => { takeEsde = !takeEsde; Refresh(); }, () => { takeEsde = !takeEsde; Refresh(); }), Short(r.Esde));
        foreach (var f in r.Folders)
        {
            var x = f;
            string sys = x.System != null ? EmuCatalog.System(x.System)?.Name : null;
            Aside(AddRow(Short(x.Path), () => x.Take ? T("common.add") : T("home.skip"), _ => { x.Take = !x.Take; Refresh(); }, () => { x.Take = !x.Take; Refresh(); }),
                (sys != null ? $"{x.From}  ·  {sys}" : x.From) + (x.Capped ? T("settings.only_partly_looked_through") : ""));
        }
        // The games, one row per system, the most games first. A system no emulator plays yet says so: its games are
        // added all the same and ask for an emulator when they are started.
        foreach (var grp in r.Games.GroupBy(g => g.System).OrderByDescending(g => g.Count()).ThenBy(g => SystemName(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            string id = grp.Key;
            bool noEmu = grp.All(g => g.NoEmu);
            string count = Loc.N("settings.games", grp.Count());
            void Flip() { if (!skipSystems.Remove(id)) skipSystems.Add(id); Refresh(); }
            Aside(AddRow(SystemName(id), () => skipSystems.Contains(id) ? T("home.skip") : T("common.add"), _ => Flip(), Flip),
                noEmu ? $"{count}  ·  {T("settings.no_emulator_yet")}" : count);
        }
        if (r.GamesCapped) Note(Loc.N("settings.games_capped_note", Scan.MaxGames), 22, new Thickness(4, 4, 0, 10), RowHost);
        if (r.Games.Any(g => g.NoEmu)) Note(T("settings.games_with_no_emulator_note"), 22, new Thickness(4, 4, 0, 10), RowHost);
        if (Rows.Count > 0) Divider();
        else if (r.Empty) Note(T("settings.nothing_new_found"), 28, new Thickness(4, 0, 0, 18), RowHost);
        bool none = r.Emus.Count == 0 && !w.Emus.Data.Emulators.Any(e => e.Present);
        if (none && EmuInstall.Offers().Count > 0) Opens(AddRow(T("settings.download_an_emulator"), activate: () => { done = true; w.Settings.ScanAsked = true; w.Settings.Save(); Close(false); new EmuInstallCard(w, then).Open(); }));
        if (!r.Deep && r.Chosen == null) AddRow(T("settings.look_harder"), activate: () => { done = true; Close(false); Run(w, first, then, true); });
        AddRow(r.Empty ? T("common.done") : T("common.add"), activate: Confirm);
        Compact();
        Focus = Rows.Count - 1;
        Refresh();
    }

    public void Confirm()
    {
        if (r == null) return;
        done = true;
        r.Games.RemoveAll(g => skipSystems.Contains(g.System));
        if (r.Chosen != null && r.Games.Count == 0) r.Folders.Clear();   // every system skipped: the chosen folder isn't kept either
        if (!takeEsde) r.Esde = null;
        int emus = r.Emus.Count(e => e.Take), folders = r.Folders.Count(f => f.Take);
        int games = Scan.Apply(w, r), waiting = Scan.Waiting;
        Close();
        if (emus + folders + games > 0)
        {
            w.Home?.Rebuild(null);
            if (games > 0) w.Home?.SlideIn();
            var bits = new List<string>();
            if (emus > 0) bits.Add(Loc.N("settings.n_emulators", emus));
            if (games > 0) bits.Add(Loc.N("settings.games", games));
            if (folders > 0) bits.Add(Loc.N("settings.n_game_folders", folders));
            if (waiting > 0) bits.Add(Loc.N("settings.n_need_an_emulator", waiting));
            w.Toasts.Show(T("settings.added"), string.Join("  ·  ", bits), "plus");
        }
        then?.Invoke();
    }

    static string SystemName(string id) => EmuCatalog.System(id)?.Name ?? id;

    public override void Close(bool sound = true)
    {
        base.Close(sound);
        if (Current == this) Current = null;
        if (!done) { done = true; w.Settings.ScanAsked = true; w.Settings.Save(); then?.Invoke(); }
    }

    // For tests: the rows as "label=value".
    public string Summary => Ready ? string.Join(" | ", Rows.Select(x => ((x.El.Child as Grid)?.Children[0] as TextBlock)?.Text + (x.Value != null ? "=" + x.Value() : ""))) : "loading";
    public bool Toggle(string label)
    {
        var row = Rows.FirstOrDefault(x => ((x.El.Child as Grid)?.Children[0] as TextBlock)?.Text == label);
        if (row?.Change == null) return false;
        row.Change(1); return true;
    }
}

// A game added with no emulator (Import a folder, Game file): what to do to play it. It names the system and offers
// the emulators Ionea can download for it, and adding one already on this PC. The first one added for the system
// takes the game (EmuList.Adopt), so starting it again just works.
public sealed class NoEmulatorCard : MenuCard
{
    public static NoEmulatorCard Current;
    readonly string said;

    public NoEmulatorCard(MainWindow w, Game g) : base(w, T("errors.needs_an_emulator", g.Name), 1180)
    {
        Current = this;
        string sys = EmuCatalog.System(g.System)?.Name;
        string text = sys != null ? T("errors.needs_an_emulator_or_native_port", sys) : T("errors.needs_an_emulator_or_native_port_any");
        said = T("errors.needs_an_emulator", g.Name) + " | " + text;
        Note(text, 26, new Thickness(4, 0, 0, 18));
        if (EmuInstallCard.Offers(g.System).Count > 0)
            Opens(AddRow(T("settings.download_an_emulator"), () => T("settings.official_releases"), activate: () => { Close(false); new EmuInstallCard(w, null, g.System).Open(); }));
        Opens(AddRow(T("settings.add_emulator_2"), () => T("settings.already_on_this_pc"), activate: () => { Close(false); new AddEmulatorCard(w).Open(); }));
        AddRow(T("common.ok"), activate: () => Close());
        Focus = 0;
        Refresh();
    }

    // For tests: the text and the rows.
    public string Summary => said + " | " + Labels;

    public override void Close(bool sound = true) { base.Close(sound); if (Current == this) Current = null; }
}

// Official releases of emulators whose definitions name one (definitions' release). The release's file list
// is fetched under Emulator installs without a question; the emulator itself is asked for with its address.
public sealed class EmuInstallCard : MenuCard
{
    public static EmuInstallCard Current;
    public static string LastResult;
    readonly Action then;

    // system: only the ones for that system (a game with no emulator asks for one).
    public EmuInstallCard(MainWindow w, Action then = null, string system = null) : base(w, T("settings.download_an_emulator"), 1080)
    {
        this.then = then;
        Current = this;
        var defs = Offers(system);
        foreach (var d in defs)
        {
            var def = d;
            string sys = string.Join(", ", def.Systems.Select(s => EmuCatalog.System(s)?.Name ?? s).Take(3));
            Aside(AddRow(def.Name, activate: () => Get(def)), sys);
        }
        if (Rows.Count == 0) AddRow(T("common.back"), activate: () => Close());
        for (int i = 0; i < defs.Count && i < Rows.Count; i++) { string kind = defs[i].IsCore ? "cores" : "emulators"; Rows[i].Dim = () => !Online.Allowed(kind); }
        Refresh();
    }

    public static List<EmuDef> Offers(string system) => EmuInstall.Offers().Where(d => system == null || d.Systems.Contains(system)).ToList();

    public async void Get(EmuDef def)
    {
        string kind = def.IsCore ? "cores" : "emulators";
        if (!Online.Allowed(kind)) { new SwitchOnCard(w, kind, () => Get(def)).Open(); return; }
        Close(false);
        var (emu, err) = await EmuInstall.Run(w, def);
        LastResult = emu != null ? "installed " + emu.Name : err ?? "cancelled";
        if (emu != null)
        {
            w.Toasts.Show(T("settings.emulator_added"), emu.Name, "plus");
            var e = emu;
            void Rest() { if (ReqFiles.Missing(e) > 0) new RequiredFilesCard(w, e, then).Open(); else then?.Invoke(); }
            if (CoreOffer.For(w, e) is EmuDef core) { new CoreOfferCard(w, e, core, Rest).Open(); return; }
            if (ReqFiles.Missing(emu) > 0) { new RequiredFilesCard(w, emu, then).Open(); return; }
        }
        else if (err != null) new MessageCard(w, T("settings.not_installed"), err).Open();
        then?.Invoke();
    }

    public override void Close(bool sound = true) { base.Close(sound); if (Current == this) Current = null; }
}

// A download that its switch (or the network connection) does not allow yet: switch that one kind on here and go on,
// instead of a trip to Settings > Network. The download itself is still asked for with its address.
public sealed class SwitchOnCard : MenuCard
{
    public SwitchOnCard(MainWindow w, string kind, Action then) : base(w, Online.Offline ? T("settings.network_connection_is_off") : kind == "cores" ? T("settings.cores_are_off") : T("settings.emulator_installs_are_off"), 900)
    {
        Note(Online.Offline ? T("settings.needs_the_network_connection_on", Online.Label(kind)) : T("settings.needs_it_on", Online.Label(kind)), 30, new Thickness(4, 0, 0, 26));
        WithIcon(AddRow(T("settings.switch_on"), activate: () =>
        {
            Close(false);
            if (Online.Offline) Online.SetOffline(false);
            Online.Set(kind, true);
            then?.Invoke();
        }), "globe");
        AddRow(T("common.cancel"), activate: () => Close());
    }
}

// Change cover: every picture the game can have, side by side, and Choose a file. Missing ones are fetched
// (when that kind of online use is on) and appear when they arrive.
public sealed class CoverPickCard : Card
{
    public static CoverPickCard Current;
    readonly Game g;
    readonly Action<string> chooseFile;
    readonly WrapPanel tiles = new() { HorizontalAlignment = HorizontalAlignment.Center };
    readonly List<(string Source, string Label, BitmapSource Cover, Border El)> items = new();
    int focus;
    bool gone;
    const double T = 300;

    public CoverPickCard(MainWindow w, Game g, Action<string> chooseFile) : base(w, T("settings.change_cover"), 1500)
    {
        this.g = g; this.chooseFile = chooseFile;
        Current = this;
        Body.Children.Add(tiles);
        Build();
        Fetch();
    }

    public string Sources => string.Join(" | ", items.Select(i => i.Source));
    public int Count => items.Count;

    void Build()
    {
        items.Clear(); tiles.Children.Clear();
        void Add(string source, string label, BitmapSource b) { if (b != null) items.Add((source, label, b, null)); }
        if (g.Rom != null) { BitmapSource ic = null; try { ic = RomArt.Cover(g.Rom, RomArt.IconsDir(w.Lib)); } catch (Exception caught) { Log.Debug("cover choice: game file", caught); } Add("icon", T("settings.game_file"), ic); }
        if (g.AppId != null) { BitmapSource st = null; try { st = SteamArt.Cover(g.AppId, AddGameLogic.CoverSize); } catch (Exception caught) { Log.Debug("cover choice: Steam", caught); } Add("steam", "Steam", st); }
        foreach (var (src, label) in new[] { ("libretro", "LibRetro"), ("steamgriddb", "SteamGridDB") })
        {
            string f = Covers.Candidate(w, g, src);
            if (File.Exists(f)) { BitmapSource b = null; try { b = RomArt.CoverFromImage(f); } catch (Exception caught) { Log.Debug("cover choice: image", caught); } Add(src, label, b); }
        }
        items.Add(("name", T("home.name"), null, null));
        items.Add(("file", T("settings.choose_a_file"), null, null));
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var stack = new StackPanel { Margin = new Thickness(14) };
            var pic = new Border { Width = T, Height = T, CornerRadius = new CornerRadius(18), Background = P.TileFallback, BorderBrush = P.TileEdge, BorderThickness = new Thickness(1.5), ClipToBounds = true };
            if (it.Cover != null) pic.Background = Art.Fill(it.Cover);
            else if (it.Source == "name")   // as Home shows a game with no picture, in its cover colour
            {
                pic.Background = CoverColour.Fill(g);
                var n = Fonts.Text(g.Name, 34, CoverColour.Ink(g), FontWeights.Normal, Fonts.Display);
                n.TextWrapping = TextWrapping.Wrap; n.TextAlignment = TextAlignment.Center; n.VerticalAlignment = VerticalAlignment.Center; n.Margin = new Thickness(24);
                pic.Child = n;
            }
            else { var ico = Art.Icon("folder", 90, P.Text2); ico.HorizontalAlignment = HorizontalAlignment.Center; ico.VerticalAlignment = VerticalAlignment.Center; pic.Child = ico; }
            var frame = new Border { BorderThickness = new Thickness(5), CornerRadius = new CornerRadius(22), Child = pic, BorderBrush = Brushes.Transparent };
            stack.Children.Add(frame);
            var t = Fonts.Text(it.Label, 26, P.Text2); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(t);
            tiles.Children.Add(stack);
            items[i] = (it.Source, it.Label, it.Cover, frame);
        }
        focus = Math.Clamp(focus, 0, items.Count - 1);
        Lit();
    }

    // The chosen candidate wears the menus' focus ring and running light.
    void Lit()
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i].El.BorderBrush = i == focus ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(items[i].El, i == focus);
        }
    }

    async void Fetch()
    {
        bool changed = false;
        if (g.Rom != null && Online.Allowed("covers") && !File.Exists(Covers.Candidate(w, g, "libretro"))) changed |= await Covers.Libretro(w, g);
        if (Covers.SgdbOn(w) && !File.Exists(Covers.Candidate(w, g, "steamgriddb"))) changed |= await Covers.SteamGrid(w, g);
        if (changed && !gone) Build();
        Fetching = false;
    }
    public bool Fetching = true;

    public void Pick(int i)
    {
        if (i < 0 || i >= items.Count) return;
        var it = items[i];
        if (it.Source == "file") { Close(false); chooseFile?.Invoke(null); return; }
        // Whatever the user picks stays: "own" is never replaced; "name" (no picture) is never filled by the fetch.
        if (it.Source == "name") GameContent.NoCover(w, g);
        else
        {
            // Box art is kept whole, so Adjust cover can frame any part of it; it starts as shown here.
            string raw = it.Source is "libretro" or "steamgriddb" ? Covers.Candidate(w, g, it.Source) : null;
            var whole = raw != null && File.Exists(raw) ? AddGameLogic.PhotoCover(raw) : null;
            GameContent.UseCover(w, g, whole ?? it.Cover, "own", it.Label, Covers.SourceUrl(w, g, it.Source), whole: whole != null);
        }
        Close();
        w.Toasts.Show(T("settings.cover_changed"), g.Name, "info");
        GameInfoCard.Current?.Redraw();
        if (it.Source != "name") new CoverAdjustCard(w, g).Open();   // a new picture: frame it at once
    }
    public bool PickSource(string source) { int i = items.FindIndex(x => x.Source == source); if (i < 0) return false; Pick(i); return true; }

    public override void OnAct(Act a)
    {
        switch (a)
        {
            case Act.Left: if (focus > 0) { focus--; Sounds.Play("scroll"); Lit(); } break;
            case Act.Right: if (focus < items.Count - 1) { focus++; Sounds.Play("scroll"); Lit(); } break;
            case Act.Accept: Pick(focus); break;
            case Act.Back: Close(); break;
        }
    }

    public override bool OnMouse(MouseAct m, Point p)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i].El != null && Pointer.Over(items[i].El, w.Stage, p))
            {
                if (m == MouseAct.Move && focus != i) { focus = i; Lit(); return true; }
                if (m == MouseAct.Down) { focus = i; Lit(); Pick(i); return true; }
            }
        if (m == MouseAct.WheelUp) { OnAct(Act.Left); return true; }
        if (m == MouseAct.WheelDown) { OnAct(Act.Right); return true; }
        return base.OnMouse(m, p);
    }

    public override void Close(bool sound = true) { gone = true; base.Close(sound); if (Current == this) Current = null; }
}
