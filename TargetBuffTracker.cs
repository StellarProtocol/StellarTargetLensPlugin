using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// Reads the current target's live buffs / debuffs for the "Target / Monster Info" window.
///
/// Unlike the local-player path (which Harmony-patches BuffComp to capture the player's component instance), the
/// target's <c>BuffComp</c> / <c>ClientBuffComp</c> are read straight off the resolved target ZEntity that
/// <see cref="TargetInfoTracker"/> already produces each frame — no patch needed. All reflection is cached once and
/// guarded; <see cref="Ensure"/> never throws. Polling only happens while the window's getters drive it, so there is
/// no always-on cost.
///
/// Reuses <c>BuffTrackPatch.LookupBuff</c> for name/type resolution (same cached BuffTableBase reflection).
/// </summary>
public sealed class TargetBuffRow
{
    public int    BaseId;
    public string Name = "";
    public int    Layer;
    public long   FireUuid;          // caster uuid (BuffItem.FireUuid); 0 = unknown (client-buff path never has it)
    public long   Duration;          // ms; 0 = permanent
    public int    SkillId;           // source skill table id; 0 = unknown
    public string SkillName = "";    // resolved source skill name; "" = unknown
    internal int  BuffType = -1;     // 0=Debuff 1=Gain 2=GainRecovery 3=Item; -1 unknown
    internal long ArrivalSeq;        // first-seen order; stable render position (CooldownBar-style)
    internal long CreateTime;        // ms; re-snap trigger (see Buff-Tracking.md §7)
    internal long  SnapTick;
    internal float SnapRemain;

    public float RemainSec => Duration == 0 ? -1f
        : MathF.Max(0f, SnapRemain - (Environment.TickCount64 - SnapTick) / 1000f);
}

internal sealed class TargetBuffTracker
{
    private const int Cap = 12;   // per-list render cap

    private readonly IPluginServices  _services;
    private readonly TargetInfoTracker _info;

    private readonly Dictionary<int, TargetBuffRow> _persist = new(); // keyed by BuffUuid
    private readonly List<TargetBuffRow> _buffs   = new();
    private readonly List<TargetBuffRow> _debuffs = new();
    private readonly List<TargetBuffRow> _all     = new();
    private readonly HashSet<int> _live  = new();
    private readonly List<int>    _stale = new();

    public IReadOnlyList<TargetBuffRow> Buffs   => _buffs;
    public IReadOnlyList<TargetBuffRow> Debuffs => _debuffs;
    // Combined buffs + debuffs (the compact Target HUD renders this single merged list).
    public IReadOnlyList<TargetBuffRow> All     => _all;

    // Three independent per-source-category filters (default ALL true = show everything). Each row is classified by
    // its caster (FireUuid) into exactly one of Mine / Other / MonsterOrUnknown (see Category); a row is kept only
    // when its category's toggle is on. All-true is a fast path that skips classification (see RebuildLists).
    public bool ShowMine    { get; set; } = true;   // effects the local player applied
    public bool ShowOthers  { get; set; } = true;   // effects a third party (not me, not the target) applied
    public bool ShowMonster { get; set; } = true;   // the target's own self-applied effects + unknown-source (0)

    // When true, iterate the FULL unfiltered buff list (pBuffList_/buffList_) so internal/no-icon buffs appear;
    // when false (default), iterate the game's display-filtered "showed" list (pShowedBuffList_/showedBuffList_).
    // Set from Plugin on load and whenever the "Show hidden effects" toggle changes.
    public bool ShowHidden { get; set; }

    // User's per-effect show/hide selection (buffs + debuffs, each with an include-only/exclude mode). When set,
    // RebuildLists skips rows the selection hides. Null (unset) shows everything (default until wired in).
    public TargetEffectSelection? Selection { get; set; }

