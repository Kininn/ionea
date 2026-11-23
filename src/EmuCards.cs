#nullable disable
// Emulator screens: the Emulators list (Settings > Emulators), one emulator's page, adding an emulator
// (install a checked download from the Inbox, choose a program, or look in the Emulators folders),
// and "+ Add game > Game file" (pick a ROM, the right emulator is chosen by its file type).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using static Ionea.Loc;

namespace Ionea;

public sealed class EmulatorsCard : MenuCard
{
    // Emulators and native ports (a PC version of a console game, "kind": "port" in its definition) are both
    // programs that play the library's game files, so they share one list, each under its own heading.
    // A future port lands under Native ports by its definition alone.
    public static string Title => T("settings.emulators_and_ports");
    public static bool IsPort(Emulator e) => EmuDefs.Of(e)?.IsPort == true;
    public static string Count(MainWindow w)
    {
        int ports = w.Emus.Data.Emulators.Count(IsPort), emus = w.Emus.Data.Emulators.Count - ports;
        string s = emus == 1 ? "1 emulator" : $"{emus} emulators";
        return ports == 0 ? s : s + (ports == 1 ? ", 1 port" : $", {ports} ports");
    }

    public EmulatorsCard(MainWindow w, string focusId = null) : base(w, Title, 1080)
    {
        var all = w.Emus.Sorted();
        var ports = all.Where(IsPort).ToList();
        if (all.Count == 0) Note(T("settings.no_emulators_yet"), 28, new Thickness(4, 0, 0, 18));
        foreach (var (head, list) in new[] { (T("settings.emulators"), all.Except(ports).ToList()), (T("settings.native_ports"), ports) })
        {
            if (list.Count == 0) continue;
            if (ports.Count > 0) Section(head);   // headings only when there is something to tell apart
            foreach (var e in list)
            {
                var em = e;
                var r = AddRow(em.Name, () => em.Present ? em.SystemText : T("settings.program_missing"), activate: () => { Close(false); new EmulatorCard(w, em).Open(); });
                if (em.Id == focusId) Focus = Rows.Count - 1;
                if (!em.Present) r.ValueText.Foreground = P.Error;
                WithIcon(r, "chip");
            }
        }
        if (all.Count > 0) Divider();
        AddRow(T("settings.add_emulator"), activate: () => { Close(false); new AddEmulatorCard(w).Open(); });
        AddRow(T("common.done"), activate: () => Close());
    }
}

