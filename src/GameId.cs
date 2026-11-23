#nullable disable
// Game id readers: a game file's own id, version, title and region, read from its header. A system
// names its reader in systems.json ("reader"); an emulator's definition may name one for its per-game
// settings file (video.gameId). Nothing is decrypted; files that need keys (Switch, encrypted 3DS CIA)
// have no reader. Icons are read by RomArt (DS, 3DS, PS4).
// Header offsets are from public format notes (GBATEK, Pan Docs, 3dbrew, wiibrew, psdevwiki): verify.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ionea;

public sealed class GameInfo
{
    public string Id;
    public string Version;
    public string Title;
    public string Region;     // "USA", "Europe", … or null (Game info)
    public override string ToString() => $"{Id}|{Version}|{Title}";
}

public static class GameId
{
    public static readonly string[] Readers = { "ps4-sfo", "ps3-sfo", "psp-pbp", "gba-header", "gb-header", "nds-header", "gc-disc", "3ds-ncsd", "wiiu-meta", "ps-cnf" };

    // The reader the game's system names, or null.
    public static GameInfo ForGame(string system, string rom) => Read(EmuCatalog.System(system)?.Reader, rom);

    public static GameInfo Read(string reader, string rom)
    {
        if (reader == null || string.IsNullOrEmpty(rom) || !File.Exists(rom)) return null;
        try
        {
            var r = reader switch
            {
                "ps4-sfo" => Ps4(rom),
                "ps3-sfo" => Ps3(rom),
                "psp-pbp" => Pbp(rom),
                "gba-header" => Gba(rom),
                "gb-header" => Gb(rom),
                "nds-header" => Nds(rom),
                "gc-disc" => Disc(rom),
                "3ds-ncsd" => Ctr(rom),
                "wiiu-meta" => WiiU(rom),
                "ps-cnf" => Cnf(rom),
                _ => null,
            };
            return r?.Id == null ? null : r;
        }
        catch (Exception e) { Log.Write($"game id ({reader}) {Path.GetFileName(rom)}: {e.Message}"); return null; }
    }

    static byte[] Head(string file, long at, int n)
    {
        using var f = File.OpenRead(file);
        if (f.Length < at + n) return null;
        f.Position = at;
        var b = new byte[n];
        int got = 0;
        while (got < n) { int k = f.Read(b, got, n - got); if (k <= 0) return null; got += k; }
        return b;
    }
    static string Ascii(byte[] b, int at, int n)
    {
        string s = Encoding.ASCII.GetString(b, at, n);
        int z = s.IndexOf('\0'); if (z >= 0) s = s[..z];
        return s.Trim();
    }
    static bool Code(string s) => s.Length > 0 && s.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    // ---- PlayStation: param.sfo (PSF) ----
    public static Dictionary<string, string> Sfo(byte[] b)
    {
        var d = new Dictionary<string, string>();
        if (b == null || b.Length < 20 || BitConverter.ToUInt32(b, 0) != 0x46535000) return d;   // "\0PSF"
        int keys = BitConverter.ToInt32(b, 8), data = BitConverter.ToInt32(b, 12), n = BitConverter.ToInt32(b, 16);
        for (int i = 0; i < n && 20 + i * 16 + 16 <= b.Length; i++)
        {
            int e = 20 + i * 16;
            int ko = BitConverter.ToUInt16(b, e), fmt = BitConverter.ToUInt16(b, e + 2), len = BitConverter.ToInt32(b, e + 4), dof = BitConverter.ToInt32(b, e + 12);
            int k0 = keys + ko, k1 = Array.IndexOf(b, (byte)0, k0);
            if (k0 >= b.Length || k1 < 0 || data + dof + len > b.Length) continue;
            string key = Encoding.ASCII.GetString(b, k0, k1 - k0);
            d[key] = fmt == 0x0404 ? BitConverter.ToUInt32(b, data + dof).ToString() : Encoding.UTF8.GetString(b, data + dof, len).TrimEnd('\0');
        }
        return d;
    }
    static GameInfo FromSfo(Dictionary<string, string> d, string idKey) =>
        d.TryGetValue(idKey, out var id) && Regex.IsMatch(id, @"^[A-Z]{4}\d{5}$")
            ? new GameInfo { Id = id, Version = d.GetValueOrDefault("APP_VER") ?? d.GetValueOrDefault("VERSION") ?? d.GetValueOrDefault("DISC_VERSION"), Title = d.GetValueOrDefault("TITLE"), Region = SonyRegion(id) }
            : null;

