#nullable disable
// Switch game files: what an .nsp / .xci holds (the game, an update or an add-on), its title
// id, version and display version. Reading needs the user's own keys (prod.keys, title.keys): NCA headers are
// AES-XTS with header_key, the sections AES-CTR with a key from the key area or a title key. Nothing is written.
// Layouts are from public format notes (switchbrew): verify. Only the small parts are read (header, cnmt,
// control.nacp); the game itself is never decrypted.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ionea;

public sealed class SwitchKeys
{
    public byte[] Header;                                        // header_key (32 bytes)
    public readonly Dictionary<int, byte[]> KeyArea = new();     // key_area_key_application_XX
    public readonly Dictionary<int, byte[]> TitleKek = new();    // titlekek_XX
    public readonly Dictionary<string, byte[]> TitleKeys = new(StringComparer.OrdinalIgnoreCase);   // rights id -> title key (as in tickets)

    // null when prod.keys is missing or has no header key.
    public static SwitchKeys Load(string prod, string title)
    {
        if (prod == null || !File.Exists(prod)) return null;
        var k = new SwitchKeys();
        foreach (var (name, value) in Lines(prod))
        {
            var m = Regex.Match(name, "^(key_area_key_application|titlekek)_([0-9a-f]{2})$");
            if (name == "header_key" && value.Length == 32) k.Header = value;
            else if (m.Success && value.Length == 16) (m.Groups[1].Value == "titlekek" ? k.TitleKek : k.KeyArea)[Convert.ToInt32(m.Groups[2].Value, 16)] = value;
        }
        if (title != null && File.Exists(title))
            foreach (var (name, value) in Lines(title)) if (name.Length == 32 && value.Length == 16) k.TitleKeys[name] = value;
        return k.Header == null ? null : k;
    }

    static IEnumerable<(string, byte[])> Lines(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var m = Regex.Match(line.Trim(), @"^([A-Za-z0-9_]+)\s*=\s*([0-9a-fA-F]+)$");
            if (m.Success && m.Groups[2].Value.Length % 2 == 0) yield return (m.Groups[1].Value.ToLowerInvariant(), Convert.FromHexString(m.Groups[2].Value));
        }
    }
}

public sealed class SwitchTitle
{
    public ulong Id;          // its own title id
    public ulong App;         // the game it belongs to (the game's own id for the game)
    public uint Version;
    public byte Type;         // 0x80 game, 0x81 update, 0x82 add-on
    public string Display;    // "1.6.0" from control.nacp (game and update), or null
    public string Name;       // from control.nacp, or null
    public byte[] Icon;       // the game's icon (a JPEG, icon_<language>.dat in the control RomFS; English first), or null
    public string IdText => Id.ToString("X16");
    public string Shown => Display ?? "v" + Version;
    public override string ToString() => $"{IdText}|{Type:X2}|{Version}|{Display}";
}

public static class SwitchRead
{
    public const byte Game = 0x80, Update = 0x81, Addon = 0x82;
    public static string LastError;   // why the last Read found nothing (for the log)

