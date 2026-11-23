#nullable disable
// "+ Add game" (port of frontend\add-game): Windows picker -> name card -> shortcut + cover in the data
// folder. Never runs the picked file. Refuses the Inbox (Settings.Paths.Inbox) and games that are already in the list.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Ionea.Loc;

namespace Ionea;

public sealed class Candidate
{
    public string Picked, Target, Args = "", WorkDir, Icon, DefaultName;
}

public static class AddGameLogic
{
    public static string Inbox;   // a folder of checked downloads (Settings.Paths.Inbox, or --inbox in tests); null = none
    public const int CoverSize = 768;

    public static bool InInbox(string p) =>
        p != null && Inbox != null && (Path.GetFullPath(p) + "\\").StartsWith(Path.GetFullPath(Inbox).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    public static string NotChecked(string what) => $"{what} in {Inbox} are not checked yet.";

    // null = fine, otherwise a sentence for the user.
    public static string Resolve(string picked, Library lib, out Candidate c)
    {
        c = new Candidate { Picked = Path.GetFullPath(picked) };
        if (!File.Exists(c.Picked)) return T("home.the_file_was_not_found");
        bool isLnk = c.Picked.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        if (isLnk)
        {
            try { Shortcut.Read(c.Picked, out c.Target, out c.Args, out c.WorkDir, out c.Icon); }
            catch { return T("home.this_shortcut_can_t_be_read"); }
            if (string.IsNullOrEmpty(c.Target) || !File.Exists(c.Target)) return T("home.this_shortcut_doesn_t_point_to");
        }
        else c.Target = c.Picked;
        if (InInbox(c.Picked) || InInbox(c.Target)) return NotChecked(T("home.files")) + T("home.install_the_game_first");
        if (!c.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return T("home.only_programs_exe_can_be_added");
        if (string.IsNullOrEmpty(c.WorkDir) || !Directory.Exists(c.WorkDir)) c.WorkDir = Path.GetDirectoryName(c.Target);
        string dup = SameTarget(lib, c);
        if (dup != null) return T("home.it_is_already_in_your_games", dup);
        c.DefaultName = isLnk ? Clean(Path.GetFileNameWithoutExtension(c.Picked)) : ProductName(c.Target);
        return null;
    }

    static string SameTarget(Library lib, Candidate c)
    {
        string full = Path.GetFullPath(c.Target), args = (c.Args ?? "").Trim();
        foreach (var g in lib.Data.Games)
        {
            if (SteamLib.InFolderOf(g, full)) return g.Name + " (from Steam" + (g.Removed ? ", removed from the list)" : ")");
            if (g.Source == "steam") continue;
            string t = g.Target, a = g.Args;
            if (t == null && lib.ShortcutPath(g) is string lnk && File.Exists(lnk))
                try { Shortcut.Read(lnk, out t, out a, out _, out _); } catch (Exception caught) { Log.Debug("add game: read shortcut", caught); }
            if (t != null && string.Equals(Path.GetFullPath(t), full, StringComparison.OrdinalIgnoreCase)
                && string.Equals((a ?? "").Trim(), args, StringComparison.OrdinalIgnoreCase))
                return g.Name + (g.Removed ? " (removed from the list)" : "");
        }
        return null;
    }

    static string ProductName(string exe)
    {
        string fallback = Path.GetFileNameWithoutExtension(exe);
        try
        {
            string p = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            if (string.IsNullOrEmpty(p) || p.Length > 40 || p.IndexOf((char)0xC2) >= 0 || p.IndexOf((char)0xFFFD) >= 0
                || Regex.IsMatch(p, @"(?i)microsoft|windows|operating system|unity|unreal|\bue[45]\b|launcher|setup|^game$")) return fallback;
            return Clean(p);
        }
        catch { return fallback; }
    }

    public static string Clean(string s)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch.ToString(), "");
        s = s.Replace("\u2122", "").Replace("\u00AE", "").Replace("\u00A9", "");
        return Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
    }

    public static string CheckName(string name, Library lib)
    {
        if (name.Length == 0) return T("home.type_a_name");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name can't contain \\ / : * ? \" < > |";
        if (name.EndsWith(".") || name.EndsWith(" ")) return "A name can't end with a dot or a space.";
        if (name.Length > 80) return T("home.that_name_is_too_long");
        if (Regex.IsMatch(name, @"(?i)^(con|prn|aux|nul|com\d|lpt\d)$")) return T("home.windows_doesn_t_allow_that_name");
        if (lib.Data.Games.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || File.Exists(Path.Combine(lib.ShortcutsDir, name + ".lnk")))
            return "A game with this name already exists.";
        return null;
    }