    // ---- regions (from public id conventions: verify) ----
    // PS4 content id "UP9000-CUSA00900_00-…": its first two letters.
    public static string Ps4Region(string contentId) => contentId == null || contentId.Length < 2 ? null : contentId[..2] switch
    {
        "UP" => "USA", "EP" => "Europe", "JP" => "Japan", "HP" => "Asia", "KP" => "Korea", _ => null,
    };
    // PS3 / PSP ids "BLUS30443", "ULES00151", "NPUB…": the third letter.
    static string SonyRegion(string id) => id.Length < 3 ? null : id[2] switch
    {
        'U' => "USA", 'E' => "Europe", 'J' => "Japan", 'A' => "Asia", 'K' => "Korea", 'H' => "Asia", _ => null,
    };
    // Nintendo game codes (GBA, DS, GameCube, Wii: the fourth letter; 3DS: the last of the product code).
    public static string NintendoRegion(char c) => c switch
    {
        'E' => "USA", 'P' => "Europe", 'J' => "Japan", 'K' => "Korea", 'A' => "All regions", 'D' => "Germany", 'F' => "France",
        'S' => "Spain", 'I' => "Italy", 'U' => "Australia", 'C' => "China", 'X' or 'Y' or 'Z' => "Europe", _ => null,
    };
    // Wii U meta.xml region bits: 1 Japan, 2 USA, 4 Europe.
    static string WiiURegion(string hex)
    {
        if (hex == null || !uint.TryParse(hex.Trim(), System.Globalization.NumberStyles.HexNumber, null, out uint r) || r == 0) return null;
        if ((r & 7) == 7) return "All regions";
        var l = new List<string>();
        if ((r & 2) != 0) l.Add("USA"); if ((r & 4) != 0) l.Add("Europe"); if ((r & 1) != 0) l.Add("Japan");
        return l.Count == 0 ? null : string.Join(", ", l);
    }

    // "Bloodborne [CUSA00900]\eboot.bin" -> CUSA00900; otherwise TITLE_ID from sce_sys\param.sfo.
    static GameInfo Ps4(string eboot)
    {
        string dir = Path.GetDirectoryName(eboot) ?? "";
        string sfo = Path.Combine(dir, "sce_sys", "param.sfo");
        var d = File.Exists(sfo) ? Sfo(File.ReadAllBytes(sfo)) : new();
        var m = Regex.Match(Path.GetFileName(dir), @"\b([A-Z]{4}\d{5})\b");
        var r = m.Success ? new GameInfo { Id = m.Groups[1].Value, Version = d.GetValueOrDefault("APP_VER"), Title = d.GetValueOrDefault("TITLE") } : FromSfo(d, "TITLE_ID");
        if (r != null) r.Region = Ps4Region(d.GetValueOrDefault("CONTENT_ID"));
        return r;
    }

