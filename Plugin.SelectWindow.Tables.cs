using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Effect-picker table loader + search filter. Splits BuffTableBase rows into debuffs (BuffType==0) vs buffs
// (else) with Id/Name/Icon/Desc, loaded lazily on first tab visit. Ported from CooldownBar's
// Plugin.Settings.Tables.cs (Skills tab removed).
public sealed partial class Plugin
{
    private const int SettingsPoolSize = 14;

    // ── Debuffs tab raw + filtered data ──────────────────────────────────────
    private int[]    _dtIds       = Array.Empty<int>();
    private string[] _dtNames     = Array.Empty<string>();
    private string[] _dtDescs     = Array.Empty<string>();
    private int      _dtCount     = 0;
    private int[]    _dtFiltIds   = Array.Empty<int>();
    private string[] _dtFiltNames = Array.Empty<string>();
    private string[] _dtFiltDescs = Array.Empty<string>();
    private int      _dtFiltCount = 0;
    private string   _dtFilter    = "";
    private int      _dtOffset    = 0;
    private readonly UvRect[] _dtUv = new UvRect[SettingsPoolSize];

    // ── Buffs tab raw + filtered data ─────────────────────────────────────────
    private int[]    _btIds       = Array.Empty<int>();
    private string[] _btNames     = Array.Empty<string>();
    private string[] _btDescs     = Array.Empty<string>();
    private int      _btCount     = 0;
    private int[]    _btFiltIds   = Array.Empty<int>();
    private string[] _btFiltNames = Array.Empty<string>();
    private string[] _btFiltDescs = Array.Empty<string>();
    private int      _btFiltCount = 0;
    private string   _btFilter    = "";
    private int      _btOffset    = 0;
    private readonly UvRect[] _btUv = new UvRect[SettingsPoolSize];
    private bool     _buffTabLoaded = false;

    // ── Lazy loader (idempotent: called from VirtualListElement Count func) ──

    private void EnsureBuffTabLoaded()
    {
        if (_buffTabLoaded) return;
        LoadSelectBuffTable();
        _buffTabLoaded = _dtCount > 0 || _btCount > 0;
        if (_dtCount > 0) ApplyDebuffTabFilter(_dtFilter);
        if (_btCount > 0) ApplyBuffTabFilter(_btFilter);
    }

    // ── Table loader ────────────────────────────────────────────────────────

    private void LoadSelectBuffTable()
    {
        try
        {
            var t = StellarInterop.FindType("Bokura.BuffTableBase") ?? StellarInterop.FindType("BuffTableBase");
            if (t == null) { _services.Log.Warning("[Select] BuffTableBase not found"); return; }

            MethodInfo? miGet = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetTable") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) { miGet = m; break; }
            }
            if (miGet == null) { _services.Log.Warning("[Select] BuffTableBase.GetTable not found"); return; }

            var tbl    = miGet.Invoke(null, new object[] { false });
            if (tbl == null) return;
            var values = tbl.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance)
                             ?.GetValue(tbl) ?? tbl;

            // Names come from the plugin's embedded English-translation JSON (same source the HUD tiles use),
            // so load it once before the enumeration loop.
            TranslatedBuffText.EnsureLoaded(_services.Log.Info);

            var dIds  = new List<int>(); var dNames = new List<string>();
            var bIds  = new List<int>(); var bNames = new List<string>();
            PropertyInfo? piKvp = null, piId = null, piName = null, piType = null, piDesc = null;
            var dDescs = new List<string>(); var bDescs = new List<string>();

