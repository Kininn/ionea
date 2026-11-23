#nullable disable
// Ionea launcher: "Open the emulator". The emulator starts
// with no game and its window comes inside Ionea's, as a game's does (borderless, owned, fitted; on its own
// when its "Play inside Ionea" is off). Its own Exit, the Home menu key, or View + Start / Guide closes it. Once it
// has closed, Ionea reads its files again: the screen that opened it is built anew, so a value changed in the
// emulator's window shows there. Controls changed there while Ionea manages them: Ionea asks which to keep.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static Ionea.Loc;

namespace Ionea;

public sealed class EmuOpen : IScreen
{
    static EmuOpen current;
    public static string OpenName => current?.e.Name;   // the emulator open now, or null (tests)
    public static Embedded Window => current?.emb;

    readonly MainWindow w;
    readonly Emulator e;
    readonly Action after;
    Process p;
    IntPtr job;
    Embedded emb;
    EmbedWatch watch;
    string ctlBefore;
    bool inside;

    EmuOpen(MainWindow w, Emulator e, Action after) { this.w = w; this.e = e; this.after = after; }

    // after: what shows once it has closed (the emulator's page again, read anew), or null for the home screen.
    public static void Start(MainWindow w, Emulator e, Action after)
    {
        if (current != null) { w.Toasts.Show(T("errors.already_open"), current.e.Name, "info"); after?.Invoke(); return; }
        if (!e.Present) { after?.Invoke(); new MessageCard(w, T("errors.can_t_open_it"), T("errors.the_emulator_s_program_is_missing")).Open(); return; }
        if (w.GameMenu.Game != null) { after?.Invoke(); w.Toasts.Show(T("home.close_the_game_first"), w.GameMenu.Game.Name, "info"); return; }
        new EmuOpen(w, e, after).Run();
    }

    void Run()
    {
        ctlBefore = EmuControls.ControlsText(e);
        inside = EmbedLogic.CanEmbed(e);
        var si = new ProcessStartInfo(inside ? EmbedLogic.Alias(e) : e.Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(e.Exe) };
        EmbedLogic.Prepare(e, si);
        try { p = Process.Start(si); }
        catch (Exception ex)
        {
            Log.Write($"open emulator {e.Name}: {ex.Message}");
            after?.Invoke();
            new MessageCard(w, T("errors.couldn_t_open_it"), ex.Message).Open();
            return;
        }
        if (p == null) { after?.Invoke(); return; }
        current = this;
        if (inside) job = EmbedLogic.Hold(p);   // it closes with the launcher
        p.EnableRaisingEvents = true;
        p.Exited += (s, a) => w.Dispatcher.BeginInvoke(Ended);
        Log.Write($"opened emulator {e.Name} {(inside ? "inside the launcher" : "on its own")} (pid {p.Id})");
        w.Input.HomePressed += Home;
        ClaimHome();
        if (!inside) return;
        w.Push(this);   // launcher actions do nothing while it is open: the keys and mouse belong to it
        w.SizeChanged += Refit;
        w.LocationChanged += Refit;
        watch = EmbedWatch.Start(p, w.Hwnd, true, () => false, (em, ms) => w.Dispatcher.BeginInvoke(() =>
        {
            if (current != this) return;
            bool first = emb == null;
            emb = em;
            if (Native.GetForegroundWindow() == w.Hwnd) emb.Focus();
            if (first) Log.Write($"embed: {e.Name} window 0x{em.Child.ToInt64():X} is over the launcher ({ms:0} ms after start)");
        }));
        if (p.HasExited) w.Dispatcher.BeginInvoke(Ended);
    }

    void Refit(object s, EventArgs a) { if (emb != null && emb.Alive) emb.Fit(); }

    // Home: ask it to close (its own "save changes?" questions still come up).
    void Home()
    {
        if (current != this) return;
        Log.Write($"open emulator: asking {e.Name} to close");
        if (emb != null && emb.Alive) emb.Close(false);
        else try { p.CloseMainWindow(); } catch { /* already ended */ }
    }
    public static void CloseNow() => current?.Home();   // tests

    void Ended()
    {
        if (current != this) return;
        current = null;
        watch?.Stop();
        w.HomeKeys.Set(this, null, null, null);
        w.Input.HomePressed -= Home;
        w.SizeChanged -= Refit;
        w.LocationChanged -= Refit;
        w.Pop(this);
        EmbedLogic.Release(job); job = IntPtr.Zero;
        bool changed = ctlBefore != null && EmuControls.ControlsText(e) != ctlBefore;
        Log.Write($"emulator {e.Name} closed; reading its files again{(changed ? ": its controls changed" : "")}");
        if (changed && EmuControls.Mapping(e) != null) { new ControlsKeepCard(w, e, after).Open(); return; }
        after?.Invoke();
    }

    // While it is open and in front, launcher actions only give it the keyboard back.
    public void OnAct(Act a) { if (emb != null && emb.Alive) emb.Focus(); }
    public bool OnMouse(MouseAct m, Point pt) => true;

    // ---- the Home menu key, while it or the launcher is in front ----
    void ClaimHome()
    {
        uint[] pids;
        try { pids = inside ? new[] { (uint)Environment.ProcessId, (uint)p.Id } : new[] { (uint)p.Id }; } catch { pids = new[] { (uint)Environment.ProcessId }; }
        w.HomeKeys.Set(this, LauncherKeys.HomeVks(w.Settings.Input, true), pids, Home);
    }
    public static void KeysChanged() { if (current != null) current.ClaimHome(); }
    public static bool HomeClaimed => current != null && current.w.HomeKeys.Claimed(current);
}

// Its controls were changed in its own window while Ionea writes them at every start: keep which?
public sealed class ControlsKeepCard : MenuCard
{
    public ControlsKeepCard(MainWindow w, Emulator e, Action after) : base(w, T("errors.controls_changed_in", e.Name), 1080)
    {
        AddRow(T("errors.keep_s", e.Name), activate: () =>
        {
            e.Controls = false;
            w.Emus.Save();
            Log.Write($"emulator {e.Name}: Ionea manages controls off (kept the emulator's own)");
            Close(false);
            after?.Invoke();
        });
        AddRow(T("errors.keep_ionea_s"), activate: () =>
        {
            Log.Write($"emulator {e.Name}: Ionea manages controls stays on");
            Close(false);
            after?.Invoke();
        });
    }

    public override void OnAct(Act a)
    {
        if (a == Act.Back) return;   // one of the two
        base.OnAct(a);
    }
}