    // PS3 game folder: <game>\PS3_GAME\USRDIR\EBOOT.BIN, the id in PS3_GAME\PARAM.SFO.
    static GameInfo Ps3(string rom)
    {
        for (string dir = Path.GetDirectoryName(rom); dir != null && dir.Length > 3; dir = Path.GetDirectoryName(dir))
        {
            foreach (string f in new[] { Path.Combine(dir, "PARAM.SFO"), Path.Combine(dir, "PS3_GAME", "PARAM.SFO") })
                if (File.Exists(f)) return FromSfo(Sfo(File.ReadAllBytes(f)), "TITLE_ID");
            if (Path.GetFileName(dir).Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }

    // PSP .pbp: "\0PBP", then the offsets of its parts; PARAM.SFO is the first. A PSP .iso: PSP_GAME\PARAM.SFO.
    static GameInfo Pbp(string rom)
    {
        if (Path.GetExtension(rom).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            using var iso = Iso.Open(rom);
            return iso?.Read("PSP_GAME/PARAM.SFO", 1 << 16) is byte[] psf ? FromSfo(Sfo(psf), "DISC_ID") : null;
        }
        var h = Head(rom, 0, 0x28);
        if (h == null || BitConverter.ToUInt32(h, 0) != 0x50425000) return null;
        int sfo = BitConverter.ToInt32(h, 0x08), next = BitConverter.ToInt32(h, 0x0C);
        if (sfo <= 0 || next <= sfo || next - sfo > 1 << 16) return null;
        return FromSfo(Sfo(Head(rom, sfo, next - sfo)), "DISC_ID");
    }

    // ---- Nintendo handhelds ----
    // GBA: title 0xA0 (12), game code 0xAC (4), fixed 0x96 at 0xB2, version 0xBC.
    static GameInfo Gba(string rom)
    {
        var h = Head(rom, 0, 0xC0);
        if (h == null || h[0xB2] != 0x96) return null;
        string code = Ascii(h, 0xAC, 4);
        return Code(code) ? new GameInfo { Id = code, Version = h[0xBC].ToString(), Title = Ascii(h, 0xA0, 12), Region = code.Length == 4 ? NintendoRegion(code[3]) : null } : null;
    }

    // Game Boy / Color: the logo starts CE ED 66 66 at 0x104; title 0x134 (up to 16), version 0x14C. No game code:
    // the id is the title.
    static GameInfo Gb(string rom)
    {
        var h = Head(rom, 0, 0x150);
        if (h == null || h[0x104] != 0xCE || h[0x105] != 0xED || h[0x106] != 0x66 || h[0x107] != 0x66) return null;
        bool cgb = (h[0x143] & 0x80) != 0;
        string title = Ascii(h, 0x134, cgb ? 15 : 16);
        if (cgb && Code(Ascii(h, 0x13F, 4)) && title.Length > 11) title = Ascii(h, 0x134, 11);   // newer: 11 + a 4-letter code
        return title.Length == 0 ? null : new GameInfo { Id = title, Version = h[0x14C].ToString(), Title = title };
    }

    // DS / DSi: title 0x00 (12), game code 0x0C (4), version 0x1E.
    static GameInfo Nds(string rom)
    {
        var h = Head(rom, 0, 0x20);
        if (h == null) return null;
        string code = Ascii(h, 0x0C, 4);
        return Code(code) && code.Length == 4 ? new GameInfo { Id = code, Version = h[0x1E].ToString(), Title = Ascii(h, 0, 12), Region = NintendoRegion(code[3]) } : null;
    }

    // 3DS .3ds / .cci (NCSD: partition 0 at 0x120, in 0x200 units) or .cxi (NCCH at 0). NCCH: "NCCH" at 0x100,
    // program id 0x118 (8, little-endian), version 0x112, product code 0x150 (16).
    static GameInfo Ctr(string rom)
    {
        long at = 0;
        var n = Head(rom, 0x100, 0x30);
        if (n == null) return null;
        if (Encoding.ASCII.GetString(n, 0, 4) == "NCSD") at = (long)BitConverter.ToUInt32(n, 0x20) * 0x200;
        var c = Head(rom, at, 0x160);
        if (c == null || Encoding.ASCII.GetString(c, 0x100, 4) != "NCCH") return null;
        string product = Ascii(c, 0x150, 16);
        ulong program = BitConverter.ToUInt64(c, 0x118);
        return new GameInfo { Id = program.ToString("X16"), Version = BitConverter.ToUInt16(c, 0x112).ToString(), Title = product, Region = product.Length > 0 ? NintendoRegion(product[^1]) : null };
    }

    // GameCube / Wii disc (.iso, .gcm, .wbfs): id 0x00 (6), version 0x07, magic (Wii 0x18, GameCube 0x1C), title 0x20.
    // .wbfs: "WBFS" at 0, the disc header at 0x200. .rvz / .wia: "RVZ" or "WIA" and 0x01 at 0, the disc
    // header kept as it is at 0x58 (after the 0x48 header and four numbers; verify on a real .rvz).
    // .gcz, .ciso: none.
    static GameInfo Disc(string rom)
    {
        long at = 0;
        var w = Head(rom, 0, 4);
        string magic = w == null ? "" : Encoding.ASCII.GetString(w);
        if (magic == "WBFS") at = 0x200;
        else if (magic == "RVZ\x01" || magic == "WIA\x01") at = 0x58;
        var h = Head(rom, at, 0x60);
        if (h == null) return null;
        uint wii = (uint)(h[0x18] << 24 | h[0x19] << 16 | h[0x1A] << 8 | h[0x1B]), gc = (uint)(h[0x1C] << 24 | h[0x1D] << 16 | h[0x1E] << 8 | h[0x1F]);
        if (wii != 0x5D1C9EA3 && gc != 0xC2339F3D) return null;
        string id = Ascii(h, 0, 6);
        return Code(id) ? new GameInfo { Id = id, Version = h[0x07].ToString(), Title = Ascii(h, 0x20, 0x40), Region = id.Length >= 4 ? NintendoRegion(id[3]) : null } : null;
    }

    // Wii U game folder: code\<game>.rpx, the id in meta\meta.xml (title_id, title_version, longname_en).
    static GameInfo WiiU(string rom)
    {
        string meta = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(rom) ?? "") ?? "", "meta", "meta.xml");
        if (!File.Exists(meta)) return null;
        var x = System.Xml.Linq.XDocument.Load(meta).Root;
        string id = x?.Element("title_id")?.Value?.Trim();
        return string.IsNullOrEmpty(id) ? null : new GameInfo { Id = id.ToUpperInvariant(), Version = x.Element("title_version")?.Value?.Trim(), Title = x.Element("longname_en")?.Value?.Trim(), Region = WiiURegion(x.Element("region")?.Value) };
    }

