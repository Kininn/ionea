#nullable disable
// Covers from the icon stored inside a game file: DS (.nds, 32x32, 16 colours), 3DS (.3ds/.cci, 48x48 from the
// SMDH in ExeFS; only unencrypted files), PS4 (sce_sys\icon0.png), and PS3
// (PS3_GAME\ICON0.PNG, in a folder or a disc image), PSP (ICON0.PNG in an .pbp or a disc image), Wii U
// (meta\iconTex.tga) and Switch (the control NCA's icon; needs the user's keys). GBA files have no icon.
// Every icon image lives in data\icons (one per ROM, named after the ROM file); pictures put there win.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ionea;

public static class RomArt
{
    // One folder holds every game's icon image: data\icons\<ROM file name>.png/.jpg/... The built-in icon
    // of a DS/3DS file is saved there on first use; a picture dropped there (box art etc.) wins over it.
    public static string IconsDir(Library lib) => Path.Combine(lib.Dir, "icons");
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public static string IconFile(string iconsDir, string rom)
    {
        string stem = RomLogic.Stem(rom);
        return ImageExts.Select(e => Path.Combine(iconsDir, stem + e)).FirstOrDefault(File.Exists);
    }

    // The icon image for a game: from the folder, or taken out of the game file (and saved to the folder).
    public static string EnsureIcon(string iconsDir, string rom)
    {
        string f = IconFile(iconsDir, rom);
        if (f != null) return f;
        var icon = Icon(rom);
        if (icon == null) return null;
        Directory.CreateDirectory(iconsDir);
        f = Path.Combine(iconsDir, RomLogic.Stem(rom) + ".png");
        Art.SavePng(icon, f);
        Log.Write($"rom icons: saved the built-in icon of {Path.GetFileName(rom)}");
        return f;
    }

