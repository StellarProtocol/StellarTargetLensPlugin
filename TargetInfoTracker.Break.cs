using System.Reflection;

namespace Stellar.TargetLens;

// Break-gauge config lookup. Mirrors ResolveMonsterName's table + row resolution (reusing the same cached
// singleton table + TryGetValue handles) but reads MonsterTable[configId].BreakingContinueTime instead of Name.
// This is the per-monster recovery-window length (float SECONDS) the HUD's local fill tween runs over.
internal sealed partial class TargetInfoTracker
{
    private PropertyInfo? _piBreakingContinueTime;   // MonsterTableBase.BreakingContinueTime (public instance float)
    private bool          _breakContTimeResolved;

    // Resolve MonsterTable[configId].BreakingContinueTime. Returns true + seconds on success; false + 0 on any miss.
    // Fully guarded — never throws. No diagnostic logging (that lives in the name path).
    internal bool TryGetBreakingContinueTime(long configId, out float seconds)
    {
        seconds = 0f;
        if (configId <= 0 || _monTblType == null || _miMonGetTable == null) return false;
        try
        {
            _monTblObj ??= _miMonGetTable.Invoke(null, new object[] { false });
            var tableObj = _monTblObj;
            if (tableObj == null) return false;

            if (!_monTryGetResolved)
            {
                foreach (var m in tableObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "TryGetValue" && m.GetParameters().Length == 3) { _miMonTryGetValue = m; break; }
                _monTryGetResolved = true;
            }
            if (_miMonTryGetValue == null) return false;

            // errorWhenNotFound = false — a miss must NOT log a spurious game error.
            var args = new object?[] { (int)configId, null, false };
            if (_miMonTryGetValue.Invoke(tableObj, args) is not bool b || !b) return false;
            var row = args[1];
            if (row == null) return false;

            if (!_breakContTimeResolved)
            {
                _piBreakingContinueTime = row.GetType().GetProperty("BreakingContinueTime",
                    BindingFlags.Public | BindingFlags.Instance);
                _breakContTimeResolved = true;
            }
            if (_piBreakingContinueTime?.GetValue(row) is float f) { seconds = f; return true; }
            return false;
        }
        catch { seconds = 0f; return false; }
    }
}
