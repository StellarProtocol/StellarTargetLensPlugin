using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Harmony capture of the game's general cast/channel ("吟唱/读条") progress, feeding <see cref="TargetInfoTracker"/>'s
/// cast-bar read. This REPLACES both the old boss HP-frame widget scrape (<c>Panda.ZUi.ZUIBossBlood</c>, bosses only)
/// AND the earlier <c>ZStateSkillComp.beginSingGuide()</c> begin/end hook — that method is AOT-inlined in this build, so
/// its standalone body never runs and the patch captured nothing (<c>[CastDiag] activeCasters=0 patched=True</c>).
///
/// <para>PRIMARY PRODUCER: <c>Panda.ZGame.EntityExtensions.SetSingGuide(ZEntity entity, float value, float maxValue,
/// bool forward, ESingGuideType type)</c> — a static extension the game calls EVERY FRAME during a cast to drive the
/// bar (verified non-inlined + patchable: <c>dump.cs:206212</c>, <c>script.json:236201</c>, RVA 0x5180E20, all params
/// by value/pointer → no in/ref-struct → no trampoline-NullRef risk). The postfix reads the bar payload straight off
/// the args: caster uuid = <c>entity.Uuid</c> (the first arg IS the ZEntity), plus <c>value</c>/<c>maxValue</c>/
/// <c>forward</c>/<c>type</c>. It upserts a latch keyed by caster uuid; because there is NO explicit end call, an entry
/// is EXPIRED when it goes un-updated for ~0.3s (see <see cref="StaleMs"/>) — an interrupt therefore clears the bar
/// within a few frames.</para>
///
/// <para>ENRICHMENT (best-effort): a postfix on <c>ZStateSkillComp.beginSingGuide()</c> is KEPT purely to fill in
/// <c>SkillId</c>/<c>Name</c> for the caster's latch entry (read off <c>curSkillId_</c>/<c>skillRow_.Name</c>). If it
/// is inlined it simply never fires and the bar shows a generic "Casting…" — the bar itself never depends on it. When
/// it does fire we DON'T clobber the count-up values, only the label fields.</para>
///
/// <para>Threading: postfixes run on the game/main thread, and the tracker reads the latch on that same thread during
/// window render (one poll per frame) — mirroring <see cref="BuffTrackPatch"/>/<see cref="DbmPatch"/>, so a plain
/// dictionary swap is safe, no lock. A version counter lets the tracker tell a fresh event from a repeat read. The
/// postfixes MUST NEVER THROW (a throw in a game-thread postfix is dangerous) → everything is guarded.</para>
/// </summary>
internal static class CastPatch
{
    /// <summary>One caster's in-flight cast, latched/updated on every <c>SetSingGuide</c> tick.</summary>
    public struct CastEntry
    {
        public long   Uuid;
        public float  Value;       // raw bar value from SetSingGuide (meaning depends on Forward)
        public float  MaxValue;    // raw bar max from SetSingGuide (may be a value or seconds — used as denominator only)
        public bool   Forward;     // true → count UP (value/max); false → count DOWN (1 − value/max)
        public bool   Danger;      // ESingGuideType.MonsterDanger (type==2) → red accent
        public long   LastTickMs;  // Environment.TickCount64 at the last update — drives the staleness expire
        public int    SkillId;     // enrichment (beginSingGuide) — drives the overlay icon; 0 when unknown
        public string Name;        // enrichment (beginSingGuide) — skill name; "" when unknown ("Casting…" shown)
    }

    /// <summary>An entry un-updated for longer than this is treated as ended (SetSingGuide stops ticking on end/interrupt).</summary>
    internal const long StaleMs = 300;

    // Latch keyed by caster uuid. Written by the postfixes, read by the tracker — all on the main thread, so no lock
    // (matches BuffTrackPatch._activeBuffs / DbmPatch._ids).
    private static readonly Dictionary<long, CastEntry> _casts = new();
    private static int _version;   // bumped on every update so the tracker can gate a fresh event

    /// <summary>True once the SetSingGuide postfix is installed (surfaced in [CastDiag] so we can confirm it fired).</summary>
    public static bool Installed { get; private set; }

    /// <summary>Opt-in event logging (wired from the plugin's "Cast-bar diagnostic" toggle).</summary>
    public static bool Diag;

    /// <summary>Number of casters currently latched (for the diagnostic census).</summary>
    public static int ActiveCount => _casts.Count;

    /// <summary>
    /// Latch lookup for the current target. Returns the live cast for <paramref name="casterUuid"/>, pruning it if it
    /// has gone stale (no <c>SetSingGuide</c> tick for <see cref="StaleMs"/>) — the normal end/interrupt path.
    /// </summary>
    public static bool TryGet(long casterUuid, out CastEntry entry)
    {
        if (_casts.TryGetValue(casterUuid, out entry))
        {
            if (Environment.TickCount64 - entry.LastTickMs > StaleMs)
            {
                _casts.Remove(casterUuid);
                _version++;
                entry = default;
                return false;
            }
            return true;
        }
        entry = default;
        return false;
    }

    private static Action<string>? _log;

