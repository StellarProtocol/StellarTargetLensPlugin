namespace Stellar.TargetLens;

// HUD-target lock: hotkey callback + poptip helper. Purely display-only — toggling the lock changes only WHICH
// entity our tracker READS for the overlays; it never touches the game's real target / auto-attack / lock-on.
public sealed partial class Plugin
{
    // Hotkey callback: toggle the HUD lock and show a poptip. Display-only — does not change the game's target.
    private void ToggleHudLock()
    {
        try
        {
            var (result, name) = _targetInfo.ToggleLock();
            switch (result)
            {
                case TargetInfoTracker.LockResult.Locked:   ShowNotice(_loc.TFormat("tl.notice.locked", name)); break;
                case TargetInfoTracker.LockResult.Unlocked: ShowNotice(_loc.T("tl.notice.unlocked")); break;
                case TargetInfoTracker.LockResult.NoTarget: ShowNotice(_loc.T("tl.notice.noTarget")); break;
            }
        }
        catch { }
    }

    // Short center poptip via the game's Lua tips VM (PopTip style, MessageTable row 101 = Type-10, ~2.5s).
    // Verified: no C# INoticeTip type exists — the tip system is Lua-only. Route through the framework ILua bridge.
    private void ShowNotice(string text)
    {
        try
        {
            if (!_services.Lua.Ready) return;
            string safe = (text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("]==]", "]=]"); // keep the long-bracket literal intact
            _services.Lua.DoString($"pcall(function() Z.TipsVM.OpenMessageViewByContextAndConfig([==[{safe}]==], 101) end)");
        }
        catch { }
    }
}
