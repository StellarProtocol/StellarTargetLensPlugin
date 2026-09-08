using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// Reads the local player's current auto/lock target and that target's live HP + name.
///
/// The target uuid lives on the local player entity as the broadcast attribute <c>EAttrType.AttrTargetId (30)</c>,
/// read via the same <c>ZEntity.GetAttr&lt;long&gt;(EAttrType, bool)</c> reflection used elsewhere here. If it doesn't
/// surface, we fall back to <c>AttrTargetUuid (450)</c> then <c>ZWorld.Instance.LockTargetUuid</c> (the hard-lock).
/// Result is frame-gated: the window getters share a single poll per rendered frame.
/// </summary>
internal sealed partial class TargetInfoTracker
{
    public readonly struct Snapshot
    {
        public readonly bool   HasTarget;
        public readonly long   Uuid;
        public readonly string Name;
        public readonly long   Hp;
        public readonly long   MaxHp;
        public readonly long   Stunned;     // AttrStunned (443) — current break/stagger gauge; 0 when absent
        public readonly long   MaxStunned;  // AttrMaxStunned (442) — break gauge max; >0 signals the target HAS a bar
        public readonly long   Level;       // AttrLevel (fallback AttrMonsterSeasonLevel); 0 = unknown
        public readonly string Rank;        // "Boss" / "Elite" / "Normal" (from ZEntity.IsBoss / IsElite)
        public readonly long   ConfigId;    // AttrId (10) — MonsterTable config id; 0 = unknown
        public readonly bool   HasDistance; // false when either model/position isn't loaded
        public readonly float  Distance;    // metres, target root vs local-player root
        public Snapshot(bool has, long uuid, string name, long hp, long maxHp, long stunned, long maxStunned,
                        long level, string rank, long configId, bool hasDistance, float distance)
        {
            HasTarget = has; Uuid = uuid; Name = name; Hp = hp; MaxHp = maxHp;
            Stunned = stunned; MaxStunned = maxStunned;
            Level = level; Rank = rank; ConfigId = configId; HasDistance = hasDistance; Distance = distance;
        }
    }

    private const int AttrTargetId          = 30;    // EAttrType.AttrTargetId — targeted entity uuid (auto/soft target)
    private const int AttrTargetUuid        = 450;   // EAttrType.AttrTargetUuid — alternate uuid slot (fallback)
    private const int AttrHpId              = 11310; // EAttrType.AttrHp
    private const int AttrMaxHpId           = 11320; // EAttrType.AttrMaxHp
    private const int AttrStunnedId         = 443;   // EAttrType.AttrStunned — current break/stagger gauge
    private const int AttrMaxStunnedId      = 442;   // EAttrType.AttrMaxStunned — break gauge max (>0 = target has one)
    private const int AttrLevel             = 10000; // EAttrType.AttrLevel — character level (monster support UNVERIFIED → fallback)
    private const int AttrMonsterSeasonLevel = 462;  // EAttrType.AttrMonsterSeasonLevel — level fallback for monsters

    private readonly IPluginServices _services;
    public TargetInfoTracker(IPluginServices services) => _services = services;

    // Resolved target entity + uuid for THIS frame's poll — read by TargetBuffTracker so it can pull BuffComp off the
    // same entity without re-resolving. Cleared to null/0 whenever there is no valid target.
    internal object? LastTargetEntity { get; private set; }
    internal long    LastTargetUuid   { get; private set; }

    // Frame-gated cache: multiple window getters resolve to one poll per frame.
    private int      _cacheFrame = -1;
    private Snapshot _cache;

    public Snapshot Current
    {
        get
        {
            int f = Time.frameCount;
            if (f == _cacheFrame) return _cache;
            _cacheFrame = f;
            _cache = Poll();
            return _cache;
        }
    }