    // The titles in an .nsp or .xci (empty when it can't be read).
    public static List<SwitchTitle> Read(string file, SwitchKeys keys)
    {
        var list = new List<SwitchTitle>();
        LastError = null;
        if (keys == null) { LastError = "no keys"; return list; }
        try
        {
            using var s = File.OpenRead(file);
            var files = Container(s);
            if (files == null) { LastError = "not an .nsp or .xci"; return list; }
            var local = new Dictionary<string, byte[]>(keys.TitleKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var t in files.Where(f => f.Name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase)))
            {
                var b = Bytes(s, t.Off, 0x2C0);
                if (b != null && b[0x281] == 0) local[Convert.ToHexString(b, 0x2A0, 16)] = b[0x180..0x190];   // a common ticket's title key
            }
            foreach (var meta in files.Where(f => f.Name.EndsWith(".cnmt.nca", StringComparison.OrdinalIgnoreCase)))
            {
                var nca = Nca.Open(s, meta.Off, keys, local);
                if (nca == null) { LastError ??= "an NCA header couldn't be read with these keys"; continue; }
                var cnmt = CnmtFile(s, nca);
                if (cnmt == null || cnmt.Length < 0x20) { LastError ??= "no content meta"; continue; }
                var t = new SwitchTitle { Id = BitConverter.ToUInt64(cnmt, 0), Version = BitConverter.ToUInt32(cnmt, 8), Type = cnmt[0xC] };
                int ext = BitConverter.ToUInt16(cnmt, 0xE), count = BitConverter.ToUInt16(cnmt, 0x10);
                t.App = t.Type is Update or Addon && cnmt.Length >= 0x28 ? BitConverter.ToUInt64(cnmt, 0x20) : t.Id;
                if (t.Type is not (Game or Update or Addon)) continue;
                for (int i = 0; i < count; i++)
                {
                    int at = 0x20 + ext + i * 0x38;
                    if (at + 0x38 > cnmt.Length || cnmt[at + 0x36] != 3) continue;   // 3: control
                    string id = Convert.ToHexString(cnmt, at + 0x20, 16).ToLowerInvariant() + ".nca";
                    var cf = files.FirstOrDefault(f => f.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (cf.Name != null) Nacp(s, Nca.Open(s, cf.Off, keys, local), t);
                }
                list.Add(t);
            }
            if (list.Count == 0) LastError ??= "no content meta";
        }
        catch (Exception e) { LastError = e.Message; }
        return list;
    }

    // ---- containers: .nsp (PFS0) and .xci (HFS0, its secure partition) ----
    record struct Entry(string Name, long Off, long Size);

    static List<Entry> Container(Stream s)
    {
        var h = Bytes(s, 0, 4);
        if (h == null) return null;
        if (Encoding.ASCII.GetString(h) == "PFS0") return Partition(s, 0, false);
        var x = Bytes(s, 0x100, 0x40);
        if (x == null || Encoding.ASCII.GetString(x, 0, 4) != "HEAD") return null;
        var root = Partition(s, (long)BitConverter.ToUInt64(x, 0x30), true);
        var secure = root?.FirstOrDefault(e => e.Name == "secure") ?? default;
        return secure.Name == null ? null : Partition(s, secure.Off, true);
    }

    // PFS0 (0x18-byte entries) or HFS0 (0x40): "magic", count, string table size; offsets are after the header.
    static List<Entry> Partition(Stream s, long at, bool hfs)
    {
        var h = Bytes(s, at, 0x10);
        if (h == null || Encoding.ASCII.GetString(h, 0, 4) != (hfs ? "HFS0" : "PFS0")) return null;
        int n = BitConverter.ToInt32(h, 4), strs = BitConverter.ToInt32(h, 8), size = hfs ? 0x40 : 0x18;
        if (n <= 0 || n > 4096 || strs < 0 || strs > 1 << 20) return null;
        var b = Bytes(s, at + 0x10, n * size + strs);
        if (b == null) return null;
        long data = at + 0x10 + n * size + strs;
        var l = new List<Entry>();
        for (int i = 0; i < n; i++)
        {
            int e = i * size;
            int so = n * size + BitConverter.ToInt32(b, e + 0x10), z = Array.IndexOf(b, (byte)0, so);
            if (so >= b.Length) continue;
            l.Add(new Entry(Encoding.ASCII.GetString(b, so, (z < 0 ? b.Length : z) - so), data + (long)BitConverter.ToUInt64(b, e), (long)BitConverter.ToUInt64(b, e + 8)));
        }
        return l;
    }

    // ---- NCA ----
    sealed class Nca
    {
        public Stream S; public long At; public byte[] H; public byte[] Key;
        public long Start(int i) => At + BitConverter.ToUInt32(H, 0x240 + i * 0x10) * 0x200L;
        public byte[] Fs(int i) => H[(0x400 + i * 0x200)..(0x600 + i * 0x200)];

