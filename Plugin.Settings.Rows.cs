using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Settings row builders + the header status (house style; layout in Plugin.Settings.Layout.cs). Every setting row is
// [label (weighted, ellipsized)] [control] [round "?"], 28 px tall, so the "?"s form one right-edge column per panel
// (house rule 17). `enabled` (optional, polled) = rule 22: false → the label fades and the control is RECESSED and
// inert, but keeps showing its value; the row never hides, so the window height never changes.
public sealed partial class Plugin
{
    private const float SettingsRowH     = 28f;   // every setting row (the prototype's min-height 28)
    private const float SettingsRowGap   = 6f;    // between a row's label / control / "?"
    // Choice-chip widths = the longest label of that choice in ANY locale (bold 13) + padding (house rule 10):
    // Classic: ja "クラシック" ≈ 65; List: fil "Listahan" ≈ 58; Off: id "Mati" / ja "オフ"; Top aggro: fil "Nangunguna" ≈ 80.
    private const float BuffClassicChipW = 72f;
    private const float ListChipW        = 64f;
    private const float OffChoiceChipW   = 48f;
    private const float TopAggroChipW    = 92f;
    private const float ChoiceChipGap    = 4f;
    private const float SliderValueW     = 36f;   // "2.5x"
    private const float SelectPillLabelW = 120f;  // "Select effects…": fil "Pumili ng epekto…" ≈ 115 at bold 13

    // A 28 px row: a zero-width-ish fixed strut pins the height, the real content sits in one weighted cell. The strut
    // lives in its OWN fixed cell beside the content (not inside the content row) because a zero-width SpacerElement in a
    // RowElement is stretchy and would steal width from the label (WindowBuilder-Patterns, 2026-11-07); outer Gap 0.01 =
    // no gap (Gap 0 would be the DEFAULT gap).
    private static HudElement SettingRow(params HudElement[] inner) => new RowElement(new HudElement[]
    {
        new CellElement(new SpacerElement(0f, SettingsRowH), Width: 0.01f),
        new CellElement(new RowElement(inner, Gap: SettingsRowGap), Weight: 1f),
    }, Gap: 0.01f);

    private static bool On(Func<bool>? enabled) => enabled == null || enabled();

    private HudElement RowLabel(Func<string> label, Func<bool>? enabled) => new CellElement(
        new TextElement(label, Color: () => On(enabled) ? TextColor() : DisabledLabel(PanelColor()), NoWrap: true)
            { Ellipsize = true }, Weight: 1f);

    // label … [On/Off chip] [?]. The help popover's title is the row label.
    private HudElement ChipRow(string labelKey, string helpKey, string sectionKey, Func<bool> get, Action<bool> set,
        Func<bool>? enabled = null)
        => ChipRowLive(() => _loc.T(labelKey), labelKey, helpKey, sectionKey, get, set, enabled);

    // Same, with a label that changes live (Nearby "Only in lockable range (18 m)"); `titleKey` is the help title.
    // Own name, not an overload of ChipRow (keeps call sites unambiguous).
    private HudElement ChipRowLive(Func<string> label, string titleKey, string helpKey, string sectionKey, Func<bool> get,
        Action<bool> set, Func<bool>? enabled = null)
        => SettingRow(
            RowLabel(label, enabled),
            OnOffChip(get, set, enabled),
            HelpDot(titleKey, helpKey, sectionKey));

    // label … [choice chips] [?] — a segmented setting (Display: Classic / List / Off …).
    private HudElement ChoiceRow(string labelKey, string helpKey, string sectionKey, HudElement[] chips,
        Func<bool>? enabled = null)
        => SettingRow(
            RowLabel(() => _loc.T(labelKey), enabled),
            new RowElement(chips, Gap: ChoiceChipGap),
            HelpDot(labelKey, helpKey, sectionKey));