public sealed class EmulatorCard : MenuCard
{
    public EmulatorCard(MainWindow w, Emulator e, string focusRow = null) : base(w, e.Name, 1180)
    {
        int games = w.Lib.Data.Games.Count(g => g.Emulator == e.Id && !g.Removed);
        // In boxes: what it plays, the program itself, how Ionea runs it, then remove / done.
        BeginBox(T("settings.games_heading"));
        AddRow(T("settings.systems"), () => e.SystemText, activate: () => { Close(false); new SystemPickCard(w, e, false).Open(); });
        AddRow(T("settings.file_types"), () => string.Join(" ", e.Exts), activate: () =>
            new TextCard(w, T("settings.file_types_separate_with_spaces"), string.Join(" ", e.Exts), 200, v =>
            {
                e.Exts = v.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(x => (x.StartsWith(".") ? x : "." + x).ToLowerInvariant()).Distinct().ToList();
                w.Emus.Save(); Refresh();
            }).Open());
        AddRow(T("settings.start_options"), () => e.Args, activate: () =>
            new TextCard(w, T("settings.start_options_rom_is_the_game"), e.Args, 200, v =>
            {
                e.Args = v.Contains("{rom}") ? v : v + " \"{rom}\"";
                w.Emus.Save(); Refresh();
                if (!v.Contains("{rom}")) w.Toasts.Show(T("settings.added_rom_at_the_end"), e.Name, "info");
            }).Open());
        BeginBox(T("settings.program"));
        AddRow(T("settings.program"), () => e.Present ? Short(e.Exe) : T("settings.missing_choose_it_again"), activate: () =>
        {
            EmuPick.Exe(w, Path.GetDirectoryName(e.Exe), exe =>
            {
                if (w.Emus.ByExe(exe) is Emulator other && other != e) { w.Toasts.Show(T("settings.already_on_the_list"), other.Name, "info"); return; }
                e.Exe = Path.GetFullPath(exe); w.Emus.Save(); Refresh();
            });
        });
        if (ReqFiles.Has(e))
        {
            var rf = AddRow(T("settings.required_files"), () => { int n = ReqFiles.Missing(e); return n == 0 ? T("settings.ready") : $"{n} missing"; },
                activate: () => { Close(false); new RequiredFilesCard(w, e, () => new EmulatorCard(w, e).Open()).Open(); });
            if (ReqFiles.Missing(e) > 0) rf.ValueText.Foreground = P.Error;
        }
        // The level and what Ionea doesn't do for it, as "Saves are shared" at a start.
        if (!e.IsCore) AddRow(T("settings.support"), () => EmuDefs.Support(e));
        // Its own window inside Ionea's; this page is built anew when it closes.
        if (!e.IsCore)
            AddRow(T("settings.open_the_emulator"), () => T("settings.controls_graphics_bios"), activate: () =>
            {
                Close(false);
                EmuOpen.Start(w, e, () => new EmulatorCard(w, e, T("settings.open_the_emulator")).Open());
            });
        bool runs = EmuControls.Defined(e) != null || !e.IsCore || (e.Present && Tweaks.Has(e));
        if (runs) BeginBox(T("settings.in_ionea"));
        if (EmuControls.Defined(e) != null)
            AddRow(T("settings.ionea_manages_controls"), () => e.Controls != false ? T("common.on") : T("common.off"), activate: () =>
            {
                e.Controls = e.Controls != false ? false : true;
                w.Emus.Save(); Refresh();
                Log.Write($"emulator {e.Name}: Ionea manages controls {(e.Controls != false ? "on" : "off")}");
            });
        if (!e.IsCore)
            AddRow(T("settings.play_inside_ionea"), () => e.Inside != false ? T("common.on") : T("common.off"), activate: () =>
            {
                e.Inside = e.Inside != false ? false : null;
                w.Emus.Save(); Refresh();
                Log.Write($"emulator {e.Name}: inside the launcher {(e.Inside != false ? "on" : "off")}");
            });
        if (e.Present && Tweaks.Has(e))
            Opens(AddRow(T("settings.settings_for_every_game"), () => Tweaks.Summary(e), activate: () =>
            {
                var ts = Tweaks.ForEmu(e);
                if (ts == null) { new MessageCard(w, T("settings.no_settings_yet"), T("settings.s_settings_show_after_one_of", e.Name)).Open(); return; }
                new TweaksCard(w, null, ts).Open();
            }));
        EndBox();
        AddRow(T("settings.remove_from_ionea"), () => games == 0 ? "" : games == 1 ? T("settings.1_game_uses_it") : T("settings.games_use_it", games), activate: () =>
        {
            w.Emus.Remove(e);
            Close(false);
            w.Toasts.Show(T("settings.emulator_removed"), e.Name, "info");
            new EmulatorsCard(w).Open();
        });
        AddRow(T("common.done"), activate: () => { Close(false); new EmulatorsCard(w, e.Id).Open(); });
        if (focusRow != null) FocusRow(focusRow);
    }

    // "Dolphin\Dolphin.exe": the emulator's folder and program, enough to tell them apart.
    static string Short(string p) => Path.Combine(Path.GetFileName(Path.GetDirectoryName(p)) ?? "", Path.GetFileName(p));

    public override void OnAct(Act a)
    {
        if (a == Act.Back) { Close(); return; }
        base.OnAct(a);
    }
}

// Which systems an emulator plays. A toggles a system; "Done" saves. Used for programs added by hand.
public sealed class SystemPickCard : MenuCard
{
    readonly Emulator e;
    readonly HashSet<string> on;
    readonly bool isNew;