        public static Nca Open(Stream s, long at, SwitchKeys k, Dictionary<string, byte[]> titleKeys)
        {
            var raw = Bytes(s, at, 0xC00);
            if (raw == null) return null;
            var h = Xts(raw, k.Header);
            if (Encoding.ASCII.GetString(h, 0x200, 4) != "NCA3") return null;
            int gen = Math.Max(h[0x206], h[0x220]); if (gen > 0) gen--;
            var n = new Nca { S = s, At = at, H = h };
            if (h[0x230..0x240].Any(b => b != 0))
            {
                if (!titleKeys.TryGetValue(Convert.ToHexString(h, 0x230, 16), out var tk) || !k.TitleKek.TryGetValue(gen, out var kek)) return null;
                n.Key = Ecb(kek, tk);
            }
            else
            {
                if (h[0x207] != 0 || !k.KeyArea.TryGetValue(gen, out var kak)) return null;   // 0: application keys
                n.Key = Ecb(kak, h[0x300..0x340])[0x20..0x30];   // the CTR key is the third
            }
            return n;
        }

        // Bytes of section i, at an offset inside it.
        public byte[] Read(int i, long off, int len)
        {
            var fs = Fs(i);
            long abs = Start(i) + off, from = At + ((abs - At) & ~15L);   // CTR blocks count from the NCA's start
            var b = Bytes(S, from, (int)(abs - from) + len);
            if (b == null) return null;
            if (fs[4] == 3)   // 3: AES-CTR
            {
                var ctr = new byte[16];
                for (int j = 0; j < 8; j++) ctr[j] = fs[0x147 - j];
                b = Ctr(Key, ctr, from - At, b);
            }
            else if (fs[4] != 1) return null;   // 1: not encrypted; others (XTS, patch layers) aren't read here
            return b[(int)(abs - from)..];
        }
    }

    // The .cnmt file in a meta NCA's first section (a PFS0 after its hash table: offset 0x38, size 0x40).
    static byte[] CnmtFile(Stream s, Nca n)
    {
        var fs = n.Fs(0);
        long pfs = (long)BitConverter.ToUInt64(fs, 0x38);
        var h = n.Read(0, pfs, 0x10);
        if (h == null || Encoding.ASCII.GetString(h, 0, 4) != "PFS0") return null;
        int count = BitConverter.ToInt32(h, 4), strs = BitConverter.ToInt32(h, 8);
        if (count <= 0 || count > 64 || strs > 4096) return null;
        var t = n.Read(0, pfs, 0x10 + count * 0x18 + strs);
        long data = 0x10 + count * 0x18 + strs;
        for (int i = 0; i < count; i++)
        {
            int e = 0x10 + i * 0x18, so = 0x10 + count * 0x18 + BitConverter.ToInt32(t, e + 0x10), z = Array.IndexOf(t, (byte)0, so);
            string name = Encoding.ASCII.GetString(t, so, (z < 0 ? t.Length : z) - so);
            long size = (long)BitConverter.ToUInt64(t, e + 8);
            if (name.EndsWith(".cnmt", StringComparison.OrdinalIgnoreCase) && size < 1 << 20) return n.Read(0, pfs + data + (long)BitConverter.ToUInt64(t, e), (int)size);
        }
        return null;
    }