    // De-duplicated diagnostic: log the first time each distinct nonzero FireUuid is seen, to confirm in-game
    // whether FireUuid shares the LocalEntityId id-space for self-applied effects (UNVERIFIED). HashSet caps spam.
    private readonly HashSet<long> _firesLogged = new();

    public TargetBuffTracker(IPluginServices services, TargetInfoTracker info)
    {
        _services = services;
        _info     = info;
    }

    // ── Frame gate ───────────────────────────────────────────────────────────
    private int  _frame = -1;
    private long _lastUuid;
    private long _arrivalSeq;   // monotonically increasing first-seen counter (stable ordering)

    public void Ensure()
    {
        int f = Time.frameCount;
        if (f == _frame) return;
        _frame = f;
        try { Refresh(); }
        catch (Exception ex) { LogError(ex); }
    }

    private void Refresh()
    {
        _ = _info.Current;                       // force the info tracker's poll this frame → populates LastTarget*
        var  ent  = _info.LastTargetEntity;
        long uuid = _info.LastTargetUuid;

        if (ent == null || uuid == 0)
        {
            _buffs.Clear(); _debuffs.Clear(); _all.Clear(); _persist.Clear(); _lastUuid = 0;
            BuffTrackPatch.SourceCaptureTargetUuid = 0;
            return;
        }
        if (uuid != _lastUuid) { _persist.Clear(); _lastUuid = uuid; }

        // Tell the OnAddBuff postfix to capture fight-source for this target too, and keep it active (the postfix
        // is demand-gated) so target buff sources get cached while this window drives the poll.
        BuffTrackPatch.SourceCaptureTargetUuid = uuid;
        BuffTrackPatch.MarkDemand();

        if (!_compReflResolved) ResolveCompRefl();
        _live.Clear();
        int serverCount = 0, clientCount = 0;

        var buffComp   = _piBuffComp?.GetValue(ent);
        var clientComp = _piClientBuffComp?.GetValue(ent);

        // ── Server list ──
        if (buffComp != null)
        {
            if (!_showedResolved) ResolveShowedList(buffComp);
            var list = ActiveList?.GetValue(buffComp);
            if (list != null)
            {
                foreach (var item in IterList(list))
                {
                    if (item == null) continue;
                    if (!_itemResolved) ResolveBuffItemFields(item);
                    if (_piItemUuid == null) break;
                    int u = (int)(_piItemUuid.GetValue(item) ?? 0);
                    if (u == 0) continue;
                    _live.Add(u);
                    int  baseId   = (int)(_piItemBaseId?.GetValue(item)   ?? 0);
                    int  layer    = (int)(_piItemLayer?.GetValue(item)    ?? 1);
                    long durMs    = (long)(_piItemDuration?.GetValue(item) ?? 0L);
                    long createMs = (long)(_piItemCreate?.GetValue(item)   ?? 0L);
                    long fire     = (long)(_piItemFire?.GetValue(item)     ?? 0L);
                    Upsert(u, baseId, layer, durMs, createMs, fire);
                    serverCount++;
                    // One-shot per distinct caster: confirm whether fire == local for self-applied effects.
                    if (fire != 0 && _firesLogged.Add(fire))
                        _services.Log.Info($"[TargetBuff] fire={fire} local={_services.CombatSnapshot.LocalEntityId.Value} baseId={baseId}");
                }
            }
        }

        // ── Client list (optional) ──
        if (clientComp != null)
        {
            if (!_clientListResolved) ResolveClientList(clientComp);
            var list = _piClientList?.GetValue(clientComp);
            if (list != null)
            {
                foreach (var item in IterList(list))
                {
                    if (item == null) continue;
                    if (!_clientFieldsResolved) ResolveClientFields(item);
                    if (_piClientUuid == null) break;
                    if (_fiClientInvalid != null && (bool)(_fiClientInvalid.GetValue(item) ?? false)) continue;
                    int u = (int)(_piClientUuid.GetValue(item) ?? 0);
                    if (u == 0) continue;
                    _live.Add(u);
                    int   buffId = (int)(_piClientId?.GetValue(item)      ?? 0);
                    int   layer  = (int)(_piClientLayer?.GetValue(item)   ?? 1);
                    float maxL   = (float)(_piClientMaxLife?.GetValue(item) ?? 0f);
                    long  durMs  = maxL > 0f ? (long)(maxL * 1000f) : 0L;
                    Upsert(u, buffId, layer, durMs, 0L, 0L);   // ClientBuffComp has no FireUuid → caster unknown (0)
                    clientCount++;
                }
            }
        }

        // Stale sweep — drop entries no longer present in either list.
        _stale.Clear();
        foreach (var kv in _persist)
            if (!_live.Contains(kv.Key)) _stale.Add(kv.Key);
        foreach (var k in _stale) _persist.Remove(k);

        RebuildLists();

        // One-shot diagnostic per distinct target.
        if (uuid != _lastDiagUuid)
        {
            _lastDiagUuid = uuid;
            _services.Log.Info($"[TargetBuff] uuid={uuid} buffComp={buffComp != null} clientComp={clientComp != null} " +
                $"server={serverCount} client={clientCount} buffs={_buffs.Count} debuffs={_debuffs.Count}");
        }
    }

