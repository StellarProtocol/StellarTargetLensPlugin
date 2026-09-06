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
/// <para>Source: <c>Panda.ZUi.DBMMgr : ZSingleton&lt;DBMMgr&gt;</c>. Its <c>DbmInfoDict</c> property is a managed
/// <c>Dictionary&lt;int, DBMDataInfo&gt;</c> (key = DbmId). Each <c>DBMDataInfo</c> struct carries
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
    // DbmTable.CountCDTime values are small (5/10/15/20), so DBMDataInfo.Duration is plausibly SECONDS. But it may
    // already be milliseconds. This is the ONE place the units are decided: DurationMs() below multiplies by 1000
    // only when this is true. Default = false (assume ms). Flip to true if [DbmDiag] shows raw dur ≈ 5..20.
    private const bool  DurationIsSeconds = false;
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
        if (!EnsureRefl()) return;                 // DBMMgr type / getter not resolvable → empty (logged once)

        object? inst = StellarInterop.GetSingleton(_dbmMgrType!);   // ZSingleton<DBMMgr>.Instance (base-chain walk)
        if (inst == null) return;                  // singleton not up yet → retry next frame

        object? dict = _miGetDict?.Invoke(inst, null);
        if (dict == null) return;

        long now = ServerNowMs();
        // Sanity-gate the clock: a real synced server time is a large Unix-epoch ms value. A tiny/zero value means
        // pre-sync or unavailable → we can't compute a meaningful countdown, so skip the list (don't show garbage).
        if (now < 1_600_000_000_000L) return;      // ≈ 2020-09; below this = not a valid server clock

        int censusCount = 0;
        foreach (var info in IterDictValues(dict))
        {
            if (info == null) continue;
            if (!_membersResolved) ResolveMembers(info);
            if (_miName == null) break;            // struct members never resolved → give up this frame

            bool isDead = ReadBool(_miIsDead, info);
            if (isDead) continue;

            int    dbmId = (int)ReadLong(_miDbmId, info);
            long   begin = ReadLong(_miBeginTime, info);
            long   rawDur = ReadLong(_miDuration, info);
            string name  = ReadString(_miName, info);

            long durMs   = DurationMs(rawDur);
            float remain = (begin + durMs - now) / 1000f;
            float total  = durMs / 1000f;

            if (DbmDiag)
                LogCensus(dbmId, name, begin, rawDur, remain, ref censusCount);

            if (remain <= 0f) continue;            // already fired / expired → drop
            _current.Add(new DbmEntry(dbmId, string.IsNullOrEmpty(name) ? $"#{dbmId}" : name, remain, total));
        }

        // Soonest cast first (game orders the list by imminence).
        _current.Sort((a, b) => a.RemainSec.CompareTo(b.RemainSec));
        if (_current.Count > Cap) _current.RemoveRange(Cap, _current.Count - Cap);

        FlushCensus(now, censusCount);
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

    // ── Managed-dictionary value enumeration (Values → GetEnumerator/MoveNext/Current, all reflected + guarded) ──
    // DbmInfoDict is a managed Dictionary<int, DBMDataInfo>; enumerating its Values yields DBMDataInfo directly. The
    // enumerator is boxed ONCE and MoveNext is invoked on that same box each iteration so its (possibly struct) state
    // advances correctly. Any reflection miss simply ends the enumeration → empty list.
    private static IEnumerable<object?> IterDictValues(object dict)
    {
        object? enumerator, current, moveNextResult;
        var valuesProp = dict.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance);
        object coll = valuesProp?.GetValue(dict) ?? dict;
        var miGetEnum = coll.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        enumerator = miGetEnum?.Invoke(coll, null);
        if (enumerator == null) yield break;
        var et = enumerator.GetType();
        var miMoveNext = et.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        var piCurrent  = et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        if (miMoveNext == null || piCurrent == null) yield break;
        while (true)
        {
            moveNextResult = miMoveNext.Invoke(enumerator, null);
            if (moveNextResult is not bool ok || !ok) yield break;
            current = piCurrent.GetValue(enumerator);
            yield return current;
        }
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