    // control.nacp in a control NCA's RomFS (IVFC level 6: offset 0x88, size 0x90): display version and name.
    static void Nacp(Stream s, Nca n, SwitchTitle t)
    {
        if (n == null) return;
        var fs = n.Fs(0);
        if (Encoding.ASCII.GetString(fs, 8, 4) != "IVFC") return;
        long rom = (long)BitConverter.ToUInt64(fs, 0x88);
        var h = n.Read(0, rom, 0x50);
        if (h == null) return;
        long metaOff = (long)BitConverter.ToUInt64(h, 0x38), metaSize = (long)BitConverter.ToUInt64(h, 0x40), dataOff = (long)BitConverter.ToUInt64(h, 0x48);
        if (metaSize <= 0 || metaSize > 1 << 20) return;
        var m = n.Read(0, rom + metaOff, (int)metaSize);
        for (int at = 0; m != null && at + 0x20 <= m.Length;)
        {
            int nameLen = BitConverter.ToInt32(m, at + 0x1C);
            if (nameLen < 0 || at + 0x20 + nameLen > m.Length) break;
            string name = Encoding.UTF8.GetString(m, at + 0x20, nameLen);
            long fileOff = (long)BitConverter.ToUInt64(m, at + 8), fileSize = (long)BitConverter.ToUInt64(m, at + 0x10);
            if (name == "control.nacp")
            {
                var nacp = n.Read(0, rom + dataOff + fileOff, 0x4000);
                if (nacp == null) return;
                string v = Text(nacp, 0x3060, 0x10);
                if (v.Length > 0) t.Display = v;
                for (int l = 0; l < 16 && t.Name == null; l++) { string nm = Text(nacp, l * 0x300, 0x200); if (nm.Length > 0) t.Name = nm; }
            }
            else if (name.StartsWith("icon_") && name.EndsWith(".dat") && fileSize > 0 && fileSize < 1 << 20 && (t.Icon == null || name == "icon_AmericanEnglish.dat"))
                t.Icon = n.Read(0, rom + dataOff + fileOff, (int)fileSize);
            at += 0x20 + ((nameLen + 3) & ~3);
        }
    }

    // ---- crypto (System.Security.Cryptography's AES; XTS and CTR are built on its ECB) ----
    static byte[] Ecb(byte[] key, byte[] data)
    {
        using var a = Aes.Create(); a.Key = key;
        return a.DecryptEcb(data, PaddingMode.None);
    }

    // Nintendo's XTS: 0x200-byte sectors from 0, the tweak is the sector number big-endian.
    internal static byte[] Xts(byte[] data, byte[] key)
    {
        using var a = Aes.Create(); a.Key = key[..16];
        using var tk = Aes.Create(); tk.Key = key[16..32];
        var o = new byte[data.Length];
        var x = new byte[16];
        for (int sec = 0; sec < data.Length / 0x200; sec++)
        {
            var tw = new byte[16];
            for (int i = 0; i < 8; i++) tw[15 - i] = (byte)((ulong)sec >> (8 * i));
            tw = tk.EncryptEcb(tw, PaddingMode.None);
            for (int blk = 0; blk < 0x200; blk += 16)
            {
                int at = sec * 0x200 + blk;
                for (int i = 0; i < 16; i++) x[i] = (byte)(data[at + i] ^ tw[i]);
                var p = a.DecryptEcb(x, PaddingMode.None);
                for (int i = 0; i < 16; i++) o[at + i] = (byte)(p[i] ^ tw[i]);
                int carry = 0;
                for (int i = 0; i < 16; i++) { int c = tw[i] >> 7; tw[i] = (byte)((tw[i] << 1) | carry); carry = c; }
                if (carry != 0) tw[0] ^= 0x87;
            }
        }
        return o;
    }

    // CTR: the upper 8 bytes from the section, the lower 8 the byte offset in the NCA / 16, big-endian.
    static byte[] Ctr(byte[] key, byte[] upper, long ncaOff, byte[] data)
    {
        int blocks = (data.Length + 15) / 16;
        var ctrs = new byte[blocks * 16];
        for (int b = 0; b < blocks; b++)
        {
            Array.Copy(upper, 0, ctrs, b * 16, 8);
            ulong n = (ulong)(ncaOff / 16 + b);
            for (int j = 0; j < 8; j++) ctrs[b * 16 + 15 - j] = (byte)(n >> (8 * j));
        }
        using var a = Aes.Create(); a.Key = key;
        var ks = a.EncryptEcb(ctrs, PaddingMode.None);
        var o = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) o[i] = (byte)(data[i] ^ ks[i]);
        return o;
    }

    static byte[] Bytes(Stream s, long at, int n)
    {
        if (at < 0 || n < 0 || at + n > s.Length) return null;
        s.Position = at;
        var b = new byte[n];
        s.ReadExactly(b);
        return b;
    }

    static string Text(byte[] b, int at, int n)
    {
        int z = Array.IndexOf(b, (byte)0, at, n);
        return Encoding.UTF8.GetString(b, at, (z < 0 ? at + n : z) - at).Trim();
    }
}