    private void Upsert(int uuid, int baseId, int layer, long durMs, long createMs, long fireUuid)
    {
        bool isNew = !_persist.TryGetValue(uuid, out var row);
        BuffTrackPatch.LookupBuff(baseId, out var nm, out var bt, out var tableSkill);
        // The game frees and REUSES buff uuids: a long-lived target (e.g. a boss churning buffs across phases while
        // it stays your selected target) can hand the same uuid to a DIFFERENT buff. Re-populate identity when the
        // incoming baseId differs from what's cached — not only on first sight — or the row keeps the freed buff's
        // name/icon. _persist is already cleared on target change (uuid != _lastUuid) and target loss, so this only
        // covers the same-target reuse case those clears can't catch. GetSourceSkill is read-through (no removal
        // API) so re-running the source resolution is the correct refresh here.
        bool reinit = isNew || row!.BaseId != baseId;
        if (reinit)
        {
            row ??= new TargetBuffRow();
            row.BaseId     = baseId;
            row.Name       = string.IsNullOrEmpty(nm) ? $"#{baseId}" : nm;
            row.BuffType   = bt;
            row.FireUuid   = fireUuid;   // caster; constant for a given buff instance
            row.ArrivalSeq = _arrivalSeq++;   // a reinit is effectively a new effect → take a fresh render position
            // Prefer the add-time captured FightSourceInfo skill; fall back to the buff table's static SkillId.
            int src     = BuffTrackPatch.GetSourceSkill(uuid);
            int skillId = src > 0 ? src : tableSkill;
            row.SkillId   = skillId;
            row.SkillName = skillId > 0 ? BuffTrackPatch.LookupSkillName(skillId) : "";
            _persist[uuid] = row;
            // Diagnostic: what the tile-icon path sees for this effect (src vs table skill, imagine detection).
            _services.Log.Info($"[HudIcon] +eff base={baseId} src={src} table={tableSkill} skill={skillId} " +
                $"name='{row.Name}' imagine={(skillId > 0 && _services.ResonanceData.GetImagineForSkill(skillId) != null)}");
        }
        else if (row!.SkillId == 0)
        {
            // Late capture: FightSourceInfo can arrive after the buff first appears (already on the mob when the
            // window opened, then re-synced). Retry cheaply while still unknown.
            int s2 = BuffTrackPatch.GetSourceSkill(uuid);
            if (s2 == 0) s2 = tableSkill;
            if (s2 > 0) { row.SkillId = s2; row.SkillName = BuffTrackPatch.LookupSkillName(s2); }
        }
        row!.Layer = layer;
        bool createChanged = row.CreateTime != createMs;
        if (reinit || row.Duration != durMs || createChanged)
        {
            row.Duration   = durMs;
            row.CreateTime = createMs;
            row.SnapTick   = Environment.TickCount64;
            row.SnapRemain = ComputeSnapRemain(durMs, createMs);
        }
    }

