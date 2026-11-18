using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Shield ("armor" / 护盾) for the Target HUD. Driven by the target's AttrShieldList (60050) summed to a
// current/max pair (see TargetInfoTracker.Shield.cs). Rendered as a striped cyan SEGMENT right after the HP fill
// (BarElement.OverlayStart01 = hp/max, Overlay01 = this) — it shrinks as the shield is consumed and disappears when
// there is none.
public sealed partial class Plugin
{
    // (HP + shield CURRENT) over HP MAX, capped at 1. Reference Cur first each call so _targetInfo is polled this
    // frame (refreshing LastTargetEntity) before the shield read, which reads off that same entity. With no shield
    // this equals the HP fraction ⇒ a zero-width (invisible) segment.
    private float TargetHudShieldEndFraction()
    {
        var s = Cur;
        if (s.MaxHp <= 0) return 0f;
        long cur = 0;
        if (!ShowExample) _targetInfo.TryGetCurrentShield(out cur, out _);
        return Clamp01((float)(s.Hp + (cur > 0 ? cur : 0)) / s.MaxHp);
    }
}
