// Harmless stand-in for a game: shows a window and closes itself after N seconds (default 3, or IONEA_TESTGAME_SECS).
// Build: csc /target:winexe /out:TestGame.exe TestGame.cs
using System;
using System.Drawing;
using System.Windows.Forms;

static class TestGame
{
    [STAThread]
    static void Main(string[] args)
    {
        int secs = 3;
        if (args.Length > 0) int.TryParse(args[0], out secs);
        // IONEA_TESTGAME_REPORT=<file>: appends how it was started (its program name, arguments and BB_GAME_DIR),
        // so a test can check what a launcher passed to a native port.
        string rep = Environment.GetEnvironmentVariable("IONEA_TESTGAME_REPORT");
        if (!string.IsNullOrEmpty(rep))
            try { System.IO.File.AppendAllText(rep, System.IO.Path.GetFileName(Application.ExecutablePath) + "|" + string.Join(" ", args) + "|" + Environment.GetEnvironmentVariable("BB_GAME_DIR") + System.Environment.NewLine); } catch { }
        // testgame-child.txt beside it, holding <exe>: a port's starter (bbport-run.exe): no window of its own; starts
        // <exe> (a copy of this, beside it) as the game, waits for it and ends with it.
        string mark = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "testgame-child.txt");
        string child = System.IO.File.Exists(mark) ? System.IO.File.ReadAllText(mark).Trim() : null;
        if (!string.IsNullOrEmpty(child) && !string.Equals(System.IO.Path.GetFileName(child), System.IO.Path.GetFileName(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase))
        {
            // BB_PROBE: the game program's path chosen by Ionea (its link named like Ionea), the way bbport's run.py reads it.
            string probe = Environment.GetEnvironmentVariable("BB_PROBE");
            var si = new System.Diagnostics.ProcessStartInfo(string.IsNullOrEmpty(probe) ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), child) : probe) { UseShellExecute = false };
            si.EnvironmentVariables.Remove("IONEA_TESTGAME_REPORT");
            using (var c = System.Diagnostics.Process.Start(si)) { c.WaitForExit(); }
            return;
        }
        int env; if (int.TryParse(Environment.GetEnvironmentVariable("IONEA_TESTGAME_SECS"), out env) && env > 0) secs = env;   // e.g. as a fake shadPS4 (its own args aren't a number)
        var f = new Form { Text = "Test game", Width = 900, Height = 600, StartPosition = FormStartPosition.CenterScreen, BackColor = Color.FromArgb(30, 30, 40) };
        // IONEA_TESTGAME_OFFSCREEN=1: opens far off the screen (a test's game a launcher takes in late stays out of sight).
        if (Environment.GetEnvironmentVariable("IONEA_TESTGAME_OFFSCREEN") == "1") { f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-30000, -30000); }
        // IONEA_TESTGAME_GROW=<ms>: after <ms> it moves and sizes its own window, as a Qt emulator going full screen does
        // a moment after it starts (100 px left and up of where it is, 300 x 200 bigger: still off the screen in a test).
        int grow; int.TryParse(Environment.GetEnvironmentVariable("IONEA_TESTGAME_GROW"), out grow);
        if (grow > 0)
        {
            var g = new Timer { Interval = grow };
            g.Tick += (s, e) => { g.Stop(); f.Bounds = new Rectangle(f.Left - 100, f.Top - 100, f.Width + 300, f.Height + 200); };
            f.Shown += (s, e) => g.Start();
        }
        f.Controls.Add(new Label { Text = "Test game - closes in " + secs + " s", ForeColor = Color.White, Font = new Font("Segoe UI", 24), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        // IONEA_TESTGAME_CLOSEMS: a close (its own or asked) takes this long, like an emulator shutting down.
        int closeMs; int.TryParse(Environment.GetEnvironmentVariable("IONEA_TESTGAME_CLOSEMS"), out closeMs);
        bool closing = false;
        f.FormClosing += (s, e) =>
        {
            if (closeMs <= 0 || closing) return;
            closing = true; e.Cancel = true;
            var c = new Timer { Interval = closeMs };
            c.Tick += (s2, e2) => { c.Stop(); f.Close(); };
            c.Start();
        };
        // IONEA_TESTGAME_ANIM=<ms>: the window changes every <ms> (a game drawing frames, for the overlay's frame count).
        // IONEA_TESTGAME_FRAMELOG=<folder> (relative: to its own): also writes <folder>\<time>.frames.csv as bbport
        // does (t_s,frame_ms,... one row per change, written at once).
        int anim; int.TryParse(Environment.GetEnvironmentVariable("IONEA_TESTGAME_ANIM"), out anim);
        if (anim > 0)
        {
            var label = (Label)f.Controls[0];
            System.IO.StreamWriter log = null;
            string fl = Environment.GetEnvironmentVariable("IONEA_TESTGAME_FRAMELOG");
            if (!string.IsNullOrEmpty(fl))
                try
                {
                    string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), fl));
                    System.IO.Directory.CreateDirectory(dir);
                    log = new System.IO.StreamWriter(System.IO.Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".frames.csv")) { AutoFlush = true };
                    log.WriteLine("t_s,frame_ms,present_wait_ms");
                }
                catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double last = 0; int n = 0;
            var a = new Timer { Interval = anim };
            a.Tick += (s, e) =>
            {
                n++;
                f.BackColor = Color.FromArgb(30 + n % 60, 30, 40);
                label.Text = "Test game - frame " + n;
                double now = sw.Elapsed.TotalSeconds;
                if (log != null) log.WriteLine(now.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + "," + ((now - last) * 1000).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ",0.00");
                last = now;
            };
            a.Start();
            f.FormClosed += (s, e) => { if (log != null) log.Dispose(); };
        }
        var t = new Timer { Interval = Math.Max(1, secs) * 1000 };
        t.Tick += (s, e) => f.Close();
        t.Start();
        Application.Run(f);
    }
}
