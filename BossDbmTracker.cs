using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// Reads the game's native boss "deadly-skill" (DBM) countdown list for the Boss Skill Timers overlay
/// (<see cref="Plugin"/>.<c>Plugin.BossTimerWindow.cs</c>). This is the same data the game shows as its
/// top-left rows ("Mighty Bite 00:08", "Opportunistic Pounce 00:18") — a SINGLE GLOBAL encounter list,
/// not keyed per target.
///
/// <para>Source: <c>Panda.ZUi.DBMMgr : ZSingleton&lt;DBMMgr&gt;</c>. Its <c>DbmInfoDict</c> property is an IL2CPP
/// <c>Il2CppSystem.Collections.Generic.Dictionary&lt;int, DBMDataInfo&gt;</c> (key = DbmId) — NOT a managed dictionary,
/// so it is walked via the robust ladder in <see cref="ReadDictValues"/> (managed-IEnumerable → Keys+indexer →
/// value-enumerator), never a plain reflected Values-enumerator. Each <c>DBMDataInfo</c> is a STRUCT carrying
/// <c>long BeginTime; int Duration; int DbmId; bool IsDead; string Name</c> — <c>Name</c> is already resolved from
/// <c>DbmTable.Content</c>, so no skill-table lookup is needed.</para>
///
/// <para>Countdown to the NEXT cast: <c>remaining = (BeginTime + DurationMs) − serverNow</c>. The unit of
/// <c>Duration</c> (seconds vs milliseconds) is validated in-game via the diagnostic below; the single
/// conversion point is <see cref="DurationIsSeconds"/> (default: assume ms — flip if the diag shows seconds).</para>
///
/// <para>Everything is guarded and frame-cached: one read per frame, a failed/missing resolve degrades to an
/// empty list, and the poll/render path never throws. The server clock reuses the same gated
/// <c>ZServerTime.GetServerTime</c> read (with the ≈2020 sanity floor) that <see cref="TargetBuffTracker"/> uses;
/// an untrusted/zero clock yields an empty list rather than garbage countdowns.</para>
/// </summary>
public readonly struct DbmEntry
{
    public readonly int    DbmId;
    public readonly string Name;
    public readonly float  RemainSec;   // seconds until the next cast (clamped ≥ 0)
    public readonly float  TotalSec;    // the full countdown period (for the bar fill fraction)

    public DbmEntry(int dbmId, string name, float remainSec, float totalSec)
    {
        DbmId = dbmId; Name = name; RemainSec = remainSec; TotalSec = totalSec;
    }
}

internal sealed class BossDbmTracker
{
    // ── Duration unit switch (VALIDATE in-game via DbmDiag) ──────────────────────────────────────────────
    // DBMDataInfo.Duration is SECONDS — confirmed in-game: [DbmDiag] showed raw dur=20 for a 20s CountCDTime skill.
    // This is the ONE place the units are decided: DurationMs() below multiplies by 1000 to convert to the ms clock
    // that BeginTime is stamped in. (Was previously false/assume-ms, which made every countdown instantly expire.)
    private const bool  DurationIsSeconds = true;
    private static long DurationMs(long rawDuration) => DurationIsSeconds ? rawDuration * 1000L : rawDuration;

    private const int Cap = 12;   // render-list safety cap (window pool is smaller; this just bounds the sort)

    private readonly IPluginServices _services;
    private readonly List<DbmEntry>  _current = new();

    // Opt-in raw census log (mirrors the other trackers' *Diag flags) — validates Duration units + the clock.
    public bool DbmDiag;

    public BossDbmTracker(IPluginServices services) => _services = services;

    /// <summary>The active encounter's upcoming boss skills, soonest-cast first. Frame-cached (one read per
    /// frame); returns the cached list on repeat calls within the same frame. Empty when no boss encounter is
    /// active, the singleton/dict can't be resolved, or the server clock isn't trustworthy yet.</summary>
    public IReadOnlyList<DbmEntry> Current
    {
        get { Ensure(); return _current; }
    }

    // ── Frame gate ───────────────────────────────────────────────────────────────
    private int _frame = -1;

    private void Ensure()
    {
        int f = Time.frameCount;
        if (f == _frame) return;
        _frame = f;
        try { Refresh(); }
        catch (Exception ex) { _current.Clear(); LogError(ex); }
    }