    internal static bool Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        // PRIMARY: EntityExtensions.SetSingGuide — the per-frame producer that carries the real bar values.
        var ext = StellarInterop.FindType("Panda.ZGame.EntityExtensions");
        if (ext == null) { log("[Cast] EntityExtensions not found — cast capture skipped"); return false; }

        var set = ResolveSetSingGuide(ext);
        if (set == null) { log("[Cast] SetSingGuide not found — cast capture skipped"); return false; }

        try
        {
            harmony.Patch(set, postfix: new HarmonyMethod(typeof(CastPatch), nameof(OnSetSingGuide)));
            Installed = true;
            log("[Cast] SetSingGuide postfix patched (primary cast producer)");
        }
        catch (Exception ex) { log($"[Cast] SetSingGuide patch failed: {ex.Message}"); return false; }

        // ENRICHMENT (optional): beginSingGuide for skill id/name. Inlined in this build → likely never fires; kept so
        // the label/icon light up automatically if a future build stops inlining it. A miss here is non-fatal.
        try
        {
            var sk = StellarInterop.FindType("Panda.ZGame.ZStateSkillComp");
            var begin = sk == null ? null : ResolveMethod0(sk, "beginSingGuide");
            if (begin != null)
            {
                harmony.Patch(begin, postfix: new HarmonyMethod(typeof(CastPatch), nameof(OnBeginSing)));
                log("[Cast] beginSingGuide enrichment postfix patched");
            }
            else log("[Cast] beginSingGuide not resolvable — enrichment skipped (bar shows 'Casting…')");
        }
        catch (Exception ex) { log($"[Cast] beginSingGuide enrichment skipped: {ex.Message}"); }