    public SystemPickCard(MainWindow w, Emulator emu, bool newOne) : base(w, newOne ? T("settings.what_does_play", emu.Name) : $"{emu.Name} plays", 1080)
    {
        e = emu; isNew = newOne;
        on = new HashSet<string>(e.Systems);
        AddRow(T("common.done"), activate: Done);
        foreach (var s in EmuCatalog.Systems.Where(x => !x.More || on.Contains(x.Id)))   // the extra ES-DE systems: chosen ones here, the rest under "Other systems"
            AddSystem(s);
        int more = EmuCatalog.Systems.Count(x => x.More && !on.Contains(x.Id));
        if (more > 0) otherRow = AddRow(T("settings.other_systems"), () => $"{more} more", activate: ShowOther);
        // The long list scrolls in the card's own scroller like every other long card. The old
        // canvas here moved Body out of that scroller and threw "already the logical child".
    }

    Row otherRow;

    Row AddSystem(EmuSystem sys) =>
        AddRow(sys.Name, () => on.Contains(sys.Id) ? "✓" : "", activate: () => { if (!on.Remove(sys.Id)) on.Add(sys.Id); Refresh(); });

    // Every other system ES-DE knows (niche consoles and computers), under their own heading, in place of the row.
    void ShowOther()
    {
        int at = Rows.IndexOf(otherRow);
        Body.Children.Remove(otherRow.El);
        Rows.Remove(otherRow);
        otherRow = null;
        double h = Rows[0].El.Height; var m = Rows[0].El.Margin;
        Section(T("settings.other_systems"));
        foreach (var s in EmuCatalog.Systems.Where(x => x.More && !on.Contains(x.Id)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var r = AddSystem(s);
            r.El.Height = h; r.El.Margin = m;
        }
        Focus = Math.Min(at, Rows.Count - 1);
        Refresh();
        Follow();
    }

    void Done()
    {
        if (on.Count == 0) { w.Toasts.Show(T("settings.choose_at_least_one_system"), e.Name, "info"); return; }
        // Keep extra file types the user typed; replace the ones that came from systems.
        var oldSys = EmuCatalog.ExtsFor(e.Systems);
        var extra = e.Exts.Where(x => !oldSys.Contains(x));
        e.Systems = EmuCatalog.Systems.Where(s => on.Contains(s.Id)).Select(s => s.Id).ToList();
        e.Exts = EmuCatalog.ExtsFor(e.Systems, extra);
        Close(false);
        if (isNew)
        {
            w.Emus.Add(e);
            Sounds.Play("favorite");
            w.Toasts.Show(T("settings.emulator_added"), $"{e.Name}: {e.SystemText}", "plus");
        }
        else w.Emus.Save();
        new EmulatorCard(w, e).Open();
    }

    // Long list: scroll by moving the list so the focused row stays inside the card, measured from the rows
    // themselves (the old fixed 82 px step overshot the compact rows and left the card half empty). Never past the end.
    double scrolled;
    public override void OnAct(Act a)
    {
        base.OnAct(a);
        if (a is Act.Up or Act.Down) Follow();
    }

    void Follow()
    {
        if (Rows.Count == 0) return;
        Body.UpdateLayout();
        double view = Box.Height - Box.Padding.Top - Box.Padding.Bottom;
        double max = Math.Max(0, Body.ActualHeight - view);
        var el = Rows[Math.Clamp(Focus, 0, Rows.Count - 1)].El;
        double top = el.TranslatePoint(new Point(0, 0), Body).Y, bottom = top + el.ActualHeight;
        if (Focus == 0) scrolled = 0;
        else if (top - 40 < scrolled) scrolled = top - 40;           // room for a heading above
        else if (bottom + 12 > scrolled + view) scrolled = bottom + 12 - view;
        scrolled = Math.Clamp(scrolled, 0, max);
        Body.RenderTransform = new System.Windows.Media.TranslateTransform(0, -scrolled);
    }
}

// A standalone emulator was just added for systems a core Ionea can download also plays.
// A standalone one runs as its own window over the launcher, so the Home menu has to picture, hide and pause it;
// a core runs inside Ionea, which draws the game, so the Home menu opens at once (as when GBA plays
// with the mGBA core). Each core is offered once, only with Cores on (Going online) and no core present
// for those systems.
public static class CoreOffer
{
    public static EmuDef For(MainWindow w, Emulator e)
    {
        if (e == null || e.IsCore || e.Systems == null || !Online.Allowed("cores")) return null;   // Cores switched off: not offered
        var asked = w.Settings.CoresOffered ?? new List<string>();
        return EmuInstall.Offers().FirstOrDefault(d => d.IsCore && d.Systems.Any(s => e.Systems.Contains(s)) && !asked.Contains(d.Id)
            && !w.Emus.Data.Emulators.Any(x => x.IsCore && x.Present && x.Systems.Any(s => d.Systems.Contains(s))));
    }
}

public sealed class CoreOfferCard : MenuCard
{
    public static CoreOfferCard Current;
    public static string LastResult;   // test scripts
    public string FirstLabel { get; private set; }   // the tutorial points at it
    readonly Action then;
    bool done;

