using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

internal static partial class BuffTrackPatch
{
    private static MethodInfo?   _miGetBuffTable;
    private static object?       _buffTableInst;
    private static MethodInfo?   _miGetBuffRow;
    private static PropertyInfo? _piBuffRowName;
    private static PropertyInfo? _piBuffRowVisible;
    private static PropertyInfo? _piBuffRowType;
    private static PropertyInfo? _piBuffRowSkillId;
    private static bool          _tableReflResolved;

    private static object?       _skillTableInst;
    private static MethodInfo?   _miGetSkillRow;
    private static PropertyInfo? _piSkillRowName;
    private static bool          _skillTableResolved;

    private static void GetBuffInfo(int baseId, out string name, out int? visible, out int? buffType, out int skillId)
    {
        name = ""; visible = null; buffType = null; skillId = 0;
        if (!_tableReflResolved) ResolveBuffTable();
        if (_miGetBuffRow == null || _buffTableInst == null) return;
        try
        {
            var row = _miGetBuffRow.Invoke(_buffTableInst, new object[] { (object)baseId });
            if (row == null) return;
            var t = row.GetType();
            _piBuffRowName    ??= t.GetProperty("Name",     BindingFlags.Public | BindingFlags.Instance);
            _piBuffRowVisible ??= t.GetProperty("Visible",  BindingFlags.Public | BindingFlags.Instance);
            _piBuffRowType    ??= t.GetProperty("BuffType", BindingFlags.Public | BindingFlags.Instance);
            _piBuffRowSkillId ??= t.GetProperty("SkillId",  BindingFlags.Public | BindingFlags.Instance);
            name     = (string?)(_piBuffRowName?.GetValue(row)) ?? "";
            visible  = (int?)(_piBuffRowVisible?.GetValue(row));
            buffType = (int?)(_piBuffRowType?.GetValue(row));
            skillId  = (int?)(_piBuffRowSkillId?.GetValue(row)) ?? 0;
        }
        catch { }
    }

    // Thin reuse wrapper for the target-buff reader: resolves a buff's display name + type + table SkillId off the
    // same cached BuffTableBase reflection the local-player path uses (self-resolves on demand). Type only:
    // 0=Debuff,1=Gain,2=GainRecovery,3=Item; -1 when unknown. skillId is the table's static fallback (often 0).
    internal static void LookupBuff(int baseId, out string name, out int buffType, out int skillId)
    {
        GetBuffInfo(baseId, out var nm, out _, out var bt, out var sid);
        name = nm; buffType = bt ?? -1; skillId = sid;
    }

    // Resolves a skill table id to its display name (same cached SkillTableBase reflection the local path uses).
    internal static string LookupSkillName(int skillId) => GetSkillName(skillId);

    private static void ResolveBuffTable()
    {
        _tableReflResolved = true;
        var t = StellarInterop.FindType("Bokura.BuffTableBase") ?? StellarInterop.FindType("BuffTableBase");
        if (t == null) return;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GetTable") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
            { _miGetBuffTable = m; break; }
        }
        if (_miGetBuffTable == null) return;
        try { _buffTableInst = _miGetBuffTable.Invoke(null, new object[] { false }); }
        catch (Exception ex) { _log?.Invoke($"[Buff] GetTable threw: {ex.InnerException?.Message ?? ex.Message}"); return; }
        if (_buffTableInst == null) return;
        foreach (var m in _buffTableInst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "get_Item") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
            { _miGetBuffRow = m; break; }
        }
        _log?.Invoke($"[Buff] BuffTable: inst={_buffTableInst != null} getItem={_miGetBuffRow != null}");
    }

    private static string GetSkillName(int skillId)
    {
        if (skillId <= 0) return "";
        if (!_skillTableResolved) ResolveSkillTable();
        if (_miGetSkillRow == null || _skillTableInst == null) return "";
        try
        {
            var row = _miGetSkillRow.Invoke(_skillTableInst, new object[] { (object)skillId });
            if (row == null) return "";
            _piSkillRowName ??= row.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            return (string?)(_piSkillRowName?.GetValue(row)) ?? "";
        }
        catch { return ""; }
    }

    private static void ResolveSkillTable()
    {
        _skillTableResolved = true;
        var t = StellarInterop.FindType("Bokura.SkillTableBase") ?? StellarInterop.FindType("SkillTableBase");
        if (t == null) return;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GetTable") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
            { try { _skillTableInst = m.Invoke(null, new object[] { false }); } catch { } break; }
        }
        if (_skillTableInst == null) return;
        foreach (var m in _skillTableInst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "get_Item") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
            { _miGetSkillRow = m; break; }
        }
    }

}