        return true;
    }

    // Harmony teardown is owned by IHarmonyHost (auto-unpatch on dispose); reset only transient state here.
    internal static void Uninstall()
    {
        _casts.Clear();
        _version = 0;
        _entUuidResolved = false; _piEntUuid = null;
        _reflResolved = false;
        _mCurSkillId = _mSkillRow = _mSkillName = null;
        _skillRowType = null;
        _piHost = _piUuid = null; _hostResolved = false;
        _loggedError = false;
        _firstSetLogged = _firstBeginLogged = false;
    }

    // ── PRIMARY postfix: SetSingGuide(entity, value, maxValue, forward, type) ────────────────────────────────────
    // Static extension → no __instance. Positional injection: __0 = ZEntity, __1 = value, __2 = maxValue,
    // __3 = forward, __4 = type (ESingGuideType, byte-backed). entity IS the ZEntity → read .Uuid directly. Fully
    // guarded; the count-up fill is the game's own bar value, so no local tick anchor is needed.
    private static void OnSetSingGuide(object __0, float __1, float __2, bool __3, object __4)
    {
        try
        {
            if (__0 == null) return;

            long uuid = ReadEntityUuid(__0);

            if (!_firstSetLogged)
            {
                _firstSetLogged = true;
                _log?.Invoke($"[Cast] SetSingGuide FIRED uuid={uuid} val={__1:F2}/{__2:F2} fwd={__3} type={ToInt(__4)}");
            }
            if (uuid == 0) return;

            bool danger = ToInt(__4) == 2;   // ESingGuideType.MonsterDanger

            // Preserve any enrichment (SkillId/Name) already on the entry — SetSingGuide carries neither.
            _casts.TryGetValue(uuid, out var prev);
            _casts[uuid] = new CastEntry
            {
                Uuid       = uuid,
                Value      = __1,
                MaxValue   = __2,
                Forward    = __3,
                Danger     = danger,
                LastTickMs = Environment.TickCount64,
                SkillId    = prev.SkillId,
                Name       = prev.Name ?? "",
            };
            _version++;

            if (Diag)
                _log?.Invoke($"[CastDiag] set uuid={uuid} val={__1:F2}/{__2:F2} fwd={__3} danger={danger} " +
                             $"active={_casts.Count}");
        }
        catch (Exception ex) { LogError("set", ex); }
    }

    // ── ENRICHMENT postfix: beginSingGuide() — fill SkillId/Name only, never touch the count-up values. ──────────
    private static void OnBeginSing(object __instance)
    {
        try
        {
            if (__instance == null) return;
            EnsureRefl(__instance.GetType());

            long uuid = GetHostUuid(__instance);

            if (!_firstBeginLogged)
            {
                _firstBeginLogged = true;
                _log?.Invoke($"[Cast] beginSingGuide FIRED uuid={uuid} skillId={ReadInt(_mCurSkillId, __instance)}");
            }
            if (uuid == 0) return;

            int    skillId = ReadInt(_mCurSkillId, __instance);
            string name    = ReadSkillName(__instance);

            // Update-or-create: if SetSingGuide already latched this caster, enrich in place; otherwise seed a fresh
            // (non-stale) entry so the label survives until the first SetSingGuide tick populates the bar values.
            if (_casts.TryGetValue(uuid, out var e))
            {
                e.SkillId = skillId; e.Name = name;
                _casts[uuid] = e;
            }
            else
            {
                _casts[uuid] = new CastEntry
                {
                    Uuid = uuid, SkillId = skillId, Name = name,
                    Forward = true, MaxValue = 0f, Value = 0f, Danger = false,
                    LastTickMs = Environment.TickCount64,
                };
            }
            _version++;

            if (Diag)
                _log?.Invoke($"[CastDiag] begin(enrich) uuid={uuid} skillId={skillId} name='{name}' active={_casts.Count}");
        }
        catch (Exception ex) { LogError("begin", ex); }
    }

    // ── Caster uuid off the ZEntity arg (SetSingGuide's first arg IS the entity). ────────────────────────────────
    private static PropertyInfo? _piEntUuid;
    private static bool          _entUuidResolved;

    private static long ReadEntityUuid(object entity)
    {
        if (!_entUuidResolved)
        {
            _entUuidResolved = true;
            _piEntUuid = entity.GetType().GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
            _log?.Invoke($"[Cast] entUuid resolved={_piEntUuid != null}");
        }
        if (_piEntUuid == null) return 0L;
        try { return Convert.ToInt64(_piEntUuid.GetValue(entity) ?? 0L); } catch { return 0L; }
    }

    // ── Enrichment reflection (resolved once off the first ZStateSkillComp instance type) ────────────────────────
    private static bool        _reflResolved;
    private static MemberInfo? _mCurSkillId;   // curSkillId_ (int)
    private static MemberInfo? _mSkillRow;     // skillRow_ (SkillTableBase)
    private static Type?       _skillRowType;  // last-seen runtime type of skillRow_ (lazy re-resolve on change)
    private static MemberInfo? _mSkillName;    // SkillTableBase.Name (string)

    private static void EnsureRefl(Type t)
    {
        if (_reflResolved) return;
        _reflResolved = true;
        _mCurSkillId = FindMember(t, "curSkillId_");
        _mSkillRow   = FindMember(t, "skillRow_");
        _log?.Invoke($"[Cast] refl skillId={_mCurSkillId != null} row={_mSkillRow != null}");
    }

    private static string ReadSkillName(object inst)
    {
        var row = ReadMember(_mSkillRow, inst);
        if (row == null) return "";
        var rt = row.GetType();
        if (rt != _skillRowType) { _skillRowType = rt; _mSkillName = FindMember(rt, "Name"); }
        return ReadMember(_mSkillName, row) as string ?? "";
    }

    // ── Caster uuid for the enrichment hook: base-walk ZStateSkillComp → ZComponent.Host (ZEntity) → ZEntity.Uuid.
    //    Mirrors BuffTrackPatch.ResolveHostRefl/GetHostEntityUuid. ─────────────────────────────────────────────
    private static PropertyInfo? _piHost;
    private static PropertyInfo? _piUuid;
    private static bool          _hostResolved;

    private static long GetHostUuid(object comp)
    {
        if (!_hostResolved)
        {
            _hostResolved = true;
            var cur = comp.GetType();
            while (cur != null && _piHost == null)
            {
                _piHost = cur.GetProperty("Host", BindingFlags.NonPublic | BindingFlags.Instance)
                       ?? cur.GetProperty("Host", BindingFlags.Public | BindingFlags.Instance);
                cur = cur.BaseType;
            }
            if (_piHost != null)
                _piUuid = _piHost.PropertyType.GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
            _log?.Invoke($"[Cast] hostRefl host={_piHost != null} uuid={_piUuid != null}");
        }
        if (_piHost == null || _piUuid == null) return 0L;
        try
        {
            var host = _piHost.GetValue(comp);
            return host == null ? 0L : Convert.ToInt64(_piUuid.GetValue(host) ?? 0L);
        }
        catch { return 0L; }
    }

    // ── Small helpers ────────────────────────────────────────────────────────────────────────────────────────

    // Resolve the single public static SetSingGuide(ZEntity, float, float, bool, ESingGuideType) — 5 params, static.
    private static MethodInfo? ResolveSetSingGuide(Type t)
    {
        try
        {
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (mi.Name == "SetSingGuide" && mi.GetParameters().Length == 5) return mi;
        }
        catch { }
        return null;
    }

    // Locate a 0-arg method by name (prefers StellarInterop.FindMethod, then a direct NonPublic scan).
    private static MethodInfo? ResolveMethod0(Type t, string name)
    {
        var m = StellarInterop.FindMethod(t, name, 0);
        if (m != null) return m;
        try
        {
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (mi.Name == name && mi.GetParameters().Length == 0) return mi;
        }
        catch { }
        return null;
    }

    // Property-then-field lookup across the full hierarchy (IL2CPP members surface as either, by generator).
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

    private static int ReadInt(MemberInfo? m, object t)
    {
        var v = ReadMember(m, t);
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    // Boxed enum / numeric → int (ESingGuideType is byte-backed; Convert handles the boxed enum).
    private static int ToInt(object? v)
    {
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    private static bool _loggedError;
    private static bool _firstSetLogged;
    private static bool _firstBeginLogged;

    private static void LogError(string src, Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _log?.Invoke($"[Cast] {src}: {ex.InnerException?.Message ?? ex.Message}");
    }
}
