#nullable disable
// Picture filters for games played inside the launcher (libretro cores).
// How the core's picture is scaled to the screen, and what is laid over it:
//   Sharp: nearest neighbour, every game pixel a hard-edged block (with Picture size > Integer scale all the same size).
//   Smooth: bilinear.
//   CRT: bilinear, with dark scanlines between the game's lines and a slight dark gap between its pixels.
//   LCD grid: nearest, with a thin dark grid between the game's pixels, as on a handheld's screen.
//   Automatic: Sharp, or Smooth for games drawn with the GPU (as before there were filters).
// Both picture paths look the same. Straight to the screen (DirectPicture.cs, Direct2D): the overlay is a small
// bitmap brush, tiled once per game pixel and drawn by the GPU over the picture. WPF (CoreSession): the same tile in
// an ImageBrush over the picture. The tile is made at the size one game pixel has on the screen; scanlines need 2
// screen pixels per game line, the gaps and the grid 3, and are left out below that.
// Which: the game's own (its Display card), else its system's (also in a game's Display card), else every game's
// (Settings > Look > In games > Game display), else Automatic. Screenshots stay the game's own picture, unfiltered.
// Not here: shader presets (.slangp) and real pixel shaders; a later step could add them to the Direct2D path.
using System;
using System.Linq;
using static Ionea.Loc;

namespace Ionea;

public static class PictureFilters
{
    public static readonly string[] Ids = { "auto", "sharp", "smooth", "crt", "lcd" };
    public const double Scan = 0.55, Gap = 0.15, Grid = 0.35;   // how dark: scanlines at their darkest, CRT gaps, LCD grid

    public static string Name(string id) => id switch
    {
        "sharp" => T("settings.sharp"), "smooth" => T("settings.smooth"), "crt" => "CRT", "lcd" => "LCD grid", _ => T("common.automatic"),
    };

    static string Known(string id) => id != null && Ids.Contains(id) ? id : null;

    // The filter used: the game's, else its system's, else every game's; Automatic is Sharp, Smooth when the game
    // is drawn with the GPU. Never "auto".
    public static string Pick(string game, string system, string all, bool hardware)
    {
        string f = Known(game) ?? Known(system) ?? Known(all) ?? "auto";
        return f == "auto" ? (hardware ? "smooth" : "sharp") : f;
    }

    // The choice a game falls back to when it has none of its own: its system's, else every game's (may be "auto").
    public static string Default(MainWindow w, string system) =>
        Known(SystemChoice(w, system)) ?? Known(w.Settings.Display?.Filter) ?? "auto";

    public static string SystemChoice(MainWindow w, string system) =>
        system != null && w.Settings.Filters != null && w.Settings.Filters.TryGetValue(system, out var f) ? f : null;

    public static string For(MainWindow w, Game g, bool hardware)
    {
        string sys = g != null ? InputMap.SystemOf(w, g) : null;
        return Pick(g?.Display?.Filter, SystemChoice(w, sys), w.Settings.Display?.Filter, hardware);
    }

    public static bool Smooth(string f) => f is "smooth" or "crt";
    public static int Pattern(string f) => f == "crt" ? 1 : f == "lcd" ? 2 : 0;   // what is laid over: none, CRT, LCD grid

    // One game pixel's overlay: tw x th screen pixels, premultiplied BGRA (black, the alpha is how dark), for a game
    // pixel of sx x sy screen pixels. Null: nothing to lay over (no pattern, or the game's pixels are too small).
    public static uint[] Tile(int pattern, double sx, double sy, out int tw, out int th)
    {
        tw = Math.Max(1, (int)Math.Round(sx)); th = Math.Max(1, (int)Math.Round(sy));
        if (pattern != 1 && pattern != 2) return null;
        bool rows = sy >= (pattern == 1 ? 2 : 3), cols = sx >= 3;   // the true sizes, so 1.5 pixels doesn't round up to scanlines
        if (!rows && !cols) return null;
        var t = new uint[tw * th];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                double a;
                if (pattern == 1)
                {
                    // The beam is brightest in the middle of each game line, darkest between lines.
                    double v = (y + 0.5) / th, row = !rows ? 0 : th == 2 ? (y == 1 ? Scan : 0) : Scan * Math.Pow(Math.Abs(2 * v - 1), 2);
                    double col = cols && x == 0 ? Gap : 0;
                    a = 1 - (1 - row) * (1 - col);
                }
                else a = (cols && x == tw - 1) || (rows && y == th - 1) ? Grid : 0;
                t[y * tw + x] = (uint)Math.Round(Math.Clamp(a, 0, 1) * 255) << 24;
            }
        return t;
    }
}
