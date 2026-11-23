#nullable disable
// Home: top bar, the game grid (1, 2 or 4 rows, scrolling sideways only) and the options bar.
// Tiles are built once at the Large size and placed/zoomed with render transforms only, so
// scrolling and zooming are GPU work (no layout passes).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static Ionea.Loc;

namespace Ionea;

public sealed class HomeScreen : IScreen
{
    // Per zoom: tile size, rows, gap between columns, space above each row (for the name), name size.
    public sealed record ZoomDef(string NameId, double T, int Rows, double G, double L, double Label)
    {
        public string Name => Loc.T(NameId);   // read when shown, so a language change shows at once
    }
    public static readonly ZoomDef[] Zooms =
    {
        new("home.large", 420, 1, 44, 72, 40),
        new("home.medium", 270, 2, 32, 58, 32),
        new("home.small", 136, 4, 22, 38, 24),
    };
    const double B = 420;                           // tiles are built at this size and scaled
    const double AreaTop = 150, AreaH = 700, LM = 96, RM = 96, VW = 1920 - LM - RM;
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    // An open folder: a glass panel in the middle over the blurred home row, its games three by three
    // on pages side by side (swipe, or step past the edge, for the next page). Screen coordinates.
    const int PageN = 9;
    const double FT = 140, FCol = 180, FRow = 184, PW = 600, PH = 630, PX = 960 - PW / 2, PY = 226, PR = 30;   // PR: the menu cards' corner (44 before the glass matched them)
    const double FL = PX + (PW - (2 * FCol + FT)) / 2, FTop = PY + 38;

    // A place on the row. Only the tiles on screen and a little to each side are built (El set, see
    // Build / Unbuild); the rest are just their game or folder, so a library of thousands builds and
    // reads covers for about a screenful. Its transforms are made the first time they are used.
    sealed class Tile
    {
        public Game Game;                            // null = the "+ Add game" tile (or a folder)
        public string Folder;                        // set = a folder tile
        public Grid El;                              // null: not built (off screen)
        public Border Face;
        public BitmapSource Cover;
        public Border Flash;
        public Border Badge;                         // "Paused" / "Playing" on the game that is open
        public TextBlock Name;                       // in an open folder: the name under the icon
        public TextBlock Title;                      // a game's name on its face until its cover is read
        public System.Threading.CancellationTokenSource Reads;   // its cover reads, cancelled when it goes off screen
        ScaleTransform ps, s, lift;
        TranslateTransform pt, t, slide;
        TransformGroup group;
        public ScaleTransform PS => ps ??= new(1, 1, B / 2, B / 2);   // press effect, around the tile centre
        public TranslateTransform PT => pt ??= new();
        public ScaleTransform S => s ??= new();
        public TranslateTransform T => t ??= new();
        public TranslateTransform In => slide ??= new();   // the slide in (SlideIn)
        public ScaleTransform Lift => lift ??= new(1, 1, B / 2, B / 2);   // the selected tile stands a little forward
        public TransformGroup Group => group ??= new() { Children = { Lift, PS, PT, S, T, In } };
    }

    sealed class OptButton
    {
        public string Kind; public Func<string> Caption; public Action Run;
        public Grid El; public UIElement Ring; public ScaleTransform RingS;
        public ScaleTransform PS; public TranslateTransform PT; public UIElement Flash;
        public double Cx;
    }

