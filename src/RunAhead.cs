// Run-ahead for games played inside the launcher (libretro cores, CoreGame.Loop).
// Many games only act on a press a frame or more after they read it. Run-ahead hides that lag: each frame the core
// runs its real frame (its sound kept, its picture not shown), its state is kept (retro_serialize), then it runs
// 1-4 frames further with the same buttons and no sound, only the last of those frames' picture shown, and the
// kept state goes back in (retro_unserialize). What is seen is that many frames ahead; what is heard and what
// is kept (battery saves, save states, rewind) is the real frame.
// One core, no second copy of it. Not for games drawn with the GPU (their states are big and slow, and the
// picture is read back a frame late), nor for cores that say their states are incomplete. It turns itself off
// for the game, with a notice, when a state fails or the frames take too long on this PC too often.
// Order of the in-game features: rewind wins over fast-forward, fast-forward over run-ahead (neither needs low
// lag; run-ahead comes back when they end). Match the display stays as it is: it paces the frames shown, and
// run-ahead only does more work inside each one.
// Setting: Settings > Look > In games > Run-ahead (Off, 1-4 frames; off by default).
using System;
using System.Linq;
using static Ionea.Loc;

namespace Ionea;

public static class RunAhead
{
    public static readonly int[] Choices = { 0, 1, 2, 3, 4 };
    public static int Clean(int n) => Choices.Contains(n) ? n : 0;
    public static string Name(int n) => n <= 0 ? T("common.off") : n == 1 ? "1 frame" : $"{n} frames";

    // Why this game can't run ahead at all (null: it can).
    public static string? Unfit(bool hardware, ulong quirks) =>
        hardware ? "a game drawn with the GPU" : (quirks & 1) != 0 ? "the core says its states are incomplete" : null;   // RETRO_SERIALIZATION_QUIRK_INCOMPLETE

    // This frame: run ahead? Set on, the game fit and not given up, and only while playing (mode 0: not
    // fast-forward, not rewinding).
    public static bool Now(int frames, string off, int mode) => Clean(frames) > 0 && off == null && mode == 0;
}

// Run-ahead's time on this PC: off when its frames take more than Share of a frame's time in Over of the last
// Window frames (a single slow frame, a load, is let go).
public sealed class AheadBudget
{
    public const double Share = 0.75;
    public const int Window = 60, Over = 20;
    readonly bool[] late = new bool[Window];
    int at, count;

    public void Clear() { Array.Clear(late); at = count = 0; }

    // One frame's run-ahead took ms of a frame of frameMs. True: too slow, turn it off.
    public bool Note(double ms, double frameMs)
    {
        bool over = frameMs > 0 && ms > frameMs * Share;
        if (late[at]) count--;
        late[at] = over;
        if (over) count++;
        at = (at + 1) % Window;
        return count >= Over;
    }
}
