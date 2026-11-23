#nullable disable
// Quick menu for a game held down (A or Enter): the tile pops out a little and a small pane slides
// out on its right, over the tile's edge. Rows, top to bottom: Tweak settings, Move, Remove, Uninstall.
// What a game can't do is greyed out (with why) and skipped.
// A container held down (or right-clicked) gets the same menu with its own rows: Open, Rename, Remove container.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using static Ionea.Loc;

namespace Ionea;

public sealed class HoldMenu : IScreen
{
    readonly MainWindow w;
    readonly Game g;
    readonly string folder;   // set = the menu is for this container (g is null)
    readonly Action restoreTile;
    readonly Palette P = Palette.I;
    readonly Grid Root = new();
    readonly ScaleTransform tileS = new(1, 1), paneS = new(0.9, 0.9);
    readonly TranslateTransform paneT = new(), bump = new();
    readonly List<Item> items = new();
    int focus = -1;
    bool armed, closed;
    public static HoldMenu Current;   // for test scripts
    Border paneEl;

    sealed class Item
    {
        public string Id, Label, Why; public bool On; public Action Do;
        public Border El; public TextBlock LabelText, WhyText; public FrameworkElement Icon;
    }

    public const double PaneW = 520, RowH = 104;

    public HoldMenu(MainWindow win, Game game, Rect tile, BitmapSource cover, Action restoreTile)
        : this(win, game, null, tile, cover != null ? CoverView.Brush(cover, game) : CoverColour.Fill(game), cover == null, restoreTile) { }

    // A container: its popped-out tile is a picture of the home tile's own glass face.
    public HoldMenu(MainWindow win, string container, Rect tile, Visual face, Action restoreTile)
        : this(win, null, container, tile, new VisualBrush(face) { Stretch = Stretch.Fill }, false, restoreTile) { }

