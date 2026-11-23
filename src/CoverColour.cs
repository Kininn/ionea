#nullable disable
// Cover colour: a game that shows its name instead of a picture can have a coloured tile, a
// solid colour or a soft gradient, instead of the plain one. Kept per game (Game.CoverColour, null = plain);
// chosen in Game info > Cover colour and shown everywhere a name tile is drawn.
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Ionea;

public static class CoverColour
{
    // B null: a solid colour. Each is dark enough for white text.
    public sealed record Choice(string Id, string Name, string A, string B);

    public static readonly Choice[] All =
    {
        new(null, "Plain", null, null),
        new("slate", "Slate", "4A5568", null),
        new("ocean", "Ocean", "1F5F99", null),
        new("forest", "Forest", "2F6B4F", null),
        new("berry", "Berry", "8A2F5E", null),
        new("sunset", "Sunset", "E07A4F", "A8325E"),
        new("aurora", "Aurora", "1FA2C8", "5B45C9"),
        new("dusk", "Dusk", "35306E", "B2557E"),
        new("lagoon", "Lagoon", "2A9D8F", "1D4E89"),
    };

    public static Choice Of(Game g) => All.FirstOrDefault(c => c.Id != null && c.Id == g?.CoverColour) ?? All[0];
    public static int IndexOf(Game g) => System.Array.IndexOf(All, Of(g));
    public static bool Coloured(Game g) => Of(g).A != null;

    // The name tile's fill: the plain tile, a colour, or a diagonal gradient (top left to bottom right).
    public static Brush Fill(Game g)
    {
        var c = Of(g);
        if (c.A == null) return Palette.I.TileFallback;
        if (c.B == null) { var s = new SolidColorBrush(Palette.Hex(c.A)); s.Freeze(); return s; }
        var b = new LinearGradientBrush(Palette.Hex(c.A), Palette.Hex(c.B), new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    // The name's colour on it, and a softer one for icons and letters.
    public static Brush Ink(Game g) => Coloured(g) ? Brushes.White : Palette.I.Text;
    public static Brush Ink2(Game g) => Coloured(g) ? new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)) : Palette.I.Text2;

    public static void Set(MainWindow w, Game g, int i)
    {
        string id = All[(i % All.Length + All.Length) % All.Length].Id;
        if (g.CoverColour == id) return;
        g.CoverColour = id;
        w.Lib.Save();
        Log.Write($"cover colour: \"{g.Name}\" {Of(g).Name}");
        w.Home?.Rebuild(g.Id);
    }
}
