using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Shield ("armor" / 护盾) for the Target HUD. Driven by the target's AttrShieldList (60050) summed to a
// current/max pair (see TargetInfoTracker.Shield.cs). Rendered as a translucent cyan OVERLAY on the HP bar
// (BarElement.Overlay01) rather than a separate bar — the band shrinks as the shield is consumed and
// disappears when there is none.
public sealed partial class Plugin
{
    private static readonly ColorRgba ShieldOverlayColor = new(0.30f, 0.85f, 1.0f, 0.5f); // translucent cyan shield layer

    // Shield CURRENT value over HP MAX. Reference Cur first each call so _targetInfo is polled this
    // frame (refreshing LastTargetEntity) before the shield read, which reads off that same entity. Returns 0
    // when the target has no shield ⇒ the overlay is invisible.
    private float TargetHudShieldOverlayFraction()
    {
        _ = Cur;
        _targetInfo.TryGetCurrentShield(out var cur, out var _);
        float raw = (Cur.MaxHp > 0 && cur > 0) ? Clamp01((float)cur / Cur.MaxHp) : 0f;
        return raw;
    }
}