    public CoreOfferCard(MainWindow w, Emulator emu, EmuDef core, Action then)
        : base(w, T("settings.play_games_inside_ionea", EmuCatalog.System(core.Systems.First(s => emu.Systems.Contains(s)))?.Name ?? T("settings.these")), 1080)
    {
        this.then = then;
        Current = this;
        LastResult = null;
        (w.Settings.CoresOffered ??= new()).Add(core.Id); w.Settings.Save();
        Log.Write($"core offer: {core.Name} for {emu.Name}'s games");
        Note(T("settings.the_home_menu_opens_at_once"), 27, new Thickness(4, 0, 0, 24));
        FirstLabel = T("settings.get_the", core.Name);
        AddRow(FirstLabel, () => "recommended", activate: () => Get(core));
        AddRow(T("settings.keep_only", emu.Name), activate: () => { LastResult = "kept"; Close(); });
    }

    async void Get(EmuDef core)
    {
        done = true;
        Close(false);
        var (c, err) = await EmuInstall.Run(w, core);
        LastResult = c != null ? "installed " + c.Name : err ?? "cancelled";
        if (c != null) w.Toasts.Show(T("settings.core_added"), T("settings.games_play_inside_ionea", c.Name), "plus");
        then?.Invoke();
        if (c == null && err != null) new MessageCard(w, T("settings.not_installed"), err).Open();
    }

    public override void Close(bool sound = true)
    {
        base.Close(sound);
        if (Current == this) Current = null;
        if (!done) { done = true; LastResult ??= "kept"; then?.Invoke(); }
    }
}

public sealed class AddEmulatorCard : MenuCard
{
    public AddEmulatorCard(MainWindow w) : base(w, T("settings.add_emulator_2"), 1080)
    {
        int n = w.Emus.InboxArchives().Count;
        if (AddGameLogic.Inbox != null)
            AddRow(T("settings.install_a_checked_download"), () => n == 0 ? T("settings.inbox_is_empty") : n == 1 ? T("settings.1_file_in_the_inbox") : T("settings.files_in_the_inbox", n),
                activate: () => { Close(false); new InboxPickCard(w).Open(); });
        if (EmuInstall.Offers().Count > 0)
            AddRow(T("settings.download_an_emulator"), () => T("settings.official_releases"), activate: () => { Close(false); new EmuInstallCard(w).Open(); });
        AddRow(T("settings.choose_a_program"), () => T("settings.exe_already_on_this_pc"), activate: () =>
        {
            EmuPick.Exe(w, w.Emus.InstallDir, exe =>
            {
                Close(false);
                EmuPick.AddExe(w, exe);
            });
        });
        AddRow(T("settings.look_for_emulators"), () => T("settings.on_this_pc"), activate: () => { Close(false); ScanCard.Run(w, false, () => new EmulatorsCard(w).Open()); });
        AddRow(T("common.cancel"), activate: () => { Close(); new EmulatorsCard(w).Open(); });
    }
}

public sealed class InboxPickCard : MenuCard
{
    public InboxPickCard(MainWindow w) : base(w, T("settings.install_from_the_inbox"), 1180)
    {
        var files = w.Emus.InboxArchives();
        if (files.Count == 0) Note(T("settings.no_downloads_in_the_inbox"), 28, new Thickness(4, 0, 0, 18));
        foreach (string f in files.Take(10))
        {
            string file = f;
            var p = EmuCatalog.ForArchive(file);
            AddRow(Path.GetFileName(file), () => p == null ? "" : Exists(w, p) ? "update " + p.Name : p.Name, activate: () => { Close(false); EmuPick.Install(w, file); });
        }
        AddRow(T("common.cancel"), activate: () => { Close(); new AddEmulatorCard(w).Open(); });
    }

