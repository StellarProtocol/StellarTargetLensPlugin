using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

public sealed class BuffTrackEntry
{
    public int    BuffUuid     { get; }
    public int    BuffBaseId   { get; internal set; }
    public string BuffName     { get; internal set; } = "";
    public int?   Visible      { get; internal set; }
    public int?   BuffType     { get; internal set; }   // 0=Debuff 1=Gain 2=GainRecovery 3=Item
    public bool   IsClientBuff { get; internal set; }
    public int    Layer        { get; internal set; }
    public int    Level        { get; internal set; }
    public int    SkillId      { get; internal set; }
    public string SkillName    { get; internal set; } = "";
    public long   Duration     { get; internal set; }   // ms; 0 = permanent

    internal long  SnapTick;
    internal float SnapRemain;

    public float RemainSec => Duration == 0 ? -1f
        : MathF.Max(0f, SnapRemain - (Environment.TickCount64 - SnapTick) / 1000f);

    internal BuffTrackEntry(int buffUuid) { BuffUuid = buffUuid; }
}

internal static partial class BuffTrackPatch
{
    public static IReadOnlyDictionary<int, BuffTrackEntry> ActiveBuffs => _activeBuffs;

    private static readonly Dictionary<int, BuffTrackEntry> _activeBuffs = new();
    private static Action<string>? _log;

    // ── Demand gate ──
    // These postfixes fire for EVERY entity's buff add/sync (all mobs + players), and each call allocates an
    // Il2Cpp wrapper + boxed longs via reflection just to reach the local-player check. In a combat-dense dungeon
    // that is a steady garbage stream → periodic GC hitch (~500ms). The tracked data is only ever read by the
    // Combat window's buff/debuff rows, which call MarkDemand() on every refresh (~100ms) while visible. When
    // nothing is consuming, we skip the whole postfix. See Knowledge Base\Performance-Levers.md (GC-hitch section).
    private const  long DemandWindowMs = 500;
    private static long _lastDemandTick;
    private static bool Active => _lastDemandTick != 0 && Environment.TickCount64 - _lastDemandTick < DemandWindowMs;
    internal static void MarkDemand() => _lastDemandTick = Environment.TickCount64;

    private static object? _localBuffComp;
    private static object? _localClientBuffComp;

    private static PropertyInfo? _piShowedBuffList;
    private static bool          _showedListResolved;

    private static PropertyInfo? _piBuffItemUuid;
    private static PropertyInfo? _piBuffItemBaseId;
    private static PropertyInfo? _piBuffItemLayer;
    private static PropertyInfo? _piBuffItemLevel;
    private static PropertyInfo? _piBuffItemDuration;
    private static bool          _buffItemFieldsResolved;

    private static PropertyInfo? _piClientBuffList;
    private static bool          _clientListResolved;

    private static PropertyInfo? _piClientBuffUuid;
    private static PropertyInfo? _piClientBuffId;
    private static PropertyInfo? _piClientBuffMaxLife;
    private static PropertyInfo? _piClientBuffLayer;
    private static FieldInfo?    _fiIsInValid;
    private static bool          _clientBuffInfoResolved;

    private static PropertyInfo? _piZComponentHost;
    private static PropertyInfo? _piZEntityUuid;
    private static bool          _hostReflResolved;

    private static object?     _entityMgrInst;
    private static MethodInfo? _miGetPlayerUuid;
    private static bool        _entityMgrResolved;

    // FightSourceInfo — read at OnAddBuff time; gives source skill ID when FightSourceType==0 (Skill)
    private static readonly Dictionary<int, int> _buffSourceSkillId = new(); // buffUuid → sourceSkillId

    // When set (by TargetBuffTracker each frame), the OnAddBuff postfix also captures the fight source for this
    // host uuid — not just the local player — so target-monster buffs get source-skill resolution. 0 = none.
    internal static long SourceCaptureTargetUuid;
    // Reads the captured source skill id for a buff uuid; 0 = unknown.
    internal static int GetSourceSkill(int buffUuid) => _buffSourceSkillId.TryGetValue(buffUuid, out var s) ? s : 0;

    private static PropertyInfo? _piFightSourceInfo;
    private static PropertyInfo? _piFightSourceType;
    private static PropertyInfo? _piFightSourceConfigId;
    private static bool          _fightSourceResolved;

    private static bool _loggedError;
    private static bool _diagLogged;