    HoldMenu(MainWindow win, Game game, string container, Rect tile, Brush faceFill, bool nameOnFace, Action restoreTile)
    {
        w = win; g = game; folder = container; this.restoreTile = restoreTile;
        string title = g?.Name ?? folder;

        Root.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)) });
        var c = new Canvas();
        Root.Children.Add(c);

        // The popped-out tile, drawn over the grid's own one.
        double rad = 16.28 * tile.Width / 297;
        var face = new Border
        {
            Width = tile.Width, Height = tile.Height, CornerRadius = new CornerRadius(rad),
            BorderThickness = new Thickness(folder != null ? 0 : 1.5), BorderBrush = P.TileEdge, Background = faceFill,
            RenderTransform = tileS, RenderTransformOrigin = new Point(0.5, 0.5),
            Effect = new DropShadowEffect { BlurRadius = 50, ShadowDepth = 14, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        if (nameOnFace)
        {
            var name = Fonts.Text(g.Name, 40 * tile.Width / 297, CoverColour.Ink(g), FontWeights.Normal, Fonts.Display);
            name.TextWrapping = TextWrapping.Wrap; name.TextAlignment = TextAlignment.Center;
            name.VerticalAlignment = VerticalAlignment.Center; name.Margin = new Thickness(20);
            face.Child = name;
        }
        Canvas.SetLeft(face, tile.X); Canvas.SetTop(face, tile.Y);
        c.Children.Add(face);

        // The options.
        if (folder != null) BuildContainerItems(); else BuildItems();
        var list = new StackPanel();
        var head = Fonts.Text(title, 30, P.Text, FontWeights.SemiBold, Fonts.Display);
        head.Margin = new Thickness(18, 2, 18, 12);
        list.Children.Add(head);
        foreach (var it in items) list.Children.Add(BuildRow(it));
        double paneH = 24 + 52 + items.Count * (RowH + 6) + 18;

        // On the right, covering the tile's edge a little; on the left when there's no room.
        bool right = tile.Right - tile.Width * 0.18 + PaneW <= 1920 - 30;
        double x = right ? tile.Right - tile.Width * 0.18 : tile.Left + tile.Width * 0.18 - PaneW;
        double y = Math.Clamp(tile.Top + tile.Height / 2 - paneH / 2, 30, 1080 - 30 - paneH);
        var pane = paneEl = new Border
        {
            Width = PaneW, Background = P.Card, CornerRadius = new CornerRadius(28), Padding = new Thickness(12, 18, 12, 14),
            Child = list,
            RenderTransform = new TransformGroup { Children = { paneS, paneT } },
            RenderTransformOrigin = new Point(right ? 0 : 1, Math.Clamp((tile.Top + tile.Height / 2 - y) / paneH, 0, 1)),
            Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 12, Direction = 270, Opacity = 0.25, Color = Colors.Black },
        };
        Canvas.SetLeft(pane, x); Canvas.SetTop(pane, y);
        c.Children.Add(pane);
        paneT.X = right ? -24 : 24;

        focus = items.FindIndex(i => i.On);
    }

    void BuildItems()
    {
        bool rom = g.Source == "rom" && g.Rom != null;
        bool ps4 = rom && RomLogic.IsEboot(g.Rom);

        // Launch first (in place of Play with: a start asks that itself when both are installed).
        items.Add(new Item
        {
            Id = "launch", Label = T("home.start"), On = true, Why = null,
            Do = () => { Close(false); w.Home?.ActivateSelected(); },
        });

        items.Add(new Item
        {
            Id = "info", Label = T("settings.game_info"), On = true, Why = null,
            Do = () => { Close(false); new GameInfoCard(w, g).Open(); },
        });

        bool anySettings = GameSettings.Any(w, g);
        items.Add(new Item
        {
            Id = "tweaks", Label = T("settings.game_settings"), On = anySettings,
            Why = anySettings ? null : GameSettings.Why(w, g),
            Do = () => { Close(false); GameSettings.Open(w, g); },
        });

        string moveWhy = g.Source == "steam" ? T("menu.steam_moves_its_own_games")
            : !rom ? T("menu.only_emulator_games_can_be_transferred")
            : ps4 ? "PS4 game folders can't be transferred from here"
            : !File.Exists(g.Rom) ? T("menu.the_game_file_is_missing")
            : null;
        items.Add(new Item
        {
            Id = "move", Label = g.Folder == null ? T("settings.put_in_a_container") : T("settings.move"), On = true,
            Why = g.Folder != null ? T("menu.in") + g.Folder : null,
            Do = () => { Close(false); new FolderPickCard(w, g).Open(); },
        });

        items.Add(new Item
        {
            Id = "transfer", Label = T("menu.transfer_files"), On = moveWhy == null && Storage.CanMove(g),
            Why = moveWhy ?? (Storage.PlaceOf(w, g.Rom)?.Name is string place ? T("menu.now_on") + place : null),
            Do = () =>
            {
                Close(false);
                if (IsRunning(w, g)) { new MessageCard(w, T("settings.the_game_is_running"), T("menu.close_it_first_then_transfer_it")).Open(); return; }
                new PlacePickCard(w, new List<Game> { g }, T("menu.transfer_to", g.Name)).Open();
            },
        });

        items.Add(new Item
        {
            Id = "remove", Label = T("common.remove"), On = true, Why = null,
            Do = () =>
            {
                Close(false);
                g.Removed = true; w.Lib.Save();
                Log.Write($"removed \"{g.Name}\" from the list");
                w.Home?.Rebuild(null);
                w.Toasts.Show(T("settings.removed_from_the_list"), g.Name, "info");
            },
        });

        string unWhy = Uninstall.WhyNot(g);
        items.Add(new Item
        {
            Id = "uninstall", Label = T("menu.uninstall"), On = unWhy == null,
            Why = unWhy,
            Do = () => { Close(false); new UninstallCard(w, g).Open(); },
        });
    }

    // A container's rows. Rename and Remove change only the games' Folder (a container is just that
    // name on its games); no game or file is deleted. Removed games in it follow too, so a restored
    // game does not bring an old container back.
    void BuildContainerItems()
    {
        items.Add(new Item
        {
            Id = "open", Label = T("home.open"), On = true, Why = null,
            Do = () => { Close(false); w.Home?.ActivateSelected(); },
        });
        items.Add(new Item
        {
            Id = "rename", Label = T("common.rename"), On = true, Why = null,
            Do = () => { Close(false); RenameContainer(w, folder); },
        });
        int n = w.Lib.FolderGames(folder, "name").Count;
        items.Add(new Item
        {
            Id = "remove", Label = T("menu.remove_container"), On = true,
            Why = Loc.N("menu.games_go_back", n),
            Do = () => { Close(false); new RemoveContainerCard(w, folder).Open(); },
        });
    }

    static List<Game> InContainer(MainWindow w, string f) =>
        w.Lib.Data.Games.Where(x => string.Equals(x.Folder, f, StringComparison.CurrentCultureIgnoreCase)).ToList();

    public static void RenameContainer(MainWindow w, string f) =>
        new TextCard(w, T("menu.rename_the_container"), f, 28, n =>
        {
            if (n == f) return;
            foreach (var x in InContainer(w, f)) x.Folder = n;
            w.Lib.Save();
            Log.Write($"container \"{f}\" renamed to \"{n}\"");
            w.Home?.Rebuild("folder:" + n);
            w.Toasts.Show(T("menu.container_renamed"), T("menu.is_now", f, n), "folder");
        }, n => !string.Equals(n, f, StringComparison.CurrentCultureIgnoreCase)
                && w.Lib.Folders().Any(x => string.Equals(x, n, StringComparison.CurrentCultureIgnoreCase))
            ? T("home.there_is_already_a_container_called") : null).Open();

    public static void RemoveContainer(MainWindow w, string f)
    {
        var games = InContainer(w, f);
        foreach (var x in games) x.Folder = null;
        w.Lib.Save();
        Log.Write($"container \"{f}\" removed, {games.Count} game(s) back on the home row");
        w.Home?.Rebuild(games.FirstOrDefault(x => !x.Removed)?.Id);
        w.Toasts.Show(T("menu.container_removed"), T("menu.its_games_are_back_on_the"), "folder");
    }

    public static bool IsRunning(MainWindow w, Game g) => w.GameMenu.Game?.Id == g.Id;

    Border BuildRow(Item it)
    {
        var ink = !it.On ? P.Text2 : it.Id == "uninstall" ? P.Error : P.Text;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        it.Icon = Art.Icon(it.Id switch { "open" => "folder", "rename" => "pen", "info" => "info", "tweaks" => "gear", "move" => "folder", "transfer" => "move", "launch" => "play", "remove" => "minus", _ => "trash" }, 36, ink);
        it.Icon.VerticalAlignment = VerticalAlignment.Center; it.Icon.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(it.Icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        it.LabelText = Fonts.Text(it.Label, 30, ink);
        it.WhyText = Fonts.Text(it.Why ?? "", 20, P.Text2);
        if (string.IsNullOrEmpty(it.Why)) it.WhyText.Visibility = Visibility.Collapsed;
        texts.Children.Add(it.LabelText); texts.Children.Add(it.WhyText);
        Grid.SetColumn(texts, 1); grid.Children.Add(texts);
        it.El = new Border
        {
            Height = RowH, CornerRadius = new CornerRadius(20), Padding = new Thickness(20, 0, 18, 0), BorderThickness = new Thickness(3),
            Margin = new Thickness(0, 0, 0, 6), Child = grid, Opacity = it.On ? 1 : 0.45,
        };
        return it.El;
    }

    void Refresh()
    {
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i]; bool f = i == focus;
            it.El.Background = f ? P.RowFocus : Brushes.Transparent;
            it.El.BorderBrush = f ? Palette.RingBrush() : Brushes.Transparent;
            Art.Lit(it.El, f);
            it.El.RenderTransform = f ? bump : null;
        }
    }

    public void Open()
    {
        Current = this;
        // The press that opened it must be let go first. Watch for that here: a new press sets AcceptHeld
        // again before it reaches OnAct, so checking it there would ignore every later press.
        armed = !w.AcceptHeld;
        if (!armed)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            t.Tick += (s, e) => { if (closed || !w.AcceptHeld) { armed = true; t.Stop(); } };
            t.Start();
        }
        Refresh();
        w.CardLayer.Children.Add(Root);
        w.Push(this);
        Sounds.Play("menuopen");
        Anim.FromTo(Root, UIElement.OpacityProperty, 0, 1, 160);
        Anim.FromTo(tileS, ScaleTransform.ScaleXProperty, 0.95, 1.07, 320, Anim.Back);
        Anim.FromTo(tileS, ScaleTransform.ScaleYProperty, 0.95, 1.07, 320, Anim.Back);
        Anim.To(paneS, ScaleTransform.ScaleXProperty, 1, 300, Anim.Back, 40);
        Anim.To(paneS, ScaleTransform.ScaleYProperty, 1, 300, Anim.Back, 40);
        Anim.To(paneT, TranslateTransform.XProperty, 0, 300, Anim.Out, 40);
        Log.Write($"hold menu: \"{g?.Name ?? "container " + folder}\" ({string.Join(", ", items.Select(i => i.Id + (i.On ? "" : " (greyed)")))})");
    }

    public void Close(bool sound = true)
    {
        if (closed) return;
        closed = true;
        if (Current == this) Current = null;
        if (sound) Sounds.Play("back");
        w.Pop(this);
        restoreTile?.Invoke();
        Anim.To(tileS, ScaleTransform.ScaleXProperty, 1, 160);
        Anim.To(tileS, ScaleTransform.ScaleYProperty, 1, 160);
        Anim.To(Root, UIElement.OpacityProperty, 0, 160, done: () => w.CardLayer.Children.Remove(Root));
    }

    // For test scripts: "on" ids in order, and which one has focus.
    public string Rows => string.Join(",", items.Select(i => i.On ? i.Id : "-" + i.Id));
    public string Focused => focus >= 0 ? items[focus].Id : "";

    // Mouse: pointing at a row focuses it, a click picks it (greyed rows do nothing), a click outside closes.
    public bool OnMouse(MouseAct m, Point p)
    {
        int i = items.FindIndex(it => Pointer.Over(it.El, w.Stage, p));
        switch (m)
        {
            case MouseAct.Move:
                if (i >= 0 && items[i].On && i != focus) { focus = i; Sounds.Play("scroll"); Refresh(); }
                return i >= 0 && items[i].On;
            case MouseAct.Down:
                if (i >= 0) { if (items[i].On) { focus = i; Refresh(); armed = true; OnAct(Act.Accept); } return true; }
                if (!Pointer.Over(paneEl, w.Stage, p)) Close();
                return true;
            case MouseAct.WheelUp: OnAct(Act.Up); return true;
            case MouseAct.WheelDown: OnAct(Act.Down); return true;
        }
        return m != MouseAct.Right;
    }

    public void OnAct(Act a)
    {
        switch (a)
        {
            case Act.Up:
            case Act.Down:
            {
                int d = a == Act.Down ? 1 : -1, i = focus + d;
                while (i >= 0 && i < items.Count && !items[i].On) i += d;   // greyed rows are skipped
                if (i >= 0 && i < items.Count) { focus = i; Sounds.Play("scroll"); Refresh(); }
                else Anim.Edge(bump, d, true);
                break;
            }
            case Act.Accept:
                if (!armed) return;   // still the press that opened the menu
                if (focus < 0) { Close(); return; }
                Sounds.Play("select");
                items[focus].Do();
                break;
            case Act.Back:
            case Act.Menu:
                Close();
                break;
        }
    }
}