    // PlayStation / PlayStation 2 disc (.iso, .bin, .cue): SYSTEM.CNF names the boot file, "SLUS_200.62" -> SLUS-20062.
    static GameInfo Cnf(string rom)
    {
        if (Path.GetExtension(rom).Equals(".cue", StringComparison.OrdinalIgnoreCase)) rom = Cue(rom);
        if (rom == null) return null;
        using var iso = Iso.Open(rom);
        string boot = iso == null ? null : CnfBoot(iso, out _);
        var m = boot == null ? null : Regex.Match(boot, @"([A-Z]{4})[_-]?(\d{3})\.?(\d{2})");
        if (m == null || !m.Success) return null;
        return new GameInfo { Id = $"{m.Groups[1].Value}-{m.Groups[2].Value}{m.Groups[3].Value}", Region = SonyRegion(m.Groups[1].Value) };
    }

    static string CnfBoot(Iso iso, out bool ps2)
    {
        ps2 = false;
        var b = iso.Read("SYSTEM.CNF", 4096);
        if (b == null) return null;
        var m = Regex.Match(Encoding.ASCII.GetString(b), @"(?im)^\s*BOOT(2?)\s*=\s*(\S+)");
        if (!m.Success) return null;
        ps2 = m.Groups[1].Value == "2";
        return m.Groups[2].Value.ToUpperInvariant();
    }