    private Snapshot Poll()
    {
        try
        {
            LastTargetEntity = null; LastTargetUuid = 0;
            if (!_services.ClientState.IsLoggedIn) return default;
            if (!EnsureApi()) return default;

            long localUuid = _services.CombatSnapshot.LocalEntityId.Value;
            if (localUuid == 0) return default;

            long targetUuid = ReadTargetUuid(localUuid);
            if (targetUuid == 0 || targetUuid == localUuid) return default;

            var targetEnt = GetEntityObj(targetUuid);
            LastTargetEntity = targetEnt; LastTargetUuid = targetUuid;

            // uuid>>16 is the runtime entId (NOT a MonsterTable key) — a last-resort Mob ID to DISPLAY only.
            // Config id + bound name come off the entity's config row (entRow_) — the reliable source; AttrId(10)
            // reads 0 and uuid>>16 is only the runtime entId, so both are demoted to fallbacks.
            long uuidMobId = targetUuid >> 16;
            long   baseId = 0, cfgId2 = 0;
            string cfgName = "";
            if (targetEnt != null)
            {
                try { baseId = _piBaseId?.GetValue(targetEnt) is int bi ? bi : 0; } catch { baseId = 0; }
                var ec = ReadEntConfig(targetEnt);
                cfgId2 = ec.id; cfgName = ec.name;
            }

            // Config id (Mob ID): bound-row Id → public BaseId → uuid-derived entId (last-ditch display only).
            long configId = cfgId2 > 0 ? cfgId2 : (baseId > 0 ? baseId : uuidMobId);

            // Name: CombatLookup (players) → bound row's _base.Name → config-table lookup by configId → by uuidMobId.
            string clName = ResolveName(targetUuid);
            string name = clName;
            if (string.IsNullOrEmpty(name)) name = cfgName;
            if (string.IsNullOrEmpty(name)) name = ResolveMonsterName(configId);
            if (string.IsNullOrEmpty(name) && uuidMobId != configId) name = ResolveMonsterName(uuidMobId);

            // Entity-dependent reads (HP / level / rank / distance). Stay at defaults when the entity never resolved.
            long   hp = 0, maxHp = 0, stunned = 0, maxStunned = 0, level = 0;
            string rank = "Normal";
            bool   hasDist = false;
            float  dist = 0f;
            if (targetEnt != null)
            {
                hp    = ReadAttr(targetEnt, _attrHpBox);
                maxHp = ReadAttr(targetEnt, _attrMaxHpBox);

                // Break/stagger gauge — read the same way as HP; MaxStunned > 0 means the target has one.
                stunned    = ReadAttrInt(targetEnt, _attrStunnedBox);
                maxStunned = ReadAttrInt(targetEnt, _attrMaxStunnedBox);

                // Level: AttrLevel primarily; monster support is UNVERIFIED at runtime, so fall back to
                // AttrMonsterSeasonLevel when AttrLevel yields nothing usable. Each read is isolated.
                try
                {
                    level = ReadAttr(targetEnt, _attrLevelBox);
                    if (level <= 0) level = ReadAttr(targetEnt, _attrMonSeasonLvlBox);
                }
                catch { level = 0; }

                // Rank: read ZEntity.IsBoss then IsElite bools directly (no table lookup).
                try
                {
                    if (ReadBool(targetEnt, _piIsBoss)) rank = "Boss";
                    else if (ReadBool(targetEnt, _piIsElite)) rank = "Elite";
                }
                catch { rank = "Normal"; }

                // Distance: target root Position vs local-player root Position (height ignored — root only).
                try
                {
                    var localEnt = GetEntityObj(localUuid);
                    if (localEnt != null
                        && TryGetPosition(targetEnt, out var tPos)
                        && TryGetPosition(localEnt, out var lPos))
                    {
                        dist    = Vector3.Distance(tPos, lPos);
                        hasDist = true;
                    }
                }
                catch { hasDist = false; }
            }

            return new Snapshot(true, targetUuid, name, hp, maxHp, stunned, maxStunned, level, rank, configId, hasDist, dist);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Target] poll error: {ex.InnerException?.Message ?? ex.Message}");
            return default;
        }
    }

    // Target uuid priority: ECS PlayerEnt.GetAttrTargetId() → legacy attr bag (AttrTargetId 30 → 450) → ZWorld lock.
    private long ReadTargetUuid(long localUuid)
    {
        long ecs    = ReadEcsTargetId();
        var  localEnt = GetEntityObj(localUuid);
        long attr30 = localEnt != null ? ReadAttr(localEnt, _attrTargetIdBox)   : 0;
        long attr450= localEnt != null ? ReadAttr(localEnt, _attrTargetUuidBox) : 0;
        long lk     = ReadLockTargetUuid();

        if (ecs    != 0 && ecs    != localUuid) return ecs;
        if (attr30 != 0 && attr30 != localUuid) return attr30;
        if (attr450!= 0 && attr450!= localUuid) return attr450;
        if (lk     != 0 && lk     != localUuid) return lk;
        return 0;
    }

    private string ResolveName(long uuid)
    {
        try
        {
            var n = _services.CombatLookup.GetEntityName(new EntityId(uuid));
            return string.IsNullOrEmpty(n) ? "" : n;
        }
        catch { return ""; }
    }

    // Monster display name via MonsterTableBase.GetTable(false).TryGetValue(configId, out row, false) → row.Name.
    // Guarded — any miss/null degrades to "" (caller keeps the CombatLookup result).
    private string ResolveMonsterName(long configId)
    {
        if (configId <= 0 || _monTblType == null || _miMonGetTable == null || _piMonName == null) return "";
        bool rowOk = false;
        string name = "";
        try
        {
            // Table instance is a singleton — resolve once, then lazily resolve TryGetValue off its runtime type.
            _monTblObj ??= _miMonGetTable.Invoke(null, new object[] { false });
            var tableObj = _monTblObj;
            if (tableObj != null)
            {
                if (!_monTryGetResolved)
                {
                    foreach (var m in tableObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (m.Name == "TryGetValue" && m.GetParameters().Length == 3) { _miMonTryGetValue = m; break; }
                    _monTryGetResolved = true;
                }
                if (_miMonTryGetValue != null)
                {
                    // errorWhenNotFound = false — a miss must NOT log a spurious game error.
                    var args = new object?[] { (int)configId, null, false };
                    rowOk = _miMonTryGetValue.Invoke(tableObj, args) is bool b && b;
                    var row = rowOk ? args[1] : null;
                    if (row != null && _piMonName.GetValue(row) is string s) name = s ?? "";
                }
            }
        }
        catch { name = ""; }

        return name;
    }

    // Real config id + display name off the entity's bound row (entRow_). Id/_base handles are lazy by entRow_'s
    // runtime type, re-resolved when it differs. Guarded → (0,"") on miss.
    private (long id, string name) ReadEntConfig(object ent)
    {
        if (_fiEntRow == null) return (0, "");
        try
        {
            var cfg = _fiEntRow.GetValue(ent);
            if (cfg == null) return (0, "");
            var t = cfg.GetType();
            if (t != _entRowType)
            {
                _entRowType   = t;
                _piEntRowId   = t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                _fiEntRowBase = t.GetField("_base", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            long id = _piEntRowId?.GetValue(cfg) is int i ? i : 0;
            var baseRow = _fiEntRowBase?.GetValue(cfg);
            string nm = (baseRow != null && _piMonName?.GetValue(baseRow) is string s) ? (s ?? "") : "";
            return (id, nm);
        }
        catch { return (0, ""); }
    }

    // ── Reflection resolve (ZEntityMgr.GetEntity, ZEntity.GetAttr<long>, ZWorld.LockTargetUuid) ──
    private bool          _apiResolved;
    private bool          _apiOk;
    private Type?         _entMgrType;         // ZEntityMgr (singleton read live via StellarInterop.GetSingleton)
    private bool          _hasEntMgrInstance;  // ZEntityMgr exposes a static Instance accessor
    private PropertyInfo? _piEntMgrStaticInstance; // ZEntityMgr.Instance (static) — the accessor ClassIconOverlay uses; preferred over GetSingleton
    private MethodInfo?   _miGetEntity;
    private MethodInfo?   _miGetAttrLong;   // ZEntity.GetAttr<long>(EAttrType, bool)
    private MethodInfo?   _miGetAttrInt;    // ZEntity.GetAttr<int>(EAttrType, bool) — 32-bit attrs (break/stagger gauge)
    private object?       _attrTargetIdBox;
    private object?       _attrTargetUuidBox;
    private object?       _attrHpBox;
    private object?       _attrMaxHpBox;
    private object?       _attrStunnedBox;
    private object?       _attrMaxStunnedBox;
    private object?       _attrLevelBox;
    private object?       _attrMonSeasonLvlBox;
    private PropertyInfo? _piIsBoss;           // ZEntity.IsBoss  (public instance bool)
    private PropertyInfo? _piIsElite;          // ZEntity.IsElite (public instance bool)
    private PropertyInfo? _piModel;            // ZEntity.Model (Panda.ZGame.ZModel)
    private PropertyInfo? _piModelGoComp;      // ZModel.ModelGoComp
    private PropertyInfo? _piGoPosition;       // GoComp.Position (Vector3) — resolved lazily off the live GoComp type
    private bool          _goPosResolved;      // one-shot flag mirroring ClassIconOverlay's lazy Position resolve
    // Bound config row off the resolved ZEntity — reliable id/name. entRow_ is MonsterConfig: .Id = real MonsterTable
    // id, ._base.Name = display (AttrId(10)=0 & uuid>>16 aren't table keys).
    private FieldInfo?    _fiEntRow;              // ZEntity.entRow_ (IEntityConfig) — MonsterConfig at runtime
    private PropertyInfo? _piBaseId;              // ZEntity.BaseId (public int) — simpler id source
    private PropertyInfo? _piConfigUuid;          // ZEntity.ConfigUuid (public long)
    private Type?         _entRowType;            // last-seen runtime type of entRow_ (lazy re-resolve on change)
    private PropertyInfo? _piEntRowId;            // MonsterConfig.Id (int)
    private FieldInfo?    _fiEntRowBase;          // MonsterConfig._base (MonsterTableBase)

    // Monster display name from the config table (CombatLookup only carries PLAYER names). All OPTIONAL: any miss
    // degrades name resolution to the CombatLookup result, never throws.
    private Type?         _monTblType;            // Bokura.MonsterTableBase
    private MethodInfo?   _miMonGetTable;         // static MonsterTableBase.GetTable(bool) → ZTable<int,MonsterTableBase>
    private PropertyInfo? _piMonName;             // MonsterTableBase.Name (public instance string)
    private object?       _monTblObj;             // cached table instance (singleton — resolved once)
    private MethodInfo?   _miMonTryGetValue;      // ZTable<int,MonsterTableBase>.TryGetValue(int, out row, bool)
    private bool          _monTryGetResolved;

    private bool EnsureApi()
    {
        if (_apiResolved) return _apiOk;
        _apiResolved = true;
        try
        {
            var entMgr   = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            var entType  = StellarInterop.FindType("Panda.ZGame.ZEntity");
            var attrEnum = StellarInterop.FindType("Zproto.EAttrType");
            if (entMgr == null || entType == null || attrEnum == null)
            {
                _services.Log.Warning($"[Target] type resolve failed entMgr={entMgr != null} ent={entType != null} attrEnum={attrEnum != null}");
                return false;
            }

            _entMgrType        = entMgr;
            _hasEntMgrInstance = StellarInterop.FindPropertyUp(entMgr, "Instance") != null;
            // Cache the STATIC Instance accessor (what ClassIconOverlay resolves entities through, and what works
            // in-game). GetEntityObj prefers this over StellarInterop.GetSingleton — the singleton path returns a
            // manager whose GetEntity(uuid) misses ECS monster entities on this build.
            _piEntMgrStaticInstance = entMgr.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            foreach (var m in entMgr.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                { _miGetEntity = m; break; }

            foreach (var m in entType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "GetAttr" || !m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType == attrEnum && ps[1].ParameterType == typeof(bool))
                { _miGetAttrLong = m.MakeGenericMethod(typeof(long));
                  _miGetAttrInt  = m.MakeGenericMethod(typeof(int));
                  break; }
            }

            _attrTargetIdBox     = Enum.ToObject(attrEnum, AttrTargetId);
            _attrTargetUuidBox   = Enum.ToObject(attrEnum, AttrTargetUuid);
            _attrHpBox           = Enum.ToObject(attrEnum, AttrHpId);
            _attrMaxHpBox        = Enum.ToObject(attrEnum, AttrMaxHpId);
            _attrStunnedBox      = Enum.ToObject(attrEnum, AttrStunnedId);
            _attrMaxStunnedBox   = Enum.ToObject(attrEnum, AttrMaxStunnedId);
            _attrLevelBox        = Enum.ToObject(attrEnum, AttrLevel);
            _attrMonSeasonLvlBox = Enum.ToObject(attrEnum, AttrMonsterSeasonLevel);

            // Rank flags — OPTIONAL: missing props just degrade Rank to "Normal", never break name/HP.
            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
            _piIsBoss  = entType.GetProperty("IsBoss",  pubInst);
            _piIsElite = entType.GetProperty("IsElite", pubInst);

            // Bound config row — OPTIONAL: entRow_ (private field) + BaseId/ConfigUuid (public) off ZEntity.
            _fiEntRow      = entType.GetField("entRow_", BindingFlags.NonPublic | BindingFlags.Instance);
            _piBaseId      = entType.GetProperty("BaseId", pubInst);
            _piConfigUuid  = entType.GetProperty("ConfigUuid", pubInst);

            // Model → GoComp chain for distance (mirrors ClassIconOverlay.Resolve). GoComp.Position resolves lazily
            // off the live GoComp type, so only ZEntity.Model + ZModel.ModelGoComp are resolved up front here.
            _piModel = entType.GetProperty("Model", pubInst);
            var zmodel = StellarInterop.FindType("Panda.ZGame.ZModel");
            if (zmodel != null) _piModelGoComp = zmodel.GetProperty("ModelGoComp", pubInst);

            // Primary source — the ECS PlayerEnt + its target-id extension (what the game HUD reads).
            _piPlayerEnt = entMgr.GetProperty("PlayerEnt", BindingFlags.Public | BindingFlags.Instance);
            var ext = StellarInterop.FindType("Panda.ZGame.PureEntityAttrPlayerTargetExtensions");
            if (ext != null)
                foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetAttrTargetId" && m.GetParameters().Length == 1) { _miGetAttrTargetIdExt = m; break; }

            // Optional fallback source — hard-lock uuid off the ZWorld singleton.
            var zworld = StellarInterop.FindType("Panda.ZGame.ZWorld");
            if (zworld != null)
            {
                _zworldType = zworld;
                _piLockTargetUuid = zworld.GetProperty("LockTargetUuid", BindingFlags.Public | BindingFlags.Instance);
            }

            // Monster display name — OPTIONAL: MonsterTableBase.GetTable(bool) → row.Name keyed by AttrId(10).
            // If the type/method/prop don't resolve, name falls back to the CombatLookup (player) result.
            var monTbl = StellarInterop.FindType("Bokura.MonsterTableBase");
            if (monTbl != null)
            {
                _monTblType = monTbl;
                foreach (var m in monTbl.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetTable" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(bool))
                    { _miMonGetTable = m; break; }
                _piMonName = monTbl.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            }

            _apiOk = _hasEntMgrInstance && _miGetEntity != null && _miGetAttrLong != null;
            _services.Log.Info($"[Target] api ok={_apiOk} getEnt={_miGetEntity != null} getAttr={_miGetAttrLong != null} " +
                $"entInst={_piEntMgrStaticInstance != null} " +
                $"playerEnt={_piPlayerEnt != null} ext={_miGetAttrTargetIdExt != null} zworld={_piLockTargetUuid != null} " +
                $"isBoss={_piIsBoss != null} isElite={_piIsElite != null} model={_piModel != null} goComp={_piModelGoComp != null} " +
                $"monTbl={_monTblType != null} monGetTable={_miMonGetTable != null} monName={_piMonName != null} " +
                $"entRow={_fiEntRow != null} baseId={_piBaseId != null} cfgUuid={_piConfigUuid != null} getAttrInt={_miGetAttrInt != null}");
            return _apiOk;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Target] api resolve error: {ex.Message}");
            return false;
        }
    }

    private object? GetEntityObj(long uuid)
    {
        try
        {
            // Prefer the static Instance accessor (ClassIconOverlay's path — resolves ECS monster entities in-game);
            // fall back to the singleton scan only if the property handle didn't resolve.
            var mgr = _piEntMgrStaticInstance?.GetValue(null) ?? StellarInterop.GetSingleton(_entMgrType);
            return mgr == null ? null : _miGetEntity!.Invoke(mgr, new object[] { uuid });
        }
        catch { return null; }
    }

    private long ReadAttr(object ent, object? attrBox)
    {
        if (attrBox == null || _miGetAttrLong == null) return 0;
        try { return Convert.ToInt64(_miGetAttrLong.Invoke(ent, new object[] { attrBox, true })); }
        catch { return 0; }
    }

    // Same as ReadAttr but closes GetAttr over int — for 32-bit attrs (break/stagger gauge). ZEntity's attr
    // collection is type-tagged, so a <long> request on an int32 attr returns 0; these must use the int method.
    private long ReadAttrInt(object ent, object? attrBox)
    {
        if (attrBox == null || _miGetAttrInt == null) return 0;
        try { return Convert.ToInt64(_miGetAttrInt.Invoke(ent, new object[] { attrBox, true })); }
        catch { return 0; }
    }

    private static bool ReadBool(object ent, PropertyInfo? pi)
    {
        if (pi == null) return false;
        try { return pi.GetValue(ent) is bool b && b; }
        catch { return false; }
    }

    // Entity root world position via ZEntity.Model → ZModel.ModelGoComp → GoComp.Position (Vector3).
    // Mirrors ClassIconOverlay.TryGetHeadWorld's proven chain; Position is resolved lazily off the live
    // GoComp type (its concrete type isn't named up front). Returns false when the model isn't loaded.
    private bool TryGetPosition(object ent, out Vector3 pos)
    {
        pos = default;
        if (_piModel == null || _piModelGoComp == null) return false;
        try
        {
            var model = _piModel.GetValue(ent);
            if (model == null) return false;
            var go = _piModelGoComp.GetValue(model);
            if (go == null) return false;
            if (!_goPosResolved)
            {
                _piGoPosition  = go.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                _goPosResolved = true;
            }
            if (_piGoPosition?.GetValue(go) is Vector3 p) { pos = p; return true; }
            return false;
        }
        catch { return false; }
    }

}
