using System;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// TEMPORARY diagnostic partial. When <see cref="BreakDiag"/> is enabled, logs the target's break-gauge attrs
/// (stunned/max plus the extra break-state attrs 444/445/453/455) to the BepInEx log, change-gated per target so
/// the log only fires when a value actually moves. Opt-in via the "Break-gauge diagnostic" settings toggle.
/// Throwaway — safe to delete wholesale.
/// </summary>
internal sealed partial class TargetInfoTracker
{
    // Master opt-in switch (mirrors TargetBuffTracker's source filters): loaded from config, toggled from settings.
    public bool BreakDiag;

    // Extra break-state attribute ids (EAttrType) sampled alongside AttrStunned(443)/AttrMaxStunned(442).
    private const int AttrInOverdrive                 = 444; // EAttrType.AttrInOverdrive
    private const int AttrIsLockStunned               = 445; // EAttrType.AttrIsLockStunned
    private const int AttrStopBreakingBarTickingFlag  = 453; // EAttrType.AttrStopBreakingBarTickingFlag
    private const int AttrBreakingStage               = 455; // EAttrType.AttrBreakingStage

    private bool    _breakDiagResolved;
    private object? _attrBreakingStageBox;      // 455
    private object? _attrInOverdriveBox;        // 444
    private object? _attrStopBreakTickBox;      // 453
    private object? _attrIsLockStunnedBox;      // 445

    // Change-gate — last-logged sample per uuid. Sentinels so the first sample for any target always logs.
    private long _bdUuid = long.MinValue;
    private int  _bdStunned  = int.MinValue;
    private int  _bdMax      = int.MinValue;
    private int  _bdStage    = int.MinValue;
    private int  _bdOverdrive = int.MinValue;
    private int  _bdStopTick = int.MinValue;
    private int  _bdLock     = int.MinValue;

    // Lazily resolve the 4 extra EAttrType boxes off the same enum the main tracker uses.
    private void EnsureBreakDiagBoxes()
    {
        if (_breakDiagResolved) return;
        _breakDiagResolved = true;
        var attrEnum = StellarInterop.FindType("Zproto.EAttrType");
        if (attrEnum == null) return;
        _attrBreakingStageBox = Enum.ToObject(attrEnum, AttrBreakingStage);
        _attrInOverdriveBox   = Enum.ToObject(attrEnum, AttrInOverdrive);
        _attrStopBreakTickBox = Enum.ToObject(attrEnum, AttrStopBreakingBarTickingFlag);
        _attrIsLockStunnedBox = Enum.ToObject(attrEnum, AttrIsLockStunned);
    }

    // Called at the end of Poll() — logs a change-gated break-state line for the current target when opted in.
    internal void LogBreakDiag(long uuid, long configId, string name, int stunned, int maxStunned, object? targetEnt)
    {
        try
        {
            if (!BreakDiag || targetEnt == null || maxStunned <= 0) return;

            EnsureBreakDiagBoxes();
            if (_attrBreakingStageBox == null || _attrInOverdriveBox == null
                || _attrStopBreakTickBox == null || _attrIsLockStunnedBox == null) return;

            int stage     = (int)ReadAttrInt(targetEnt, _attrBreakingStageBox);
            int overdrive = (int)ReadAttrInt(targetEnt, _attrInOverdriveBox);
            int stopTick  = (int)ReadAttrInt(targetEnt, _attrStopBreakTickBox);
            int lockv     = (int)ReadAttrInt(targetEnt, _attrIsLockStunnedBox);

            // Fresh target → reset the gate so its first sample always logs.
            if (uuid != _bdUuid)
            {
                _bdUuid = uuid;
                _bdStunned = _bdMax = _bdStage = _bdOverdrive = _bdStopTick = _bdLock = int.MinValue;
            }

            bool changed = stunned != _bdStunned || maxStunned != _bdMax || stage != _bdStage
                           || overdrive != _bdOverdrive || stopTick != _bdStopTick || lockv != _bdLock;
            if (!changed) return;

            _bdStunned = stunned; _bdMax = maxStunned; _bdStage = stage;
            _bdOverdrive = overdrive; _bdStopTick = stopTick; _bdLock = lockv;

            long ms = Environment.TickCount64;
            _services.Log.Info(
                $"[BreakDiag] t={ms} cfg={configId} name='{name}' stunned={stunned}/{maxStunned} " +
                $"stage={stage} overdrive={overdrive} stopTick={stopTick} lock={lockv}");
        }
        catch { /* diagnostic must never throw from the poll */ }
    }
}