    static bool Exists(MainWindow w, EmuDef p) => Directory.Exists(Path.Combine(w.Emus.InstallDir, p.Name));
}

public static class EmuPick
{
    public static void Exe(MainWindow w, string startDir, Action<string> done) =>
        Picker.File(w, T("settings.choose_the_emulator_s_program_exe"), T("settings.program_exe_exe"), new[] { ".exe" },
            startDir != null && Directory.Exists(startDir) ? startDir : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), done);

    // A known emulator is added at once; anything else asks what it plays.
    public static void AddExe(MainWindow w, string exe)
    {
        exe = Path.GetFullPath(exe);
        if (AddGameLogic.InInbox(exe)) { new MessageCard(w, T("home.can_t_add_this"), T("settings.programs_in_are_not_installed_yet", AddGameLogic.Inbox)).Open(); return; }
        if (w.Emus.ByExe(exe) is Emulator have) { new MessageCard(w, T("settings.already_added"), T("settings.this_is_already_on_the_list", have.Name)).Open(); return; }
        if (EmuCatalog.ForExe(exe) is EmuDef p)
        {
            var e = w.Emus.FromPreset(p, exe);
            e.Controls = true;   // Ionea manages its controls, as for every emulator; its page's switch turns it off
            w.Emus.Add(e);
            Sounds.Play("favorite");
            w.Toasts.Show(T("settings.emulator_added"), $"{e.Name}: {e.SystemText}", "plus");
            Next(w, e);
            return;
        }
        string name = Path.GetFileNameWithoutExtension(exe);
        try { var pn = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim(); if (!string.IsNullOrEmpty(pn) && pn.Length <= 30) name = AddGameLogic.Clean(pn); } catch { /* no version info: keep the file name */ }
        var custom = new Emulator { Id = Library.NewId(), Name = name, Exe = exe, Args = "\"{rom}\"", Added = DateTime.Now };
        new SystemPickCard(w, custom, true).Open();
    }

    public static void Install(MainWindow w, string archive)
    {
        try
        {
            var e = w.Emus.Install(archive, out bool updated);
            Sounds.Play("favorite");
            w.Toasts.Show(updated ? T("settings.emulator_updated") : T("settings.emulator_installed"), $"{e.Name}: {e.SystemText}", "plus");
            Next(w, e);
        }
        catch (Exception ex)
        {
            Log.Write($"emulator install {archive}: {ex.Message}");
            new MessageCard(w, T("settings.couldn_t_install_it"), ex.Message).Open();
        }
    }

    // After adding one: its Required files when it lacks one, else the list.
    public static void Next(MainWindow w, Emulator e)
    {
        void Rest()
        {
            if (ReqFiles.Missing(e) > 0) new RequiredFilesCard(w, e, () => new EmulatorsCard(w, e.Id).Open()).Open();
            else new EmulatorsCard(w, e.Id).Open();
        }
        if (CoreOffer.For(w, e) is EmuDef core) { new CoreOfferCard(w, e, core, Rest).Open(); return; }
        Rest();
    }

    public static void Announce(MainWindow w, List<Emulator> found)
    {
        if (found.Count == 0) return;
        if (found.Count == 1) w.Toasts.Show(T("settings.emulator_found"), $"{found[0].Name}: {found[0].SystemText}", "plus");
        else w.Toasts.Show(T("settings.emulators_found", found.Count), string.Join(", ", found.Select(f => f.Name)), "plus");
    }
}

