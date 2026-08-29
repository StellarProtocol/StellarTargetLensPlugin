namespace Stellar.TargetLens;

// Local break-gauge fill state machine. The raw AttrStunned attribute drains smoothly to 0 (player breaking the
// target) but then SNAPS back to max in one step after the recovery window — the smooth 0→full "refill" seen in
// the game HUD is a client-side visual tween, not streamed. This reproduces that tween: while the gauge is broken
// (stunned<=0, maxStunned>0), Fill is driven 0→1 over the monster's BreakingContinueTime and a seconds-remaining
// countdown is exposed. Depletion is left on the live attr (rendered directly as stunned/max when not broken).
//
// Driven each frame from the HUD getters with the live snapshot values + looked-up duration. Multiple getters
// call Update per frame, so state advances at most once per Unity frame (frame-gated); repeated same-frame calls
// no-op and leave the computed values stable.
internal sealed class BreakGauge
{
    private int  _lastFrame = -1;
    private long _uuid;
    private bool _wasBroken;
    private long _breakStartMs;
    private float _durSec;

    private float _fill;
    private bool  _broken;
    private float _remainSec;

    internal float Fill         => _fill;        // 0..1 to render
    internal bool  Broken       => _broken;      // true during the recovery window
    internal float RemainingSec => _remainSec;   // countdown while Broken (>=0)

    internal void Update(long uuid, long stunned, long maxStunned, float durationSec, long nowMs)
    {
        int frame = UnityEngine.Time.frameCount;
        if (frame == _lastFrame) return;   // once per frame — repeated getters this frame keep prior values
        _lastFrame = frame;

        // Target changed → drop any in-flight break state.
        if (uuid != _uuid) { _uuid = uuid; _wasBroken = false; }

        bool broken = maxStunned > 0 && stunned <= 0;

        if (!_wasBroken && broken)
        {
            // Rising edge: start the local fill tween.
            _breakStartMs = nowMs;
            _durSec       = durationSec;
            _wasBroken    = true;
        }
        else if (_wasBroken && !broken)
        {
            // Falling edge: gauge snapped back to >0, recovery over.
            _wasBroken = false;
        }

        if (broken && _wasBroken)
        {
            float elapsed = (nowMs - _breakStartMs) / 1000f;
            _fill      = _durSec <= 0f ? 1f : Clamp01(elapsed / _durSec);
            _remainSec = _durSec - elapsed;
            if (_remainSec < 0f) _remainSec = 0f;
            _broken    = true;
        }
        else
        {
            _fill      = maxStunned > 0 ? Clamp01((float)stunned / maxStunned) : 0f;
            _remainSec = 0f;
            _broken    = false;
        }
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