    readonly MainWindow w;
    readonly Palette P = Palette.I;
    public readonly Grid Root = new() { RenderTransformOrigin = new Point(0.5, 0.5) };
    readonly ScaleTransform rootScale = new();
    readonly Canvas strip = new();
    readonly TranslateTransform stripT = new();
    readonly List<Tile> tiles = new();
    readonly List<int> built = new();               // the built tiles' places, in order (their El are the strip's first children, in this order)
    readonly Stack<Tile> spare = new();             // built game tiles gone off screen, dressed again for the next game that comes on
    const int SpareMax = 120, MarginCols = 2;       // MarginCols: columns built past each side of the screen
    System.Windows.Threading.DispatcherTimer trimTimer;
    ZoomDef laidZoom;                               // the zoom the tiles were last placed for (a zoom change moves them from there)
    readonly Canvas ring;
    UIElement ringGlow;
    readonly ScaleTransform ringS = new();
    readonly TranslateTransform ringT = new(), ringBump = new(), ringPT = new();
    readonly ScaleTransform ringPS = new(1, 1, B / 2, B / 2);
    readonly ScaleTransform ringLand = new(1, 1, B / 2, B / 2);   // the small "landing" pop when the ring arrives
    readonly TranslateTransform labelRise = new(), stripSlide = new();
    readonly TranslateTransform ringIn = new();   // the ring rides in with its tile (SlideIn)
    readonly ScaleTransform ringLift = new(LiftK, LiftK, B / 2, B / 2);   // the ring stays on the lifted tile's edge
    const double LiftK = 1.035;
    // Finish pass: behind the row, a soft wash of the selected game's own colours (two wide
    // glows that drift with the selection and change colour with it), and the screen's edges: the row fades
    // out where it runs on past the screen instead of being cut off.
    readonly Canvas ambient = new() { Width = 1920, Height = 1080, IsHitTestVisible = false, ClipToBounds = true };
    readonly SolidColorBrush ambA = new(Colors.Transparent), ambB = new(Colors.Transparent);   // shaped by Art.SoftMask: no rings
    readonly TranslateTransform ambAT = new(), ambBT = new();
    readonly GradientStop edgeL = new(Colors.Black, 0), edgeR = new(Colors.Black, 1);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, Tuple<Color, Color>> moods = new();
    readonly StackPanel hints = new() { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
    string hintKey;
    // Covers read for tiles, by file: the least recently shown go first once they pass the budget (about
    // 360 tile covers; a zoomed-in cover read at full size weighs more). Before, the whole cache was emptied
    // at 400 covers, so above that every rebuild read every cover again.
    static readonly LruCache<string, (DateTime at, BitmapSource bmp)> coverCache =
        new(256L << 20, 4000, c => (long)c.bmp.PixelWidth * c.bmp.PixelHeight * 4, StringComparer.OrdinalIgnoreCase);
    long pressUntil;                                // input is held until the press effect has played
    readonly Grid labelBox = new() { Width = 1400, IsHitTestVisible = false };
    readonly TextBlock label;
    readonly TranslateTransform labelT = new();
    readonly TextBlock nameText, clockText, padCount, barCaption, viewText;
    readonly FrameworkElement padIcon;
    readonly TranslateTransform captionT = new(), barBump = new();
    readonly List<OptButton> buttons = new();
    int sel, barSel;
    string folder, filter;                          // the open folder / the search text (null = none)
    bool onBar, onChip;
    readonly Grid avatarHost = new() { Width = 64, Height = 64 };
    readonly Canvas chip = new();
    Grid chipRing;
    readonly TranslateTransform chipBump = new();
    double scroll;
    ZoomDef Z => Zooms[(int)w.Settings.Zoom];
    readonly Canvas view = new() { ClipToBounds = true, Width = 1920, Height = 1080 };
    readonly Canvas folderLayer = new() { Width = 1920, Height = 1080, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Image backdrop = new() { Width = 1920, Height = 1080, Stretch = Stretch.Fill };
    readonly Border frost = new() { CornerRadius = new CornerRadius(PR) };   // inside the glass: the home row blurred harder
    readonly ScaleTransform folderScale = new(1, 1, 960, PY + PH / 2), viewScale = new(1, 1, 960, PY + PH / 2);
    readonly StackPanel dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock folderTitle;
    Canvas panelBox;                                    // the glass panel (hidden while its snapshot shrinks away)
    readonly Canvas closeLayer = new() { Width = 1920, Height = 1080, IsHitTestVisible = false };
    Canvas closeGhost;                                  // the closing folder (its glass panel and games), shrinking to its tile
    public string CloseFx { get; private set; } = "none";   // test scripts: "running" while a folder closes, else "none"
    public string CloseTarget { get; private set; } = "";   // test scripts: the tile the last close went to

    bool FolderMode => folder != null && filter == null;
    double TS => FolderMode ? FT : Z.T;                 // a tile's size on screen
    double Left0 => FolderMode ? FL : LM;               // where the strip's first tile sits, unscrolled
    int Page => sel / PageN;
    int Pages => (tiles.Count + PageN - 1) / PageN;
    Point Pos(int i) => FolderMode ? new(i / PageN * PW + i % 3 * FCol, FTop + i % PageN / 3 * FRow) : PosOf(i, Z);
    double ScrollTo(int i, double current) => FolderMode ? i / PageN * PW : ScrollFor(i, Z, current);
    Rect ScreenRect(int i) { var p = Pos(i); return new Rect(Left0 + p.X - scroll, p.Y, TS, TS); }
    static readonly Rect Panel = new(PX, PY, PW, PH);
    public int FolderPage => FolderMode ? Page + 1 : 0;   // test scripts: 1-based, 0 = no folder open
    public int FolderPages => FolderMode ? Pages : 0;
    public string FolderLook { get; private set; } = "";  // test scripts: what the last folder tile built on home shows

    public int Selected => sel;
    public int BarFocus => onBar ? barSel : -1;
    public int Count => tiles.Count;
    public double Scroll => scroll;
    public string SelectedName => sel < tiles.Count ? tiles[sel].Game?.Name ?? (tiles[sel].Folder != null ? "folder:" + tiles[sel].Folder : "+") : null;
    public string NameAt(int i) => i >= 0 && i < tiles.Count ? tiles[i].Game?.Name ?? (tiles[i].Folder != null ? "folder:" + tiles[i].Folder : "+") : null;
    // The tutorial (Tutorial.cs): "gear" (Settings on the options bar), "add" (the + tile) or a game's id.
    public FrameworkElement TutorialSpot(string what) => what == "gear" ? buttons.FirstOrDefault(b => b.Kind == "gear")?.El
        : tiles.FirstOrDefault(t => what == "add" ? t.Game == null && t.Folder == null : t.Game != null && t.Game.Id == what) is Tile t ? (FrameworkElement)t.Face ?? t.El : null;
    public string View => filter != null ? "search:" + filter : folder != null ? "folder:" + folder : "home";
    public string ViewFolder => folder;
    string AddWord => folder != null ? T("home.add_game") : T("common.add");   // "+" on Home adds a game or a container; in a container, a game

    public HomeScreen(MainWindow win)
    {
        w = win;
        w.GameMenu.Changed += GameMenuChanged;
        Root.RenderTransform = rootScale;
        Root.Children.Add(new Rectangle { Fill = P.Bg });
        var glowA = new Rectangle { Width = 1600, Height = 1100, Fill = ambA, OpacityMask = new ImageBrush(Art.SoftMask(640, 440, false)), RenderTransform = ambAT };
        Canvas.SetLeft(glowA, -800); Canvas.SetTop(glowA, 500 - 550);   // centred on (ambAT.X, 500)
        var glowB = new Rectangle { Width = 1800, Height = 1000, Fill = ambB, OpacityMask = new ImageBrush(Art.SoftMask(720, 400, false)), RenderTransform = ambBT };
        Canvas.SetLeft(glowB, -900); Canvas.SetTop(glowB, 1060 - 500);  // centred on (ambBT.X, 1060), low on the screen
        ambient.Children.Add(glowA); ambient.Children.Add(glowB);
        ambient.Opacity = 0;   // shows once the first game has given it its colours
        Root.Children.Add(Anim.Effect(ambient));
        Root.Children.Add(new Rectangle { Fill = P.VignetteInk, OpacityMask = new ImageBrush(Art.SoftMask(480, 270, true)), IsHitTestVisible = false });

        // The open folder: the home row blurred behind, a scrim, the folder's name and the glass panel.
        folderLayer.Children.Add(backdrop);
        folderLayer.Children.Add(new Rectangle { Width = 1920, Height = 1080, Fill = P.FolderScrim });
        folderTitle = Fonts.Text("", 54, P.Text, FontWeights.SemiBold, Fonts.Display);
        folderTitle.HorizontalAlignment = HorizontalAlignment.Center; folderTitle.TextTrimming = TextTrimming.CharacterEllipsis; folderTitle.MaxWidth = 1200;
        var titleBox = new Grid { Width = 1920 };
        titleBox.Children.Add(folderTitle);
        Canvas.SetTop(titleBox, 140);
        folderLayer.Children.Add(titleBox);
        var box = panelBox = new Canvas { Width = 1920, Height = 1080, RenderTransform = folderScale };
        var glass = new Grid { Width = PW, Height = PH };
        // Glass 2: a soft shadow drawn once (no live blur under the moving panel), the home
        // row blurred much more strongly inside the glass (frost, set in SnapBackdrop), the tint, a bright
        // rim (top brighter), a thin inner highlight and the top sheen. All still pictures and brushes.
        const double SP = 70;
        glass.Children.Add(new Image { Source = Art.PanelShadow(PW, PH, PR, SP), Width = PW + 2 * SP, Height = PH + 2 * SP, Margin = new Thickness(-SP), IsHitTestVisible = false });
        glass.Children.Add(frost);
        glass.Children.Add(new Border { CornerRadius = new CornerRadius(PR), Background = new LinearGradientBrush(new GradientStopCollection { P.GlassA, P.GlassB }, 90), BorderBrush = new LinearGradientBrush(new GradientStopCollection { P.RimA, P.RimB }, 90), BorderThickness = new Thickness(1.5) });
        glass.Children.Add(new Border { CornerRadius = new CornerRadius(PR - 1.5, PR - 1.5, 0, 0), Height = PH * 0.4, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1.5, 1.5, 1.5, 0), Background = new LinearGradientBrush(new GradientStopCollection { P.ShineA, P.ShineB }, 90) });
        glass.Children.Add(new Border { CornerRadius = new CornerRadius(PR - 1.5), Margin = new Thickness(1.5), BorderBrush = new LinearGradientBrush(new GradientStopCollection { P.HiA, P.HiB }, 90), BorderThickness = new Thickness(1) });
        Canvas.SetLeft(glass, PX); Canvas.SetTop(glass, PY);
        box.Children.Add(glass);
        var dotBox = new Grid { Width = PW };
        dotBox.Children.Add(dots);
        Canvas.SetLeft(dotBox, PX); Canvas.SetTop(dotBox, PY + PH - 32);
        box.Children.Add(dotBox);
        folderLayer.Children.Add(box);
        Root.Children.Add(folderLayer);

        var top = new Canvas();
        Root.Children.Add(top);

        // Top bar: who's playing (picture and name: Up from the top row, A opens the profile), controllers, clock.
        chip.RenderTransform = chipBump;
        chipRing = Art.CircleRing(64, out var chipGlow); chipRing.Opacity = 0;
        Anim.Glow(chipGlow);
        Canvas.SetLeft(chipRing, 44); Canvas.SetTop(chipRing, 24); chip.Children.Add(chipRing);
        Canvas.SetLeft(avatarHost, 64); Canvas.SetTop(avatarHost, 44); chip.Children.Add(avatarHost);
        nameText = Fonts.Text("", 32, P.Text, FontWeights.Medium); Canvas.SetLeft(nameText, 146); Canvas.SetTop(nameText, 54);
        nameText.MaxWidth = 900; chip.Children.Add(nameText);
        top.Children.Add(chip);
        RefreshName();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        padIcon = Art.Icon("controller", 36, P.Text2); padIcon.Margin = new Thickness(0, 4, 6, 0);
        padCount = Fonts.Text("", 26, P.Text2); padCount.Margin = new Thickness(0, 4, 28, 0); padCount.VerticalAlignment = VerticalAlignment.Center;
        clockText = Fonts.Text("", 34, P.Text, FontWeights.Light, Fonts.Display);
        right.Children.Add(padIcon); right.Children.Add(padCount); right.Children.Add(clockText);
        Canvas.SetRight(right, 66); Canvas.SetTop(right, 50); top.Children.Add(right);
        // Which view is open (folder or search), under the top bar.
        viewText = Fonts.Text("", 30, P.Text2, FontWeights.Normal, Fonts.Display); viewText.MaxWidth = 1700;
        Canvas.SetLeft(viewText, LM); Canvas.SetTop(viewText, 128); top.Children.Add(viewText);

        // Game strip: scrolls sideways. The ring and the name ride on it.
        view.RenderTransform = viewScale;
        view.OpacityMask = new LinearGradientBrush(new GradientStopCollection { edgeL, new GradientStop(Colors.Black, 0.04), new GradientStop(Colors.Black, 0.96), edgeR }, new Point(0, 0), new Point(1, 0));
        strip.RenderTransform = new TransformGroup { Children = { stripT, stripSlide } };
        view.Children.Add(strip);
        Root.Children.Add(view);
        Root.Children.Add(closeLayer);   // the closing folder shrinks above the home row

        ring = Art.Ring(B, out ringGlow);
        ring.RenderTransform = new TransformGroup { Children = { ringLift, ringLand, ringPS, ringPT, ringS, ringT, ringBump, ringIn } };
        Anim.Glow(ringGlow);
        label = Fonts.Text("", 40, P.Accent, FontWeights.SemiBold, Fonts.Display);
        label.TextAlignment = TextAlignment.Center; label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
        labelBox.Children.Add(label);
        labelBox.RenderTransform = new TransformGroup { Children = { labelRise, labelT } };

        // Options bar.
        var bar = new Canvas { RenderTransform = barBump };
        bar.Children.Add(Pos(new Rectangle { Width = 1792, Height = 2, Fill = P.LineFade, RadiusX = 1, RadiusY = 1 }, 64, 870));
        AddButton(bar, "layout", () => T("home.layout") + Z.Name, CycleZoom);
        AddButton(bar, "search", () => T("home.search"), OpenSearch);
        AddButton(bar, "input", InputCaption, () => DeviceScreen.OpenInput(w));
        AddButton(bar, "gear", () => T("home.settings"), () => new SettingsCard(w).Open());
        AddButton(bar, "power", () => T("home.quit_ionea"), () => new QuitCard(w).Open());
        for (int i = 0; i < buttons.Count; i++) buttons[i].Cx = 960 + (i - (buttons.Count - 1) / 2.0) * 140;
        foreach (var b in buttons) { Canvas.SetLeft(b.El, b.Cx - b.El.Width / 2); Canvas.SetTop(b.El, 952 - b.El.Height / 2); }
        barCaption = Fonts.Text("", 26, P.Accent, FontWeights.Normal, Fonts.Display);
        var capBox = new Grid { Width = 600, RenderTransform = captionT, Opacity = 0 };
        barCaption.HorizontalAlignment = HorizontalAlignment.Center;
        capBox.Children.Add(barCaption);
        Canvas.SetTop(capBox, 1008);
        bar.Children.Add(capBox);
        Root.Children.Add(bar);

        // Button hints, bottom right: what A (and X on a game) does here, as pad buttons or keys.
        Canvas.SetRight(hints, 64); Canvas.SetTop(hints, 1000);
        top.Children.Add(hints);
        RefreshHints();

        Tick();
        w.Clock.Tick += (s, e) => Tick();
        Rebuild(FrontKey());
    }

    static UIElement Pos(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }

    void AddButton(Canvas bar, string kind, Func<string> caption, Action run)
    {
        const double d = 84;
        var g = new Grid { Width = d + 40, Height = d + 40 };
        // the shadow, face and flash are "discs" of the icon shape (Art.Disc); Tag = how far past the face, for RefreshShape
        foreach (var (r, a) in new[] { (d + 8, 0.03), (d + 4, 0.05), (d + 1, 0.08) })
        {
            var sh = Art.Disc(r, new SolidColorBrush(Color.FromArgb((byte)(a * 255), 0, 0, 0))); sh.Margin = new Thickness(0, 5, 0, 0); sh.Tag = (r - d) / 2;
            g.Children.Add(sh);
        }
        var faceDisc = Art.Disc(d, P.ButtonLit); faceDisc.Tag = 0.0;
        faceDisc.Stroke = P.ButtonRim; faceDisc.StrokeThickness = 1.25;   // lit from above, with a crisp rim
        g.Children.Add(faceDisc);
        if (kind == "input") { g.Children.Add(inputIcon); RefreshInput(); }
        else g.Children.Add(Art.Icon(kind, kind == "controller" ? 46 : 40, P.IconInk));
        var ringEl = Art.CircleRing(d, out var glow);
        Anim.Glow(glow);
        ringEl.Opacity = 0;
        var rs = new ScaleTransform(); ringEl.RenderTransform = rs; ringEl.RenderTransformOrigin = new Point(0.5, 0.5);
        g.Children.Add(ringEl);
        var flash = Art.Disc(d, Brushes.White); flash.Opacity = 0; flash.IsHitTestVisible = false; flash.Tag = 0.0;
        g.Children.Add(flash);
        var ps = new ScaleTransform(1, 1, g.Width / 2, g.Height / 2); var pt = new TranslateTransform();
        g.RenderTransform = new TransformGroup { Children = { ps, pt } };
        bar.Children.Add(g);
        buttons.Add(new OptButton { Kind = kind, Caption = caption, Run = run, El = g, Ring = ringEl, RingS = rs, PS = ps, PT = pt, Flash = flash });
    }

    // Settings > Icon shape changed: the tiles, the tile ring, the option buttons and their rings, and the
    // profile picture take the new shape at once (Art.IconShape is already set).
    public void RefreshShape()
    {
        var fresh = Art.Ring(B, out var glow);
        MoveKids(fresh, ring);
        ringGlow = glow; Anim.Glow(ringGlow);
        ReshapeRing(chipRing, 64);
        foreach (var b in buttons)
        {
            foreach (var r in b.El.Children.OfType<Rectangle>())
                if (r.Tag is double ex) r.RadiusX = r.RadiusY = Art.DiscRad(84) + ex;
            ReshapeRing((Grid)b.Ring, 84);
        }
        RefreshName();
        for (int k = built.Count - 1; k >= 0; k--) Unbuild(built[k], false);
        spare.Clear();
        Rebuild(FrontKey());
    }

    static void MoveKids(Panel from, Panel to)
    {
        var kids = from.Children.Cast<UIElement>().ToList();
        from.Children.Clear();
        to.Children.Clear();
        foreach (var k in kids) to.Children.Add(k);
    }

    static void ReshapeRing(Grid ringEl, double d)
    {
        var fresh = Art.CircleRing(d, out var glow);
        MoveKids(fresh, ringEl);
        Anim.Glow(glow);
    }

    void Tick()
    {
        clockText.Text = DateTime.Now.ToString("HH:mm", En);
    }

    public void RefreshName()
    {
        var p = w.Profiles.Active;
        nameText.Text = p?.Name ?? "";
        avatarHost.Children.Clear();
        avatarHost.Children.Add(Art.Avatar(w.Profiles, p, 64));
    }

    // Another profile: its picture and name, its own folders, order and layout, and the game it
    // played last in front (or the folder holding it).
    public void RefreshProfile()
    {
        RefreshName();
        folder = null; filter = null; sel = 0;
        Rebuild(FrontKey());
        if (onBar && barSel < buttons.Count) barCaption.Text = buttons[barSel].Caption();
    }

    // The tile to start on: the active profile's last played game, or the folder it sits in.
    string FrontKey()
    {
        var g = w.Lib.Data.Games.Where(x => !x.Removed && w.Lib.PlayedAt(x) != null).OrderByDescending(x => w.Lib.PlayedAt(x)).FirstOrDefault();
        return g == null ? null : g.Folder != null ? "folder:" + g.Folder : g.Id;
    }
    public string FrontName => FrontKey() is string k ? (k.StartsWith("folder:") ? k : w.Lib.Data.Games.First(x => x.Id == k).Name) : null;

    public bool OnChip => onChip;

    void FocusChip()
    {
        onChip = true;
        Sounds.Play("scroll");
        LiftTo(sel, false, 160);
        Anim.To(ring, UIElement.OpacityProperty, 0, 160);
        Anim.To(labelBox, UIElement.OpacityProperty, 0, 160);
        Anim.To(chipRing, UIElement.OpacityProperty, 1, 160);
        nameText.Foreground = P.Accent;
        RefreshHints();
        if (FolderMode) UpdateLabel(0, false);
    }

    void LeaveChip(bool sound = true)
    {
        onChip = false;
        if (sound) Sounds.Play("scroll");
        LiftTo(sel, TilesFocused, 200);
        Anim.To(chipRing, UIElement.OpacityProperty, 0, 120);
        nameText.Foreground = P.Text;
        Anim.To(ring, UIElement.OpacityProperty, 1, 160);
        Anim.To(labelBox, UIElement.OpacityProperty, 1, 160);
        RefreshHints();
        if (FolderMode) UpdateLabel(0, false);
    }

    public void OpenProfile() => new ProfileCard(w).Open();
    // Every colour is a shared palette brush (they animate by themselves) except the glow behind the row,
    // which is stronger in the dark look.
    public void RefreshTheme() => UpdateAmbient(380);
    public void RefreshPads()
    {
        int n = w.PadCount;
        padIcon.Visibility = padCount.Visibility = n > 0 && w.Settings.Input.PadOn ? Visibility.Visible : Visibility.Collapsed;
        padCount.Text = n.ToString();
        RefreshInput();
    }

    // ---- the Input button: one slice per device in use, split by slashes ----

    readonly Grid inputIcon = new() { Width = 84, Height = 84, IsHitTestVisible = false };
    public string InputText { get; private set; } = "";

    List<string> InputKinds()
    {
        var k = new List<string>();
        foreach (var kind in new[] { "keyboard", "pad", "mouse" })
            if (w.Settings.Input.On(kind) && (w.Devs?.Connected(kind) ?? false)) k.Add(kind);
        return k;
    }

    string InputCaption()
    {
        var k = InputKinds();
        return k.Count == 0 ? T("home.input") : T("home.input_2") + string.Join(" / ", k.Select(InputDevices.Noun));
    }

    public void RefreshInput()
    {
        if (w.Devs == null) return;
        var kinds = InputKinds();
        InputText = string.Join("/", kinds);
        inputIcon.Children.Clear();
        const double d = 84, c = d / 2;
        bool none = kinds.Count == 0;
        if (none) kinds.Add("keyboard");
        int n = kinds.Count;
        // The devices share one icon's room, split by a "/" (a small clear gap each side). Each icon
        // is drawn whole and clipped to its own side, moved out from the cut so what shows is what
        // makes it recognisable: most of the keyboard's keys, the whole mouse with its wheel.
        double size = n > 2 ? 34 : 40, reach = n > 2 ? 18 : 21;
        double[] cuts = n > 2 ? new[] { c - 12, c + 12 } : new[] { c };       // where each "/" crosses the middle
        double[] push = n > 2 ? new[] { -20.5, -3, 22 } : new[] { -14.5, 14.5 }; // each icon's centre, from c
        double k = n > 2 ? 0.24 : 0.32;   // the slash's lean (dx per dy)
        const double gap = 3.2;
        double Cut(int j, double y) => cuts[j - 1] + k * (c - y);   // cut j's x at height y
        for (int i = 0; i < n; i++)
        {
            double L(double y) => i == 0 ? 0 : Cut(i, y) + gap;
            double R(double y) => i == n - 1 ? d : Cut(i + 1, y) - gap;
            var clip = new PathGeometry(new[] { new PathFigure(new Point(L(0), 0), new PathSegment[] { new LineSegment(new Point(R(0), 0), false), new LineSegment(new Point(R(d), d), false), new LineSegment(new Point(L(d), d), false) }, true) });
            var slice = new Grid { Width = d, Height = d, Clip = clip };
            var icon = Art.Icon(kinds[i] == "pad" ? "controller" : kinds[i], kinds[i] == "pad" ? size + 2 : size, none ? P.Text2 : P.IconInk);
            icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
            if (n > 1) icon.RenderTransform = new TranslateTransform(push[i], 0);
            if (none) icon.Opacity = 0.45;
            slice.Children.Add(icon);
            inputIcon.Children.Add(slice);
        }
        for (int j = 1; j < n; j++)
            inputIcon.Children.Add(new Line { X1 = Cut(j, c - reach), Y1 = c - reach, X2 = Cut(j, c + reach), Y2 = c + reach, Stroke = P.Accent, StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        if (onBar && barSel < buttons.Count) barCaption.Text = buttons[barSel].Caption();
    }

    // ---- mouse ----

    // Test scripts: stage points at the middle of a tile / an options-bar button.
    public Point TileCentre(int i)
    {
        if (tiles[i].El is Grid e) return e.TransformToAncestor(w.Stage).Transform(new Point(e.ActualWidth / 2, e.ActualHeight / 2));
        var p = Pos(i);   // not built (off screen): where it would be
        return strip.TransformToAncestor(w.Stage).Transform(new Point(p.X + TS / 2, p.Y + TS / 2));
    }
    public Point BarCentre(int i) { var e = buttons[i].El; return e.TransformToAncestor(w.Stage).Transform(new Point(e.ActualWidth / 2, e.ActualHeight / 2)); }
    public Point ChipCentre() => avatarHost.TransformToAncestor(w.Stage).Transform(new Point(avatarHost.ActualWidth / 2, avatarHost.ActualHeight / 2));

    public bool OnMouse(MouseAct m, Point p)
    {
        if (FolderMode && FolderDrag(m, p)) return true;
        if (PressLeftMs > 0 || holdTile != null) return true;   // a press is playing / a hold is running (the button-up ends it)
        // Tiles made a moment ago (the row rebuilt as the launcher came back to the front, a container
        // opened or closed) have no size until the next layout pass, so a click then hit nothing.
        if (m != MouseAct.Move || built.Any(i => !tiles[i].El.IsArrangeValid)) w.Stage.UpdateLayout();
        bool inPanel = !FolderMode || Panel.Contains(p);
        int ti = inPanel ? built.FirstOrDefault(i => Pointer.Over(tiles[i].El, w.Stage, p), -1) : -1;   // what is on screen is built
        if (FolderMode && ti >= 0 && ti / PageN != Page) ti = -1;   // a page off to the side (only its edge can show)
        int bi = buttons.FindIndex(b => Pointer.Over(b.El, w.Stage, p));
        bool overChip = Pointer.Over(avatarHost, w.Stage, p) || Pointer.Over(nameText, w.Stage, p);
        if (overChip)
        {
            if (m == MouseAct.Down) { Sounds.Play("select"); OpenProfile(); return true; }
            // Pointing at the picture or name lights its ring, as Up from the top row does.
            if (m == MouseAct.Move) { if (!onChip) { if (onBar) LeaveBar(); FocusChip(); } return true; }
        }
        switch (m)
        {
            case MouseAct.Move:
                if ((ti >= 0 || bi >= 0) && onChip) LeaveChip(false);
                if (ti >= 0) { if (onBar) LeaveBar(); Select(ti); }
                else if (bi >= 0 && (!onBar || barSel != bi)) { barSel = bi; FocusBar(); }
                return ti >= 0 || bi >= 0;
            case MouseAct.Down:
                // The profile chip focused (Up from the top row) and a click on a tile or button without moving
                // the mouse first: the click is for what it is on, not the chip (it opened the profile card).
                if ((ti >= 0 || bi >= 0) && onChip) LeaveChip(false);
                if (ti >= 0) { if (onBar) LeaveBar(); Select(ti); OnAct(Act.Accept); }
                else if (bi >= 0) { if (!onBar || barSel != bi) { barSel = bi; FocusBar(false); } OnAct(Act.Accept); }
                else if (FolderMode && !inPanel) OnAct(Act.Back);   // a click beside the open folder closes it
                return true;
            case MouseAct.Right:
                if (ti >= 0 && Holdable(tiles[ti])) { if (onBar) LeaveBar(); Select(ti); OpenHoldMenuForTest(); return true; }
                return false;   // elsewhere: back
            case MouseAct.WheelUp: OnAct(FolderMode ? Act.PageLeft : Act.Left); return true;
            case MouseAct.WheelDown: OnAct(FolderMode ? Act.PageRight : Act.Right); return true;
        }
        return true;
    }

    // Swiping an open folder sideways: the left button down in the panel, then moved. The pages follow
    // the pointer (stretching past the first and last), and on release land on the next / previous
    // page or spring back. A press that turns into a swipe never starts or holds the game under it.
    Point dragFrom;
    double dragScroll;
    bool dragArmed, dragging;
    public bool Dragging => dragging;   // test scripts

    bool FolderDrag(MouseAct m, Point p)
    {
        switch (m)
        {
            case MouseAct.Down:
                dragArmed = Panel.Contains(p) && Pages > 1 && PressLeftMs <= 0; dragging = false;
                dragFrom = p; dragScroll = scroll;
                return false;   // the press goes on as usual
            case MouseAct.Move:
            {
                if (!dragArmed || !w.Mouse.Held) { dragArmed = dragging = false; return false; }
                double dx = p.X - dragFrom.X;
                if (!dragging && Math.Abs(dx) < 22) return false;
                if (!dragging) { dragging = true; EndHold(true); }
                double max = (Pages - 1) * PW, to = dragScroll - dx;
                if (to < 0) to /= 3; else if (to > max) to = max + (to - max) / 3;
                Anim.Set(stripT, TranslateTransform.XProperty, Left0 - to);
                return true;
            }
            case MouseAct.Up:
            {
                if (!dragging) { dragArmed = false; return false; }
                dragArmed = dragging = false;
                double d = p.X - dragFrom.X;
                int page = Math.Clamp(Page + (d < -90 ? 1 : d > 90 ? -1 : 0), 0, Pages - 1);
                int target = Math.Min(page * PageN + sel % PageN, tiles.Count - 1);
                if (target != sel) Select(target);   // slides the strip on to that page
                else Anim.To(stripT, TranslateTransform.XProperty, Left0 - scroll, 300, Anim.Out);   // springs back
                return true;
            }
        }
        return false;
    }

    // ---- building ----

    // Timing for big libraries: how long a rebuild takes, until it is drawn, and until its covers are read.
    // "home rebuild" / "home covers" lines in the log; CoverReads / CoverCached for the "mem" script command.
    static readonly System.Diagnostics.Stopwatch rebuildWatch = new();
    static int coverPending;
    static int coverReads;
    public static int CoverReads => coverReads;   // covers actually read (a read skipped because its tile went is not)
    public static int CoverCached => coverCache.Count;

    public void Rebuild(string selectId)
    {
        rebuildWatch.Restart();
        string keep = selectId ?? (sel < tiles.Count ? tiles[sel].Game?.Id : null);
        bool reuse = PressLeftMs <= 0;   // a press's afterimage shows its tile's face: that one is not dressed as another game
        for (int k = built.Count - 1; k >= 0; k--) Unbuild(built[k], reuse);
        strip.Children.Clear();
        tiles.Clear();
        string order = w.Settings.Order;
        if (filter != null) foreach (var g in w.Lib.Search(filter, order)) tiles.Add(new Tile { Game = g });
        else if (folder != null) foreach (var g in w.Lib.FolderGames(folder, order)) tiles.Add(new Tile { Game = g });
        else
        {
            // "Last played first": a folder sits where its most recently played game would, so whatever
            // this profile played last is at the start. A to Z: folders first.
            var folders = w.Lib.Folders();
            var home = w.Lib.HomeGames(order);
            if (order == "name") { foreach (string f in folders) tiles.Add(new Tile { Folder = f }); foreach (var g in home) tiles.Add(new Tile { Game = g }); }
            else
            {
                var played = w.Lib.FolderPlayedAt();   // one pass over the library, not one per folder
                DateTime FolderTime(string f) => played.TryGetValue(f, out var t) ? t : DateTime.MinValue;
                var items = folders.Select(f => (time: FolderTime(f), folder: f, game: (Game)null))
                    .Concat(home.Select(g => (time: w.Lib.PlayedAt(g) ?? DateTime.MinValue, folder: (string)null, game: g)))
                    .Select((x, i) => (x.time, x.folder, x.game, i))     // never played: folders, then games, as before
                    .OrderByDescending(x => x.time).ThenBy(x => x.i);
                foreach (var x in items) tiles.Add(new Tile { Folder = x.folder, Game = x.game });
            }
        }
        // The open game comes first, like the suspended game on a Switch HOME Menu. On the home row it is
        // pulled out of its folder (it stays in the folder too); in a search it stays where it matched.
        if (w.GameMenu.Game is Game run && filter == null)
        {
            int at = tiles.FindIndex(t => t.Game?.Id == run.Id);
            if (at > 0) { var t = tiles[at]; tiles.RemoveAt(at); tiles.Insert(0, t); }
            else if (at < 0 && folder == null && !run.Removed) tiles.Insert(0, new Tile { Game = run });
        }
        runningId = w.GameMenu.Game?.Id;
        tiles.Add(new Tile());                                  // "+ Add game" is always last
        int n = tiles.Count - 1;
        string games = n == 0 ? T("home.no_games") : N("settings.games", n);
        viewText.Text = filter != null ? T("home.b_clears", filter.Length == 0 ? T("home.search_type_a_name") : T("home.search_2", filter, games))
                      : "";   // an open folder shows its name over the glass panel instead
        strip.Children.Add(labelBox);   // the tiles near the screen go in before these as Layout builds them
        strip.Children.Add(ring);
        int idx = keep == null ? -1 : keep.StartsWith("folder:") ? tiles.FindIndex(t => "folder:" + t.Folder == keep) : tiles.FindIndex(t => t.Game?.Id == keep);
        sel = idx >= 0 ? idx : Math.Min(sel, tiles.Count - 1);
        ApplyMode();
        Layout(false);
        RefreshPads();
        RefreshRunning();
        RefreshHints();
        long ms = rebuildWatch.ElapsedMilliseconds;
        int count = tiles.Count, made = built.Count;
        w.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            Log.Write($"home rebuild: {count} tiles, {made} built, {ms} ms, drawn after {rebuildWatch.ElapsedMilliseconds} ms, {coverPending} covers to read"));
    }

    // ---- the open game: first on the row, a badge on its tile ("Playing", "Paused", "Closing…"), after its name too, A says Resume ----
    string runningId;
    public string RunningBadge => tiles.FirstOrDefault(t => t.Badge?.Visibility == Visibility.Visible) is Tile bt ? bt.Game.Name + "|" + ((bt.Badge.Tag as string) ?? "")
        : tiles.FirstOrDefault(t => t.El == null && IsRunning(t)) is Tile off ? off.Game.Name + "|" + RunWord : "none";   // test scripts (off screen: what its badge will say)

    bool IsRunning(Tile t) => t.Game != null && t.Game.Id == w.GameMenu.Game?.Id;
    string RunWord => w.GameMenu.Closing ? T("home.closing") : w.GameMenu.Paused ? T("home.paused") : T("home.playing");
    // For tests: the part of the cover a game's home tile shows ("plain" = today's fill), x|y|w|h in the picture's units.
    public string TileView(string name)
    {
        var t = tiles.FirstOrDefault(x => x.Game?.Name == name);
        if (t == null) return null;
        var shown = t.Face?.Background;
        if (t.Face == null)   // off screen, not built: what its face will show when it is
        {
            string file = w.Lib.CoverPath(t.Game);
            var bmp = CachedCover(t.Game) ?? (file != null && System.IO.File.Exists(file) ? Art.LoadCover(file, CoverView.Zoom(t.Game) > 1.001 ? 0 : (int)B) : null);
            shown = bmp != null ? CoverView.Brush(bmp, t.Game) : null;
        }
        if (shown is not ImageBrush ib) return "no picture";
        if (ib.Stretch == Stretch.UniformToFill) return "plain";
        var v = ib.Viewbox;
        return FormattableString.Invariant($"{v.X:0.00}|{v.Y:0.00}|{v.Width:0.00}|{v.Height:0.00}");
    }
    public BitmapSource CoverOf(Game g) => tiles.FirstOrDefault(t => t.Game?.Id == g.Id)?.Cover ?? CachedCover(g);   // the closing screen

    void GameMenuChanged() => w.Dispatcher.BeginInvoke(() =>
    {
        // A game that started or ended moves the row (it goes to the front / back to its place).
        if (w.GameMenu.Game?.Id != runningId) Rebuild(null);
        else { RefreshRunning(); RefreshHints(); }
    });

    void RefreshRunning()
    {
        foreach (int i in built) RefreshBadge(tiles[i]);
        if (!onBar && !onChip && sel < tiles.Count && tiles[sel].Game is Game g)
            label.Text = IsRunning(tiles[sel]) ? $"{g.Name}  ·  {RunWord}" : g.Name;
    }

    void RefreshBadge(Tile t)
    {
        if (t.Badge == null) return;
        if (!IsRunning(t)) { t.Badge.Visibility = Visibility.Collapsed; t.Badge.Child = null; t.Badge.Tag = null; return; }
        double k = TS >= B ? 1 : TS >= 270 ? 1.3 : 2.1;   // smaller tiles keep a readable badge
        string word = RunWord;
        if (t.Badge.Visibility != Visibility.Visible || t.Badge.Tag as string != word)
        {
            t.Badge.Tag = word;
            t.Badge.Child = BadgeFace(word);
            t.Badge.Visibility = Visibility.Visible;
            Anim.FromTo(t.Badge, UIElement.OpacityProperty, 0, 1, 220);
        }
        t.Badge.HorizontalAlignment = HorizontalAlignment.Left; t.Badge.VerticalAlignment = VerticalAlignment.Top;
        t.Badge.Margin = new Thickness(18);
        t.Badge.RenderTransform = new ScaleTransform(k, k);
    }

    // A pill in the ring's blue-violet: a pause sign (two bars) or a beating dot, then the word.
    FrameworkElement BadgeFace(string word)
    {
        bool paused = word == "Paused";
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement sign;
        if (paused)
        {
            var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            bars.Children.Add(new Border { Width = 7, Height = 22, CornerRadius = new CornerRadius(2), Background = Brushes.White, Margin = new Thickness(0, 0, 5, 0) });
            bars.Children.Add(new Border { Width = 7, Height = 22, CornerRadius = new CornerRadius(2), Background = Brushes.White });
            sign = bars;
        }
        else
        {
            var dot = new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            Anim.Forever(dot, UIElement.OpacityProperty, 0.35, 1, 800);
            sign = dot;
        }
        sign.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(sign);
        var tx = Fonts.Text(word, 26, Brushes.White, FontWeights.SemiBold);
        tx.VerticalAlignment = VerticalAlignment.Center; tx.Margin = new Thickness(0, 0, 0, 2);
        row.Children.Add(tx);
        return new Border
        {
            Background = new LinearGradientBrush(new GradientStopCollection { P.FillA, P.FillB }, 0) { Opacity = 240 / 255.0 },   // the colour preset's fill
            CornerRadius = new CornerRadius(24), Padding = new Thickness(16, 7, 20, 8), Child = row,
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1.5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.3, Color = Colors.Black },
        };
    }

    // ---- building only what is near the screen ----

    // Which tiles to build: those on screen at this scroll plus margin columns each side. Tiles fill each
    // column top to bottom (rows per column); column c sits at c * step in the strip, and the strip shows
    // from left - scroll on a screen width wide. First..Last, both in; Last < First: none.
    public static (int First, int Last) RowRange(int count, int rows, double step, double tile, double scroll, double left, double width, int margin)
    {
        if (count <= 0 || rows <= 0 || step <= 0) return (0, -1);
        int cols = (count + rows - 1) / rows;
        int c0 = (int)Math.Floor((scroll - left - tile) / step) + 1 - margin;   // the first column whose right edge is past the screen's left
        int c1 = (int)Math.Ceiling((scroll - left + width) / step) - 1 + margin;   // the last whose left edge is before the screen's right
        c0 = Math.Max(0, c0); c1 = Math.Min(cols - 1, c1);
        return c1 < c0 ? (0, -1) : (c0 * rows, Math.Min(count - 1, (c1 + 1) * rows - 1));
    }

    // An open folder: the page shown and margin pages each side (a swipe shows the next page's edge).
    public static (int First, int Last) PageRange(int count, int page, int perPage, int margin) =>
        count <= 0 ? (0, -1) : (Math.Max(0, (page - margin) * perPage), Math.Min(count - 1, (page + margin + 1) * perPage - 1));

    (int First, int Last) Wanted(int extra) => FolderMode
        ? PageRange(tiles.Count, Page, PageN, 1 + extra)
        : RowRange(tiles.Count, Z.Rows, Z.T + Z.G, Z.T, scroll, LM, 1920, MarginCols + extra);

    // Build the tiles near the screen at the current scroll. from: a zoom change is under way and new
    // tiles start where that zoom had them, so they move with the rest. Tiles that went off screen are
    // dropped a little later (Trim), once the strip has stopped moving, or at once when too many pile up.
    void BuildNear(ZoomDef from = null)
    {
        var (a, b) = Wanted(0);
        for (int i = a; i <= b; i++) Build(i, from);
        if (trimTimer == null)
        {
            trimTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            trimTimer.Tick += (s, e) => Trim(2);
        }
        trimTimer.Stop();
        if (built.Count > 3 * (b - a + 1) + 40) Trim(FolderMode ? 2 : (int)(VW / (Z.T + Z.G)) + 2);   // keeps a screen each side: what is still sliding past
        else trimTimer.Start();
    }

    void Trim(int extra)
    {
        trimTimer?.Stop();
        var (a, b) = Wanted(extra);
        for (int k = built.Count - 1; k >= 0; k--)
        {
            int i = built[k];
            if ((i < a || i > b) && i != sel && tiles[i] != holdTile) Unbuild(i, true);
        }
    }

    void Build(int i, ZoomDef from = null)
    {
        var t = tiles[i];
        if (t.El != null) return;
        t.Reads = new();
        if (t.Folder != null) BuildFolder(t);
        else if (t.Game != null) BuildGame(t);
        else { MakeParts(t, false); BuildAddFace(t.Face); }
        int at = ~built.BinarySearch(i);
        built.Insert(at, i);
        strip.Children.Insert(at, t.El);
        t.El.RenderTransform = t.Group;
        var p = from != null ? PosOf(i, from) : Pos(i);
        double k = (from?.T ?? TS) / B;
        Anim.Set(t.S, ScaleTransform.ScaleXProperty, k); Anim.Set(t.S, ScaleTransform.ScaleYProperty, k);
        Anim.Set(t.T, TranslateTransform.XProperty, p.X); Anim.Set(t.T, TranslateTransform.YProperty, p.Y);
        Anim.Set(t.In, TranslateTransform.XProperty, 0);
        LiftTo(i, i == sel && TilesFocused, 0);
        if (FolderMode)
        {
            t.Name = Fonts.Text(t.Game?.Name ?? T("common.add"), 21, i == sel && !onBar && !onChip ? P.Accent : P.Text);
            t.Name.Width = FCol - 8; t.Name.TextAlignment = TextAlignment.Center;
            t.Name.TextTrimming = TextTrimming.CharacterEllipsis; t.Name.IsHitTestVisible = false;
            Canvas.SetLeft(t.Name, p.X + FT / 2 - t.Name.Width / 2); Canvas.SetTop(t.Name, p.Y + FT + 8);
            strip.Children.Insert(strip.Children.IndexOf(labelBox), t.Name);
        }
        RefreshBadge(t);
    }

    // Off the strip: its cover reads are dropped, and a plain game tile is kept to be dressed as another.
    void Unbuild(int i, bool reuse)
    {
        var t = tiles[i];
        if (t.El == null) return;
        int at = built.BinarySearch(i);
        if (at >= 0) built.RemoveAt(at);
        t.Reads?.Cancel(); t.Reads = null;
        if (t.Name != null) strip.Children.Remove(t.Name);
        bool here = t.El.Parent == strip;   // not when a closing folder has lifted it into its own layer
        if (here) strip.Children.Remove(t.El);
        if (reuse && here && t.Title != null && t.El.Children.Count == 5 && spare.Count < SpareMax && holdTile != t)
        {
            foreach (var e in new UIElement[] { t.El, t.Face, t.Flash }) e.BeginAnimation(UIElement.OpacityProperty, null);
            t.El.Opacity = 1; t.Face.Opacity = 1; t.Flash.Opacity = 0;
            t.El.RenderTransform = null; t.Face.Background = null; t.Face.Child = null;
            t.Badge.Visibility = Visibility.Collapsed; t.Badge.Child = null; t.Badge.Tag = null;
            spare.Push(new Tile { El = t.El, Face = t.Face, Flash = t.Flash, Badge = t.Badge, Title = t.Title });
        }
        t.El = null; t.Face = null; t.Flash = null; t.Badge = null; t.Name = null; t.Title = null; t.Cover = null;
    }

    // A tile's shadow, face, flash and (on a game) the gloss and the running badge.
    void MakeParts(Tile t, bool game)
    {
        double rad = Art.Corner(16.28 * B / 297);
        var el = new Grid { Width = B, Height = B };
        var shadow = new Image { Source = Art.TileShadow(B, rad, out double pad), IsHitTestVisible = false };
        shadow.Width = shadow.Height = B + 2 * pad; shadow.Margin = new Thickness(-pad);
        el.Children.Add(shadow);
        var face = new Border { CornerRadius = new CornerRadius(rad), BorderThickness = new Thickness(1.5), BorderBrush = P.TileEdge };
        el.Children.Add(face);
        if (game) el.Children.Add(new Border { CornerRadius = new CornerRadius(rad - 1.5), Margin = new Thickness(1.5), Background = Gloss, BorderBrush = GlossRim, BorderThickness = new Thickness(1.25), IsHitTestVisible = false });
        t.Flash = new Border { CornerRadius = new CornerRadius(rad), Background = Brushes.White, Opacity = 0, IsHitTestVisible = false };
        el.Children.Add(t.Flash);
        if (game) { t.Badge = new Border { Visibility = Visibility.Collapsed, IsHitTestVisible = false }; el.Children.Add(t.Badge); }
        t.El = el; t.Face = face;
    }

    void BuildGame(Tile t)
    {
        var g = t.Game;
        if (spare.TryPop(out var old)) { t.El = old.El; t.Face = old.Face; t.Flash = old.Flash; t.Badge = old.Badge; t.Title = old.Title; }
        else
        {
            MakeParts(t, true);
            t.Title = Fonts.Text("", 40, P.Text, FontWeights.Normal, Fonts.Display);
            t.Title.TextWrapping = TextWrapping.Wrap; t.Title.TextAlignment = TextAlignment.Center;
            t.Title.VerticalAlignment = VerticalAlignment.Center; t.Title.Margin = new Thickness(30);
        }
        var face = t.Face;
        face.Background = CoverColour.Fill(g);
        t.Title.Text = g.Name; t.Title.Foreground = CoverColour.Ink(g);
        face.Child = t.Title;
        LoadCover(g, (bmp, fresh) =>
        {
            if (t.Face != face) return;   // gone off screen while it was read
            t.Cover = bmp; face.Child = null; face.Background = CoverView.Brush(bmp, g);
            if (fresh) Anim.FromTo(face, UIElement.OpacityProperty, 0, 1, 240);   // first time: the picture fades in
            if (sel < tiles.Count && tiles[sel] == t) UpdateAmbient(fresh ? 650 : 0);
        }, t.Reads.Token);
    }

    // The finish on every game tile: a faint gloss from the top and a touch of depth at the bottom, and an
    // inner rim that catches the light along the top edge (inside the tile's dark edge).
    static readonly Brush Gloss = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromArgb(30, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.4),
        new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.68), new GradientStop(Color.FromArgb(30, 0, 0, 0), 1),
    }, 90));
    static readonly Brush GlossRim = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromArgb(110, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.3),
        new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.75), new GradientStop(Color.FromArgb(26, 0, 0, 0), 1),
    }, 90));
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    // ---- the finish: lift, the glow behind the row, the screen's edges ----

    bool TilesFocused => !onBar && !onChip;

    void LiftTo(int i, bool up, double ms)
    {
        if (i < 0 || i >= tiles.Count || tiles[i].El == null) return;   // a tile is lifted (or not) as it is built
        var s = tiles[i].Lift; double v = up ? LiftK : 1;
        if (ms <= 0) { Anim.Set(s, ScaleTransform.ScaleXProperty, v); Anim.Set(s, ScaleTransform.ScaleYProperty, v); return; }
        Anim.To(s, ScaleTransform.ScaleXProperty, v, ms); Anim.To(s, ScaleTransform.ScaleYProperty, v, ms);
    }

    void ApplyLifts()
    {
        foreach (int i in built) LiftTo(i, i == sel && TilesFocused, 0);
    }

    // Two colours that carry a cover's mood: the most colourful part of its top half and of its bottom half,
    // made clear and bright enough to glow. Grey pictures take the colour preset's.
    (Color a, Color b) MoodOf(Tile t)
    {
        Color pa = Palette.Cyan, pb = Palette.Violet;
        if (t.Game == null) return t.Folder != null ? (pa, pb) : (Palette.Blue, pb);
        if (t.Cover != null)
        {
            if (!moods.TryGetValue(t.Cover, out var m))
            {
                m = Moods(t.Cover, pa, pb);
                moods.AddOrUpdate(t.Cover, m);
            }
            return (m.Item1, m.Item2);
        }
        var c = CoverColour.Of(t.Game);
        if (c.A == null) return (Palette.Blue, pb);
        return (Vivid(Palette.Hex(c.A)), Vivid(Palette.Hex(c.B ?? c.A)));
    }

    static Tuple<Color, Color> Moods(BitmapSource bmp, Color fallA, Color fallB)
    {
        try
        {
            const int n = 24;
            var small = new FormatConvertedBitmap(new TransformedBitmap(bmp, new ScaleTransform((double)n / bmp.PixelWidth, (double)n / bmp.PixelHeight)), PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);
            Color Half(int y0, int y1, Color fall)
            {
                double r = 0, g = 0, b = 0, sum = 0, satSum = 0, count = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        double B = px[i] / 255.0, G = px[i + 1] / 255.0, R = px[i + 2] / 255.0, A = px[i + 3] / 255.0;
                        double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                        double sat = max <= 0 ? 0 : (max - min) / max;
                        double wt = A * (sat * sat * max + 0.01);
                        r += R * wt; g += G * wt; b += B * wt; sum += wt; satSum += sat * A; count += A;
                    }
                if (sum <= 0 || count <= 0 || satSum / count < 0.1) return fall;
                return Vivid(Color.FromRgb((byte)(r / sum * 255), (byte)(g / sum * 255), (byte)(b / sum * 255)));
            }
            var top = Half(0, h / 2, fallA);
            return Tuple.Create(top, Half(h / 2, h, top));
        }
        catch (Exception e) { Log.Write("cover mood: " + e.Message); return Tuple.Create(fallA, fallB); }
    }

    // The same hue, saturated and bright enough to read as coloured light (not grey) at low opacity.
    static Color Vivid(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double hue = d <= 0 ? 0 : max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        double s = Math.Clamp(max <= 0 ? 0 : d / max, 0.45, 0.9), v = Math.Clamp(max, 0.7, 0.95);
        hue = (hue * 60 + 360) % 360;
        double cc = v * s, x = cc * (1 - Math.Abs(hue / 60 % 2 - 1)), m = v - cc;
        (double R, double G, double B) = hue < 60 ? (cc, x, 0.0) : hue < 120 ? (x, cc, 0.0) : hue < 180 ? (0.0, cc, x) : hue < 240 ? (0.0, x, cc) : hue < 300 ? (x, 0.0, cc) : (cc, 0.0, x);
        return Color.FromRgb((byte)Math.Round((R + m) * 255), (byte)Math.Round((G + m) * 255), (byte)Math.Round((B + m) * 255));
    }

    // The glow behind the row takes the selected tile's colours and drifts after it.
    void UpdateAmbient(double ms)
    {
        if (sel < 0 || sel >= tiles.Count) return;
        var (a, b) = MoodOf(tiles[sel]);
        double ka = P.Dark ? 0.5 : 0.26, kb = P.Dark ? 0.36 : 0.18;
        void Tint(SolidColorBrush s, Color c, double k)
        {
            var to = Color.FromArgb((byte)Math.Round(k * 255), c.R, c.G, c.B);
            if (ms <= 0) { s.BeginAnimation(SolidColorBrush.ColorProperty, null); s.Color = to; }
            else s.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(Anim.Reduced ? ms * 0.55 : ms)) { EasingFunction = Anim.InOut });
        }
        Tint(ambA, a, ka); Tint(ambB, b, kb);
        double cx = Math.Clamp(Left0 + Pos(sel).X - scroll + TS / 2, 360, 1560);
        double bx = 960 + (960 - cx) * 0.6;   // the second glow sits low on the other side
        if (ms <= 0) { Anim.Set(ambAT, TranslateTransform.XProperty, cx); Anim.Set(ambBT, TranslateTransform.XProperty, bx); }
        else { Anim.To(ambAT, TranslateTransform.XProperty, cx, ms * 1.3, Anim.InOut); Anim.To(ambBT, TranslateTransform.XProperty, bx, ms * 1.3, Anim.InOut); }
        if (ambient.Opacity < 1 && ambient.Tag == null) { ambient.Tag = true; Anim.To(ambient, UIElement.OpacityProperty, 1, 700); }
    }

    // The row fades out at a screen edge only where more of it runs on past that edge.
    void RefreshEdges(double ms)
    {
        bool f = FolderMode, left = !f && scroll > 0.5, right = !f && Cols(Z) * (Z.T + Z.G) - Z.G - scroll > VW + 0.5;
        void Edge(GradientStop s, bool fade)
        {
            var to = fade ? Color.FromArgb(0, 0, 0, 0) : Colors.Black;
            if (ms <= 0) { s.BeginAnimation(GradientStop.ColorProperty, null); s.Color = to; }
            else s.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(Anim.Reduced ? ms * 0.55 : ms)));
        }
        Edge(edgeL, left); Edge(edgeR, right);
    }

    // A game's cover: from the cache at once when it has been seen (a rebuild never flashes the name
    // tiles), else read in the background. fresh = it was just read.
    // cancel: the tile went off screen; a read not started yet is skipped, one done is kept in the cache.
    public void LoadCover(Game g, Action<BitmapSource, bool> show, System.Threading.CancellationToken cancel = default)
    {
        string file = w.Lib.CoverPath(g);
        if (file == null) return;
        DateTime at = System.IO.File.Exists(file) ? System.IO.File.GetLastWriteTimeUtc(file) : default;
        string key = CoverKey(g, file, out bool full);
        if (coverCache.TryGet(key, out var hit) && hit.at == at) { show(hit.bmp, false); return; }
        coverPending++;
        Task.Run(() =>
        {
            if (cancel.IsCancellationRequested) return null;
            System.Threading.Interlocked.Increment(ref coverReads);
            return Art.LoadCover(file, full ? 0 : (int)B);
        }).ContinueWith(r =>
        {
            if (--coverPending == 0) Log.Write($"home covers: all read {rebuildWatch.ElapsedMilliseconds} ms after the last rebuild, {CoverReads} read so far");
            if (r.Result == null) return;
            coverCache.Set(key, (at, r.Result));
            if (!cancel.IsCancellationRequested) show(r.Result, true);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // a zoomed-in cover is read at full size, so the part shown stays sharp
    static string CoverKey(Game g, string file, out bool full)
    {
        full = CoverView.Zoom(g) > 1.001;
        return full ? file + "|full" : file;
    }

    // A cover already read, if it still is in the cache (a tile off screen keeps none of its own).
    BitmapSource CachedCover(Game g)
    {
        string file = w.Lib.CoverPath(g);
        return file != null && coverCache.Peek(CoverKey(g, file, out _), out var hit) ? hit.bmp : null;
    }

    // A folder on home: a frosted glass square holding its first nine games' covers, three by three
    // (the rest are on the open folder's next pages). Its name is the label over it, as a game's is.
    void BuildFolder(Tile t)
    {
        MakeParts(t, false);
        string name = t.Folder;
        var face = t.Face;
        double rad = Art.Corner(16.28 * B / 297), edge = 2, inner = B - 2 * edge;
        t.Face.Background = new LinearGradientBrush(new GradientStopCollection { P.FolderA, P.FolderB }, new Point(0, 0), new Point(1, 1));
        t.Face.BorderBrush = new LinearGradientBrush(new GradientStopCollection { P.RimA, P.RimB }, 90); t.Face.BorderThickness = new Thickness(edge);
        // Glass 2: a second soft shadow lifts the folder a little more than a game tile
        var lift = new Image { Source = Art.TileShadow(B, rad, out double sp), IsHitTestVisible = false, Width = B + 2 * sp, Height = B + 2 * sp, Margin = new Thickness(-sp, -sp + sp * 0.35, -sp, -sp - sp * 0.35) };
        t.El.Children.Insert(1, lift);
        var g = new Grid();
        // frosted glass: the covers inside, blurred hard, glow through a tint (one small picture made
        // when the covers have loaded, not a live blur), then the sheen and a thin inner highlight
        var frostTile = new Border { CornerRadius = new CornerRadius(rad - edge) };
        g.Children.Add(frostTile);
        g.Children.Add(new Border { CornerRadius = new CornerRadius(rad - edge), Background = new LinearGradientBrush(new GradientStopCollection { P.FrostA, P.FrostB }, new Point(0, 0), new Point(1, 1)) });
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(rad - edge, rad - edge, 0, 0), Height = inner * 0.46, VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush(new GradientStopCollection { P.ShineA, P.ShineB }, 90),
        });
        g.Children.Add(new Border { CornerRadius = new CornerRadius(rad - edge), BorderBrush = new LinearGradientBrush(new GradientStopCollection { P.HiA, P.HiB }, 90), BorderThickness = new Thickness(1) });
        const double pad = 42, gap = 17;
        double cell = (inner - 2 * pad - 2 * gap) / 3;
        var minis = new Canvas { Width = inner, Height = inner };
        var games = w.Lib.FolderGames(name, w.Settings.Order).Take(PageN).ToList();
        var covers = new BitmapSource[games.Count];
        bool frostDue = false;
        void Refrost()
        {
            if (frostDue) return;
            frostDue = true;   // several covers arriving together make one picture
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                frostDue = false;
                try
                {
                    const int n = 96;
                    double k = n / inner;
                    var mosaic = Art.Render(n, n, dc =>
                    {
                        for (int i = 0; i < covers.Length; i++)
                            if (covers[i] != null)
                                dc.DrawRectangle(CoverView.Brush(covers[i], games[i]), null, new Rect((pad + i % 3 * (cell + gap) - gap) * k, (pad + i / 3 * (cell + gap) - gap) * k, (cell + 2 * gap) * k, (cell + 2 * gap) * k));
                    });
                    frostTile.Background = new ImageBrush(Art.Frost(mosaic, 48, 48, 6)) { Stretch = Stretch.Fill };
                }
                catch (Exception e) { Log.Write("folder frost: " + e.Message); }
            });
        }
        for (int i = 0; i < games.Count; i++)
        {
            var mini = new Border
            {
                Width = cell, Height = cell, CornerRadius = new CornerRadius(Art.Corner(cell * 0.21)),
                Background = CoverColour.Fill(games[i]), BorderBrush = P.TileEdge, BorderThickness = new Thickness(1),
            };
            var letter = Fonts.Text(games[i].Name.Length > 0 ? games[i].Name[..1].ToUpperInvariant() : "?", cell * 0.42, CoverColour.Ink2(games[i]), FontWeights.SemiBold, Fonts.Display);
            letter.HorizontalAlignment = HorizontalAlignment.Center; letter.VerticalAlignment = VerticalAlignment.Center;
            mini.Child = letter;
            int at = i;
            LoadCover(games[i], (bmp, fresh) =>
            {
                if (t.Face != face) return;   // gone off screen while it was read
                mini.Child = null; mini.Background = CoverView.Brush(bmp, games[at]);
                if (fresh) Anim.FromTo(mini, UIElement.OpacityProperty, 0, 1, 240);
                covers[at] = bmp; Refrost();
            }, t.Reads.Token);
            Canvas.SetLeft(mini, pad + i % 3 * (cell + gap)); Canvas.SetTop(mini, pad + i / 3 * (cell + gap));
            minis.Children.Add(mini);
        }
        g.Children.Add(minis);
        t.Face.Child = g;
        FolderLook = $"{name}: {games.Count} of {w.Lib.FolderGames(name, "name").Count}";
    }

    void BuildAddFace(Border face)
    {
        face.Background = new LinearGradientBrush(new GradientStopCollection { P.SoftA, P.SoftB }, 90);
        var g = new Grid();
        double d = B * 0.36;
        var disc = new Grid { Width = d, Height = d, VerticalAlignment = VerticalAlignment.Center };
        // The disc follows Icon shape (round stays the ellipses it always was).
        bool round = Art.IconShape is not ("rounded" or "square");
        Shape Part(double r) => round ? new Ellipse() : new Rectangle { RadiusX = r, RadiusY = r };
        var shadow = Part(Art.DiscRad(d) + 6); shadow.Fill = new SolidColorBrush(Color.FromArgb(18, 0x2B, 0x3A, 0x60)); shadow.Margin = new Thickness(-6, 2, -6, -12);
        disc.Children.Add(shadow);
        var body = Part(Art.DiscRad(d)); body.Fill = new LinearGradientBrush(new GradientStopCollection { P.DiscA, P.DiscB }, 45);
        disc.Children.Add(body);
        var sheen = Part(Art.DiscRad(d) * 0.84);
        sheen.Fill = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        sheen.Margin = new Thickness(d * 0.08, d * 0.04, d * 0.08, d * 0.46);
        disc.Children.Add(sheen);
        disc.Children.Add(Art.Icon("plus", d * 0.62, Brushes.White));
        g.Children.Add(disc);   // no words on it: the label over it and the hint say what it does
        face.Child = g;
    }

    // ---- geometry ----

    static double TopOf(ZoomDef z) => AreaTop + (AreaH - (z.L + z.Rows * z.T + (z.Rows - 1) * z.L)) / 2 + z.L;
    static Point PosOf(int i, ZoomDef z) => new(i / z.Rows * (z.T + z.G), TopOf(z) + i % z.Rows * (z.T + z.L));
    int Cols(ZoomDef z) => (tiles.Count + z.Rows - 1) / z.Rows;

    double ScrollFor(int i, ZoomDef z, double current)
    {
        double x = PosOf(i, z).X, s = current;
        if (x < s) s = x;
        if (x + z.T > s + VW) s = x + z.T - VW;
        double max = Math.Max(0, Cols(z) * (z.T + z.G) - z.G - VW);
        return Math.Clamp(s, 0, max);
    }

    // Place everything for the current zoom and selection (animated or not).
    void Layout(bool animate, double ms = 420)
    {
        var z = Z;
        scroll = ScrollTo(sel, 0);
        BuildNear(animate && !FolderMode && laidZoom != null && laidZoom != z ? laidZoom : null);
        laidZoom = z;
        foreach (int i in built)
        {
            var t = tiles[i]; var p = Pos(i); double s = TS / B;
            if (t.Name != null) { Canvas.SetLeft(t.Name, p.X + FT / 2 - t.Name.Width / 2); Canvas.SetTop(t.Name, p.Y + FT + 8); }
            if (animate)
            {
                Anim.To(t.S, ScaleTransform.ScaleXProperty, s, ms, Anim.InOut); Anim.To(t.S, ScaleTransform.ScaleYProperty, s, ms, Anim.InOut);
                Anim.To(t.T, TranslateTransform.XProperty, p.X, ms, Anim.InOut); Anim.To(t.T, TranslateTransform.YProperty, p.Y, ms, Anim.InOut);
            }
            else
            {
                Anim.Set(t.S, ScaleTransform.ScaleXProperty, s); Anim.Set(t.S, ScaleTransform.ScaleYProperty, s);
                Anim.Set(t.T, TranslateTransform.XProperty, p.X); Anim.Set(t.T, TranslateTransform.YProperty, p.Y);
            }
        }
        if (animate) Anim.To(stripT, TranslateTransform.XProperty, Left0 - scroll, ms, Anim.InOut);
        else Anim.Set(stripT, TranslateTransform.XProperty, Left0 - scroll);
        MoveRing(animate ? ms : 0, Anim.InOut);
        ApplyLifts();
        UpdateAmbient(animate ? ms : 0);
        RefreshEdges(animate ? ms : 0);
        label.FontSize = z.Label;
        UpdateLabel(animate ? ms : 0, true);
        foreach (var b in buttons) { }
        if (onBar) FocusBar(false);
    }

    void MoveRing(double ms, System.Windows.Media.Animation.IEasingFunction ease = null)
    {
        var p = Pos(sel); double s = TS / B;
        if (ms <= 0)
        {
            Anim.Set(ringT, TranslateTransform.XProperty, p.X); Anim.Set(ringT, TranslateTransform.YProperty, p.Y);
            Anim.Set(ringS, ScaleTransform.ScaleXProperty, s); Anim.Set(ringS, ScaleTransform.ScaleYProperty, s);
            return;
        }
        Anim.To(ringT, TranslateTransform.XProperty, p.X, ms, ease); Anim.To(ringT, TranslateTransform.YProperty, p.Y, ms, ease);
        Anim.To(ringS, ScaleTransform.ScaleXProperty, s, ms, ease); Anim.To(ringS, ScaleTransform.ScaleYProperty, s, ms, ease);
    }

    void UpdateLabel(double ms, bool fade)
    {
        if (FolderMode)
        {
            // In a folder every game has its name under it; the selected one's is lit.
            foreach (int i in built) if (tiles[i].Name is TextBlock n) n.Foreground = i == sel && !onBar && !onChip ? P.Accent : P.Text;
            return;
        }
        var z = Z; var p = PosOf(sel, z);
        var t = tiles[sel];
        label.Text = t.Game == null ? t.Folder ?? AddWord : IsRunning(t) ? $"{t.Game.Name}  ·  {RunWord}" : t.Game.Name;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double half = Math.Min(label.DesiredSize.Width, 1400) / 2;
        double cx = LM + p.X + z.T / 2 - scroll;                       // on screen, after the scroll settles
        cx = Math.Clamp(cx, 48 + half, 1872 - half);
        double x = cx - (LM - scroll) - 700;                           // back to strip coordinates
        labelBox.Height = z.L;
        double y = p.Y - z.L - z.T * 0.035;
        if (ms <= 0) { Anim.Set(labelT, TranslateTransform.XProperty, x); Anim.Set(labelT, TranslateTransform.YProperty, y); }
        else { Anim.To(labelT, TranslateTransform.XProperty, x, ms); Anim.To(labelT, TranslateTransform.YProperty, y, ms); }
        if (fade && !onBar)
        {
            Anim.FromTo(label, UIElement.OpacityProperty, 0.2, 1, 200);
            Anim.FromTo(labelRise, TranslateTransform.YProperty, 7, 0, 220);
        }
    }

    // ---- moving ----

    void Select(int i)
    {
        if (i == sel) return;
        int was = sel;
        sel = i;
        Sounds.Play("scroll");
        double ns = ScrollTo(sel, scroll);
        if (Math.Abs(ns - scroll) > 0.5)
        {
            scroll = ns;
            if (FolderMode) { Anim.To(stripT, TranslateTransform.XProperty, Left0 - scroll, 380, Anim.InOut); RefreshDots(); }   // the next page slides in
            else Anim.To(stripT, TranslateTransform.XProperty, Left0 - scroll, 300);
            RefreshEdges(300);
        }
        BuildNear();   // the tiles coming on screen
        LiftTo(was, false, 160);
        LiftTo(sel, TilesFocused, 120);   // in step with the ring, so it lands on the tile's edge
        UpdateAmbient(650);
        MoveRing(120, Anim.Out);
        Anim.FromTo(ringLand, ScaleTransform.ScaleXProperty, 1.035, 1, 240, Anim.Back);
        Anim.FromTo(ringLand, ScaleTransform.ScaleYProperty, 1.035, 1, 240, Anim.Back);
        UpdateLabel(150, true);
        RefreshHints();
    }

    void FocusBar(bool sound = true)
    {
        onBar = true;
        if (sound) Sounds.Play("scroll");
        LiftTo(sel, false, 160);
        Anim.To(ring, UIElement.OpacityProperty, 0, 160);
        Anim.To(labelBox, UIElement.OpacityProperty, 0, 160);
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            bool f = i == barSel;
            Anim.To(b.Ring, UIElement.OpacityProperty, f ? 1 : 0, f ? 160 : 120);
            if (f) { Anim.FromTo(b.RingS, ScaleTransform.ScaleXProperty, 0.9, 1, 260, Anim.Back); Anim.FromTo(b.RingS, ScaleTransform.ScaleYProperty, 0.9, 1, 260, Anim.Back); }
        }
        barCaption.Text = buttons[barSel].Caption();
        RefreshHints();
        if (FolderMode) UpdateLabel(0, false);
        Anim.To(captionT, TranslateTransform.XProperty, buttons[barSel].Cx - 300, 160);
        Anim.To((UIElement)barCaption.Parent, UIElement.OpacityProperty, 1, 160);
    }

    void LeaveBar()
    {
        onBar = false;
        Sounds.Play("scroll");
        LiftTo(sel, TilesFocused, 200);
        foreach (var b in buttons) Anim.To(b.Ring, UIElement.OpacityProperty, 0, 120);
        Anim.To((UIElement)barCaption.Parent, UIElement.OpacityProperty, 0, 120);
        Anim.To(ring, UIElement.OpacityProperty, 1, 160);
        Anim.To(labelBox, UIElement.OpacityProperty, 1, 160);
        Anim.FromTo(label, UIElement.OpacityProperty, 0.2, 1, 220);
        RefreshHints();
        if (FolderMode) UpdateLabel(0, false);
    }

    public void CycleZoom()
    {
        w.Settings.Zoom = (Zoom)(((int)w.Settings.Zoom + 1) % Zooms.Length);
        w.Settings.Save();
        SetZoom(w.Settings.Zoom);
    }

    public void SetZoom(Zoom zm)
    {
        w.Settings.Zoom = zm; w.Settings.Save();
        Sounds.Play("quicksysselect");
        Layout(true);
        RefreshRunning();
        if (onBar) barCaption.Text = buttons[barSel].Caption();
    }

    // Milliseconds left of a press effect (input waits for it). Test scripts wait for it too.
    public double PressLeftMs => Math.Max(0, pressUntil - Environment.TickCount64);

    // A on a tile or button: sound + squash/flash/shake, input held, then the action once it has played.
    void Press(ScaleTransform s, TranslateTransform shake, UIElement flash, double amp, Action then)
    {
        Sounds.Play("press");
        Anim.Press(s, shake, flash, amp);
        pressUntil = Environment.TickCount64 + (long)Anim.PressMs;
        Ui.After(Anim.PressMs, () => { pressUntil = 0; then(); });
    }

    public void OnAct(Act a)
    {
        if (PressLeftMs > 0) return;
        if (holdTile != null)
        {
            if (a == Act.Accept) return;   // the same press (key repeat)
            EndHold(true);                 // anything else: no quick menu, act on it as usual
        }
        var z = Z;
        int rows = z.Rows, row = sel % rows, count = tiles.Count;
        if (onChip)
        {
            switch (a)
            {
                case Act.Down: case Act.Back: LeaveChip(); break;
                case Act.Accept: Sounds.Play("select"); OpenProfile(); break;
                case Act.Left: Anim.Edge(chipBump, -1); break;
                case Act.Right: Anim.Edge(chipBump, 1); break;
                case Act.Up: Anim.Edge(chipBump, -1, true); break;
                case Act.Menu: new SettingsCard(w).Open(); break;
            }
            return;
        }
        if (onBar)
        {
            switch (a)
            {
                case Act.Up: case Act.Back: LeaveBar(); break;
                case Act.Left: if (barSel > 0) { barSel--; FocusBar(); } else Anim.Edge(barBump, -1); break;
                case Act.Right: if (barSel < buttons.Count - 1) { barSel++; FocusBar(); } else Anim.Edge(barBump, 1); break;
                case Act.Down: Anim.Edge(barBump, 1, true); break;
                case Act.Accept:
                {
                    var b = buttons[barSel];
                    Anim.FromTo(b.RingS, ScaleTransform.ScaleXProperty, 1.14, 1, 320, Anim.Back, 50);
                    Anim.FromTo(b.RingS, ScaleTransform.ScaleYProperty, 1.14, 1, 320, Anim.Back, 50);
                    Press(b.PS, b.PT, b.Flash, 0, b.Run);
                    break;
                }
                case Act.Menu: new SettingsCard(w).Open(); break;
            }
            return;
        }
        if (FolderMode && FolderAct(a)) return;
        switch (a)
        {
            case Act.Left:
                if (sel - rows >= 0) Select(sel - rows); else Anim.Edge(ringBump, -1);
                break;
            case Act.Right:
                if (sel + rows < count) Select(sel + rows);
                else if ((sel / rows + 1) * rows < count) Select(count - 1);   // next column is shorter
                else Anim.Edge(ringBump, 1);
                break;
            case Act.Up:
                if (row > 0) Select(sel - 1); else FocusChip();
                break;
            case Act.Down:
                if (row + 1 < rows && sel + 1 < count) Select(sel + 1); else FocusBar();
                break;
            case Act.PageLeft: case Act.PageRight:
            {
                int step = Math.Max(1, (int)(VW / (z.T + z.G))) * rows * (a == Act.PageRight ? 1 : -1);
                int target = Math.Clamp(sel + step, 0, count - 1);
                if (target != sel) Select(target); else Anim.Edge(ringBump, step > 0 ? 1 : -1);
                break;
            }
            case Act.Accept:
                // A game held down (A or Enter) opens its quick menu; a short press starts it on release.
                if (Holdable(tiles[sel]) && w.AcceptHeld) BeginHold(tiles[sel]); else Activate();
                break;
            case Act.Back:
                if (filter != null) SetView(folder, null, null);
                else if (folder != null) SetView(null, null, "folder:" + folder);
                else if (w.Tutor.Active) w.Tutor.AskLeave();
                else GoToStart();
                break;
            case Act.Menu: new SettingsCard(w).Open(); break;
            case Act.Option:
                if (tiles[sel].Game is Game g) new GameMenuCard(w, g).Open();
                else Anim.Edge(ringBump, 1, true);
                break;
        }
    }

    // Moving in an open folder: three by three on each page; past the side of a page is the next /
    // previous page (same row), LB / RB (Page Up / Down, the wheel) turn a whole page.
    bool FolderAct(Act a)
    {
        int count = tiles.Count, pg = sel / PageN, slot = sel % PageN, r = slot / 3, c = slot % 3;
        switch (a)
        {
            case Act.Left:
                if (c > 0) Select(sel - 1);
                else if (pg > 0) Select((pg - 1) * PageN + r * 3 + 2);
                else Anim.Edge(ringBump, -1);
                return true;
            case Act.Right:
                if (c < 2 && sel + 1 < count) Select(sel + 1);
                else if ((pg + 1) * PageN < count) Select(Math.Min((pg + 1) * PageN + r * 3, count - 1));
                else Anim.Edge(ringBump, 1);
                return true;
            case Act.Up:
                if (r > 0) Select(sel - 3); else FocusChip();
                return true;
            case Act.Down:
                if (r < 2 && sel + 3 < count) Select(sel + 3);
                else if (r < 2 && (count - 1) / PageN == pg && (count - 1) % PageN / 3 > r) Select(count - 1);   // the row below is shorter
                else FocusBar();
                return true;
            case Act.PageLeft: case Act.PageRight:
            {
                int to = pg + (a == Act.PageRight ? 1 : -1);
                if (to < 0 || to >= Pages) Anim.Edge(ringBump, a == Act.PageRight ? 1 : -1);
                else Select(Math.Min(to * PageN + slot, count - 1));
                return true;
            }
        }
        return false;
    }

    public void ActivateSelected() => Activate();

    // Opening a tile: copies of its picture grow out of it and fade (an afterimage), drawn above the
    // neighbouring tiles but under the ring. They start as the squash bottoms out.
    void PopOut(Tile t)
    {
        double rad = Art.Corner(16.28 * B / 297);
        var look = new VisualBrush(t.Face) { Stretch = Stretch.Fill };
        int at = strip.Children.IndexOf(ring);
        foreach (var (delay, grow, peak, ms) in new[] { (40.0, 1.2, 0.75, 300.0), (90.0, 1.38, 0.35, 340.0) })
        {
            var gs = new ScaleTransform(1, 1, B / 2, B / 2);
            var ghost = new Border
            {
                Width = B, Height = B, CornerRadius = new CornerRadius(rad), Background = look, Opacity = 0, IsHitTestVisible = false,
                RenderTransform = new TransformGroup { Children = {
                    gs, new ScaleTransform(t.S.ScaleX, t.S.ScaleY), new TranslateTransform(t.T.X, t.T.Y) } },
            };
            strip.Children.Insert(at, ghost);
            Anim.FromTo(gs, ScaleTransform.ScaleXProperty, 1, grow, ms, Anim.Out, delay);
            Anim.FromTo(gs, ScaleTransform.ScaleYProperty, 1, grow, ms, Anim.Out, delay);
            var op = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delay + ms) };
            op.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            op.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + ms)), Anim.Out));
            op.Completed += (s, e) => strip.Children.Remove(ghost);
            ghost.BeginAnimation(UIElement.OpacityProperty, op);
        }
    }

    void Activate()
    {
        var t = tiles[sel];
        // The ring squashes along with the tile; no shake here, the picture pops out of the tile instead.
        Anim.Press(ringPS, ringPT, null, 0);
        PopOut(t);
        Press(t.PS, t.PT, t.Flash, 0, () =>
        {
            // The row may have been rebuilt while the press played (a game that ended or started, the launcher
            // back in front): the press goes on with the same game or container, so one press always opens it.
            // Before, a rebuilt row dropped the press and it took a second one.
            int i = w.TopScreen == this ? Find(t) : -1;
            if (i < 0) { Log.Write($"press on {KeyOf(t)} dropped: {(w.TopScreen != this ? "another screen in front" : "its tile is gone")}"); return; }
            if (i != sel) Select(i);
            t = tiles[i];
            if (t.Folder != null) { SetView(t.Folder, null, null); return; }
            if (t.Game == null) { if (folder != null) new AddChoiceCard(w).Open(); else new AddMenuCard(w).Open(); return; }   // in a container: games only
            w.Launch.Start(t.Game, ScreenRect(i), t.Cover ?? CachedCover(t.Game));
        });
    }

    // A tile's game, container or "+ Add game", and where that is on the row now (the same tile after a rebuild).
    static string KeyOf(Tile t) => t.Game != null ? t.Game.Id : t.Folder != null ? "folder:" + t.Folder : "+";
    int Find(Tile t)
    {
        int i = tiles.IndexOf(t);
        if (i >= 0) return i;
        string k = KeyOf(t);
        return tiles.FindIndex(x => KeyOf(x) == k);
    }

    // ---- hold A / Enter (or the mouse button) on a game or container: the tile sinks a little, then pops out with its quick menu ----
    public const int HoldMs = 480;
    Tile holdTile;
    long holdT0;
    System.Windows.Threading.DispatcherTimer holdTimer;
    public bool Holding => holdTile != null;
    static bool Holdable(Tile t) => t.Game != null || t.Folder != null;   // a game or a container (not "+")

    HoldMenu MenuFor(Tile t, Rect at, Action restore) =>
        t.Game != null ? new HoldMenu(w, t.Game, at, t.Cover, restore) : new HoldMenu(w, t.Folder, at, t.Face, restore);

    void BeginHold(Tile t)
    {
        holdTile = t; holdT0 = Environment.TickCount64;
        Anim.To(t.PS, ScaleTransform.ScaleXProperty, 0.95, HoldMs, Anim.Out);
        Anim.To(t.PS, ScaleTransform.ScaleYProperty, 0.95, HoldMs, Anim.Out);
        if (holdTimer == null)
        {
            holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            holdTimer.Tick += (s, e) => HoldTick();
        }
        holdTimer.Start();
    }

    void HoldTick()
    {
        var t = holdTile;
        if (t == null || w.TopScreen != this || w.Launch.Blocks) { EndHold(true); return; }
        // A rebuild under the hold: it goes on on the same game's new tile instead of being dropped.
        int i = Find(t);
        if (i < 0) { EndHold(true); return; }
        if (tiles[i] != t)
        {
            holdTile = t = tiles[i];
            Anim.To(t.PS, ScaleTransform.ScaleXProperty, 0.95, 120, Anim.Out);
            Anim.To(t.PS, ScaleTransform.ScaleYProperty, 0.95, 120, Anim.Out);
        }
        if (i != sel) Select(i);
        if (!w.AcceptHeld) { EndHold(true); Activate(); return; }   // let go early: a normal press
        if (Environment.TickCount64 - holdT0 < HoldMs) return;
        EndHold(false);
        MenuFor(t, ScreenRect(sel), () =>
        {
            Anim.To(t.PS, ScaleTransform.ScaleXProperty, 1, 260, Anim.Back);
            Anim.To(t.PS, ScaleTransform.ScaleYProperty, 1, 260, Anim.Back);
        }).Open();
    }

    // Test scripts: the quick menu on the selected game, without a hold.
    public void OpenHoldMenuForTest()
    {
        var t = tiles[sel];
        if (!Holdable(t)) return;
        MenuFor(t, ScreenRect(sel), null).Open();
    }

    void EndHold(bool reset)
    {
        holdTimer?.Stop();
        var t = holdTile; holdTile = null;
        if (reset && t != null) { Anim.Set(t.PS, ScaleTransform.ScaleXProperty, 1); Anim.Set(t.PS, ScaleTransform.ScaleYProperty, 1); }
    }

    // Start a game from outside the grid (a restart after tweaks): the launch grows from its tile when it's shown.
    public void StartGame(Game g)
    {
        if (tiles.Any(t => t.Game?.Id == g.Id)) Rebuild(g.Id);
        int i = tiles.FindIndex(t => t.Game?.Id == g.Id);
        Rect rect;
        BitmapSource cover = null;
        if (i >= 0) { rect = ScreenRect(i); cover = tiles[i].Cover ?? CachedCover(g); }
        else rect = new Rect(960 - Z.T / 2, 540 - Z.T / 2, Z.T, Z.T);
        w.Launch.Start(g, rect, cover);
    }

    // ---- views: home row, one folder, search results ----

    public void SetView(string f, string q, string selectId)
    {
        bool same = f == folder && q == filter;
        bool deeper = (f != null && folder == null) || (q != null && filter == null);   // into a folder / results: slides in from the right
        bool closing = FolderMode && f == null && q == null;   // an open folder goes back to its tile on home
        EndCloseFx();
        var ghost = closing ? LiftPanel() : null;
        if (f != null && q == null && folder == null && filter == null) SnapBackdrop();   // home -> a folder: the row goes behind the glass
        folder = f; filter = q;
        if (!same) sel = 0;
        if (onBar) LeaveBar();
        Rebuild(selectId);
        if (!same)
        {
            // Sounds: a container unfolds as it opens and folds back as it closes; leaving search
            // results is a plain back.
            if (FolderMode && deeper) Sounds.Play("unfold");
            else if (closing) Sounds.Play("fold");
            else if (!deeper) Sounds.Play("back");
            Anim.FromTo(strip, UIElement.OpacityProperty, 0, 1, 280);
            if (FolderMode)
            {
                // The folder opens: the blurred row and the panel fade in as the panel (with its games) grows into place.
                Anim.Set(stripSlide, TranslateTransform.XProperty, 0);
                Anim.FromTo(folderLayer, UIElement.OpacityProperty, 0, 1, 240);
                foreach (var sc in new[] { folderScale, viewScale })
                {
                    Anim.FromTo(sc, ScaleTransform.ScaleXProperty, 0.86, 1, 380, Anim.Back);
                    Anim.FromTo(sc, ScaleTransform.ScaleYProperty, 0.86, 1, 380, Anim.Back);
                }
            }
            else if (closing) PlayCloseFx(ghost);
            else Anim.FromTo(stripSlide, TranslateTransform.XProperty, deeper ? 70 : -70, 0, 340);
            RefreshHints();
        }
    }

    // The folder look on (an open folder) or off: names under the icons instead of the label, the strip
    // clipped to the glass panel, the panel and blurred backdrop shown (they fade away on leaving).
    void ApplyMode()
    {
        bool f = FolderMode;
        labelBox.Visibility = f ? Visibility.Collapsed : Visibility.Visible;
        view.Clip = f ? new RectangleGeometry(Panel, PR, PR) : null;
        if (f)
        {
            folderTitle.Text = folder;
            RefreshDots();
            Anim.Set(folderLayer, UIElement.OpacityProperty, 1);
            Anim.Set(panelBox, UIElement.OpacityProperty, 1);
            folderLayer.Visibility = Visibility.Visible;
            return;
        }
        foreach (var sc in new[] { viewScale, folderScale }) { Anim.Set(sc, ScaleTransform.ScaleXProperty, 1); Anim.Set(sc, ScaleTransform.ScaleYProperty, 1); }
        dragArmed = dragging = false;
        if (folderLayer.Visibility == Visibility.Visible)
            Anim.To(folderLayer, UIElement.OpacityProperty, 0, 220, done: () => { if (!FolderMode) folderLayer.Visibility = Visibility.Collapsed; });
    }

    // ---- closing a folder: the reverse of opening, like a window closing ----
    // The open panel (glass and the games on it, as they are) is lifted into a layer above the home row,
    // and the home row is built at once, so input goes straight to it. The lifted panel then shrinks and
    // moves to the folder's tile while it fades; the blurred backdrop fades out, the home row fades in and
    // settles, and the folder tile fades in under the panel as it lands. Nothing is redrawn into a picture:
    // the same elements move, and only transforms and opacity animate (GPU, no hitch at the start).
    const double CloseMs = 260;
    int panelAt;   // where the glass panel sits among the folder layer's children

    Canvas LiftPanel()
    {
        foreach (var sc in new[] { folderScale, viewScale }) { Anim.Set(sc, ScaleTransform.ScaleXProperty, 1); Anim.Set(sc, ScaleTransform.ScaleYProperty, 1); }
        var games = new Canvas { RenderTransform = new TranslateTransform(stripT.X + stripSlide.X, stripT.Y + stripSlide.Y) };
        foreach (var el in strip.Children.Cast<UIElement>().Where(e => e != ring && e != labelBox).ToList())
        {
            strip.Children.Remove(el);
            games.Children.Add(el);
        }
        var clip = new Canvas { Width = 1920, Height = 1080, Clip = new RectangleGeometry(Panel, PR, PR) };
        clip.Children.Add(games);
        var g = new Canvas { Width = 1920, Height = 1080, IsHitTestVisible = false };
        panelAt = folderLayer.Children.IndexOf(panelBox);
        folderLayer.Children.Remove(panelBox);
        g.Children.Add(panelBox);
        g.Children.Add(clip);
        return g;
    }

    void PlayCloseFx(Canvas ghost)
    {
        // The home row: fades in and settles from slightly small, as the opening grew the panel from small.
        // (no start delays here: before an animation's start WPF shows the old value, which would flash)
        Anim.Set(stripSlide, TranslateTransform.XProperty, 0);
        Anim.FromTo(strip, UIElement.OpacityProperty, 0, 1, CloseMs, Anim.Out);
        foreach (var dp in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            Anim.FromTo(viewScale, dp, 0.965, 1, CloseMs + 60, Anim.Out);
        Anim.FromTo(folderLayer, UIElement.OpacityProperty, 1, 0, CloseMs, Anim.InOut, done: () => { if (!FolderMode) folderLayer.Visibility = Visibility.Collapsed; });

        int i = sel < tiles.Count && tiles[sel].Folder != null ? sel : -1;
        var to = i >= 0 ? ScreenRect(i) : new Rect(960 - Z.T / 2, PY + PH / 2 - Z.T / 2, Z.T, Z.T);
        CloseTarget = i >= 0 ? "folder:" + tiles[i].Folder : "middle";
        if (i >= 0)
        {
            var el = tiles[i].El;
            var land = new CubicEase { EasingMode = EasingMode.EaseIn }; land.Freeze();   // stays faint, then shows as the panel lands
            Anim.FromTo(el, UIElement.OpacityProperty, 0, 1, CloseMs, land, 0, () => { if (el.Opacity >= 1) el.BeginAnimation(UIElement.OpacityProperty, null); });
        }
        if (ghost == null) return;

        // The panel: from where it is to the tile's middle, shrinking to about the tile's size.
        double cx = PX + PW / 2, cy = PY + PH / 2;
        double end = to.Width / Math.Sqrt(PW * PH);
        var gs = new ScaleTransform(1, 1, cx, cy);
        var gt = new TranslateTransform();
        ghost.RenderTransform = new TransformGroup { Children = { gs, gt } };
        closeLayer.Children.Add(ghost);
        closeGhost = ghost;
        CloseFx = "running";
        var shrink = new CubicEase { EasingMode = EasingMode.EaseInOut }; shrink.Freeze();
        Anim.FromTo(gs, ScaleTransform.ScaleXProperty, 1, end, CloseMs, shrink);
        Anim.FromTo(gs, ScaleTransform.ScaleYProperty, 1, end, CloseMs, shrink);
        Anim.FromTo(gt, TranslateTransform.XProperty, 0, to.X + to.Width / 2 - cx, CloseMs, shrink);
        Anim.FromTo(gt, TranslateTransform.YProperty, 0, to.Y + to.Height / 2 - cy, CloseMs, shrink);
        var fade = new QuadraticEase { EasingMode = EasingMode.EaseIn }; fade.Freeze();
        Anim.FromTo(ghost, UIElement.OpacityProperty, 1, 0, CloseMs, fade, done: () => { if (closeGhost == ghost) EndCloseFx(); });
    }

    // The close is over (or a folder opened again before it ended): the lifted games go, the glass panel
    // goes back into the folder layer.
    void EndCloseFx()
    {
        if (closeGhost == null) return;
        closeLayer.Children.Clear();
        closeGhost.Children.Clear();
        if (panelBox.Parent == null) folderLayer.Children.Insert(Math.Min(panelAt, folderLayer.Children.Count), panelBox);
        closeGhost = null;
        CloseFx = "none";
    }

    // One dot per page under the grid (none for a single page); the page shown is the solid one.
    void RefreshDots()
    {
        dots.Children.Clear();
        int n = Pages;
        if (!FolderMode || n < 2) return;
        for (int i = 0; i < n; i++)
            dots.Children.Add(new Ellipse { Width = 11, Height = 11, Margin = new Thickness(7, 0, 7, 0), Fill = P.Text, Opacity = i == Page ? 0.85 : 0.25 });
    }

    // The home row as it is now, drawn small and blurred once: what the open folder sits over.
    void SnapBackdrop()
    {
        try
        {
            const int sw = 480, sh = 270;
            var brush = new VisualBrush(view) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 1920, 1080), Stretch = Stretch.Fill };
            var small = Art.Render(sw, sh, dc => dc.DrawRectangle(brush, null, new Rect(0, 0, sw, sh)));
            var img = new Image { Source = small, Width = sw, Height = sh, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 9 } };
            img.Measure(new Size(sw, sh)); img.Arrange(new Rect(0, 0, sw, sh));
            var rtb = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(img); rtb.Freeze();
            backdrop.Source = rtb;
            // frosted glass: the same row blurred much more (drawn at a quarter size, so it stays cheap)
            // and shown inside the panel, lined up with where it sits on the screen
            var fr = Art.Frost(small, 160, 90, 7);
            frost.Background = new ImageBrush(fr) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(PX / 12, PY / 12, PW / 12, PH / 12), Stretch = Stretch.Fill };
        }
        catch (Exception e) { Log.Write("folder backdrop: " + e.Message); backdrop.Source = null; frost.Background = null; }
    }

    // Live filter from the search card ("" shows every game; null closes search).
    public void SetFilter(string q) { filter = q; sel = 0; Rebuild(null); }

    public void ShowViewText(bool on) => Anim.To(viewText, UIElement.OpacityProperty, on ? 1 : 0, 150);

    public void OpenSearch()
    {
        if (onBar) LeaveBar();
        new SearchCard(w, filter ?? "").Open();
    }

    // ---- entering / leaving ----

    public void Enter(bool fromStart)
    {
        w.Replace(w.StartScr, this);
        ApplyLifts();   // leaving for the start screen from the bar or the profile left the focus there
        Root.Opacity = 0;
        Anim.FromTo(Root, UIElement.OpacityProperty, 0, 1, 460, Anim.Out, 120);
        Anim.FromTo(rootScale, ScaleTransform.ScaleXProperty, 0.965, 1, 520, Anim.Out, 120);
        Anim.FromTo(rootScale, ScaleTransform.ScaleYProperty, 0.965, 1, 520, Anim.Out, 120);
        SlideIn(120);
    }

    // ---- the slide in: arriving at the library, or after a game is added ----
    // The tiles on screen glide in from the right one column after another, and a soft band of light
    // sweeps over them in the same order, so it reads as one light passing along the row. The ring
    // rides in with its tile. Reduce effects: the slide is shorter (as every animation) and no light.
    public const double SlideStep = 55, SlideMs = 680, SlideFrom = 200, ShineMs = 760;
    static readonly IEasingFunction Glide = new QuinticEase { EasingMode = EasingMode.EaseOut };
    public int SlideCount { get; private set; }   // test scripts: tiles the last slide in moved
    readonly List<Border> shines = new();

    public void SlideIn(double delay = 0)
    {
        foreach (var s in shines) (s.Parent as Panel)?.Children.Remove(s);
        shines.Clear();
        if (tiles.Count == 0) { SlideCount = 0; return; }
        w.Stage.UpdateLayout();
        var on = built
            .Select(i => (i, r: ScreenRect(i)))
            .Where(x => x.r.Right > 0 && x.r.Left < 1920)
            .ToList();
        double x0 = on.Count > 0 ? on.Min(x => x.r.Left) : 0, step = Z.T + Z.G;
        SlideCount = on.Count;
        double rad = Art.Corner(16.28 * B / 297);
        foreach (var (i, r) in on)
        {
            var t = tiles[i];
            int col = (int)Math.Round((r.Left - x0) / step), row = (int)Math.Round((r.Top - on.Min(x => x.r.Top)) / step);
            double d = delay + col * SlideStep + row * 14;
            // where it waits until its turn (a delayed animation shows the plain value until it starts)
            Anim.Set(t.In, TranslateTransform.XProperty, SlideFrom); Anim.Set(t.El, UIElement.OpacityProperty, 0);
            Anim.FromTo(t.In, TranslateTransform.XProperty, SlideFrom, 0, SlideMs, Glide, d);
            Anim.FromTo(t.El, UIElement.OpacityProperty, 0, 1, SlideMs * 0.55, Anim.Out, d);
            if (i == sel)
            {
                Anim.Set(ringIn, TranslateTransform.XProperty, SlideFrom);
                Anim.FromTo(ringIn, TranslateTransform.XProperty, SlideFrom, 0, SlideMs, Glide, d);
            }
            if (Anim.Reduced) continue;
            // the light: a diagonal band moved across the tile's face (clipped by its rounded corners)
            var band = new TranslateTransform(-1.4, 0);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = band,
                GradientStops = { new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.32), new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.68) },
            };
            var shine = new Border { CornerRadius = new CornerRadius(rad), Background = brush, IsHitTestVisible = false };
            var el = t.El;   // the tile may be rebuilt (or dropped off screen) before the light has crossed it
            el.Children.Insert(el.Children.IndexOf(t.Face) + 1, shine);
            shines.Add(shine);
            Anim.FromTo(band, TranslateTransform.XProperty, -1.4, 1.4, ShineMs, Anim.InOut, d + SlideMs * 0.35, done: () =>
            {
                el.Children.Remove(shine); shines.Remove(shine);
            });
        }
    }

    // Test scripts: "rest" once every tile has landed (no offset, fully shown, no light left), else "moving".
    public string SlideState => built.All(i => Math.Abs(tiles[i].In.X) < 0.01 && tiles[i].El.Opacity > 0.999) && shines.Count == 0 && Math.Abs(ringIn.X) < 0.01 ? "rest" : "moving";

    void GoToStart()
    {
        Sounds.Play("back");
        if (onChip) { onChip = false; chipRing.Opacity = 0; nameText.Foreground = P.Text; ring.Opacity = 1; labelBox.Opacity = 1; }
        if (onBar) { onBar = false; foreach (var b in buttons) b.Ring.Opacity = 0; ((UIElement)barCaption.Parent).Opacity = 0; ring.Opacity = 1; labelBox.Opacity = 1; }
        Anim.To(Root, UIElement.OpacityProperty, 0, 320, Anim.InOut, done: () => { if (w.TopScreen != this) Root.Visibility = Visibility.Hidden; });
        Anim.To(rootScale, ScaleTransform.ScaleXProperty, 0.97, 320, Anim.InOut);
        Anim.To(rootScale, ScaleTransform.ScaleYProperty, 0.97, 320, Anim.InOut);
        w.Replace(this, w.StartScr);
        w.StartScr.FadeIn();
        w.ScreenLayer.Children.Remove(w.StartScr.Root);
        w.ScreenLayer.Children.Add(w.StartScr.Root);   // start screen on top while home fades
    }

    // Back from a game: home settles in from a touch larger, as if the game had just shrunk away.
    public void Welcome()
    {
        if (w.TopScreen != this) return;
        Anim.FromTo(Root, UIElement.OpacityProperty, 0.4, 1, 380);
        Anim.FromTo(rootScale, ScaleTransform.ScaleXProperty, 1.035, 1, 460);
        Anim.FromTo(rootScale, ScaleTransform.ScaleYProperty, 1.035, 1, 460);
    }

    // ---- button hints ----

    public string HintText { get; private set; } = "";   // test scripts

    public void RefreshHints()
    {
        bool pad = w.PadLast;
        var lk = w.Settings.Input;   // the first key of each launcher action (Settings > Input > Launcher keys)
        string ok = LauncherKeys.First(lk, "accept"), back = LauncherKeys.First(lk, "back"), opt = LauncherKeys.First(lk, "option");
        var list = new List<(string pad, string key, string what)>();
        if (onChip) list.Add(("A", ok, T("home.profile")));
        else if (onBar) list.Add(("A", ok, buttons[barSel].Kind == "layout" ? T("home.change") : T("home.open")));
        else if (sel < tiles.Count)
        {
            var t = tiles[sel];
            if (t.Game != null) { if (pad || opt != "") list.Add(("X", opt, T("home.options"))); list.Add(("A", ok, IsRunning(t) ? T("home.resume") : T("home.start"))); }
            else list.Add(("A", ok, t.Folder != null ? T("home.open") : AddWord));
        }
        if (folder != null || filter != null) list.Insert(0, ("B", back, folder != null ? T("common.back") : T("home.clear")));
        string key = (pad ? "p" : "k") + string.Join("|", list.Select(x => x.pad + x.key + x.what));
        if (key == hintKey) return;
        hintKey = key;
        HintText = string.Join("  ", list.Select(x => (pad ? x.pad : x.key) + " " + x.what));
        hints.Children.Clear();
        foreach (var (pb, kb, what) in list)
        {
            var glyph = pad ? Art.PadGlyph(pb, 36) : Art.KeyCap(kb, 36);
            glyph.Margin = new Thickness(26, 0, 10, 0); glyph.VerticalAlignment = VerticalAlignment.Center;
            hints.Children.Add(glyph);
            var tx = Fonts.Text(what, 26, P.Text2); tx.VerticalAlignment = VerticalAlignment.Center;
            hints.Children.Add(tx);
        }
        Anim.FromTo(hints, UIElement.OpacityProperty, 0.35, 1, 180);
    }

    public void ShowHomeOnTop()
    {
        w.ScreenLayer.Children.Remove(Root);
        w.ScreenLayer.Children.Add(Root);
    }
}