    private void Refresh()
    {
        _current.Clear();
        if (!EnsureRefl()) { EmitCensus(false, false, -1, 0, ""); return; }   // DBMMgr type / getter not resolvable

        object? inst = ResolveSingletonInstance();   // ZSingleton<DBMMgr>.Instance (base-chain + FlattenHierarchy fallback)
        if (inst == null) { EmitCensus(false, false, -1, 0, ""); return; }    // singleton not up yet → retry next frame

        object? dict = null;
        try { dict = _miGetDict?.Invoke(inst, null); } catch { }
        if (dict == null) { EmitCensus(true, false, -1, 0, ""); return; }
        LogDictTypeOnce(dict);

        int count = TryCount(dict);                  // authoritative entry count (-1 if the getter isn't readable)

        long now = ServerNowMs();
        // Sanity-gate the clock: a real synced server time is a large Unix-epoch ms value. A tiny/zero value means
        // pre-sync or unavailable → we can't compute a meaningful countdown, so skip the list (don't show garbage).
        // Still emit the census so the log shows inst/dict/count even when the clock isn't ready.
        if (now < 1_600_000_000_000L) { EmitCensus(true, true, count, 0, "clock<2020"); return; }

        int    censusCount = 0;
        int    enumerated  = 0;
        string firstRaw    = "";

        // Iterate the IL2CPP dictionary VALUES robustly (see ReadDictValues — managed-IEnumerable → Keys+indexer →
        // value-enumerator ladder). The old System.Reflection Values-enumerator path silently yielded nothing here.
        foreach (var info in ReadDictValues(dict))
        {
            if (info == null) continue;
            if (!_membersResolved) ResolveMembers(info);
            if (_miName == null) break;            // struct members never resolved → give up this frame

            enumerated++;
            bool   isDead = ReadBool(_miIsDead, info);
            int    dbmId = (int)ReadLong(_miDbmId, info);
            long   begin = ReadLong(_miBeginTime, info);
            long   rawDur = ReadLong(_miDuration, info);
            string name  = ReadString(_miName, info);

            if (enumerated == 1)                   // first raw entry → into the always-on census line
                firstRaw = $"[{dbmId} '{name}' begin={begin} dur={rawDur}]";

            long durMs   = DurationMs(rawDur);
            float remain = (begin + durMs - now) / 1000f;
            float total  = durMs / 1000f;

            if (DbmDiag)
                LogCensus(dbmId, name, begin, rawDur, remain, ref censusCount);

            if (isDead) continue;                  // skill already resolved this cycle
            if (remain <= 0f) continue;            // already fired / expired → drop
            _current.Add(new DbmEntry(dbmId, string.IsNullOrEmpty(name) ? $"#{dbmId}" : name, remain, total));
        }

        // Soonest cast first (game orders the list by imminence).
        _current.Sort((a, b) => a.RemainSec.CompareTo(b.RemainSec));
        if (_current.Count > Cap) _current.RemoveRange(Cap, _current.Count - Cap);

        EmitCensus(true, true, count, enumerated, firstRaw);
        FlushCensus(now, censusCount);
    }

    // ── Singleton instance resolve (StellarInterop first, then the explicit FlattenHierarchy walk) ────────────
    // StellarInterop.GetSingleton already walks the base chain; the explicit ZSingleton<T>.Instance walk
    // (Knowledge Base\SkillCD-Tracking.md §7) is a belt-and-braces fallback. Never latched — the singleton only
    // exists during an encounter, so a null this frame must be retried next frame.
    private object? ResolveSingletonInstance()
    {
        var inst = StellarInterop.GetSingleton(_dbmMgrType!);
        if (inst != null) return inst;
        try
        {
            for (var cur = _dbmMgrType; cur != null; cur = cur.BaseType)
            {
                var p = cur.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                     ?? cur.GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (p?.GetGetMethod(nonPublic: true) is { } g)
                {
                    var r = g.Invoke(null, null);
                    if (r != null) return r;
                }
            }
        }
        catch { }
        return null;
    }

    // ── DBMMgr type + DbmInfoDict getter (resolved once, guarded) ─────────────────
    private bool        _reflResolved;
    private Type?       _dbmMgrType;
    private MethodInfo? _miGetDict;

