#nullable disable
// UI text by stable id. English is in assets\lang\en.json; another language is one more file there
// (embedded as lang.<id>) or in <data>\lang\<id>.json. T("id") gives the active language's text, else English,
// else the id itself. T("id", args) fills {0}, {1}, ... N("id", count) picks the "one" or "other" form ({0} is the
// count). A file with "_pseudo": true is the test pseudo-locale: every English string comes out [bracketed, accented
// and ~30 % longer], so text that was never moved here, or that gets cut off, is easy to spot.
// Settings.Language: "auto" (or null) follows the Windows display language when there is a file for it, else English.
// Not for log lines, file names or definition data (system and emulator names).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ionea;

public static class Loc
{
    public const string English = "en", Pseudo = "xx-pseudo";

    sealed class Lang
    {
        public string Id, Name;
        public bool IsPseudo;
        public readonly Dictionary<string, string> Text = new();                       // id -> text
        public readonly Dictionary<string, Dictionary<string, string>> Plural = new();  // id -> form ("one", "other") -> text
    }

    static readonly object gate = new();
    static Lang en, cur;
    static string dataDir;
    static readonly HashSet<string> missing = new();
    static Dictionary<string, string> toEnglish;
#if DEBUG
    const bool DebugBuild = true;
#else
    const bool DebugBuild = false;
#endif

    public static string Current { get { Ensure(); return cur.Id; } }