    // The game's own icon at its real size (32x32 DS, 48x48 3DS), or null when the file has none.
    public static BitmapSource Icon(string rom)
    {
        try
        {
            if (RomLogic.IsEboot(rom)) return Ps4(rom);
            string ext = Path.GetExtension(rom).ToLowerInvariant();
            if (Path.GetFileName(rom).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase)) return Ps3(rom);
            if (ext == ".rpx") return WiiU(rom);
            if (ext is ".nsp" or ".xci") return Switch(rom);
            if (ext == ".iso") return DiscIcon(rom);
            using var f = File.OpenRead(rom);
            return ext switch
            {
                ".nds" or ".dsi" => Nds(f),
                ".3ds" or ".cci" => Cci(f),
                ".pbp" => Pbp(f),
                _ => null,
            };
        }
        catch (Exception e) { Log.Write($"rom icon {Path.GetFileName(rom)}: {e.Message}"); return null; }
    }

    // A PS4 game folder carries its icon as sce_sys\icon0.png (512x512).
    static BitmapSource Ps4(string eboot)
    {
        string png = Path.Combine(Path.GetDirectoryName(eboot) ?? "", "sce_sys", "icon0.png");
        if (!File.Exists(png) || new FileInfo(png).Length > 8 << 20) return null;
        var b = new BitmapImage();
        b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(png); b.EndInit(); b.Freeze();
        return b;
    }

    static BitmapSource Png(byte[] b)
    {
        if (b == null || b.Length < 8) return null;
        var i = new BitmapImage();
        i.BeginInit(); i.CacheOption = BitmapCacheOption.OnLoad; i.StreamSource = new MemoryStream(b); i.EndInit(); i.Freeze();
        return i;
    }
    static BitmapSource PngFile(string png) => File.Exists(png) && new FileInfo(png).Length < 8 << 20 ? Png(File.ReadAllBytes(png)) : null;

    // PS3 game folder: <game>\PS3_GAME\USRDIR\EBOOT.BIN; ICON0.PNG (320x176) sits beside PARAM.SFO.
    static BitmapSource Ps3(string eboot)
    {
        for (string dir = Path.GetDirectoryName(eboot); dir != null && dir.Length > 3; dir = Path.GetDirectoryName(dir))
        {
            foreach (string f in new[] { Path.Combine(dir, "ICON0.PNG"), Path.Combine(dir, "PS3_GAME", "ICON0.PNG") })
                if (File.Exists(f) && File.Exists(Path.Combine(Path.GetDirectoryName(f), "PARAM.SFO"))) return PngFile(f);
            if (Path.GetFileName(dir).Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }

    // A PSP or PS3 disc image: PSP_GAME\ICON0.PNG (144x80) or PS3_GAME\ICON0.PNG.
    static BitmapSource DiscIcon(string iso)
    {
        using var d = Iso.Open(iso);
        return d == null ? null : Png(d.Read("PSP_GAME/ICON0.PNG", 1 << 20) ?? d.Read("PS3_GAME/ICON0.PNG", 1 << 20));
    }

    // PSP .pbp: its ICON0.PNG runs from the offset at 0x0C to the one at 0x10.
    static BitmapSource Pbp(Stream f)
    {
        var h = Read(f, 0, 0x28);
        if (h == null || U32(h, 0) != 0x50425000) return null;
        long a = U32(h, 0x0C), b = U32(h, 0x10);
        return b > a && b - a < 1 << 20 ? Png(Read(f, a, (int)(b - a))) : null;
    }

    // Wii U game folder: code\<game>.rpx, the icon in meta\iconTex.tga (128x128).
    static BitmapSource WiiU(string rpx)
    {
        string tga = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(rpx) ?? "") ?? "", "meta", "iconTex.tga");
        return File.Exists(tga) && new FileInfo(tga).Length < 4 << 20 ? Tga(File.ReadAllBytes(tga)) : null;
    }

    // TGA: uncompressed (type 2) or run-length (type 10) true colour, 24 or 32 bits. Others: null.
    public static BitmapSource Tga(byte[] b)
    {
        if (b == null || b.Length < 18) return null;
        int idLen = b[0], type = b[2], w = U16(b, 12), h = U16(b, 14), bpp = b[16];
        bool top = (b[17] & 0x20) != 0;
        if (b[1] != 0 || (type != 2 && type != 10) || (bpp != 24 && bpp != 32) || w == 0 || h == 0 || w > 2048 || h > 2048) return null;
        int px = bpp / 8, at = 18 + idLen, n = w * h;
        var o = new uint[n];
        uint Pix(int i) => Argb(px == 4 ? b[i + 3] : (byte)255, b[i + 2], b[i + 1], b[i]);
        for (int i = 0; i < n;)
        {
            if (type == 2) { if (at + px > b.Length) return null; o[i++] = Pix(at); at += px; continue; }
            if (at >= b.Length) return null;
            int c = b[at++], count = (c & 0x7F) + 1;
            if ((c & 0x80) != 0) { if (at + px > b.Length) return null; uint v = Pix(at); at += px; for (int k = 0; k < count && i < n; k++) o[i++] = v; }
            else for (int k = 0; k < count && i < n; k++) { if (at + px > b.Length) return null; o[i++] = Pix(at); at += px; }
        }
        if (!top)
        {
            var f = new uint[n];
            for (int y = 0; y < h; y++) Array.Copy(o, (h - 1 - y) * w, f, y * w, w);
            o = f;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, o, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // Switch .nsp / .xci: the game's icon from its control data, read with the keys of a Switch emulator that has them.
    public static Func<IEnumerable<Emulator>> Emus;
    static BitmapSource Switch(string rom)
    {
        foreach (var e in Emus?.Invoke() ?? Array.Empty<Emulator>())
        {
            if (!e.Present || !e.Systems.Contains("switch")) continue;
            var files = ReqFiles.Of(e);
            string P(string check) => files.FirstOrDefault(x => x.Check == check) is DefFile d ? ReqFiles.Target(e, d) : null;
            var keys = SwitchKeys.Load(P("switch-keys"), P("switch-titlekeys"));
            if (keys == null) continue;
            var t = SwitchRead.Read(rom, keys).FirstOrDefault(x => x.Type == SwitchRead.Game && x.Icon != null);
            return t == null ? null : Png(t.Icon);   // a JPEG: the same decoder reads it
        }
        return null;
    }

    public static BitmapSource Cover(string rom, string iconsDir) => CoverFromImage(EnsureIcon(iconsDir, rom));

    // Small pixel icons fill the tile, enlarged by a whole number so every pixel stays a sharp square.
    // Larger pictures (box art) are shown whole, centred, over a soft blurred copy of themselves.
    public static BitmapSource CoverFromImage(string file)
    {
        var src = Art.LoadImage(file, 0);
        if (src == null) return null;
        int n = AddGameLogic.CoverSize;
        if (Math.Max(src.PixelWidth, src.PixelHeight) <= 128)
        {
            int k = Math.Max(1, n / Math.Max(src.PixelWidth, src.PixelHeight));
            var big = Sharp(src, k);
            double sc = (double)n / (Math.Max(src.PixelWidth, src.PixelHeight) * k);
            double w = big.PixelWidth * sc, h = big.PixelHeight * sc;
            return Art.Render(n, n, dc => { AddGameLogic.SoftTile(dc, n); dc.DrawImage(big, new System.Windows.Rect((n - w) / 2, (n - h) / 2, w, h)); });
        }
        // A (near) square picture fills the tile edge to edge, no blurred frame (up to ~8% per side cropped).
        double aspect = (double)src.PixelWidth / src.PixelHeight;
        if (aspect > 0.85 && aspect < 1.18)
        {
            double fs = Math.Max((double)n / src.PixelWidth, (double)n / src.PixelHeight);
            double fw = src.PixelWidth * fs, fh = src.PixelHeight * fs;
            return Art.Render(n, n, dc => dc.DrawImage(src, new System.Windows.Rect((n - fw) / 2, (n - fh) / 2, fw, fh)));
        }
        var blur = new TransformedBitmap(src, new ScaleTransform(12.0 / src.PixelWidth, 12.0 / src.PixelWidth));
        blur.Freeze();
        return Art.Render(n, n, dc =>
        {
            double cs = Math.Max((double)n / blur.PixelWidth, (double)n / blur.PixelHeight);
            double bw = blur.PixelWidth * cs, bh = blur.PixelHeight * cs;
            dc.DrawImage(blur, new System.Windows.Rect((n - bw) / 2, (n - bh) / 2, bw, bh));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, new System.Windows.Rect(0, 0, n, n));
            double m = n * 0.05, s = Math.Min((n - 2 * m) / src.PixelWidth, (n - 2 * m) / src.PixelHeight);
            double w = src.PixelWidth * s, h = src.PixelHeight * s;
            var r = new System.Windows.Rect((n - w) / 2, (n - h) / 2, w, h);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new System.Windows.Rect(r.X + 4, r.Y + 8, w, h));
            dc.DrawImage(src, r);
        });
    }

    static BitmapSource Sharp(BitmapSource src, int k)
    {
        var c = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = c.PixelWidth, h = c.PixelHeight;
        var px = new uint[w * h]; c.CopyPixels(px, w * 4, 0);
        int W = w * k, H = h * k; var dst = new uint[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) dst[y * W + x] = px[(y / k) * w + x / k];
        var b = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, dst, W * 4);
        b.Freeze();
        return b;
    }

    // At start: every ROM game gets its icon saved in the icons folder, and its cover is (re)made when it
    // has none or the icon image is newer than the cover (so a picture dropped in the folder shows up).
    public static void Backfill(Library lib)
    {
        string dir = IconsDir(lib); int n = 0;
        // A cover the user chose, or one fetched for the game (Covers.cs), is never replaced by the file's icon.
        foreach (var g in lib.Data.Games.Where(g => g.Source == "rom" && g.Rom != null && (g.CoverFrom == null || g.CoverFrom == "icon") && File.Exists(g.Rom)))
        {
            string icon = EnsureIcon(dir, g.Rom);
            if (icon == null) continue;
            string cover = lib.CoverPath(g);
            if (cover != null && File.Exists(cover) && File.GetLastWriteTimeUtc(cover) >= File.GetLastWriteTimeUtc(icon)) continue;
            var c = CoverFromImage(icon);
            if (c == null) continue;
            g.Cover = g.Id + ".png"; g.CoverFrom = "icon"; g.CoverUrl = "";
            Art.SavePng(c, Path.Combine(lib.CoversDir, g.Cover));
            n++;
        }
        if (n > 0) { lib.Save(); Log.Write($"rom icons: made {n} cover(s) from the icons folder"); }
    }

    static byte[] Read(Stream f, long at, int n)
    {
        if (at < 0 || at + n > f.Length) return null;
        var b = new byte[n]; f.Position = at;
        int got = 0; while (got < n) { int r = f.Read(b, got, n - got); if (r <= 0) return null; got += r; }
        return b;
    }

    static uint U32(byte[] b, int i) => BitConverter.ToUInt32(b, i);
    static ushort U16(byte[] b, int i) => BitConverter.ToUInt16(b, i);

    // DS: header 0x68 -> icon/title block; 4bpp bitmap (4x4 tiles of 8x8) at +0x20, BGR555 palette at +0x220.
    static BitmapSource Nds(Stream f)
    {
        var h = Read(f, 0, 0x200); if (h == null) return null;
        uint at = U32(h, 0x68); if (at == 0) return null;
        var b = Read(f, at, 0x240); if (b == null) return null;
        var px = new uint[32 * 32];
        for (int i = 0; i < 1024; i++)
        {
            int tile = i / 64, t = i % 64;
            int x = (tile % 4) * 8 + t % 8, y = (tile / 4) * 8 + t / 8;
            int v = b[0x20 + i / 2]; int idx = (i & 1) == 0 ? v & 15 : v >> 4;
            if (idx == 0) continue;   // colour 0 is see-through
            int c = U16(b, 0x220 + idx * 2);
            px[y * 32 + x] = Argb(255, Five(c & 31), Five((c >> 5) & 31), Five((c >> 10) & 31));
        }
        return Pixels(px, 32, 1);
    }

    // 3DS CCI: NCSD partition 0 -> NCCH -> ExeFS -> "icon" (SMDH) -> 48x48 RGB565 at 0x24C0, 8x8 tiles in Z order.
    static BitmapSource Cci(Stream f)
    {
        var ncsd = Read(f, 0, 0x200);
        if (ncsd == null || Encoding.ASCII.GetString(ncsd, 0x100, 4) != "NCSD") return null;
        long ncch = (long)U32(ncsd, 0x120) * 0x200;
        var nh = Read(f, ncch, 0x200);
        if (nh == null || Encoding.ASCII.GetString(nh, 0x100, 4) != "NCCH") return null;
        if ((nh[0x18F] & 0x4) == 0) { Log.Write("rom icon: 3DS file is encrypted; no icon"); return null; }
        long exefs = ncch + (long)U32(nh, 0x1A0) * 0x200;
        var eh = Read(f, exefs, 0x200); if (eh == null) return null;
        for (int e = 0; e < 10; e++)
        {
            string name = Encoding.ASCII.GetString(eh, e * 16, 8).TrimEnd('\0');
            if (name != "icon") continue;
            var s = Read(f, exefs + 0x200 + U32(eh, e * 16 + 8), 0x36C0);
            if (s == null || Encoding.ASCII.GetString(s, 0, 4) != "SMDH") return null;
            var px = new uint[48 * 48];
            for (int i = 0; i < 48 * 48; i++)
            {
                int tile = i / 64, t = i % 64;
                int tx = (t & 1) | ((t >> 1) & 2) | ((t >> 2) & 4), ty = ((t >> 1) & 1) | ((t >> 2) & 2) | ((t >> 3) & 4);
                int x = (tile % 6) * 8 + tx, y = (tile / 6) * 8 + ty;
                int c = U16(s, 0x24C0 + i * 2);
                px[y * 48 + x] = Argb(255, Five((c >> 11) & 31), (byte)(((c >> 5) & 63) * 255 / 63), Five(c & 31));
            }
            return Pixels(px, 48, 1);
        }
        return null;
    }

    static byte Five(int v) => (byte)(v * 255 / 31);
    static uint Argb(byte a, byte r, byte g, byte b) => (uint)(a << 24 | r << 16 | g << 8 | b);

    // Whole-number enlargement (each source pixel becomes a k x k block) so pixel art stays sharp.
    static BitmapSource Pixels(uint[] src, int n, int k)
    {
        int m = n * k; var dst = new uint[m * m];
        for (int y = 0; y < m; y++) for (int x = 0; x < m; x++) dst[y * m + x] = src[(y / k) * n + x / k];
        var b = BitmapSource.Create(m, m, 96, 96, PixelFormats.Bgra32, null, dst, m * 4);
        b.Freeze();
        return b;
    }
}
