// Quiet: nothing of an emulator's or port's own on top of the game: no FPS or
// performance overlays, status bars, OSD messages, toasts, splash screens, "what's new" or warning popups.
// A definition says how ("quiet"); Ionea writes it at every start, so it stays off after the emulator
// saves its settings, after an update and on a new install (where a seed made the file first):
//   "quiet": {
//     "files": { "<file beside the program>": { "format": "ini" | "json", "style": "qt", "sep": " = ",
//                                              "set": { "[Section]key": "value", "Section.key": "value" } } },
//     "env":   { "NAME": "value" },          // for the started program (a port's own switches)
//     "args":  [ "--no-splash" ],            // added to the command line when not there yet
//     "core":  { "core_option": "value" }    // a libretro core's own options (CoreGame)
//   }
// "files" is an object by file, so a family's keys and a member's own merge (Lichii adds to Eden's).
// Within the tested versions missing keys are added (an ini file is made if its folder is there); outside them only
// keys the file already has are changed, as the rest of Ionea writes nothing it doesn't know there.
// Qt ("style": "qt"): each key gets key\default=false too, or the emulator reads its default instead.
// The user can still open the emulator's own window and turn anything back on for one run.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ionea;

public static class Quiet
{
    // Before the emulator or port starts (Launch.cs): its files, then its environment and command line.
    public static void Apply(Emulator e, ProcessStartInfo psi)
    {
        var q = EmuDefs.Of(e)?.Quiet;
        if (q == null || e?.Exe == null) return;
        bool tested = EmuDefs.Tested(e);
        var did = new List<string>();
        foreach (var kv in q.Files ?? new())
        {
            try { int n = WriteFile(Path.Combine(Path.GetDirectoryName(e.Exe) ?? "", kv.Key), kv.Value, tested); if (n > 0) did.Add($"{kv.Key} ({n})"); }
            catch (Exception x) { Log.Write($"quiet: {e.Name}: could not write {kv.Key}: {x.Message}"); }
        }
        if (psi != null)
        {
            foreach (var kv in q.Env ?? new()) { psi.Environment[kv.Key] = kv.Value; did.Add(kv.Key + "=" + kv.Value); }
            var add = (q.Args ?? new()).Where(a => !string.IsNullOrWhiteSpace(a) && !HasArg(psi.Arguments, a)).ToList();
            if (add.Count > 0) { psi.Arguments = (psi.Arguments ?? "").TrimEnd() + " " + string.Join(" ", add); did.Add(string.Join(" ", add)); }
        }
        if (did.Count > 0) Log.Write($"quiet: {e.Name}{(tested ? "" : " (untested version: only keys it has)")}: {string.Join(", ", did)}");
    }

    static bool HasArg(string args, string a) => (" " + (args ?? "") + " ").Contains(" " + a.Trim() + " ", StringComparison.OrdinalIgnoreCase);

    // How many keys changed. Public for the test (test\quiet.txt).
    public static int WriteFile(string file, DefQuietFile f, bool tested)
    {
        if (f?.Set == null || f.Set.Count == 0) return 0;
        bool exists = File.Exists(file);
        if (f.Format == "json")
        {
            if (!exists) return 0;   // a half file could stop it starting: it makes its own on the first run
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root) return 0;
            int n = 0;
            foreach (var kv in f.Set)
            {
                var p = kv.Key.Split('.', 2);
                if (p.Length < 2) continue;
                if (root[p[0]] is not JsonObject sec) { if (!tested) continue; sec = new JsonObject(); root[p[0]] = sec; }
                if (!tested && !sec.ContainsKey(p[1])) continue;
                JsonNode v = kv.Value is "true" or "false" ? JsonValue.Create(kv.Value == "true")
                           : long.TryParse(kv.Value, out long l) ? JsonValue.Create(l) : JsonValue.Create(kv.Value);
                if (sec[p[1]]?.ToJsonString() == v.ToJsonString()) continue;
                sec[p[1]] = v; n++;
            }
            if (n > 0) WriteText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return n;
        }
        // ini / toml
        // A missing file is made only in a folder that is there: a new user\ folder would turn some emulators portable.
        if (!exists && (!tested || !Directory.Exists(Path.GetDirectoryName(file)))) return 0;
        string text = exists ? File.ReadAllText(file) : "";
        var ini = new IniText(text, f.Sep ?? "=");
        bool qt = f.Style == "qt";
        int c = 0;
        foreach (var kv in f.Set)
        {
            var (sec, key) = Tweaks.SplitKey(kv.Key);
            string have = ini.Get(sec, key);
            if (have == null && !tested) continue;
            if (have != kv.Value) { ini.Set(sec, key, kv.Value); c++; }
            if (qt && ini.Get(sec, key + "\\default") != "false") { ini.Set(sec, key + "\\default", "false"); if (have == kv.Value) c++; }
        }
        if (c > 0) WriteText(file, ini.Text);
        return c;
    }

    // Through a .tmp file; the emulator's own file is kept once as <file>.before-launcher (as Tweaks does).
    static void WriteText(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);   // a full path
        string keep = file + ".before-launcher";
        if (File.Exists(file) && !File.Exists(keep)) File.Copy(file, keep);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, true);
    }

    // A libretro core's own message options, over the saved ones (CoreGame, before the core lists them).
    public static void Core(string dll, Dictionary<string, string> saved)
    {
        var q = EmuDefs.ForExe(dll)?.Quiet?.Core;
        if (q == null || saved == null) return;
        foreach (var kv in q) saved[kv.Key] = kv.Value;
        Log.Write($"quiet: {Path.GetFileName(dll)}: {string.Join(", ", q.Select(kv => kv.Key + "=" + kv.Value))}");
    }
}
