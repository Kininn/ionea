#nullable disable
// This PC's folders. ES-DE, the Inbox, emulator and game folders are settings
// (Settings.Paths), filled by the scan (Scan.cs) or by hand. No folder is built in, so every PC starts the same
// way.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ionea;

public sealed class PathPrefs
{
    public int Version { get; set; }                       // 0: never set; 1: set
    public string Esde { get; set; }                       // ES-DE's folder (its ROMs, Emulators and covers), or null
    public string Inbox { get; set; }                      // a folder of checked downloads the user trusts, or null
    public List<string> Emulators { get; set; } = new();   // folders emulators live in; the first gets installs
    public List<GameFolder> Games { get; set; } = new();   // game folders (the scan's, or added): New game files looks in them
}

public sealed class GameFolder
{
    public string Path { get; set; }
    public string System { get; set; }   // the system its games are for when the scan could tell, or null
    public string From { get; set; }     // where it was found ("ES-DE", "RetroArch", "Dolphin", ...), for the results screen
}

public static class Paths
{
    // At start: the options' folders (tests' --esde / --inbox win over the settings).
    public static void Apply(Settings s, Options o)
    {
        var p = s.Paths;
        if (p.Version < 1)
        {
            p.Version = 1;
            s.Save();
            Log.Write($"paths: ES-DE {p.Esde ?? "none"}, Inbox {p.Inbox ?? "none"}, emulators {(p.Emulators.Count == 0 ? "Ionea's own folder" : string.Join("; ", p.Emulators))}");
        }
        o.EsdeDir ??= p.Esde;
        AddGameLogic.Inbox = o.InboxDir ?? p.Inbox;
    }

    // The emulator folders: a test's --emulators alone, else the settings' (ES-DE's Emulators folder first).
    public static List<string> EmuFolders(Settings s, Options o)
    {
        if (o.EmuDir != null) return new() { o.EmuDir };
        var l = new List<string>();
        if (o.EsdeDir != null && Directory.Exists(Path.Combine(o.EsdeDir, "Emulators"))) l.Add(Path.Combine(o.EsdeDir, "Emulators"));
        l.AddRange(s.Paths.Emulators);
        return l;
    }

    // A game folder the user confirmed (results screen) or added.
    public static bool AddGames(Settings s, string path, string system, string from)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        if (s.Paths.Games.Any(g => string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase))) return false;
        s.Paths.Games.Add(new GameFolder { Path = path, System = system, From = from });
        return true;
    }

    public static bool AddEmulators(Settings s, string path)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        if (s.Paths.Emulators.Any(g => string.Equals(g, path, StringComparison.OrdinalIgnoreCase))) return false;
        s.Paths.Emulators.Add(path);
        return true;
    }
}