// "+ Add game > New game files": game files already in ES-DE\ROMs\<system>\ (where a game moved in from outside puts
// them) that are not in the library yet and that an emulator opens. PS4 games count as <game folder>\eboot.bin.
public static class NewRoms
{
    public static List<string> Find(MainWindow w)
    {
        var found = new List<string>();
        if (w.Emus.Data.Emulators.Count == 0) return found;
        // ES-DE's ROMs\<system>\ and every game folder (found by the scan or added): its files and one level of folders.
        void Take(string f) { if (!found.Contains(f, StringComparer.OrdinalIgnoreCase) && w.Emus.ForFile(f).Count > 0 && RomLogic.Check(f, w.Lib) == null) found.Add(f); }
        void Folder(string sys, int depth)
        {
            foreach (string f in Safe(() => Directory.GetFiles(sys))) Take(f);
            foreach (string d in Safe(() => Directory.GetDirectories(sys)))
            {
                if (RomLogic.IsPs4Update(d)) continue;
                string eboot = Path.Combine(d, "eboot.bin");
                if (File.Exists(eboot)) Take(eboot);
                else if (depth > 0) Folder(d, depth - 1);
            }
        }
        foreach (string root in GameRoots(w)) Folder(root, root == EsdeRoms(w) ? 1 : 0);
        // Newest first: a file that just arrived (unpacked now) is at the top.
        return found.OrderByDescending(f => { var i = new FileInfo(f); return i.CreationTime > i.LastWriteTime ? i.CreationTime : i.LastWriteTime; }).ToList();
    }

    static string EsdeRoms(MainWindow w) => w.Opt.EsdeDir is string e ? Path.Combine(e, "ROMs") : null;
    public static List<string> GameRoots(MainWindow w)
    {
        var l = new List<string>();
        if (EsdeRoms(w) is string r && Directory.Exists(r)) l.Add(r);
        foreach (var g in w.Settings.Paths.Games) if (Directory.Exists(g.Path) && !l.Contains(g.Path, StringComparer.OrdinalIgnoreCase)) l.Add(g.Path);
        return l;
    }

    public static string Name(string file) => RomLogic.NameFromFile(file);

    public static string SystemName(MainWindow w, string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        var fits = w.Emus.ForFile(file);
        return EmuCatalog.Systems.FirstOrDefault(s => s.Exts.Contains(ext) && fits.Any(e => e.Systems.Contains(s.Id)))?.Name ?? fits.FirstOrDefault()?.SystemText ?? "";
    }

    // Straight to the name card: no picker and no "which emulator?". The rule is cores first, so a core that
    // fits wins; the game menu's Emulator row can switch the game to the standalone emulator later.
    public static void Add(MainWindow w, string file)
    {
        string err = RomLogic.Check(file, w.Lib);
        if (err != null) { new MessageCard(w, T("home.can_t_add_this"), err).Open(); return; }
        var fits = w.Emus.ForFile(file);
        if (fits.Count == 0) { new MessageCard(w, T("settings.no_emulator_for_this_file"), T("settings.none_of_your_emulators_opens_files", Path.GetExtension(file))).Open(); return; }
        // A core from an imported definition (never run in Ionea's host) is only the pick when nothing else opens the file.
        new AddRomCard(w, file, fits.FirstOrDefault(e => e.IsCore && !EmuDefs.Imported(EmuDefs.Of(e))) ?? fits.FirstOrDefault(e => !e.IsCore) ?? fits[0]).Open();
    }

    static string[] Safe(Func<string[]> f) { try { return f(); } catch { return Array.Empty<string>(); } }
}

public sealed class NewRomsCard : MenuCard
{
    const int Max = 12;
    public NewRomsCard(MainWindow w) : base(w, T("settings.new_game_files"), 1180)
    {
        var files = NewRoms.Find(w);
        Log.Write($"new game files: {files.Count}");
        if (files.Count == 0)
            Note(T("settings.no_new_games"), 28, new Thickness(4, 0, 0, 18));
        foreach (string f in files.Take(Max))
        {
            string file = f;
            AddRow(NewRoms.Name(file), () => NewRoms.SystemName(w, file), activate: () => { Close(false); NewRoms.Add(w, file); });
        }
        if (files.Count > Max) Note(T("settings.and_more", files.Count - Max), 22, new Thickness(4, 6, 0, 0));
        AddRow(T("common.cancel"), activate: () => Close());
    }
}