    internal static bool Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var buffCompType = StellarInterop.FindType("Panda.ZGame.BuffComp");
        if (buffCompType == null)
        {
            log("[Buff] BuffComp not found — patch skipped");
            return false;
        }

        // Both are count-only name matches → StellarInterop.FindMethod; patch each if present, mirroring the old scan.
        bool patchedAdd = false;
        var onAddBuff  = StellarInterop.FindMethod(buffCompType, "OnAddBuff", 2);
        if (onAddBuff != null)
        {
            try
            {
                harmony.Patch(onAddBuff, postfix: new HarmonyMethod(typeof(BuffTrackPatch), nameof(PostfixOnAddBuff)));
                log("[Buff] OnAddBuff patched");
                patchedAdd = true;
            }
            catch (Exception ex) { log($"[Buff] OnAddBuff patch failed: {ex.Message}"); }
        }

        var onBuffSync = StellarInterop.FindMethod(buffCompType, "OnBuffSync", 2);
        if (onBuffSync != null)
        {
            try
            {
                harmony.Patch(onBuffSync, postfix: new HarmonyMethod(typeof(BuffTrackPatch), nameof(PostfixOnAddBuff)));
                log("[Buff] OnBuffSync patched");
            }
            catch (Exception ex) { log($"[Buff] OnBuffSync patch failed: {ex.Message}"); }
        }
        // OnAddBuff is required; if it failed, roll back any OnBuffSync patch already applied on this instance
        // (install-time rollback of a partial install — distinct from teardown, which IHarmonyHost owns).
        if (!patchedAdd) { harmony.UnpatchSelf(); return false; }