    // Makes the shortcut and the cover and adds the game. The picked file itself is never started.
    public static Game Commit(Library lib, Candidate c, string name, BitmapSource cover)
    {
        var g = new Game
        {
            Id = Library.NewId(), Name = name, Source = "added", Added = DateTime.Now, Shortcut = name + ".lnk",
            Target = c.Target, Args = string.IsNullOrWhiteSpace(c.Args) ? null : c.Args,
        };
        Shortcut.Create(Path.Combine(lib.ShortcutsDir, g.Shortcut), c.Target, c.Args, c.WorkDir, c.Icon);
        if (cover != null) { g.Cover = g.Id + ".png"; Art.SavePng(cover, Path.Combine(lib.CoversDir, g.Cover)); }
        lib.Data.Games.Add(g);
        lib.Save();
        Log.Write($"added game \"{name}\" -> {c.Target}");
        return g;
    }

    // ---- covers ----

    internal static void SoftTile(DrawingContext dc, int n)
    {
        dc.DrawRectangle(new LinearGradientBrush(Palette.Hex("FBFCFE"), Palette.Hex("E3E8F0"), 90), null, new Rect(0, 0, n, n));
        var glow = new RadialGradientBrush(Color.FromArgb(70, 0xC9, 0xE6, 0xFF), Color.FromArgb(0, 0xC9, 0xE6, 0xFF));
        dc.DrawEllipse(glow, null, new Point(n / 2.0, n * 0.5), n * 0.6, n * 0.55);
    }

    public static BitmapSource IconCover(BitmapSource icon)
    {
        if (icon == null) return null;   // no icon: the tile shows the name instead
        int n = CoverSize;
        return Art.Render(n, n, dc =>
        {
            SoftTile(dc, n);
            if (icon == null) return;
            // Small icons stay crisp: never more than 2x, at most 44 % of the tile.
            double s = Math.Min(n * 0.44, Math.Max(icon.PixelWidth, 128) * 2.0);
            if (icon.PixelWidth >= 200) s = n * 0.44;
            var r = new Rect((n - s) / 2, (n - s) / 2 - n * 0.01, s, s);
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(46, 0x2B, 0x3A, 0x60), Color.FromArgb(0, 0x2B, 0x3A, 0x60)), null,
                           new Point(n / 2.0, r.Bottom - s * 0.02), s * 0.55, s * 0.14);
            dc.DrawImage(icon, r);
        });
    }

    // The user's own picture, whole (was cropped to its centre square): at most PhotoMax pixels on
    // its long side. The tiles fill with its centre until Adjust cover (CoverZoom.cs) frames it otherwise.
    public const int PhotoMax = 2048;
    public static BitmapSource PhotoCover(string file)
    {
        var src = Art.LoadImage(file, 0);
        if (src == null) return null;
        double sc = Math.Min(1, (double)PhotoMax / Math.Max(src.PixelWidth, src.PixelHeight));
        int w = Math.Max(1, (int)Math.Round(src.PixelWidth * sc)), h = Math.Max(1, (int)Math.Round(src.PixelHeight * sc));
        return Art.Render(w, h, dc => dc.DrawImage(src, new Rect(0, 0, w, h)));
    }

    // Largest icon of the exe (or the shortcut's own icon), trimmed to its visible part.
    public static BitmapSource ExeIcon(Candidate c)
    {
        string file = c.Target; int index = 0;
        if (!string.IsNullOrEmpty(c.Icon))
        {
            int comma = c.Icon.LastIndexOf(',');
            string f = Environment.ExpandEnvironmentVariables((comma > 0 ? c.Icon[..comma] : c.Icon).Trim());
            if (f.Length > 0 && File.Exists(f)) { file = f; if (comma > 0) int.TryParse(c.Icon[(comma + 1)..], out index); }
        }
        foreach (int size in new[] { 256, 128, 64, 48, 32 })
        {
            var h = new IntPtr[1]; var id = new int[1];
            if (Native.PrivateExtractIcons(file, index, size, size, h, id, 1, 0) != 1 || h[0] == IntPtr.Zero) continue;
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(h[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return Trim(bmp);
            }
            catch { /* not a usable icon: try the next */ }
            finally { Native.DestroyIcon(h[0]); }
        }
        return null;
    }

    static BitmapSource Trim(BitmapSource b)
    {
        var src = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            if (px[(y * w + x) * 4 + 3] > 8) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        if (maxX < 0) return b;
        int cw = maxX - minX + 1, ch = maxY - minY + 1, side = Math.Max(cw, ch);
        var crop = new CroppedBitmap(src, new Int32Rect(minX, minY, cw, ch));
        return Art.Render(side, side, dc => dc.DrawImage(crop, new Rect((side - cw) / 2.0, (side - ch) / 2.0, cw, ch)));
    }
}

public sealed class AddGameFlow
{
    readonly MainWindow w;
    public AddGameFlow(MainWindow win) { w = win; }

