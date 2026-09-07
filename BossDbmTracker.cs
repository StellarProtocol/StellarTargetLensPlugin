using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// Feeds the Boss Skill Timers overlay (<see cref="Plugin"/>.<c>Plugin.BossTimerWindow.cs</c>) with the game's
/// native boss "deadly-skill" (DBM) countdown list — the same rows the game shows top-left ("Mighty Bite 00:08").
/// A SINGLE GLOBAL encounter list, not keyed per target.
///
/// <para><b>Source: a Harmony postfix, not polling.</b> The old path polled <c>DBMMgr.DbmInfoDict</c>, whose backing
/// <c>dbmInfoDict_</c> field is Init/UnInit lifecycle-owned and returned null across whole encounters → the overlay
/// blanked for entire fights. We now capture from the PRODUCER via <see cref="DbmPatch"/> (postfix on
/// <c>DBMMgr.onDBMDatacChanged(RepeatedField&lt;int&gt; skillCds, long startTime)</c>). Each server push carries the
/// current active DBM skill-id set plus one shared <c>startTime</c> anchor (server-epoch ms).</para>
///
/// <para>Per skill, next-cast/expiry = <c>startTime + DbmTable[id].CountCDTime*1000</c> (CountCDTime is SECONDS).
/// The row name comes from <c>DbmTable[id].Content</c>. The lookup mirrors <see cref="TargetInfoTracker"/>'s
/// <c>ResolveMonsterName</c>: static <c>Bokura.DbmTableBase.GetTable(false)</c> → 3-arg <c>TryGetValue(id, out row,
/// false)</c> → row props. The table instance is retried each call until non-null (config tables may not be loaded
/// on the first read).</para>
///
/// <para>Each captured id is UPSERTED into a persistent latch keyed by DbmId (name + begin + duration cached), and
/// the display is driven from the latch — entries count smoothly to 0 and are dropped ONLY when they genuinely
/// expire (remain ≤ 0). This survives both full-set pushes (the common case) and any push that momentarily omits a
/// mid-countdown skill. Server clock reuses the gated <c>ZServerTime.GetServerTime</c> read (≈2020 sanity floor);
/// an untrusted/zero clock returns the last-built list rather than garbage countdowns. Everything is frame-cached
/// and guarded — one read per frame, never throws.</para>
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
    private const int Cap = 12;   // render-list safety cap (window pool is smaller; this just bounds the sort)

    private readonly IPluginServices _services;
    private readonly List<DbmEntry>  _current = new();

    // ── Persistent latch (keyed by DbmId) ─────────────────────────────────────────────────────────────────
    // A server push carries the current active set, but we UPSERT (not replace) so a mid-countdown skill survives
    // a push that momentarily omits it. An entry is dropped ONLY when it genuinely expires (remain ≤ 0), so the
    // per-entry expiry bounds any staleness to a single countdown window — no explicit encounter-exit reset needed
    // (the old singleton-null exit signal is gone with the poll path).
    private readonly Dictionary<int, (long beginMs, long durMs, string name)> _latch = new();
    private readonly List<int> _expired = new();   // scratch reused each build to remove expired keys after iterating

    private int _lastVersion;   // last DbmPatch capture version consumed (upsert only on a fresh batch)

    // Opt-in per-entry census log (mirrors the other trackers' *Diag flags) — validates the CD units + the clock.
    public bool DbmDiag;

    public BossDbmTracker(IPluginServices services) => _services = services;

    /// <summary>The active encounter's upcoming boss skills, soonest-cast first. Frame-cached (one read per
    /// frame). Empty when no push has landed, the DbmTable can't be resolved, or the server clock isn't trusted.</summary>
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
        DbmPatch.GetBatch(out var ids, out var startTime, out var version);

        long now = ServerNowMs();
        // Sanity-gate the clock: a real synced server time is a large Unix-epoch ms value. A tiny/zero value means
        // pre-sync or unavailable → we can't compute a meaningful countdown this frame.
        bool clockOk = now >= 1_600_000_000_000L;

        // ── Phase 1: UPSERT the latest captured batch into the latch (no clock needed). Only on a fresh push —
        // re-latching every frame would re-run the DbmTable reflection for nothing. ──────────────────────────────
        if (version != _lastVersion)
        {
            _lastVersion = version;
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                if (!TryGetDbmRow(id, out string name, out int cdSec)) { name = $"#{id}"; cdSec = 0; }
                long durMs = (long)cdSec * 1000L;               // CountCDTime is SECONDS → ×1000 to the ms clock
                _latch[id] = (startTime, durMs, name);          // overwrite → a re-armed skill's new startTime refreshes it
            }
        }

        // ── Phase 2: an untrusted clock can't produce a meaningful countdown. Return the last-built list WITHOUT
        // clearing the latch or _current. ────────────────────────────────────────────────────────────────────────
        if (!clockOk) { EmitCensus(ids.Length, _latch.Count); return; }

        // ── Phase 3: build the display straight from the latch. Entries persist across empty pushes and count
        // smoothly to 0; only a genuinely expired entry (remain ≤ 0) is dropped from the latch here. ─────────────
        _current.Clear();
        _expired.Clear();
        foreach (var kv in _latch)
        {
            var (beginMs, durMs, name) = kv.Value;
            float remain = (beginMs + durMs - now) / 1000f;
            if (DbmDiag)
                _diagBuf.Append($"[{kv.Key} '{name}' begin={beginMs} cd={durMs / 1000L} remain={remain:F1}] ");
            if (remain <= 0f) { _expired.Add(kv.Key); continue; }   // genuinely expired → remove after the loop
            float total = durMs / 1000f;
            _current.Add(new DbmEntry(kv.Key, string.IsNullOrEmpty(name) ? $"#{kv.Key}" : name, remain, total));
        }
        for (int i = 0; i < _expired.Count; i++) _latch.Remove(_expired[i]);

        // Soonest cast first (game orders the list by imminence).
        _current.Sort((a, b) => a.RemainSec.CompareTo(b.RemainSec));
        if (_current.Count > Cap) _current.RemoveRange(Cap, _current.Count - Cap);

        EmitCensus(ids.Length, _latch.Count);
        FlushDiag(now);
    }

    // ── DbmTable lookup (mirrors TargetInfoTracker.ResolveMonsterName) ────────────────────────────────────────
    // static Bokura.DbmTableBase.GetTable(bool) → ZTable<int,DbmTableBase>; 3-arg TryGetValue(id, out row, false);
    // row.Content = display name, row.CountCDTime = countdown period in SECONDS.
    private bool         _dbmReflResolved;   // type + GetTable method resolved (once)
    private Type?        _dbmTblType;
    private MethodInfo?  _miGetTable;
    private object?      _dbmTblObj;         // ZTable instance — lazy, retried until non-null (tables load late)
    private MethodInfo?  _miTryGetValue;
    private bool         _tryGetResolved;
    private PropertyInfo? _piContent;
    private PropertyInfo? _piCountCD;

    private bool TryGetDbmRow(int id, out string name, out int cdSec)
    {
        name = ""; cdSec = 0;
        if (!EnsureDbmTable()) return false;
        try
        {
            // errorWhenNotFound = false — a miss must NOT log a spurious game error.
            var args = new object?[] { id, null, false };
            bool ok = _miTryGetValue!.Invoke(_dbmTblObj, args) is bool b && b;
            var row = ok ? args[1] : null;
            if (row == null) return false;
            _piContent ??= row.GetType().GetProperty("Content",     BindingFlags.Public | BindingFlags.Instance);
            _piCountCD ??= row.GetType().GetProperty("CountCDTime", BindingFlags.Public | BindingFlags.Instance);
            name  = _piContent?.GetValue(row) as string ?? "";
            cdSec = _piCountCD?.GetValue(row) is int c ? c : 0;
            return true;
        }
        catch { return false; }
    }

    private bool EnsureDbmTable()
    {
        if (!_dbmReflResolved)
        {
            _dbmReflResolved = true;
            _dbmTblType = StellarInterop.FindType("Bokura.DbmTableBase");
            if (_dbmTblType != null)
                foreach (var m in _dbmTblType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetTable" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(bool))
                    { _miGetTable = m; break; }
            _services.Log.Info($"[BossDbm] DbmTable refl: type={_dbmTblType != null} getTable={_miGetTable != null}");
        }
        if (_miGetTable == null) return false;
        // Table instance may be null until config tables finish loading → retry each call (do NOT latch on a miss).
        if (_dbmTblObj == null)
            try { _dbmTblObj = _miGetTable.Invoke(null, new object[] { false }); } catch { }
        if (_dbmTblObj == null) return false;
        if (!_tryGetResolved)
        {
            _tryGetResolved = true;
            foreach (var m in _dbmTblObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "TryGetValue" && m.GetParameters().Length == 3) { _miTryGetValue = m; break; }
            _services.Log.Info($"[BossDbm] DbmTable: inst={_dbmTblObj != null} tryGet={_miTryGetValue != null}");
        }
        return _miTryGetValue != null;
    }

    // ── Server clock (duplicated from TargetBuffTracker.ServerNowMs — same gated ZServerTime read) ──────────
    private bool        _serverTimeResolved;
    private object?     _serverTimeInst;
    private MethodInfo? _miGetServerTime;

    // Current server time in ms (Unix-epoch, same clock as the DBM startTime anchor). 0 = unavailable. Lazy-retry
    // the singleton while still null (a frame-1 miss before the singleton is up mustn't permanently disable this).
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

    // ── Always-on census (NOT gated behind DbmDiag) — change-gated so it doesn't spam. Confirms the postfix is
    // firing and the latch holds across runs: patched (did the postfix install), batchIds (size of the last capture),
    // latched (current latch size), and the first latched entry (id/name/begin/cd). ──────────────────────────────
    private string _censusSig = "";

    private void EmitCensus(int batchIds, int latched)
    {
        string firstRaw = "-";
        foreach (var kv in _latch)
        { firstRaw = $"[{kv.Key} '{kv.Value.name}' begin={kv.Value.beginMs} cd={kv.Value.durMs / 1000L}]"; break; }

        string sig = $"patched={DbmPatch.Installed} batchIds={batchIds} latched={latched} firstRaw={firstRaw}";
        if (sig == _censusSig) return;             // only log on state CHANGE
        _censusSig = sig;
        _services.Log.Info($"[BossDbm] {sig}");
    }

    // ── Diagnostics (change-gated per-entry census, opt-in via DbmDiag) — validates CD units + the server clock ──
    private readonly System.Text.StringBuilder _diagBuf = new();
    private string _diagSig = "";

    private void FlushDiag(long now)
    {
        if (!DbmDiag) { _diagBuf.Clear(); return; }
        string sig = $"latched={_latch.Count} now={now} {_diagBuf}";
        _diagBuf.Clear();
        if (sig == _diagSig) return;
        _diagSig = sig;
        _services.Log.Info($"[DbmDiag] serverNow={now} latched={_latch.Count} {(_latch.Count == 0 ? "(none)" : sig)}");
    }

    private bool _loggedError;

    private void LogError(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _services.Log.Warning($"[BossDbm] {ex.InnerException?.Message ?? ex.Message}");
    }
}