        var clientCompType = StellarInterop.FindType("Panda.ZGame.ClientBuffComp");
        if (clientCompType != null)
        {
            var addBuff = StellarInterop.FindMethod(clientCompType, "AddBuff", 4);
            if (addBuff != null)
            {
                try
                {
                    harmony.Patch(addBuff, postfix: new HarmonyMethod(typeof(BuffTrackPatch), nameof(PostfixClientAddBuff)));
                    log("[Buff] ClientBuffComp.AddBuff postfix patched");
                }
                catch (Exception ex) { log($"[Buff] ClientBuffComp.AddBuff patch failed: {ex.Message}"); }
            }
        }
        return true;
    }

    // Harmony teardown is owned by IHarmonyHost (auto-unpatches on plugin dispose); reset only transient state here.
    internal static void Uninstall()
    {
        _activeBuffs.Clear();
        _localBuffComp = _localClientBuffComp = null;
        _piShowedBuffList = null; _showedListResolved = false;
        _piBuffItemUuid = _piBuffItemBaseId = _piBuffItemLayer = _piBuffItemLevel = _piBuffItemDuration = null;
        _buffItemFieldsResolved = false;
        _piClientBuffList = null; _clientListResolved = false;
        _piClientBuffUuid = _piClientBuffId = _piClientBuffMaxLife = _piClientBuffLayer = null;
        _fiIsInValid = null; _clientBuffInfoResolved = false;
        _piZComponentHost = _piZEntityUuid = null; _hostReflResolved = false;
        _entityMgrInst = null; _miGetPlayerUuid = null; _entityMgrResolved = false;
        _cachedPlayerUuid = 0; _playerUuidTick = 0; _lastDemandTick = 0;
        SourceCaptureTargetUuid = 0;
        _buffSourceSkillId.Clear();
        _piFightSourceInfo = _piFightSourceType = _piFightSourceConfigId = null;
        _fightSourceResolved = false;
        _tableReflResolved = false;
        _miGetBuffTable = _miGetBuffRow = null; _buffTableInst = null;
        _piBuffRowName = _piBuffRowVisible = _piBuffRowType = _piBuffRowSkillId = null;
        _skillTableResolved = false;
        _skillTableInst = null; _miGetSkillRow = null; _piSkillRowName = null;
        _loggedError = _diagLogged = false;
    }

    // Reads the live buff lists from captured component instances and rebuilds _activeBuffs.
    // Replaces event-based tracking — removal and replacement are handled automatically.
    internal static void RefreshActiveBuffs()
    {
        if (!_diagLogged) { _diagLogged = true; _log?.Invoke($"[Buff] Refresh: buffComp={_localBuffComp != null} clientComp={_localClientBuffComp != null}"); }
        var live = new HashSet<int>();

        if (_localBuffComp != null)
        {
            if (!_showedListResolved) ResolveShowedList();
            var list = _piShowedBuffList?.GetValue(_localBuffComp);
            if (list != null)
            {
                try
                {
                    foreach (var item in IterList(list))
                    {
                        if (item == null) continue;
                        if (!_buffItemFieldsResolved) ResolveBuffItemFields(item);
                        if (_piBuffItemUuid == null) break;
                        int uuid = (int)(_piBuffItemUuid.GetValue(item) ?? 0);
                        if (uuid == 0) continue;
                        live.Add(uuid);
                        int baseId  = (int)(_piBuffItemBaseId?.GetValue(item)   ?? 0);
                        int layer   = (int)(_piBuffItemLayer?.GetValue(item)    ?? 1);
                        int level   = (int)(_piBuffItemLevel?.GetValue(item)    ?? 0);
                        long durMs  = (long)(_piBuffItemDuration?.GetValue(item) ?? 0L);
                        UpsertServerBuff(uuid, baseId, layer, level, durMs);
                    }
                }
                catch (Exception ex) { LogError("server list", ex); }
            }
        }

        if (_localClientBuffComp != null)
        {
            if (!_clientListResolved) ResolveClientList();
            var list = _piClientBuffList?.GetValue(_localClientBuffComp);
            if (list != null)
            {
                try
                {
                    foreach (var item in IterList(list))
                    {
                        if (item == null) continue;
                        if (!_clientBuffInfoResolved) ResolveClientBuffInfoRefl(item);
                        if (_piClientBuffUuid == null) break;
                        if (_fiIsInValid != null && (bool)(_fiIsInValid.GetValue(item) ?? false)) continue;
                        int uuid   = (int)(_piClientBuffUuid.GetValue(item)    ?? 0);
                        if (uuid == 0) continue;
                        live.Add(uuid);
                        int buffId = (int)(_piClientBuffId?.GetValue(item)     ?? 0);
                        int layer  = (int)(_piClientBuffLayer?.GetValue(item)  ?? 1);
                        float maxL = (float)(_piClientBuffMaxLife?.GetValue(item) ?? 0f);
                        UpsertClientBuff(uuid, buffId, layer, maxL);
                    }
                }
                catch (Exception ex) { LogError("client list", ex); }
            }
        }

        var stale = new List<int>();
        foreach (var kv in _activeBuffs)
            if (!live.Contains(kv.Key)) stale.Add(kv.Key);
        // NOTE: _buffSourceSkillId is NOT purged here. It is now shared with the target-monster path (entries keyed
        // by buffUuids that are not in the local _activeBuffs), so the local stale sweep must not evict them. The
        // cache is bounded by a size cap in CacheBuffFightSource instead.
        foreach (var k in stale) _activeBuffs.Remove(k);
    }

    private static void UpsertServerBuff(int uuid, int baseId, int layer, int level, long durMs)
    {
        if (!_activeBuffs.TryGetValue(uuid, out var entry))
        {
            entry = new BuffTrackEntry(uuid);
            _activeBuffs[uuid] = entry;
            GetBuffInfo(baseId, out var nm, out var vis, out var bt, out var sid);
            if (sid == 0) _buffSourceSkillId.TryGetValue(uuid, out sid);
            entry.BuffBaseId = baseId; entry.BuffName = nm;
            entry.Visible = vis; entry.BuffType = bt;
            entry.SkillId = sid; entry.SkillName = GetSkillName(sid);
        }
        entry.Layer = layer; entry.Level = level;
        if (entry.Duration != durMs)
        {
            entry.Duration   = durMs;
            entry.SnapTick   = Environment.TickCount64;
            entry.SnapRemain = durMs > 0 ? durMs / 1000f : -1f;
        }
    }

    private static void UpsertClientBuff(int uuid, int buffId, int layer, float maxLife)
    {
        if (!_activeBuffs.TryGetValue(uuid, out var entry))
        {
            entry = new BuffTrackEntry(uuid) { IsClientBuff = true };
            _activeBuffs[uuid] = entry;
            GetBuffInfo(buffId, out var nm, out var vis, out var bt, out var sid);
            entry.BuffBaseId = buffId; entry.BuffName = nm;
            entry.Visible = vis; entry.BuffType = bt;
            entry.SkillId = sid; entry.SkillName = GetSkillName(sid);
            entry.Duration   = maxLife > 0f ? (long)(maxLife * 1000f) : 0L;
            entry.SnapTick   = Environment.TickCount64;
            entry.SnapRemain = maxLife > 0f ? maxLife : -1f;
        }
        entry.Layer = layer;
    }

    private static void PostfixOnAddBuff(object __instance, object __0, bool __1)
    {
        if (!Active) return;
        try
        {
            long host = GetHostEntityUuid(__instance);
            bool isLocal = IsLocalPlayer(host);
            if (isLocal) _localBuffComp = __instance;
            // Capture the fight source for the local player AND the current target host, so the target-monster
            // buff display can resolve source skills too. _localBuffComp capture stays local-only.
            if (isLocal || (SourceCaptureTargetUuid != 0 && host == SourceCaptureTargetUuid))
                CacheBuffFightSource(__0);
        }
        catch (Exception ex) { LogError("PostfixOnAddBuff", ex); }
    }

    private static PropertyInfo? _piBuffInfoUuid;

    private static void CacheBuffFightSource(object buffInfo)
    {
        try
        {
            // Bound the cache — it is shared with the target path and no longer purged by the local stale sweep.
            // Entries re-add on the next OnAddBuff/OnBuffSync, so a clear is cheap and 512 is far above live count.
            if (_buffSourceSkillId.Count > 512) _buffSourceSkillId.Clear();

            if (!_fightSourceResolved)
            {
                _fightSourceResolved = true;
                var t = buffInfo.GetType();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name == "BuffUuid")        _piBuffInfoUuid      = p;
                    if (p.Name == "FightSourceInfo") _piFightSourceInfo   = p;
                }
                _log?.Invoke($"[Buff] FightSource refl: uuid={_piBuffInfoUuid != null} fsi={_piFightSourceInfo != null}");
            }

            if (_piBuffInfoUuid == null || _piFightSourceInfo == null) return;
            int uuid = (int)(_piBuffInfoUuid.GetValue(buffInfo) ?? 0);
            if (uuid == 0) return;

            var fsi = _piFightSourceInfo.GetValue(buffInfo);
            if (fsi == null) return;

            if (_piFightSourceType == null)
            {
                foreach (var p in fsi.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name == "FightSourceType") _piFightSourceType     = p;
                    if (p.Name == "SourceConfigId")  _piFightSourceConfigId = p;
                }
            }

            int sourceType     = (int)(_piFightSourceType?.GetValue(fsi)     ?? -1);
            int sourceConfigId = (int)(_piFightSourceConfigId?.GetValue(fsi) ?? 0);
            if (sourceType == 0 && sourceConfigId > 0) // EFightSourceSkill = 0
                _buffSourceSkillId[uuid] = sourceConfigId;
        }
        catch { }
    }

    // Captures local player's ClientBuffComp instance.
    private static void PostfixClientAddBuff(object __instance, object __0, int __1, int __2, float __3, object __result)
    {
        if (!Active) return;
        try
        {
            if (!IsLocalPlayer(GetHostEntityUuid(__instance))) return;
            _localClientBuffComp = __instance;
        }
        catch (Exception ex) { LogError("PostfixClientAddBuff", ex); }
    }

    private static void LogError(string src, Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _log?.Invoke($"[Buff] {src}: {ex.InnerException?.Message ?? ex.Message}");
    }

    private static IEnumerable<object?> IterList(object list)
    {
        var t   = list.GetType();
        var cnt = (int)(t.GetProperty("Count")?.GetValue(list) ?? 0);
        var idx = t.GetProperty("Item");
        for (int i = 0; i < cnt; i++)
            yield return idx?.GetValue(list, new object[] { i });
    }

    // The player uuid is stable within a session (a char switch / relog reloads the world), so cache it and
    // re-resolve at most ~every 2s. Avoids a reflection Invoke + boxed-long alloc on every buff event while the
    // window is open (the postfix runs for all entities, so this is called once per remote buff sync too).
    private static long _cachedPlayerUuid;
    private static long _playerUuidTick;

    private static long GetPlayerUuid()
    {
        long now = Environment.TickCount64;
        if (_cachedPlayerUuid != 0 && now - _playerUuidTick < 2000) return _cachedPlayerUuid;
        if (!_entityMgrResolved) ResolveEntityMgr();
        if (_entityMgrInst == null || _miGetPlayerUuid == null) return 0L;
        _cachedPlayerUuid = (long)_miGetPlayerUuid.Invoke(_entityMgrInst, null)!;
        _playerUuidTick   = now;
        return _cachedPlayerUuid;
    }

    private static bool IsLocalPlayer(long hostUuid)
    {
        var puuid = GetPlayerUuid();
        return puuid != 0L && hostUuid == puuid;
    }

    private static void ResolveEntityMgr()
    {
        var t = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
        if (t == null) { _entityMgrResolved = true; return; }
        _entityMgrInst   = StellarInterop.GetSingleton(t);
        _miGetPlayerUuid = t.GetMethod("get_PlayerUuid", BindingFlags.Public | BindingFlags.Instance);
        if (_entityMgrInst != null) _entityMgrResolved = true;
        _log?.Invoke($"[Buff] EntityMgr: inst={_entityMgrInst != null} puuid={_miGetPlayerUuid != null}");
    }

    private static long GetHostEntityUuid(object comp)
    {
        if (!_hostReflResolved) ResolveHostRefl(comp);
        if (_piZComponentHost == null || _piZEntityUuid == null) return 0L;
        var host = _piZComponentHost.GetValue(comp);
        return host == null ? 0L : (long)(_piZEntityUuid.GetValue(host) ?? 0L);
    }

    private static void ResolveHostRefl(object comp)
    {
        _hostReflResolved = true;
        var cur = comp.GetType();
        while (cur != null && _piZComponentHost == null)
        {
            _piZComponentHost = cur.GetProperty("Host", BindingFlags.NonPublic | BindingFlags.Instance) ?? cur.GetProperty("Host", BindingFlags.Public | BindingFlags.Instance);
            cur = cur.BaseType;
        }
        if (_piZComponentHost != null)
            _piZEntityUuid = _piZComponentHost.PropertyType
                .GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
        _log?.Invoke($"[Buff] HostRefl: host={_piZComponentHost != null} uuid={_piZEntityUuid != null}");
    }

    private static void ResolveShowedList()
    {
        _showedListResolved = true;
        PropertyInfo? best = null; int bestPrio = 99;
        foreach (var p in _localBuffComp!.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            int prio = p.Name switch { "pShowedBuffList_" => 0, "showedBuffList_" => 1, "pBuffList_" => 2, "buffList_" => 3, _ => 99 };
            if (prio < bestPrio) { best = p; bestPrio = prio; if (bestPrio == 0) break; }
        }
        _piShowedBuffList = best;
        _log?.Invoke($"[Buff] showedBuffList_: {_piShowedBuffList?.Name ?? "not found"}");
    }

    private static void ResolveBuffItemFields(object item)
    {
        _buffItemFieldsResolved = true;
        foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.Name == "BuffUuid")   _piBuffItemUuid     = p;
            else if (p.Name == "BuffBaseId") _piBuffItemBaseId = p;
            else if (p.Name == "Layer")  _piBuffItemLayer   = p;
            else if (p.Name == "Level")  _piBuffItemLevel   = p;
            else if (p.Name == "Duration") _piBuffItemDuration = p;
        }
        _log?.Invoke($"[Buff] BuffItem props: uuid={_piBuffItemUuid != null} dur={_piBuffItemDuration != null}");
    }

    private static void ResolveClientList()
    {
        _clientListResolved = true;
        _piClientBuffList = _localClientBuffComp!.GetType()
            .GetProperty("BuffList", BindingFlags.Public | BindingFlags.Instance);
        _log?.Invoke($"[Buff] ClientBuffComp.BuffList: {_piClientBuffList != null}");
    }

    private static void ResolveClientBuffInfoRefl(object item)
    {
        _clientBuffInfoResolved = true;
        var t = item.GetType();
        _piClientBuffUuid    = t.GetProperty("BuffUuid",     BindingFlags.Public | BindingFlags.Instance);
        _piClientBuffId      = t.GetProperty("BuffId",       BindingFlags.Public | BindingFlags.Instance);
        _piClientBuffMaxLife = t.GetProperty("BuffMaxLife",  BindingFlags.Public | BindingFlags.Instance);
        _piClientBuffLayer   = t.GetProperty("CurrentLayer", BindingFlags.Public | BindingFlags.Instance);
        _fiIsInValid         = t.GetField("IsInValid",       BindingFlags.Public | BindingFlags.Instance);
        _log?.Invoke($"[Buff] ClientBuffInfo: uuid={_piClientBuffUuid != null} maxLife={_piClientBuffMaxLife != null}");
    }

}