// "+ Add game > Game file": pick the file, the emulator is chosen by file type (asks when several fit).
public sealed class AddRomFlow
{
    readonly MainWindow w;
    public AddRomFlow(MainWindow win) { w = win; }

    public void Start(string pickedForTest = null)
    {
        if (w.Emus.Data.Emulators.Count == 0)
        {
            new MessageCard(w, T("settings.add_an_emulator_first"), T("settings.game_files_start_through_an_emulator")).Open();
            return;
        }
        if (pickedForTest == null)
        {
            var exts = w.Emus.AllExts();
            string roms = NewRoms.GameRoots(w).FirstOrDefault();
            Picker.File(w, T("settings.choose_a_game_file"), T("settings.game_files") + string.Join(";", exts.Select(x => "*" + x)) + T("settings.all_files"), exts.ToArray(),
                roms ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Use);
            return;
        }
        Use(pickedForTest);
    }

    void Use(string rom)
    {
        string err = RomLogic.Check(rom, w.Lib);
        if (err != null) { Log.Write($"add rom refused: {rom}: {err}"); new MessageCard(w, T("home.can_t_add_this"), err).Open(); return; }
        var fits = w.Emus.ForFile(rom);
        if (fits.Count == 0)
        {
            new MessageCard(w, T("settings.no_emulator_for_this_file"), T("settings.none_of_your_emulators_opens_files", Path.GetExtension(rom))).Open();
            return;
        }
        if (fits.Count == 1) new AddRomCard(w, rom, fits[0]).Open();
        else new EmuChoiceCard(w, rom, fits).Open();
    }
}

public sealed class EmuChoiceCard : MenuCard
{
    public EmuChoiceCard(MainWindow w, string rom, List<Emulator> fits) : base(w, T("settings.which_emulator"), 1000)
    {
        Note(Path.GetFileName(rom), 26, new Thickness(4, 0, 0, 16));
        // Cores first (they play inside Ionea: the Home menu opens at once), and said so.
        foreach (var e in fits.OrderBy(f => EmuDefs.TakesGames(EmuDefs.Of(f)) ? 0 : 1))
        {
            var em = e;
            bool inside = EmuDefs.TakesGames(EmuDefs.Of(em));
            AddRow(em.Name, () => inside ? T("settings.inside_ionea_recommended") : em.SystemText, activate: () => { Close(false); new AddRomCard(w, rom, em).Open(); });
        }
        AddRow(T("common.cancel"), activate: () => Close());
    }
}

// Name the game file; optional own image (otherwise the tile shows the name).
public sealed class AddRomCard : MenuCard, IScreen
{
    readonly string rom;
    readonly Emulator emu;
    readonly System.Windows.Controls.TextBox box;
    readonly System.Windows.Controls.TextBlock msg;
    readonly System.Windows.Controls.Border preview;
    readonly System.Windows.Media.TranslateTransform shake = new();
    System.Windows.Media.Imaging.BitmapSource photo, iconCover;
    // Typing in the name box: also while the pointer has moved the highlight to a row (AddGameCard.InBox).
    bool InBox => Focus < 0 || box.IsKeyboardFocusWithin;
    public bool WantsText => InBox;
    public System.Windows.Controls.TextBox NameBox => box;

