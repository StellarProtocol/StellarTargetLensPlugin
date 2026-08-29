using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Secondary target-uuid sources for <see cref="TargetInfoTracker"/>: the ECS PlayerEnt target-id extension
/// (the game HUD's own read) and the ZWorld hard-lock uuid. Both are fallbacks behind the legacy attr-bag
/// reads in the main partial; their reflection handles are resolved there in <c>EnsureApi</c>.
/// </summary>
internal sealed partial class TargetInfoTracker
{
    private Type?         _zworldType;         // ZWorld (singleton read live via StellarInterop.GetSingleton)
    private PropertyInfo? _piLockTargetUuid;
    private PropertyInfo? _piPlayerEnt;            // ZEntityMgr.PlayerEnt (the ECS PlayerEnt)
    private MethodInfo?   _miGetAttrTargetIdExt;   // PureEntityAttrPlayerTargetExtensions.GetAttrTargetId(ZPureEntity)

    // ZEntityMgr.Instance.PlayerEnt → PureEntityAttrPlayerTargetExtensions.GetAttrTargetId(playerEnt).
    private long ReadEcsTargetId()
    {
        if (_piPlayerEnt == null || _miGetAttrTargetIdExt == null) return 0;
        try
        {
            var mgr = StellarInterop.GetSingleton(_entMgrType);
            if (mgr == null) return 0;
            var playerEnt = _piPlayerEnt.GetValue(mgr);
            if (playerEnt == null) return 0;
            return Convert.ToInt64(_miGetAttrTargetIdExt.Invoke(null, new object[] { playerEnt }) ?? 0L);
        }
        catch { return 0; }
    }

    private long ReadLockTargetUuid()
    {
        if (_zworldType == null || _piLockTargetUuid == null) return 0;
        try
        {
            var inst = StellarInterop.GetSingleton(_zworldType);
            return inst == null ? 0 : Convert.ToInt64(_piLockTargetUuid.GetValue(inst) ?? 0L);
        }
        catch { return 0; }
    }
}