    // True remaining seconds from the server clock: CreateTime + Duration − serverNow (all ms, same epoch).
    // Falls back to full-duration (old behavior) when we lack a real CreateTime (client buffs pass createMs==0)
    // or the server clock isn't available/synced yet — so nothing regresses in those cases.
    private float ComputeSnapRemain(long durMs, long createMs)
    {
        if (durMs <= 0) return -1f;                         // permanent → no timer
        if (createMs > 0)
        {
            long now = ServerNowMs();
            // Sanity-gate the clock: a real synced server time is a large Unix-epoch ms value. A tiny/zero value
            // means pre-sync or unavailable → don't trust it, fall back to full duration.
            if (now >= 1_600_000_000_000L)                  // ≈ 2020-09; below this = not a valid server clock
            {
                float remain = (createMs + durMs - now) / 1000f;
                float full   = durMs / 1000f;
                return remain < 0f ? 0f : (remain > full ? full : remain);   // clamp to [0, full]
            }
        }
        return durMs / 1000f;                               // client buff / pre-sync → assume full (unchanged)
    }

    // Source category for a row, classified by caster (FireUuid). Order matters: Mine → MonsterOrUnknown → Other,
    // each row is exactly one. localUuid/targetUuid may be 0 (unresolved) — a 0 id simply never matches, and an
    // otherwise-unclassifiable row falls to Other, so classification never throws and never hides on its own.
    private enum EffectSource { Mine, Other, MonsterOrUnknown }

    private static EffectSource Category(long fire, long localUuid, long targetUuid)
    {
        // Mine: strict uuid match OR roleId (>>16) fallback — a self-source uuid can differ from the entity uuid in
        // the low (client/summon/entType) bits, same encoding nuance as the threat local-player match.
        if (localUuid != 0 && (fire == localUuid || (fire >> 16) == (localUuid >> 16)))
            return EffectSource.Mine;
        // Monster/unknown: no/unresolved source (0), or the target's OWN self-applied effect (strict + roleId).
        if (fire == 0 || (targetUuid != 0 && (fire == targetUuid || (fire >> 16) == (targetUuid >> 16))))
            return EffectSource.MonsterOrUnknown;
        return EffectSource.Other;   // a valid FireUuid that is neither mine nor the target
    }

    private void RebuildLists()
    {
        _buffs.Clear(); _debuffs.Clear(); _all.Clear();
        // Per-category source filter. Fast path: all three on → show everything, skip classification entirely.
        // Otherwise classify each row and keep it only when its category's toggle is on. Fully guarded: unresolved
        // ids (localUuid/targetUuid == 0) just fail their equality checks; rows fall to "Other" rather than vanish.
        bool filter = !(ShowMine && ShowOthers && ShowMonster);
        long localUuid  = filter ? _services.CombatSnapshot.LocalEntityId.Value : 0L;
        long targetUuid = filter ? _info.LastTargetUuid : 0L;
        foreach (var row in _persist.Values)
        {
            if (row.Duration > 0 && row.RemainSec < 0.05f) continue;  // render-time expiry guard
            if (filter)
            {
                var cat = Category(row.FireUuid, localUuid, targetUuid);
                bool keep = cat switch
                {
                    EffectSource.Mine             => ShowMine,
                    EffectSource.MonsterOrUnknown => ShowMonster,
                    _                             => ShowOthers,
                };
                if (!keep) continue;
            }
            if (Selection != null && !Selection.ShouldShow(row.BaseId, row.BuffType == 0)) continue;  // user hid this effect
            if (row.BuffType == 0) _debuffs.Add(row);
            else                   _buffs.Add(row);                    // type 1/2 and unknown → Buffs
            _all.Add(row);                                             // combined list for the compact HUD
        }
        Sort(_buffs); Sort(_debuffs); Sort(_all);
        Cap0(_buffs, "buffs"); Cap0(_debuffs, "debuffs"); Cap0(_all, "all");
    }