    // ---- which system a game file is for, from its header ----
    // A system id, or null when the header doesn't say (then the file type, a folder name or the user decides).
    public static string SystemOf(string file)
    {
        try
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (RomLogic.IsEboot(file)) return "ps4";
            if (Path.GetFileName(file).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) && Ps3(file) != null) return "ps3";
            if (ext == ".rpx") return "wiiu";
            if (ext is ".nsp" or ".xci" or ".nca" or ".nro") return "switch";
            if (ext == ".cue") return Cue(file) is string bin ? SystemOf(bin) : null;
            if (ext == ".m3u" || ext == ".chd") return null;   // a list, or compressed: the folder or the user decides
            long size = new FileInfo(file).Length;
            var h = Head(file, 0, (int)Math.Min(0x200, size));
            if (h == null || h.Length < 16) return null;
            string A(int at, int n) => at + n <= h.Length ? Encoding.ASCII.GetString(h, at, n) : "";
            if (A(0, 4) == "NES\x1A") return "nes";
            if (h.Length >= 0x150 && h[0x104] == 0xCE && h[0x105] == 0xED && h[0x106] == 0x66 && h[0x107] == 0x66)
                return (h[0x143] & 0x80) != 0 ? "gbc" : "gb";
            if (h.Length >= 0xC0 && h[0xB2] == 0x96 && Code(Ascii(h, 0xAC, 4)) && ext is ".gba" or ".bin" or ".agb") return "gba";
            if (h.Length >= 0x15E && h[0x15C] == 0x56 && h[0x15D] == 0xCF) return "nds";
            if (A(0x100, 4) is "NCSD" or "NCCH") return "3ds";
            if (A(0, 4) == "WBFS") return "wii";
            if (A(0, 4) is "RVZ\x01" or "WIA\x01")
            {
                var t = Head(file, 0x48, 4);
                uint n = t == null ? 0 : (uint)(t[0] << 24 | t[1] << 16 | t[2] << 8 | t[3]);
                return n == 1 ? "gc" : n == 2 ? "wii" : null;
            }
            if (ext == ".gcz" && h.Length >= 8) { uint n = BitConverter.ToUInt32(h, 4); return n == 0 ? "gc" : n == 1 ? "wii" : null; }
            if (h.Length >= 0x20)
            {
                uint wii = (uint)(h[0x18] << 24 | h[0x19] << 16 | h[0x1A] << 8 | h[0x1B]), gc = (uint)(h[0x1C] << 24 | h[0x1D] << 16 | h[0x1E] << 8 | h[0x1F]);
                if (gc == 0xC2339F3D) return "gc";
                if (wii == 0x5D1C9EA3) return "wii";
            }
            if (BitConverter.ToUInt32(h, 0) == 0x50425000) return Pbp1(file) ? "psx" : "psp";
            // Sega: a cartridge says "SEGA" at 0x100; Sega CD and Saturn discs name themselves in their first sector
            // (at 0, or at 0x10 in a raw 2352-byte sector).
            if (A(0x100, 4) == "SEGA" && ext is ".md" or ".gen" or ".bin" or ".smd") return "genesis";
            foreach (int at in new[] { 0, 0x10 })
            {
                if (A(at, 15) == "SEGA SEGASATURN") return "saturn";
                if (A(at, 14) == "SEGADISCSYSTEM") return "segacd";
            }
            // ISO 9660 discs: PSP, PS3, PS2, PlayStation
            using var iso = Iso.Open(file);
            if (iso != null)
            {
                if (iso.Has("PSP_GAME/PARAM.SFO")) return "psp";
                if (iso.Has("PS3_DISC.SFB") || iso.Has("PS3_GAME/PARAM.SFO")) return "ps3";
                if (CnfBoot(iso, out bool ps2) != null) return ps2 ? "ps2" : "psx";
            }
        }
        catch (Exception e) { Log.Write($"system of {Path.GetFileName(file)}: {e.Message}"); }
        return null;
    }

    // A PlayStation game made for the PSP's player: its DATA.PSAR starts "PSISOIMG" or "PSTITLEIMG".
    static bool Pbp1(string file)
    {
        var h = Head(file, 0, 0x28);
        if (h == null) return false;
        var p = Head(file, BitConverter.ToUInt32(h, 0x24), 10);
        string t = p == null ? "" : Encoding.ASCII.GetString(p);
        return t.StartsWith("PSISOIMG") || t.StartsWith("PSTITLEIMG");
    }

    // The first data file a .cue names (beside it), or null.
    public static string Cue(string cue)
    {
        try
        {
            if (new FileInfo(cue).Length > 1 << 16) return null;
            var m = Regex.Match(File.ReadAllText(cue), "(?im)^\\s*FILE\\s+\"([^\"]+)\"");
            string f = m.Success ? Path.Combine(Path.GetDirectoryName(cue) ?? "", m.Groups[1].Value) : null;
            return f != null && File.Exists(f) ? f : null;
        }
        catch { return null; }
    }
}