    public AddRomCard(MainWindow w, string file, Emulator e) : base(w, T("home.add_game"), 1080)
    {
        rom = file; emu = e;
        var top = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 20) };
        top.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(230) });
        top.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        preview = new System.Windows.Controls.Border { Width = 200, Height = 200, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), BorderBrush = P.TileEdge, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        top.Children.Add(preview);
        var right = new System.Windows.Controls.StackPanel(); System.Windows.Controls.Grid.SetColumn(right, 1);
        right.Children.Add(Fonts.Text(T("home.name"), 24, P.Text2));
        box = new System.Windows.Controls.TextBox
        {
            Text = RomLogic.NameFromFile(rom), FontSize = 42, FontFamily = Fonts.Display, MaxLength = 80, Foreground = P.Text, CaretBrush = P.Accent,
            Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), SelectionBrush = P.Accent, Margin = new Thickness(0, 4, 0, 0),
        };
        right.Children.Add(box);
        box.GotKeyboardFocus += (_, _) => { if (Focus >= 0) { Focus = -1; Refresh(); } };
        right.Children.Add(new System.Windows.Controls.Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = P.Accent, Margin = new Thickness(0, 2, 0, 0), RenderTransform = shake });
        var path = Fonts.Text($"{e.Name}  ·  {Path.GetFileName(rom)}", 20, P.Text2); path.Margin = new Thickness(0, 14, 0, 0);
        right.Children.Add(path);
        msg = Fonts.Text(T("home.enter_adds_the_game_down_for"), 22, P.Text2); msg.Margin = new Thickness(0, 10, 0, 0);
        msg.TextWrapping = TextWrapping.Wrap; msg.TextTrimming = TextTrimming.None;
        right.Children.Add(msg);
        top.Children.Add(right);
        Body.Children.Add(top);
        iconCover = RomArt.Cover(rom, RomArt.IconsDir(w.Lib));
        ShowPreview();
        AddRow(T("home.cover"), () => photo != null ? T("home.own_image") : iconCover != null ? T("home.from_the_game_s_icon") : T("settings.name_on_a_plain_tile"), activate: ChooseImage);
        AddRow(T("common.add"), activate: Add);
        AddRow(T("common.cancel"), activate: () => Close());
        Focus = -1;
    }

    void ShowPreview()
    {
        var b = photo ?? iconCover;
        preview.Background = b != null ? Art.Fill(b) : P.TileFallback;
        if (b == null)
        {
            var t = Fonts.Text(box.Text, 24, P.Text, FontWeights.Normal, Fonts.Display);
            t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(14);
            preview.Child = t;
        }
        else preview.Child = null;
    }

    public override void Open()
    {
        base.Open();
        box.SelectAll();
        Root.Dispatcher.BeginInvoke(() => { box.Focus(); System.Windows.Input.Keyboard.Focus(box); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void ChooseImage()
    {
        Picker.File(w, T("home.choose_a_cover_image"), Picker.ImageFilter, Picker.Images, null, file =>
        {
            if (AddGameLogic.InInbox(file)) Error(AddGameLogic.NotChecked(T("home.images")));
            else { photo = AddGameLogic.PhotoCover(file); if (photo == null) Error(T("home.that_image_can_t_be_read")); }
            ShowPreview(); Refresh();
        });
    }

    void Error(string text) { msg.Text = text; msg.Foreground = P.Error; Anim.Refuse(shake); }

    public void Add()
    {
        string name = AddGameLogic.Clean(box.Text ?? "");
        string err = AddGameLogic.CheckName(name, w.Lib) ?? RomLogic.Check(rom, w.Lib);
        if (err != null) { Error(err); if (Focus >= 0) { Focus = -1; Refresh(); box.Focus(); } return; }
        Game g;
        try { g = RomLogic.Commit(w.Lib, rom, emu, name, photo ?? iconCover, coverFrom: photo != null ? "own" : "icon"); if (w.Home?.ViewFolder is string f) { g.Folder = f; w.Lib.Save(); } }
        catch (Exception e) { Log.Write("add rom: " + e); Error(T("home.could_not_add_the_game") + e.Message); return; }
        Close(false);
        Sounds.Play("favorite");
        w.Home?.Rebuild(g.Id);
        w.Home?.SlideIn();
        w.Toasts.Show(T("home.game_added"), $"{name}  ·  {emu.Name}", "plus");
        if (string.IsNullOrEmpty(w.Opt.Script)) Covers.Queue(w);   // its box art now, not at the next start (test scripts use coverfetch)
    }

    public override void OnAct(Act a)
    {
        if (InBox)
        {
            switch (a)
            {
                case Act.Accept: Add(); break;
                case Act.Back: Close(); break;
                case Act.Down: Focus = 0; Sounds.Play("scroll"); Refresh(); w.FocusSelf(); break;
            }
            return;
        }
        if (a == Act.Up && Focus == 0) { Focus = -1; Sounds.Play("scroll"); Refresh(); box.Focus(); return; }
        base.OnAct(a);
    }
}