    // label … [round-knob slider 1.0–2.5] [muted "1.4x"] [?]. The readout string is rebuilt only when the rounded value
    // changes (polled every refresh; no per-poll string building).
    private HudElement SliderRow(string labelKey, string helpKey, string sectionKey, Func<float> get, Action<float> set,
        Func<bool>? enabled = null)
    {
        float shown = float.NaN;
        string text = string.Empty;
        string Value()
        {
            float v = MathF.Round(get() * 10f) / 10f;
            if (v != shown) { shown = v; text = v.ToString("0.0", CultureInfo.InvariantCulture) + "x"; }
            return text;
        }
        return SettingRow(
            RowLabel(() => _loc.T(labelKey), enabled),
            new CellElement(RoundSlider(get, set, 1.0f, 2.5f, enabled), Width: SliderTrackW),
            new CellElement(new TextElement(Value, Color: () => On(enabled) ? MutedText() : DisabledLabel(PanelColor()),
                Align: TextAlign.Right, NoWrap: true), Width: SliderValueW),
            HelpDot(labelKey, helpKey, sectionKey));
    }

    // Small muted UPPERCASE sub-caption inside a panel ("SHOW"); re-uppercased only on a language change.
    private HudElement SubCaption(string key)
    {
        string? src = null, upper = string.Empty;
        string Text()
        {
            var c = _loc.T(key);
            if (!string.Equals(c, src, StringComparison.Ordinal)) { src = c; upper = c.ToUpperInvariant(); }
            return upper;
        }
        return new TextElement(Text, Color: () => MutedText(), NoWrap: true, FontSize: 11) { Bold = true };
    }

    // [Select effects…] + muted "Pick which ones appear". Opens the existing effect picker centred on this window.
    private HudElement SelectEffectsRow() => SettingRow(
        NeutralButton(() => _loc.T("tl.button.selectEffects"), OpenSelectEffects, SelectPillLabelW),   // .Open.cs
        new CellElement(new TextElement(() => _loc.T("tl.desc.selectEffects"), Color: () => MutedText(), NoWrap: true)
            { Ellipsize = true }, Weight: 1f));

    // Right-column sections show a muted "off" hint in the caption while switched off (prototype CAST BAR state).
    private string SectionOffHint(bool on) => on ? string.Empty : _loc.T("tl.hint.off");

    // "Only in lockable range (18 m)" once the tracker has read the player's real lock range; without metres until then
    // (the range is only resolved while the filter is on, and the 18 m fallback must not pose as the player's range).
    // Composed only when the template or the value changes.
    private string? _lockLabelTpl;
    private float _lockLabelM = float.NaN;
    private string _lockLabel = string.Empty;

    private string LockRangeLabel()
    {
        if (!_nearby.LockableRangeResolved) return _loc.T("tl.row.lockRange");
        var tpl = _loc.T("tl.row.lockRangeM");
        float m = _nearby.LockableRangeM;
        if (m != _lockLabelM || !string.Equals(tpl, _lockLabelTpl, StringComparison.Ordinal))
        {
            _lockLabelTpl = tpl; _lockLabelM = m;
            try { _lockLabel = string.Format(CultureInfo.CurrentCulture, tpl, m.ToString("0.#", CultureInfo.CurrentCulture)); }
            catch (FormatException) { _lockLabel = tpl; }
        }
        return _lockLabel;
    }

    // ── header status (the ONE status builder) ────────────────────────────────────────────────────────────────────
    // "N of 6 overlays on": Target HUD, Buffs/debuffs (on unless Display = Off), Threat, Cast bar, Boss timers, Nearby.
    // None on → its own sentence. FmtCache → no allocation unless the count or the language changes.
    private const int OverlayCount = 6;
    private readonly FmtCache _statusFmt = new();

    private string SettingsStatus()
    {
        int n = (_targetHudOn ? 1 : 0) + (_buffStyle != 2 ? 1 : 0) + (_showThreat ? 1 : 0) + (_castBarOn ? 1 : 0)
              + (_bossTimerOn ? 1 : 0) + (_nearbyOn ? 1 : 0);
        return n == 0 ? _loc.T("tl.status.none") : _statusFmt.Get(_loc.T("tl.status.overlays"), n, OverlayCount);
    }
}