    private bool EnsureRefl()
    {
        if (_reflResolved) return _dbmMgrType != null && _miGetDict != null;
        _reflResolved = true;
        _dbmMgrType = StellarInterop.FindType("Panda.ZUi.DBMMgr");
        if (_dbmMgrType != null)
            _miGetDict = _dbmMgrType.GetProperty("DbmInfoDict",
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
                         .GetGetMethod(nonPublic: true);
        _services.Log.Info($"[BossDbm] refl: type={_dbmMgrType != null} dict={_miGetDict != null}");
        return _dbmMgrType != null && _miGetDict != null;
    }

    // ── DBMDataInfo struct members (property-or-field; Il2CppInterop surfaces value-type members either way) ──
    private bool        _membersResolved;
    private MemberInfo? _miBeginTime;
    private MemberInfo? _miDuration;
    private MemberInfo? _miDbmId;
    private MemberInfo? _miIsDead;
    private MemberInfo? _miName;

    private void ResolveMembers(object info)
    {
        _membersResolved = true;
        var t = info.GetType();
        _miBeginTime = FindMember(t, "BeginTime");
        _miDuration  = FindMember(t, "Duration");
        _miDbmId     = FindMember(t, "DbmId");
        _miIsDead    = FindMember(t, "IsDead");
        _miName      = FindMember(t, "Name");
        _services.Log.Info($"[BossDbm] members: begin={_miBeginTime != null} dur={_miDuration != null} " +
                           $"id={_miDbmId != null} dead={_miIsDead != null} name={_miName != null} elem={t.FullName}");
    }

    // ── IL2CPP dictionary value read (robust ladder; the winning strategy is recorded for the census) ──────────
    // KEY POINT: DbmInfoDict is NOT a managed Dictionary — it's Il2CppSystem.Collections.Generic.Dictionary<int,
    // DBMDataInfo> and DBMDataInfo is a STRUCT (value type). The old approach (reflect Values → GetEnumerator/
    // MoveNext/Current) silently yielded nothing: the Il2CppInterop value-collection enumerator is a struct whose
    // Current marshals a value-type entry, and driving it through System.Reflection produced an empty walk. So try,
    // in order:
    //   1. managed IEnumerable bridge — when Il2CppInterop surfaces the proxy as System.Collections.IEnumerable we
    //      foreach it and read each KeyValuePair.Value (cleanest; no per-entry native invoke).
    //   2. Keys + indexer (option b) — read the int Keys collection (ints marshal cleanly, unlike the struct
    //      values) then get_Item(key) each entry. Sidesteps the value-struct enumerator entirely.
    //   3. value-collection enumerator (the original path) — last resort.
    // Each strategy is fully guarded; the first to return entries wins and is named in _dictStrat.
    private string _dictStrat = "";

    private List<object?> ReadDictValues(object dict)
    {
        var outList = new List<object?>();

        // Strategy 1 — managed IEnumerable over KeyValuePair<int, DBMDataInfo>.
        try
        {
            if (dict is System.Collections.IEnumerable en)
                foreach (var kvp in en)
                {
                    if (kvp == null) continue;
                    var v = ReadMember(FindMember(kvp.GetType(), "Value"), kvp);
                    if (v != null) outList.Add(v);
                }
        }
        catch { }
        if (outList.Count > 0) { _dictStrat = "ienum"; return outList; }

        // Strategy 2 — Keys collection + get_Item(key). StellarInterop.Item passes the key straight through the
        // dictionary's get_Item(int) indexer, returning the boxed struct value FindMember/ReadMember can then read.
        try
        {
            var keysColl = ReadMember(FindMember(dict.GetType(), "Keys"), dict);
            if (keysColl != null)
                foreach (var k in IterViaEnumerator(keysColl))
                {
                    if (k == null) continue;
                    int key; try { key = Convert.ToInt32(k); } catch { continue; }
                    var v = StellarInterop.Item(dict, key);
                    if (v != null) outList.Add(v);
                }
        }
        catch { }
        if (outList.Count > 0) { _dictStrat = "keys"; return outList; }

        // Strategy 3 — original value-collection enumerator (kept as a last resort).
        try
        {
            object coll = ReadMember(FindMember(dict.GetType(), "Values"), dict) ?? dict;
            foreach (var v in IterViaEnumerator(coll))
                if (v != null) outList.Add(v);
        }
        catch { }
        _dictStrat = outList.Count > 0 ? "values" : "none";
        return outList;
    }

    // Generic reflected enumerator walk (GetEnumerator → MoveNext/Current), boxed once so struct-enumerator state
    // advances in place. Used for the int Keys collection and the Strategy-3 value walk. Any miss ends it cleanly.
    private static IEnumerable<object?> IterViaEnumerator(object coll)
    {
        var miGetEnum = coll.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        object? enumerator = miGetEnum?.Invoke(coll, null);
        if (enumerator == null) yield break;
        var et = enumerator.GetType();
        var miMoveNext = et.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        var piCurrent  = et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        if (miMoveNext == null || piCurrent == null) yield break;
        while (true)
        {
            object? mv; try { mv = miMoveNext.Invoke(enumerator, null); } catch { yield break; }
            if (mv is not bool ok || !ok) yield break;
            object? current; try { current = piCurrent.GetValue(enumerator); } catch { yield break; }
            yield return current;
        }
    }

    // Read the dictionary's Count (get_Count via the property surface). −1 when the getter isn't readable — lets the
    // census distinguish "dict has 0 entries" (count=0) from "couldn't read count" (count=−1).
    private static int TryCount(object dict)
    {
        try { var v = ReadMember(FindMember(dict.GetType(), "Count"), dict); return v == null ? -1 : Convert.ToInt32(v); }
        catch { return -1; }
    }

    // ── Server clock (duplicated from TargetBuffTracker.ServerNowMs — same gated ZServerTime read) ──────────
    private bool        _serverTimeResolved;
    private object?     _serverTimeInst;
    private MethodInfo? _miGetServerTime;

    // Current server time in ms (Unix-epoch, same clock as DBMDataInfo.BeginTime). 0 = unavailable. Lazy-retry the
    // singleton while still null (a frame-1 miss before the singleton is up mustn't permanently disable the feature).
    private long ServerNowMs()
    {
        if (!_serverTimeResolved)
        {
            var t = StellarInterop.FindType("Panda.Utility.ZServerTime");
            if (t == null) { _serverTimeResolved = true; return 0L; }
            _serverTimeInst  ??= StellarInterop.GetSingleton(t);
            _miGetServerTime ??= t.GetMethod("GetServerTime", BindingFlags.Public | BindingFlags.Instance);
            if (_serverTimeInst != null) _serverTimeResolved = true;   // latch only once the instance resolves
        }
        if (_serverTimeInst == null || _miGetServerTime == null) return 0L;
        try { return (long)(_miGetServerTime.Invoke(_serverTimeInst, null) ?? 0L); }
        catch { return 0L; }
    }

    // ── Member read helpers (property-then-field across the full hierarchy) ────────
    private static MemberInfo? FindMember(Type t, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        try { var p = t.GetProperty(name, F); if (p != null && p.CanRead) return p; } catch { }
        try { var f = t.GetField(name, F); if (f != null) return f; } catch { }
        return null;
    }

    private static object? ReadMember(MemberInfo? m, object target)
    {
        try
        {
            return m switch
            {
                PropertyInfo p => p.GetValue(target),
                FieldInfo    f => f.GetValue(target),
                _              => null,
            };
        }
        catch { return null; }
    }

    private static long   ReadLong(MemberInfo? m, object t)   { var v = ReadMember(m, t); try { return v == null ? 0L : Convert.ToInt64(v); } catch { return 0L; } }
    private static bool   ReadBool(MemberInfo? m, object t)   { var v = ReadMember(m, t); try { return v != null && Convert.ToBoolean(v); } catch { return false; } }
    private static string ReadString(MemberInfo? m, object t) => ReadMember(m, t)?.ToString() ?? "";

    // ── Always-on census (NOT gated behind DbmDiag) — change-gated so it doesn't spam. This is the line that
    // pinpoints WHERE the read is empty next run: inst (singleton non-null), dictObj (getter returned non-null),
    // count (dict's own Count, −1 if unreadable), enumerated (entries we actually walked), the winning strategy,
    // and the first raw entry. A null singleton, an empty dict, and an enumeration failure are now distinguishable.
    private string _censusSig = "";

    private void EmitCensus(bool inst, bool dictObj, int count, int enumerated, string firstRaw)
    {
        string sig = $"inst={inst} dictObj={dictObj} count={count} enumerated={enumerated} " +
                     $"strat={(_dictStrat.Length == 0 ? "-" : _dictStrat)} " +
                     $"firstRaw={(firstRaw.Length == 0 ? "-" : firstRaw)}";
        if (sig == _censusSig) return;             // only log on state CHANGE
        _censusSig = sig;
        _services.Log.Info($"[BossDbm] {sig}");
    }

    // Log the dictionary's concrete runtime type exactly once (it's constant + long, so it stays out of the census).
    private bool _dictTypeLogged;

    private void LogDictTypeOnce(object dict)
    {
        if (_dictTypeLogged) return;
        _dictTypeLogged = true;
        _services.Log.Info($"[BossDbm] dictType={dict.GetType().FullName}");
    }

    // ── Diagnostics (change-gated census so it doesn't spam; validates Duration units + the server clock) ──────
    private string _diagSig = "";

    private void LogCensus(int dbmId, string name, long begin, long rawDur, float remain, ref int count)
    {
        // Accumulate one line per entry into the frame's signature; FlushCensus emits only when it changes.
        count++;
        _diagBuf.Append($"[{dbmId} '{name}' begin={begin} dur={rawDur} remain={remain:F1}] ");
    }

    private readonly System.Text.StringBuilder _diagBuf = new();

    private void FlushCensus(long now, int count)
    {
        if (!DbmDiag) { _diagBuf.Clear(); return; }
        string sig = $"n={count} now={now} {_diagBuf}";
        _diagBuf.Clear();
        if (sig == _diagSig) return;
        _diagSig = sig;
        _services.Log.Info($"[DbmDiag] serverNow={now} entries={count} {(count == 0 ? "(none)" : sig)}");
    }

    private bool _loggedError;

    private void LogError(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _services.Log.Warning($"[BossDbm] {ex.InnerException?.Message ?? ex.Message}");
    }
}