// Remove container asks first: its games go back to the home row, nothing is deleted. Cancel is focused.
public sealed class RemoveContainerCard : MenuCard
{
    public RemoveContainerCard(MainWindow w, string f) : base(w, T("menu.remove", f), 860)
    {
        Note(T("menu.its_games_go_back_to_the"), 30, new Thickness(4, 0, 0, 26));
        AddRow(T("common.cancel"), activate: () => Close());
        WithIcon(AddRow(T("menu.remove_container"), activate: () => { Close(false); HoldMenu.RemoveContainer(w, f); }), "minus");
    }
}

// Uninstalling: the game's files go to the Recycle Bin (saves stay); a Steam game is uninstalled by Steam.
public static class Uninstall
{
    static readonly string[] SaveExt = { ".sav", ".srm", ".dsv", ".state", ".sgm", ".mcr", ".mcd" };

    public static string WhyNot(Game g)
    {
        if (g.Source == "steam") return string.IsNullOrEmpty(g.AppId) ? T("menu.steam_didn_t_give_its_id") : null;
        if (g.Source == "dummy") return T("menu.it_s_a_test_game");
        if (g.Source != "rom" || g.Rom == null) return T("menu.it_s_a_shortcut_uninstall_the");
        if (g.Rom.StartsWith(@"\\")) return T("menu.it_s_on_a_network_drive");
        if (RomLogic.IsEboot(g.Rom)) return Ps4Folder(g) == null ? T("menu.its_ps4_game_folder_wasn_t") : null;
        return File.Exists(g.Rom) ? null : T("menu.the_game_file_is_missing");
    }

