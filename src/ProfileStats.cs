#nullable disable
// Personal play stats: everything the Play stats page, the profile card and Game info show,
// worked out from the active profile's stats.json (Profiles.cs). All local; nothing goes online.
//   Days with play: a day with at least a minute of play. Streaks run over such days; the current one
//   still counts while today has no play yet but yesterday had.
//   Weeks start on Monday. "This week" is Monday up to now, "last week" the whole week before.
//   Systems: a ROM game's console, Steam for Steam games, PC for the rest.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ionea;

public sealed class PlayStats
{
    public double Total, ThisWeek, LastWeek, Longest;
    public int Games, Sessions, Streak, BestStreak, Systems, DaysPlayed;
    public Game Most, Last, LongestGame;
    public double MostSeconds;
    public DateTime? LastAt;
    public DateTime FirstDay;
    public string TopSystem; public double TopSystemSeconds;
    public double[] Daily = new double[30];   // seconds per day, the oldest first, today last

    const double DayMin = 60;
    static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string SystemOf(MainWindow w, Game g)
    {
        if (g.Source == "steam") return "Steam";
        string sys = InputMap.SystemOf(w, g);
        return string.IsNullOrEmpty(sys) ? "PC" : InputMap.SystemName(sys);
    }

    public static PlayStats Of(MainWindow w) => Of(w, w.Profiles, DateTime.Now);

    public static PlayStats Of(MainWindow w, Profiles pr, DateTime now)
    {
        var st = pr.Stats;
        var r = new PlayStats();
        var games = w.Lib.Data.Games.ToDictionary(g => g.Id);
        Game G(string id) => id != null && games.TryGetValue(id, out var g) ? g : null;

        r.Total = st.Games.Values.Sum(s => s.Seconds);
        r.Games = st.Games.Count(kv => kv.Value.Seconds >= DayMin || kv.Value.Sessions > 0);
        r.Sessions = st.Games.Values.Sum(s => s.Sessions);
        var most = st.Games.Where(kv => kv.Value.Seconds > 0 && G(kv.Key) != null).OrderByDescending(kv => kv.Value.Seconds).FirstOrDefault();
        if (most.Key != null) { r.Most = G(most.Key); r.MostSeconds = most.Value.Seconds; }
        var last = st.Games.Where(kv => kv.Value.Last != null && G(kv.Key) != null).OrderByDescending(kv => kv.Value.Last).FirstOrDefault();
        if (last.Key != null) { r.Last = G(last.Key); r.LastAt = last.Value.Last; }
        var lng = st.Games.Where(kv => G(kv.Key) != null).OrderByDescending(kv => kv.Value.Longest).FirstOrDefault();
        if (lng.Key != null && lng.Value.Longest > 0) { r.Longest = lng.Value.Longest; r.LongestGame = G(lng.Key); }

        // systems by time
        var bySys = new Dictionary<string, double>();
        foreach (var kv in st.Games)
            if (G(kv.Key) is Game g && kv.Value.Seconds > 0) { string s = SystemOf(w, g); bySys[s] = (bySys.TryGetValue(s, out var x) ? x : 0) + kv.Value.Seconds; }
        r.Systems = bySys.Count(kv => kv.Value >= DayMin);
        if (bySys.Count > 0) { var top = bySys.OrderByDescending(kv => kv.Value).First(); r.TopSystem = top.Key; r.TopSystemSeconds = top.Value; }

        // days, streaks, weeks, the chart
        double DayTotal(DateTime d) => st.Days.TryGetValue(Key(d), out var m) ? m.Values.Sum() : 0;
        var played = st.Days.Where(kv => kv.Value.Values.Sum() >= DayMin)
            .Select(kv => DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null)
            .Where(d => d != null).Select(d => d.Value.Date).Distinct().OrderBy(d => d).ToList();
        r.DaysPlayed = played.Count;
        int run = 0; DateTime prev = DateTime.MinValue;
        foreach (var d in played) { run = prev != DateTime.MinValue && (d - prev).Days == 1 ? run + 1 : 1; r.BestStreak = Math.Max(r.BestStreak, run); prev = d; }
        var set = played.ToHashSet();
        var today = now.Date;
        var from = set.Contains(today) ? today : today.AddDays(-1);
        while (set.Contains(from)) { r.Streak++; from = from.AddDays(-1); }

        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        for (var d = monday; d <= today; d = d.AddDays(1)) r.ThisWeek += DayTotal(d);
        for (var d = monday.AddDays(-7); d < monday; d = d.AddDays(1)) r.LastWeek += DayTotal(d);
        for (int i = 0; i < 30; i++) r.Daily[i] = DayTotal(today.AddDays(i - 29));

        var created = pr.Active?.Created ?? now;
        var firstStat = st.Games.Values.Where(s => s.First != null).Select(s => s.First.Value).DefaultIfEmpty(created).Min();
        // the first day with Ionea: the profile's start, or older play carried over into it
        r.FirstDay = new[] { created, firstStat, played.Count > 0 ? played[0] : created }.Min().Date;

        return r;
    }

    // One game's numbers for Game info.
    public sealed record GameNumbers(double Seconds, int Sessions, DateTime? First, DateTime? Last, double Average, double Share, double Longest);
    public static GameNumbers ForGame(Profiles pr, string id)
    {
        var s = pr.StatOf(id);
        double total = pr.Stats.Games.Values.Sum(x => x.Seconds);
        if (s == null) return new(0, 0, null, null, 0, 0, 0);
        return new(s.Seconds, s.Sessions, s.First, s.Last, s.Sessions > 0 ? s.Seconds / s.Sessions : 0, total > 0 ? s.Seconds / total : 0, s.Longest);
    }
}