            foreach (var item in SettingsReflectEnumerate(values))
            {
                if (item == null) continue;
                object? row = item;
                if (piKvp == null && piId == null)
                {
                    var kv = item.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (kv != null) piKvp = kv;
                }
                if (piKvp != null) row = piKvp.GetValue(item);
                if (row == null) continue;
                if (piId == null)
                {
                    var rt = row.GetType();
                    piId   = rt.GetProperty("Id",       BindingFlags.Public | BindingFlags.Instance);
                    piName = rt.GetProperty("Name",     BindingFlags.Public | BindingFlags.Instance);
                    piType = rt.GetProperty("BuffType", BindingFlags.Public | BindingFlags.Instance);
                    piDesc = rt.GetProperty("Desc",     BindingFlags.Public | BindingFlags.Instance);
                }
                int id = (int)(piId?.GetValue(row) ?? 0);
                if (id <= 0) continue;
                string gameName = (string?)(piName?.GetValue(row)) ?? "";
                string desc = (string?)(piDesc?.GetValue(row)) ?? "";
                // Display name priority (mirrors the HUD's ResolveEffectName, including the manual override dict):
                // EffectOverrides → English-translation JSON → game Name → "#<id>". No Name/Icon filtering — every id>0 row appears.
                string name;
                if (EffectOverrides.TryGetValue(id, out var ov) && !string.IsNullOrEmpty(ov.Name))     name = ov.Name;
                else if (TranslatedBuffText.TryGet(id, out var tn, out _) && !string.IsNullOrEmpty(tn)) name = tn;
                else if (!string.IsNullOrEmpty(gameName))                                              name = gameName;
                else                                                                                   name = $"#{id}";
                int buffType = (int)(piType?.GetValue(row) ?? -1);
                if (buffType == 0) { dIds.Add(id); dNames.Add(name); dDescs.Add(desc); }
                else               { bIds.Add(id); bNames.Add(name); bDescs.Add(desc); }
            }
            _dtIds = dIds.ToArray(); _dtNames = dNames.ToArray(); _dtDescs = dDescs.ToArray(); _dtCount = dIds.Count;
            _btIds = bIds.ToArray(); _btNames = bNames.ToArray(); _btDescs = bDescs.ToArray(); _btCount = bIds.Count;
            _services.Log.Info($"[Select] loaded {_dtCount} debuffs, {_btCount} buffs");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Select] LoadBuffTable: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private void ApplyDebuffTabFilter(string text)
    {
        _dtFilter = text; _dtOffset = 0;
        SettingsFilterTable(text, _dtIds, _dtNames, _dtDescs, _dtCount,
            _selection.IsDebuffTracked, out _dtFiltIds, out _dtFiltNames, out _dtFiltDescs, out _dtFiltCount);
    }

    private void ApplyBuffTabFilter(string text)
    {
        _btFilter = text; _btOffset = 0;
        SettingsFilterTable(text, _btIds, _btNames, _btDescs, _btCount,
            _selection.IsBuffTracked, out _btFiltIds, out _btFiltNames, out _btFiltDescs, out _btFiltCount);
    }

    private static void SettingsFilterTable(string text, int[] srcIds, string[] srcNames, string[] srcDescs,
        int srcCount, Func<int, bool> isTracked,
        out int[] ids, out string[] names, out string[] descs, out int count)
    {
        var q = text?.Trim() ?? "";
        var ri = new List<int>(); var rn = new List<string>(); var rd = new List<string>();
        for (int i = 0; i < srcCount; i++)
        {
            if (q.Length > 0 && srcNames[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            ri.Add(srcIds[i]); rn.Add(srcNames[i]); rd.Add(srcDescs[i]);
        }
        // Stable sort: tracked items float to the top
        var trackedIds = new List<int>(); var trackedNames = new List<string>(); var trackedDescs = new List<string>();
        var restIds    = new List<int>(); var restNames    = new List<string>(); var restDescs    = new List<string>();
        for (int i = 0; i < ri.Count; i++)
        {
            if (isTracked(ri[i])) { trackedIds.Add(ri[i]); trackedNames.Add(rn[i]); trackedDescs.Add(rd[i]); }
            else                  { restIds.Add(ri[i]);    restNames.Add(rn[i]);    restDescs.Add(rd[i]); }
        }
        trackedIds.AddRange(restIds); trackedNames.AddRange(restNames); trackedDescs.AddRange(restDescs);
        ids = trackedIds.ToArray(); names = trackedNames.ToArray(); descs = trackedDescs.ToArray(); count = ids.Length;
    }

    // ── IL2CPP duck-typed enumerator ─────────────────────────────────────────

    private static IEnumerable<object?> SettingsReflectEnumerate(object collection)
    {
        var t  = collection.GetType();
        var mi = t.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
        if (mi == null) yield break;
        var enumerator = mi.Invoke(collection, null);
        if (enumerator == null) yield break;
        var et     = enumerator.GetType();
        var miNext = et.GetMethod("MoveNext",  BindingFlags.Public | BindingFlags.Instance);
        var piCur  = et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        if (miNext == null || piCur == null) yield break;
        while ((bool)(miNext.Invoke(enumerator, null) ?? false))
            yield return piCur.GetValue(enumerator);
    }
}