// A read-only ISO 9660 disc image: plain 2048-byte sectors, or raw 2352-byte CD sectors (mode 1 or mode 2).
// Only directory records and small files are read. Layout from ECMA-119 (verify on a real raw .bin).
public sealed class Iso : IDisposable
{
    readonly FileStream f;
    readonly int sector, skip;   // bytes per sector, and before each sector's data
    readonly long rootAt; readonly int rootLen;

    Iso(FileStream s, int sec, int sk, long at, int len) { f = s; sector = sec; skip = sk; rootAt = at; rootLen = len; }

    public static Iso Open(string file)
    {
        FileStream s = null;
        try
        {
            s = File.OpenRead(file);
            foreach (var (sec, sk) in new[] { (2048, 0), (2352, 16), (2352, 24) })
            {
                var pvd = Sector(s, sec, sk, 16);
                if (pvd == null || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") continue;
                return new Iso(s, sec, sk, BitConverter.ToUInt32(pvd, 156 + 2), BitConverter.ToInt32(pvd, 156 + 10));
            }
        }
        catch (Exception caught) { Log.Debug("game id: disc image", caught); }
        s?.Dispose();
        return null;
    }

    static byte[] Sector(Stream s, int sec, int skip, long lba)
    {
        long at = lba * sec + skip;
        if (at < 0 || at + 2048 > s.Length) return null;
        s.Position = at;
        var b = new byte[2048]; int got = 0;
        while (got < 2048) { int k = s.Read(b, got, 2048 - got); if (k <= 0) return null; got += k; }
        return b;
    }

    byte[] Data(long lba, int len)
    {
        var o = new byte[len];
        for (int i = 0; i * 2048 < len; i++)
        {
            var b = Sector(f, sector, skip, lba + i);
            if (b == null) return null;
            Array.Copy(b, 0, o, i * 2048, Math.Min(2048, len - i * 2048));
        }
        return o;
    }

    // "A/B/C.EXT": its extent, size and whether it is a folder, or null.
    (long At, int Len, bool Dir)? Find(string path)
    {
        long at = rootAt; int len = rootLen;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int p = 0; p < parts.Length; p++)
        {
            if (len <= 0 || len > 1 << 20) return null;
            var d = Data(at, len);
            if (d == null) return null;
            bool found = false;
            for (int i = 0; i < d.Length;)
            {
                int rl = d[i];
                if (rl == 0) { i = (i / 2048 + 1) * 2048; continue; }   // a record never crosses a sector
                if (i + 33 > d.Length) break;
                int nl = d[i + 32];
                string name = i + 33 + nl <= d.Length ? Encoding.ASCII.GetString(d, i + 33, nl) : "";
                int semi = name.IndexOf(';'); if (semi >= 0) name = name[..semi];
                name = name.TrimEnd('.');
                if (name.Equals(parts[p], StringComparison.OrdinalIgnoreCase))
                {
                    at = BitConverter.ToUInt32(d, i + 2); len = BitConverter.ToInt32(d, i + 10);
                    bool dir = (d[i + 25] & 2) != 0;
                    if (p == parts.Length - 1) return (at, len, dir);
                    if (!dir) return null;
                    found = true; break;
                }
                i += rl;
            }
            if (!found) return null;
        }
        return null;
    }

    public bool Has(string path) => Find(path) != null;

    // A file's bytes (when it is at most max long), or null.
    public byte[] Read(string path, int max)
    {
        var r = Find(path);
        if (r == null || r.Value.Dir || r.Value.Len <= 0 || r.Value.Len > max) return null;
        return Data(r.Value.At, r.Value.Len);
    }

    public void Dispose() => f.Dispose();
}