    // The languages there are files for: id and the name to show (a file's "_name").
    public static List<(string Id, string Name)> Languages()
    {
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in typeof(Loc).Assembly.GetManifestResourceNames())
            if (r.StartsWith("lang.")) ids.Add(r[5..]);
        if (dataDir != null && Directory.Exists(Path.Combine(dataDir, "lang")))
            foreach (var f in Directory.GetFiles(Path.Combine(dataDir, "lang"), "*.json")) ids.Add(Path.GetFileNameWithoutExtension(f));
        var list = new List<(string, string)>();
        foreach (var id in ids) { var l = Read(id); if (l != null) list.Add((l.Id, l.Name)); }
        return list;
    }

    // Picks and loads the language for a Language setting ("auto", null or an id). dir: the data folder (its lang\).
    public static void Use(string setting, string dir = null)
    {
        lock (gate)
        {
            dataDir = dir ?? dataDir;
            en = Read(English) ?? new Lang { Id = English, Name = "English" };
            string id = Resolve(setting, CultureInfo.CurrentUICulture.Name, Languages().Select(l => l.Id));
            cur = id == English ? en : Read(id) ?? en;
            toEnglish = null;
            missing.Clear();
            if (cur != en) Log.Write($"language: {cur.Id}");
        }
    }

    // Which language a setting means: an id that has a file; "auto" or null: the Windows display language
    // (e.g. "nb-NO", then "nb") when there is a file for it; else English. The pseudo-locale is never automatic.
    internal static string Resolve(string setting, string uiCulture, IEnumerable<string> available)
    {
        var have = new HashSet<string>(available ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        string Has(string id) => id != null && have.Contains(id) ? have.First(h => h.Equals(id, StringComparison.OrdinalIgnoreCase)) : null;
        if (!string.IsNullOrEmpty(setting) && !setting.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return Has(setting) ?? English;
        if (string.IsNullOrEmpty(uiCulture)) return English;
        string full = Has(uiCulture), lang = Has(uiCulture.Split('-')[0]);
        string pick = full ?? lang;
        return pick == null || pick.Equals(Pseudo, StringComparison.OrdinalIgnoreCase) ? English : pick;
    }

    public static string T(string id)
    {
        Ensure();
        return Find(id) ?? id;
    }

    public static string T(string id, params object[] args) => Fill(id, T(id), args);

    // A count string: the "one" form for 1, else "other". {0} is the count, {1}... are args.
    public static string N(string id, long count, params object[] args)
    {
        Ensure();
        string form = count == 1 ? "one" : "other";
        string s = FindPlural(id, form) ?? FindPlural(id, "other") ?? id;
        var all = new object[(args?.Length ?? 0) + 1];
        all[0] = count;
        args?.CopyTo(all, 1);
        return Fill(id, s, all);
    }

    // The English text for a string in the active language (or the string itself): rows pick their picture by it.
    public static string ToEnglish(string text)
    {
        if (text == null) return null;
        Ensure();
        if (cur == en) return text;
        var map = toEnglish;
        if (map == null)
        {
            map = new Dictionary<string, string>();
            foreach (var (id, e) in en.Text)
                map.TryAdd(cur.Text.TryGetValue(id, out var s) ? s : cur.IsPseudo ? PseudoOf(e) : e, e);
            toEnglish = map;
        }
        return map.TryGetValue(text, out var english) ? english : text;
    }

    static void Ensure() { if (cur == null) Use(null); }

    static string Find(string id)
    {
        if (id == null) return null;
        if (cur.Text.TryGetValue(id, out var s)) return s;
        if (cur.IsPseudo) return en.Text.TryGetValue(id, out var e) ? PseudoOf(e) : Miss(id, null);
        return en.Text.TryGetValue(id, out var f) ? Miss(id, f) : Miss(id, null);
    }

    static string FindPlural(string id, string form)
    {
        Dictionary<string, string> forms;
        if (cur.IsPseudo) return en.Plural.TryGetValue(id, out forms) && forms.TryGetValue(form, out var p) ? PseudoOf(p) : null;
        if (cur.Plural.TryGetValue(id, out forms) && forms.TryGetValue(form, out var s)) return s;
        if (en.Plural.TryGetValue(id, out forms) && forms.TryGetValue(form, out var f)) { if (cur != en) Miss(id, f); return f; }
        if (form == "other") Miss(id, null);
        return null;
    }

    // Logged once per id: always when English has no such id (a bug), else in debug builds and the pseudo-locale.
    static string Miss(string id, string fallback)
    {
        bool log = fallback == null || DebugBuild || cur.IsPseudo;
        if (log) lock (gate) if (missing.Add(id)) Log.Write(fallback == null ? $"language: no text for \"{id}\"" : $"language: {cur.Id} has no \"{id}\", English used");
        return fallback;
    }

    static string Fill(string id, string s, object[] args)
    {
        if (args == null || args.Length == 0) return s;
        try { return string.Format(CultureInfo.InvariantCulture, s, args); }
        catch (FormatException) { lock (gate) if (missing.Add(id + "#format")) Log.Write($"language: bad placeholders in \"{id}\""); return s; }
    }

    static Lang Read(string id)
    {
        try
        {
            string json = null;
            if (dataDir != null)
            {
                string f = Path.Combine(dataDir, "lang", id + ".json");
                if (File.Exists(f)) json = File.ReadAllText(f);
            }
            if (json == null)
            {
                string res = typeof(Loc).Assembly.GetManifestResourceNames().FirstOrDefault(r => r.Equals("lang." + id, StringComparison.OrdinalIgnoreCase));
                if (res == null) return null;
                using var st = typeof(Loc).Assembly.GetManifestResourceStream(res);
                using var rd = new StreamReader(st, Encoding.UTF8);
                json = rd.ReadToEnd();
            }
            return Parse(id, json);
        }
        catch (Exception e) { Log.Write($"language: {id} can't be read: {e.Message}"); return null; }
    }

    // { "_name": "English", "id": "text", "id2": { "one": "{0} game", "other": "{0} games" } }
    static Lang Parse(string id, string json)
    {
        var l = new Lang { Id = id, Name = id };
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name == "_name") { l.Name = p.Value.GetString(); continue; }
            if (p.Name == "_pseudo") { l.IsPseudo = p.Value.ValueKind == JsonValueKind.True; continue; }
            if (p.Name.StartsWith("_")) continue;
            if (p.Value.ValueKind == JsonValueKind.String) l.Text[p.Name] = p.Value.GetString();
            else if (p.Value.ValueKind == JsonValueKind.Object)
                l.Plural[p.Name] = p.Value.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.String).ToDictionary(f => f.Name, f => f.Value.GetString());
        }
        return l;
    }

    // [Ŧĥé ŧéxŧ ~~~]: letters accented, placeholders kept, about 30 % longer.
    internal static string PseudoOf(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        const string from = "aceinorsuyACEINORSUY", to = "åçéîñöŕšüýÅÇÉÎÑÖŔŠÜÝ";
        var b = new StringBuilder("[");
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '{') depth++;
            if (c == '}' && depth > 0) { depth--; b.Append(c); continue; }
            int i = depth == 0 ? from.IndexOf(c) : -1;
            b.Append(i >= 0 ? to[i] : c);
        }
        b.Append(' ').Append('~', Math.Max(1, (int)Math.Ceiling(s.Length * 0.3) - 1)).Append(']');
        return b.ToString();
    }
}
