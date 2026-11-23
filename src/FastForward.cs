// Fast-forward for games played inside the launcher (libretro cores, CoreGame.Loop).
// While its key is held (or after a press, when it is set to turn on and off) the core runs several times its own
// rate, or as fast as the PC goes. The sound is left out meanwhile and the display's timing steps aside (DisplaySync
// is not waited on, the sound's skew is left alone); both pick up again when it ends. Only about 60 pictures a
// second are shown while fast: the other frames are run without being copied or drawn.
// Keys: Settings > Input > Launcher keys > Fast-forward (F6), and View + RB on Player 1's controller.
using System;
using System.Linq;
using System.Windows;
using static Ionea.Loc;

namespace Ionea;

// Settings > Look > In games > Fast-forward and rewind. This PC's.
public sealed class PlayPrefs
{
    public int FastSpeed { get; set; } = FastForward.DefaultSpeed;   // times the game's own rate; 0 = as fast as the PC goes
    public bool FastToggle { get; set; }                               // the key turns it on and off instead of being held
    public bool Rewind { get; set; } = true;                           // keep states to rewind (Rewind.cs)
    public bool Rewind3D { get; set; }                                 // also for games drawn with the GPU (big, slow states)
    public int RewindMb { get; set; } = DefaultMb;                     // memory for rewind, MB
    public int RunAhead { get; set; }                                  // frames to run ahead, 0-4; 0 = off (RunAhead.cs)
    public static readonly int[] Mbs = { 64, 128, 256 };
    public const int DefaultMb = 128;
    public int Mb => Array.IndexOf(Mbs, RewindMb) >= 0 ? RewindMb : DefaultMb;
}

public static class FastForward
{
    public static readonly int[] Speeds = { 2, 3, 4, 6, 8, 0 };
    public const int DefaultSpeed = 4;
    public const double ShowMs = 1000.0 / 60;   // while fast: a picture at most this often

    public static int Clean(int speed) => Speeds.Contains(speed) ? speed : DefaultSpeed;
    public static string SpeedName(int speed) => speed <= 0 ? T("settings.no_limit") : $"{speed}x";

    // How long one frame takes while fast (ms); 0: no waiting at all.
    public static double FrameMs(double fps, int speed) => speed <= 0 || fps <= 0 ? 0 : 1000.0 / (fps * speed);

    // While fast: show this frame's picture? One when ShowMs have passed since the last one shown (half a ms early
    // is close enough, so 60 fps at 2x doesn't miss every other chance by a hair).
    public static bool Show(double nowMs, double lastShownMs) => nowMs - lastShownMs >= ShowMs - 0.5;

    // On after this poll? Held: while the key is down. Toggle: each new press turns it over.
    public static bool Next(bool on, bool down, bool wasDown, bool toggle) => toggle ? (down && !wasDown ? !on : on) : down;
}

// Settings > Look > In games > Fast-forward and rewind.
public sealed class PlayCard : MenuCard
{
    public PlayCard(MainWindow w) : base(w, T("settings.fast_forward_and_rewind"), 1100)
    {
        var p = w.Settings.Play ??= new PlayPrefs();
        Note(T("settings.games_played_inside_ionea"), 26, new Thickness(4, -14, 0, 20));
        int n = FastForward.Speeds.Length;
        int At() => Math.Max(0, Array.IndexOf(FastForward.Speeds, FastForward.Clean(p.FastSpeed)));
        void Speed(int i) { p.FastSpeed = FastForward.Speeds[i]; w.Settings.Save(); }
        WithList(AddRow(T("settings.speed"), () => FastForward.SpeedName(FastForward.Clean(p.FastSpeed)), d => Speed((At() + d + n) % n)),
            () => FastForward.Speeds.Select(FastForward.SpeedName).ToList(), At, Speed);
        AddRow(T("settings.hold_or_press"), () => p.FastToggle ? T("settings.press_to_turn_on_and_off") : T("settings.hold"), _ => { p.FastToggle = !p.FastToggle; w.Settings.Save(); });
        Aside(Opens(AddRow(T("settings.fast_forward_key"), () => LauncherKeys.Names(w.Settings.Input, LauncherKeys.Fast), activate: () => new LauncherKeyCard(w, LauncherKeys.Fast).Open())),
            T("settings.controller") + LauncherKeys.PadCombo(LauncherKeys.Fast));
        Divider();
        void Save() => w.Settings.Save();
        Aside(AddRow(T("settings.rewind"), () => p.Rewind ? T("common.on") : T("common.off"), _ => { p.Rewind = !p.Rewind; Save(); }), T("settings.from_the_next_game_started"));
        AddRow(T("settings.rewind_in_3d_games"), () => p.Rewind3D ? T("common.on") : T("common.off"), _ => { p.Rewind3D = !p.Rewind3D; Save(); });
        int m = PlayPrefs.Mbs.Length;
        int MbAt() => Array.IndexOf(PlayPrefs.Mbs, p.Mb);
        void Mb(int i) { p.RewindMb = PlayPrefs.Mbs[i]; Save(); }
        WithList(AddRow(T("settings.rewind_memory"), () => T("settings.mb", p.Mb), d => Mb((MbAt() + d + m) % m)),
            () => PlayPrefs.Mbs.Select(x => $"{x} MB").ToList(), MbAt, Mb);
        Aside(Opens(AddRow(T("settings.rewind_key"), () => LauncherKeys.Names(w.Settings.Input, LauncherKeys.Rewind), activate: () => new LauncherKeyCard(w, LauncherKeys.Rewind).Open())),
            T("settings.controller") + LauncherKeys.PadCombo(LauncherKeys.Rewind));
        AddRow(T("common.done"), activate: () => Close());
    }

    public static string Summary(MainWindow w)
    {
        var p = w.Settings.Play ??= new PlayPrefs();
        return FastForward.SpeedName(FastForward.Clean(p.FastSpeed)) + (p.Rewind ? ", rewind on" : "");
    }

}
