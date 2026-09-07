using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Harmony capture of the game's DBM (boss deadly-skill) push, feeding <see cref="BossDbmTracker"/>. This
/// REPLACES the old approach of polling <c>Panda.ZUi.DBMMgr.DbmInfoDict</c>, whose backing <c>dbmInfoDict_</c>
/// field is Init/UnInit lifecycle-owned and intermittently returned null across entire encounters — blanking the
/// overlay for a whole fight.
///
/// <para>Instead we patch the PRODUCER: <c>Panda.ZUi.DBMMgr.onDBMDatacChanged(RepeatedField&lt;int&gt; skillCds,
/// long startTime)</c> — a real non-inlined instance method on the <c>ZSingleton&lt;DBMMgr&gt;</c> that fires only
/// on server DBM pushes (cold; no demand gate needed). Each call carries the CURRENT active set of DBM skill ids
/// plus one shared <c>startTime</c> anchor (server-epoch ms). The postfix copies that batch into static fields the
/// tracker reads on its own frame tick; the reflection-heavy <c>DbmTable</c> name/CD lookup is deferred to the
/// tracker so it stays off the game event thread. No in/ref-struct param → no trampoline-NullRef risk.</para>
///
/// <para>Threading: the postfix runs on the game/main thread and does nothing but a guarded copy + a whole-batch
/// field assignment. The tracker (also main-thread, per-frame) reads the newest snapshot and never mutates it, so
/// a plain field swap is safe — no lock. A version counter lets the tracker tell a fresh push from a repeat read.
/// The postfix MUST NEVER THROW (a throw in a game-thread postfix is dangerous) → everything is guarded.</para>
/// </summary>
internal static class DbmPatch
{
    // ── Latest captured batch (written by the postfix, read by the tracker) ──────────────────────────────────
    private static int[] _ids = Array.Empty<int>();
    private static long  _startTime;
    private static int   _version;   // bumped each capture; the tracker upserts only when this changes

    /// <summary>True once the postfix is installed (surfaced in the tracker census so we can confirm it fired).</summary>
    public static bool Installed { get; private set; }

    /// <summary>Latest push: the active DBM skill ids, the shared server-epoch-ms anchor, and a capture version.
    /// <paramref name="ids"/> is treated as read-only by the caller (never mutated in place).</summary>
    public static void GetBatch(out int[] ids, out long startTime, out int version)
    {
        ids = _ids; startTime = _startTime; version = _version;
    }

    private static Action<string>? _log;

    // RepeatedField<int> walk (Count + get_Item), resolved once off the runtime arg type — mirrors the Threat/Shield
    // RepeatedField idioms. ints marshal cleanly, so no boxed-struct dance is needed.
    private static PropertyInfo? _piCount;
    private static MethodInfo?   _miItem;
    private static bool          _walkResolved;
    private static bool          _loggedError;

    internal static bool Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var dbmMgrType = StellarInterop.FindType("Panda.ZUi.DBMMgr");
        if (dbmMgrType == null) { log("[BossDbm] DBMMgr not found — DBM capture skipped"); return false; }

        // Count-only name match → StellarInterop.FindMethod, mirroring BuffTrackPatch's OnAddBuff resolve.
        var target = StellarInterop.FindMethod(dbmMgrType, "onDBMDatacChanged", 2);
        if (target == null) { log("[BossDbm] onDBMDatacChanged not found — DBM capture skipped"); return false; }

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(DbmPatch), nameof(OnDbmDataChanged)));
            Installed = true;
            log("[BossDbm] onDBMDatacChanged postfix patched");
            return true;
        }
        catch (Exception ex) { log($"[BossDbm] onDBMDatacChanged patch failed: {ex.Message}"); return false; }
    }

    // Harmony postfix. Params bound BY NAME to the original method's (skillCds, startTime). skillCds is taken as
    // `object` (a Google.Protobuf RepeatedField<int> — ref type; walked via Count + get_Item), startTime as a plain
    // long. Kept minimal + fully guarded; the DbmTable lookup happens later, in the tracker's frame tick.
    private static void OnDbmDataChanged(object skillCds, long startTime)
    {
        try
        {
            int[] ids = Array.Empty<int>();
            if (skillCds != null)
            {
                if (!_walkResolved) ResolveWalk(skillCds.GetType());
                if (_piCount != null && _miItem != null)
                {
                    int n = _piCount.GetValue(skillCds) is int c ? c : 0;
                    if (n > 0)
                    {
                        ids = new int[n];
                        int w = 0;
                        for (int i = 0; i < n; i++)
                        {
                            var v = _miItem.Invoke(skillCds, new object[] { i });
                            if (v is int id) ids[w++] = id;
                        }
                        if (w != n) Array.Resize(ref ids, w);   // a stray non-int entry trims the array
                    }
                }
            }
            // Whole-batch swap — the tracker only ever reads the newest snapshot, so no lock is required.
            _ids       = ids;
            _startTime = startTime;
            _version++;
        }
        catch (Exception ex) { LogError(ex); }
    }

    private static void ResolveWalk(Type t)
    {
        _walkResolved = true;
        _piCount = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "get_Item" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(int))
            { _miItem = m; break; }
        _log?.Invoke($"[BossDbm] RepeatedField walk: count={_piCount != null} item={_miItem != null} type={t.FullName}");
    }

    private static void LogError(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _log?.Invoke($"[BossDbm] postfix: {ex.InnerException?.Message ?? ex.Message}");
    }
}
