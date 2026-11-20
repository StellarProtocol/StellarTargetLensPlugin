namespace Stellar.TargetLens;

// "Break text" setting (Settings → TARGET HUD, under Size): what the break-gauge label shows.
//   0 = %     "BREAK 70%" in the right cap's label box (the default, the original look)
//   1 = Value "BREAK 350 / 500" in the label box (current / max gauge points)
//   2 = Both  the label box keeps "BREAK 70%" and "350 / 500" is written ON the gauge — except at Slim, where the gauge is
//             only 8 px tall, so Both behaves exactly like Value there.
// While broken every mode shows "BROKEN 5.2s" (TargetHudBreakText) and the gauge text is hidden (the broken gauge layer
// has no label at all). Live: the label getters read _breakTextMode on every refresh, so no window rebuild.
// The numbers are the same Stunned / MaxStunned the % comes from (not the local refill tween, which only runs while
// broken, when no value is shown).
public sealed partial class Plugin
{
    private int _breakTextMode;   // config "break_text"; loaded in LoadBreakTextMode (RegisterTargetHud)

    private void LoadBreakTextMode()
    {
        int m = _cfg.Get<int>("break_text", 0);
        _breakTextMode = m is >= 0 and <= 2 ? m : 0;   // unknown → % (the default)
    }

    private void SetBreakTextMode(int mode)
    {
        if (mode == _breakTextMode) return;
        _breakTextMode = mode;
        _cfg.Set<int>("break_text", mode);
        _cfg.Save();
    }

    // The label box shows the value (Value, or Both at Slim); otherwise the percentage.
    private bool BreakCapShowsValue => _breakTextMode == 1 || (_breakTextMode == 2 && _hudSize == 0);

    // The value is drawn on the gauge only in Both at Medium / Large.
    private bool BreakGaugeShowsValue => _breakTextMode == 2 && _hudSize != 0;

    // Short form keeps "BREAK cur / max" inside the Slim label box (135 px, bold 11): full digits up to 9,999
    // ("BREAK 9,999 / 9,999" ≈ 106 px), the HP label's K/M form from 10,000 (worst "999.9K / 999.9K" ≈ 122 px; with the
    // 4-glyph ja/ko prefix ≈ 127 px).
    private static string BreakNum(long v) => HpNum(v, v >= 10_000);

    // On-gauge text "350 / 500" (Both, Medium / Large, not broken). Cached on the two shown numbers.
    private long _brGaugeCur = -1, _brGaugeMax = -1; private string _brGaugeLbl = "";
    private string TargetHudBreakGaugeText()
    {
        if (!BreakGaugeShowsValue) return "";
        var s = Cur;
        if (s.MaxStunned <= 0) return "";
        if (s.Stunned == _brGaugeCur && s.MaxStunned == _brGaugeMax) return _brGaugeLbl;
        _brGaugeCur = s.Stunned; _brGaugeMax = s.MaxStunned;
        _brGaugeLbl = BreakNum(s.Stunned) + " / " + BreakNum(s.MaxStunned);
        return _brGaugeLbl;
    }

    // Label-box value form "BREAK 350 / 500", cached on the numbers + the localized template.
    private long _brCapCur = -1, _brCapMax = -1; private string? _brCapTpl; private string _brCapLbl = "";
    private string TargetHudBreakValueText(TargetInfoTracker.Snapshot s)
    {
        string tpl = _loc.T("tl.hud.break_val");
        if (s.Stunned == _brCapCur && s.MaxStunned == _brCapMax && ReferenceEquals(tpl, _brCapTpl)) return _brCapLbl;
        _brCapCur = s.Stunned; _brCapMax = s.MaxStunned; _brCapTpl = tpl;
        _brCapLbl = string.Format(tpl, BreakNum(s.Stunned), BreakNum(s.MaxStunned));
        return _brCapLbl;
    }
}