    public void Start(string pickedForTest = null)
    {
        if (pickedForTest == null)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Picker.File(w, T("home.choose_a_game_exe_or_shortcut"), T("home.game_exe_lnk_exe_lnk"), new[] { ".exe", ".lnk" },
                Directory.Exists(pf) ? pf : Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Use, dereferenceLinks: false);
            return;
        }
        Use(pickedForTest);
    }

    void Use(string picked)
    {
        string err = AddGameLogic.Resolve(picked, w.Lib, out Candidate c);
        if (err != null) { Log.Write($"add game refused: {picked}: {err}"); new MessageCard(w, T("home.can_t_add_this"), err).Open(); return; }
        new AddGameCard(w, c).Open();
    }
}

// Name the new game; optional own image. Focus 0 = the name box, then "Choose image", "Add", "Cancel".
public sealed class AddGameCard : MenuCard, IScreen
{
    readonly Candidate c;
    readonly TextBox box;
    readonly TextBlock msg;
    readonly Border preview;
    readonly TranslateTransform shake = new();
    BitmapSource iconCover, photo;
    // Typing in the name box: also while the pointer has moved the highlight to a row (Delete there
    // was the Game options key and closed the card, with Settings under it).
    bool InBox => Focus < 0 || box.IsKeyboardFocusWithin;
    public bool WantsText => InBox;
    public TextBox NameBox => box;

    public AddGameCard(MainWindow w, Candidate cand) : base(w, T("home.add_game"), 1080)
    {
        c = cand;
        var top = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        preview = new Border { Width = 200, Height = 200, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), BorderBrush = P.TileEdge, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        top.Children.Add(preview);
        var right = new StackPanel(); Grid.SetColumn(right, 1);
        right.Children.Add(Fonts.Text(T("home.name"), 24, P.Text2));
        box = new TextBox
        {
            Text = c.DefaultName, FontSize = 42, FontFamily = Fonts.Display, MaxLength = 80, Foreground = P.Text, CaretBrush = P.Accent,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionBrush = P.Accent, Margin = new Thickness(0, 4, 0, 0),
        };
        right.Children.Add(box);
        box.GotKeyboardFocus += (_, _) => { if (Focus >= 0) { Focus = -1; Refresh(); } };
        right.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = P.Accent, Margin = new Thickness(0, 2, 0, 0), RenderTransform = shake });
        var path = Fonts.Text(c.Target, 20, P.Text2); path.Margin = new Thickness(0, 14, 0, 0);
        right.Children.Add(path);
        msg = Fonts.Text(T("home.enter_adds_the_game_down_for"), 22, P.Text2); msg.Margin = new Thickness(0, 10, 0, 0);
        msg.TextWrapping = TextWrapping.Wrap; msg.TextTrimming = TextTrimming.None;
        right.Children.Add(msg);
        top.Children.Add(right);
        Body.Children.Add(top);

        try { iconCover = AddGameLogic.IconCover(AddGameLogic.ExeIcon(c)); } catch (Exception e) { Log.Write("icon: " + e.Message); }
        ShowPreview();

        AddRow(T("home.cover"), () => photo != null ? T("home.own_image") : T("home.from_the_game_s_icon"), activate: ChooseImage);
        AddRow(T("common.add"), activate: Add);
        AddRow(T("common.cancel"), activate: () => Close());
        Focus = -1;
    }

    void ShowPreview()
    {
        var b = photo ?? iconCover;
        preview.Background = b != null ? Art.Fill(b) : P.TileFallback;
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
            else
            {
                photo = AddGameLogic.PhotoCover(file);
                if (photo == null) Error(T("home.that_image_can_t_be_read"));
            }
            ShowPreview(); Refresh();
        });
    }

    public void SetPhotoForTest(string file) { photo = AddGameLogic.PhotoCover(file); ShowPreview(); Refresh(); }

    void Error(string text) { msg.Text = text; msg.Foreground = P.Error; Anim.Refuse(shake); }

    public void Add()
    {
        string name = AddGameLogic.Clean(box.Text ?? "");
        string err = AddGameLogic.CheckName(name, w.Lib);
        if (err != null) { Error(err); if (Focus >= 0) { Focus = -1; Refresh(); box.Focus(); } return; }
        // A duplicate may have been added meanwhile; check once more right before writing.
        string again = AddGameLogic.Resolve(c.Picked, w.Lib, out _);
        if (again != null) { Error(again); return; }
        Game g;
        try { g = AddGameLogic.Commit(w.Lib, c, name, photo ?? iconCover); if (w.Home?.ViewFolder is string f) { g.Folder = f; w.Lib.Save(); } }
        catch (Exception e) { Log.Write("add game: " + e); Error(T("home.could_not_add_the_game") + e.Message); return; }
        Close(false);
        Sounds.Play("favorite");
        w.Home?.Rebuild(g.Id);
        w.Home?.SlideIn();
        w.Toasts.Show(T("home.game_added"), name, "plus");
    }

    public override void OnAct(Act a)
    {
        if (InBox)   // in the name box
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