    // First-seen (arrival) order — effects keep their position, new ones append, expired ones drop out
    // (CooldownBar-style stable ordering rather than re-sorting by remaining time each frame).
    private static void Sort(List<TargetBuffRow> list) =>
        list.Sort((a, b) => a.ArrivalSeq.CompareTo(b.ArrivalSeq));

    private void Cap0(List<TargetBuffRow> list, string tag)
    {
        if (list.Count <= Cap) return;
        _services.Log.Info($"[TargetBuff] {tag} truncated {list.Count}→{Cap}");
        list.RemoveRange(Cap, list.Count - Cap);
    }

    // ── Reflection ───────────────────────────────────────────────────────────
    private bool          _compReflResolved;
    private PropertyInfo? _piBuffComp;
    private PropertyInfo? _piClientBuffComp;

    private void ResolveCompRefl()
    {
        _compReflResolved = true;
        var t = StellarInterop.FindType("Panda.ZGame.ZEntity");
        if (t == null) return;
        _piBuffComp       = t.GetProperty("BuffComp",       BindingFlags.Public | BindingFlags.Instance);
        _piClientBuffComp = t.GetProperty("ClientBuffComp", BindingFlags.Public | BindingFlags.Instance);
        _services.Log.Info($"[TargetBuff] comp refl: buffComp={_piBuffComp != null} clientComp={_piClientBuffComp != null}");
    }

    // ── Server clock (for true buff remaining = CreateTime + Duration − serverNow) ──
    private bool        _serverTimeResolved;
    private object?     _serverTimeInst;
    private MethodInfo? _miGetServerTime;

    // Current server time in ms (Unix-epoch, same clock as BuffItem.CreateTime/Duration). 0 = unavailable.
    // Lazy-retry the singleton instance while it's still null (mirrors ResolveEntityMgr/GetPlayerUuid) so a
    // frame-1 miss — before the singleton is up — doesn't permanently disable the feature; the method-info caches.
    private long ServerNowMs()
    {
        if (!_serverTimeResolved)
        {
            var t = StellarInterop.FindType("Panda.Utility.ZServerTime");
            if (t == null) { _serverTimeResolved = true; return 0L; }   // type gone → give up permanently
            _serverTimeInst  ??= StellarInterop.GetSingleton(t);
            _miGetServerTime ??= t.GetMethod("GetServerTime", BindingFlags.Public | BindingFlags.Instance);
            if (_serverTimeInst != null)
            {
                _serverTimeResolved = true;   // latch only once the instance actually resolves
                _services.Log.Info($"[TargetBuff] serverTime: inst=True m={_miGetServerTime != null}");
            }
        }
        if (_serverTimeInst == null || _miGetServerTime == null) return 0L;
        try { return (long)(_miGetServerTime.Invoke(_serverTimeInst, null) ?? 0L); }
        catch { return 0L; }
    }

    private bool          _showedResolved;
    private PropertyInfo? _piFullList;     // pBuffList_ / buffList_        — unfiltered  (ShowHidden ON)
    private PropertyInfo? _piShowedList;   // pShowedBuffList_ / showedBuffList_ — display-filtered (ShowHidden OFF)

