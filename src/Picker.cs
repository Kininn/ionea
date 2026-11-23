#nullable disable
// Choosing a file or a folder. After controller use the launcher's own browser (FileBrowserCard: drives, the
// usual places, folders and the files that fit, all with the pad); after keyboard or mouse use Windows' dialog.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using static Ionea.Loc;

namespace Ionea;

public static class Picker
{
    public static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    public static string ImageFilter => T("settings.images_png_jpg_jpeg_bmp_gif");

    // A file. exts: what the browser lists (null: every file); filter: the same for Windows' dialog.
    // done runs only when a file was chosen.
    public static void File(MainWindow w, string title, string filter, string[] exts, string startDir, Action<string> done, bool dereferenceLinks = true)
    {
        if (UseBrowser(w)) { new FileBrowserCard(w, title, exts, false, startDir, done).Open(); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter ?? T("settings.all_files_2"), DereferenceLinks = dereferenceLinks };
        if (startDir != null && Directory.Exists(startDir)) dlg.InitialDirectory = startDir;
        bool ok = dlg.ShowDialog(w) == true;
        w.FocusSelf();
        if (ok) done(dlg.FileName);
    }

    public static void Folder(MainWindow w, string title, string startDir, Action<string> done)
    {
        if (UseBrowser(w)) { new FileBrowserCard(w, title, null, true, startDir, done).Open(); return; }
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (startDir != null && Directory.Exists(startDir)) dlg.InitialDirectory = startDir;
        bool ok = dlg.ShowDialog(w) == true;
        w.FocusSelf();
        if (ok) done(dlg.FolderName);
    }

    public static bool ForceBrowser;   // tests: always the launcher's browser
    static bool UseBrowser(MainWindow w) => ForceBrowser || w.PadLast || !w.KeyboardOn;
}

// The launcher's own file and folder browser. "This PC" first lists the drives and the usual places; a folder
// lists its folders, then the files that fit. B goes up a folder (on "This PC": cancel).
public sealed class FileBrowserCard : MenuCard
{
    const int MaxShown = 400;
    readonly string[] exts;
    readonly bool folders;
    readonly Action<string> done;
    readonly TextBlock where;
    readonly StackPanel list = new();
    string dir;   // null = This PC
    public static FileBrowserCard Current;   // tests

    public FileBrowserCard(MainWindow w, string title, string[] exts, bool pickFolder, string startDir, Action<string> done) : base(w, title, 1240)
    {
        this.exts = exts?.Select(x => x.ToLowerInvariant()).ToArray();
        folders = pickFolder; this.done = done;
        where = Note("", 24, new Thickness(4, -12, 0, 16));
        where.TextWrapping = TextWrapping.NoWrap; where.TextTrimming = TextTrimming.CharacterEllipsis;
        Body.Children.Add(list);
        RowHost = list;
        Current = this;
        Show(startDir != null && Directory.Exists(startDir) ? Path.GetFullPath(startDir) : null, null);
    }

    public string Dir => dir;

    void Show(string to, string focusOn)
    {
        dir = to;
        ClearRows();
        where.Text = dir ?? T("settings.this_pc");
        if (dir == null)
        {
            foreach (var (name, path) in Places()) Opens(Aside(AddRow(name, activate: () => Show(path, null)), path));
        }
        else
        {
            if (folders) WithIcon(AddRow(T("settings.choose_this_folder"), activate: () => Pick(dir)), "plus");
            var subs = Safe(() => new DirectoryInfo(dir).GetDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList()) ?? new List<DirectoryInfo>();
            var files = folders ? new List<FileInfo>() : Safe(() => new DirectoryInfo(dir).GetFiles()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && (exts == null || exts.Contains(f.Extension.ToLowerInvariant())))
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList()) ?? new List<FileInfo>();
            int shown = 0;
            foreach (var d in subs)
            {
                if (++shown > MaxShown) break;
                string p = d.FullName;
                WithIcon(Opens(AddRow(d.Name, activate: () => Show(p, null))), "folder");
            }
            foreach (var f in files)
            {
                if (++shown > MaxShown) break;
                string p = f.FullName;
                Aside(AddRow(f.Name, activate: () => Pick(p)), Size(f.Length));
            }
            if (subs.Count + files.Count > MaxShown) Say(T("settings.and_more", subs.Count + files.Count - MaxShown), 22);
            if (subs.Count + files.Count == 0) Say(folders ? T("settings.no_folders_here") : T("settings.nothing_here_fits"), 26);
        }
        Compact();
        Focus = 0;
        if (focusOn != null)
        {
            int i = Rows.FindIndex(r => string.Equals(((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text, focusOn, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Focus = i;
        }
        Refresh();
    }

    void Say(string text, double size)
    {
        var t = Fonts.Text(text, size, P.Text2);
        t.Margin = new Thickness(4, 4, 0, 14);
        list.Children.Add(t);
    }

    static T Safe<T>(Func<T> f) where T : class { try { return f(); } catch (Exception e) { Log.Write("browse: " + e.Message); return null; } }

    static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

    // The drives and the usual places, each once.
    static List<(string name, string path)> Places()
    {
        var l = new List<(string, string)>();
        void Add(string name, string p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !l.Any(x => string.Equals(x.Item2, p, StringComparison.OrdinalIgnoreCase))) l.Add((name, p)); }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(T("settings.desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(T("settings.downloads"), Path.Combine(home, "Downloads"));
        Add(T("settings.documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(T("settings.pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        foreach (var (name, root) in Drives()) Add(name, root);
        return l;
    }

    // The drives that answer within a moment. A network drive whose server is gone can take a minute to say so,
    // so each is asked on its own thread and the ones still thinking are left out.
    static List<(string name, string root)> Drives()
    {
        var l = new List<(string, string)>();
        try
        {
            var asks = DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                .Select(d => Task.Run(() =>
                {
                    try
                    {
                        if (!d.IsReady) return ((string)null, (string)null);
                        string letter = d.Name.TrimEnd('\\');
                        return (string.IsNullOrWhiteSpace(d.VolumeLabel) ? letter : $"{d.VolumeLabel} ({letter})", d.RootDirectory.FullName);
                    }
                    catch { return (null, null); }   // a drive that went away while asked
                })).ToArray();
            Task.WaitAll(asks, 1500);
            foreach (var t in asks)
                if (t.Status == TaskStatus.RanToCompletion && t.Result.Item2 != null) l.Add(t.Result);
                else if (!t.IsCompleted) Log.Write("browse: a drive did not answer in time; left out");
        }
        catch (Exception e) { Log.Write("browse: drives: " + e.Message); }
        return l;
    }

    void Pick(string path)
    {
        Sounds.Play("select");
        Close(false);
        done(path);
    }

    void Up()
    {
        if (dir == null) { Close(); return; }
        Sounds.Play("back");
        string root = Path.GetPathRoot(dir);
        string here = dir.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root?.TrimEnd(Path.DirectorySeparatorChar), here, StringComparison.OrdinalIgnoreCase)) { Show(null, null); return; }
        Show(Directory.GetParent(here)?.FullName, Path.GetFileName(here));
    }

    public override void OnAct(Act a)
    {
        if (a is Act.Back or Act.PageLeft) { Up(); return; }
        base.OnAct(a);
    }

    public override void Close(bool sound = true)
    {
        base.Close(sound);
        if (Current == this) Current = null;
    }

    // For tests: open a row by its label.
    public bool Choose(string label)
    {
        int i = Rows.FindIndex(r => string.Equals(((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text, label, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        Focus = i; Refresh();
        OnAct(Act.Accept);
        return true;
    }
}