    // A PS4 game is its whole folder (eboot.bin, sce_sys, …). Never a drive's root.
    public static string Ps4Folder(Game g)
    {
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(g.Rom));
            if (dir == null || Path.GetPathRoot(dir)?.TrimEnd('\\') == dir.TrimEnd('\\')) return null;
            if (!File.Exists(Path.Combine(dir, "eboot.bin")) || !Directory.Exists(Path.Combine(dir, "sce_sys"))) return null;
            return dir;
        }
        catch { return null; }
    }

    // What goes to the Recycle Bin.
    public static List<string> Targets(Game g)
    {
        if (RomLogic.IsEboot(g.Rom))
        {
            var dir = Ps4Folder(g);
            if (dir == null) return new();
            var l = new List<string> { dir };
            if (Directory.Exists(dir + "-patch")) l.Add(dir + "-patch");   // shadPS4's update folder
            return l;
        }
        return Storage.FilesOf(g).Where(f => !SaveExt.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
    }

    public static long Size(List<string> paths)
    {
        long n = 0;
        foreach (var p in paths)
            try
            {
                if (File.Exists(p)) n += new FileInfo(p).Length;
                else if (Directory.Exists(p)) n += new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { /* in use or no access: counted as nothing */ }
        return n;
    }

    // To the Recycle Bin. Test runs set IONEA_TEST_RECYCLE to a folder: things are moved there instead.
    public static string Recycle(List<string> paths)
    {
        string test = Environment.GetEnvironmentVariable("IONEA_TEST_RECYCLE");
        if (!string.IsNullOrEmpty(test))
        {
            try
            {
                Directory.CreateDirectory(test);
                foreach (var p in paths)
                {
                    string to = Path.Combine(test, Path.GetFileName(p.TrimEnd('\\')));
                    if (Directory.Exists(p)) Directory.Move(p, to); else File.Move(p, to, true);
                }
                return null;
            }
            catch (Exception e) { return e.Message; }
        }
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join("\0", paths) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        int r = SHFileOperation(ref op);
        if (r != 0) return T("menu.windows_said_no_code_0x", r);
        if (op.fAnyOperationsAborted) return T("menu.it_was_cancelled");
        return paths.Any(p => File.Exists(p) || Directory.Exists(p)) ? T("menu.some_files_are_still_there") : null;
    }

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}

public sealed class UninstallCard : MenuCard
{
    public static string LastResult = "";

    public UninstallCard(MainWindow w, Game g) : base(w, T("menu.uninstall_2", g.Name), 900)
    {
        if (g.Source == "steam")
        {
            Note(T("menu.steam_deletes_the_files_for_good"), 28, new Thickness(4, 0, 0, 24));
            AddRow(T("menu.uninstall_with_steam"), activate: () =>
            {
                Close(false);
                try
                {
                    Process.Start(new ProcessStartInfo("steam://uninstall/" + g.AppId) { UseShellExecute = true });
                    Log.Write($"uninstall: \"{g.Name}\" handed to Steam (app {g.AppId})");
                    LastResult = "steam";
                    w.Toasts.Show(T("menu.steam_is_uninstalling_it"), g.Name, "info");
                }
                catch (Exception e) { Log.Write("uninstall: steam: " + e.Message); new MessageCard(w, T("menu.couldn_t_reach_steam"), e.Message).Open(); }
            });
        }
        else
        {
            var paths = Uninstall.Targets(g);
            string what = RomLogic.IsEboot(g.Rom)
                ? T(paths.Count > 1 ? "menu.the_game_folder_and_update" : "menu.the_game_folder")
                : N("menu.files", paths.Count);
            Note(T("menu.uninstall_what_size_where", what, Storage.Size(Uninstall.Size(paths)), RomLogic.IsEboot(g.Rom) ? paths.FirstOrDefault() : Path.GetDirectoryName(paths.FirstOrDefault() ?? "")), 22, new Thickness(4, 0, 0, 22));
            var row = AddRow(T("menu.uninstall"), activate: () => Run(w, g, paths));
            ((row.El.Child as Grid).Children[0] as TextBlock).Foreground = P.Error;
        }
        AddRow(T("common.cancel"), activate: () => { LastResult = "cancelled"; Close(); });
        Focus = Rows.Count - 1;   // Cancel first: nothing is lost by a stray press
    }

    void Run(MainWindow w, Game g, List<string> paths)
    {
        if (HoldMenu.IsRunning(w, g)) { new MessageCard(w, T("settings.the_game_is_running"), T("menu.close_it_first_then_uninstall_it")).Open(); return; }
        Close(false);
        Log.Write($"uninstall: \"{g.Name}\": {string.Join(" | ", paths)}");
        Task.Run(() => Uninstall.Recycle(paths)).ContinueWith(r =>
        {
            string err = r.Exception?.GetBaseException().Message ?? r.Result;
            if (err != null)
            {
                LastResult = "failed";
                Log.Write($"uninstall: \"{g.Name}\" failed: {err}");
                new MessageCard(w, T("menu.couldn_t_uninstall_it"), err).Open();
                return;
            }
            LastResult = "done";
            w.Lib.Data.Games.Remove(g);
            w.Lib.Save();
            Log.Write($"uninstall: \"{g.Name}\" is in the Recycle Bin and off the list");
            w.Home?.Rebuild(null);
            w.Toasts.Show(T("menu.uninstalled"), T("menu.is_in_the_recycle_bin", g.Name), "info");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