    // Resolve BOTH list sources once; ActiveList picks between them at read-time by the ShowHidden flag.
    private void ResolveShowedList(object comp)
    {
        _showedResolved = true;
        PropertyInfo? full = null;   int fullPrio   = 99;
        PropertyInfo? showed = null; int showedPrio = 99;
        foreach (var p in comp.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            int fp = p.Name switch { "pBuffList_" => 0, "buffList_" => 1, _ => 99 };
            if (fp < fullPrio)   { full   = p; fullPrio   = fp; }
            int sp = p.Name switch { "pShowedBuffList_" => 0, "showedBuffList_" => 1, _ => 99 };
            if (sp < showedPrio) { showed = p; showedPrio = sp; }
        }
        // Cross-fall-back so neither is null on a build that lacks one set.
        _piFullList   = full   ?? showed;
        _piShowedList = showed ?? full;
        _services.Log.Info($"[TargetBuff] lists: full={_piFullList?.Name ?? "not found"} showed={_piShowedList?.Name ?? "not found"}");
    }

    // The buff list to iterate this frame: full (unfiltered) when ShowHidden, else the display-filtered showed list.
    private PropertyInfo? ActiveList => ShowHidden ? _piFullList : _piShowedList;

    private bool          _itemResolved;
    private PropertyInfo? _piItemUuid;
    private PropertyInfo? _piItemBaseId;
    private PropertyInfo? _piItemLayer;
    private PropertyInfo? _piItemDuration;
    private PropertyInfo? _piItemCreate;
    private PropertyInfo? _piItemFire;

    private void ResolveBuffItemFields(object item)
    {
        _itemResolved = true;
        foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if      (p.Name == "BuffUuid")   _piItemUuid     = p;
            else if (p.Name == "BuffBaseId") _piItemBaseId   = p;
            else if (p.Name == "Layer")      _piItemLayer    = p;
            else if (p.Name == "Duration")   _piItemDuration = p;
            else if (p.Name == "CreateTime") _piItemCreate   = p;
            else if (p.Name == "FireUuid")   _piItemFire     = p;
        }
        _services.Log.Info($"[TargetBuff] item props: uuid={_piItemUuid != null} dur={_piItemDuration != null} create={_piItemCreate != null}");
    }

    private bool          _clientListResolved;
    private PropertyInfo? _piClientList;

    private void ResolveClientList(object comp)
    {
        _clientListResolved = true;
        _piClientList = comp.GetType().GetProperty("BuffList", BindingFlags.Public | BindingFlags.Instance);
        _services.Log.Info($"[TargetBuff] client BuffList: {_piClientList != null}");
    }

    private bool          _clientFieldsResolved;
    private PropertyInfo? _piClientUuid;
    private PropertyInfo? _piClientId;
    private PropertyInfo? _piClientMaxLife;
    private PropertyInfo? _piClientLayer;
    private FieldInfo?    _fiClientInvalid;

    private void ResolveClientFields(object item)
    {
        _clientFieldsResolved = true;
        var t = item.GetType();
        _piClientUuid    = t.GetProperty("BuffUuid",     BindingFlags.Public | BindingFlags.Instance);
        _piClientId      = t.GetProperty("BuffId",       BindingFlags.Public | BindingFlags.Instance);
        _piClientMaxLife = t.GetProperty("BuffMaxLife",  BindingFlags.Public | BindingFlags.Instance);
        _piClientLayer   = t.GetProperty("CurrentLayer", BindingFlags.Public | BindingFlags.Instance);
        _fiClientInvalid = t.GetField("IsInValid",       BindingFlags.Public | BindingFlags.Instance);
    }

    private static IEnumerable<object?> IterList(object list)
    {
        var t   = list.GetType();
        var cnt = (int)(t.GetProperty("Count")?.GetValue(list) ?? 0);
        var idx = t.GetProperty("Item");
        for (int i = 0; i < cnt; i++)
            yield return idx?.GetValue(list, new object[] { i });
    }

    private long _lastDiagUuid = long.MinValue;
    private bool _loggedError;

    private void LogError(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _services.Log.Warning($"[TargetBuff] {ex.InnerException?.Message ?? ex.Message}");
    }
}
