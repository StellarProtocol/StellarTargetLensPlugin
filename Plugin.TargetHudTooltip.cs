using System.Text.RegularExpressions;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

// Click-to-tooltip for a target effect. Left-clicking a Target Effects list row (Plugin.BuffListWindow.cs; formerly a
// Target HUD Classic tile) pops a context-menu-style panel (PanelElement — themed background + 1px border) showing the
// effect icon, name, description, and source skill, placed beside the clicked row. Mirrors the CooldownBar plugin's
// tile tooltip (Plugin.Tooltip.cs): a separate Borderless DismissOnOutsideClick window whose rect is re-asserted after
// the destroy-on-hide remount (SetRect no-ops while the window is still unmounted).
public sealed partial class Plugin
{
    private IWindowControl _targetTip = null!;

    private string  _tipName = "";
    private string  _tipDesc = "";
    private string  _tipSource = "";
    private object? _tipTex;
    private UvRect  _tipUv;
    // Effect currently shown, by IDENTITY (the tracker keeps one TargetBuffRow per buff uuid) + its BaseId (the game
    // reuses buff uuids, and the tracker re-populates the same row object for the new buff). null when closed.
    private TargetBuffRow? _tipShownFor;
    private int            _tipShownBase;
    // The click-away dismiss fires on mouse DOWN (WindowInteractionTicker.TickDismissables → OnClose), BEFORE the row's
    // click on release — so "click the open row again" would see a closed tip and re-open it. Remember which effect a
    // mouse press just dismissed; a click on that same row shortly after is the toggle-close, not a re-open.
    private TargetBuffRow? _tipDismissedFor;
    private float          _tipDismissedAt;
    private const float    TipToggleWindowSec = 1f;
    private bool    _tipOpen;
    private bool    _tipPlaced;
    private WindowRect _tipRect;

    private static readonly Regex TagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static string StripTags(string? s) => s == null ? "" : TagPattern.Replace(s, "");

    private void RegisterTargetHudTooltip()
    {
        _targetTip = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.hud.tip",
                Title:       "",
                DefaultRect: new WindowRect(897f, 830f, 380f, 150f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                StartVisible = false, DismissOnOutsideClick = true,
                // Transient tooltip for the in-world HUD: draw only in-world, hide during loading screens.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            Root: new PanelElement(
                new ColumnElement(new HudElement[]
                {
                    new RowElement(new HudElement[]
                    {
                        new CellElement(
                            new GameTextureElement(() => _tipTex, 36, 36, () => _tipUv),
                            Width: 44f),
                        new TextElement(() => _tipName, Emphasis: true),
                    }, Gap: 6f),
                    new SeparatorElement(),
                    new TextElement(() => _tipDesc),
                    // Source skill line — shown only when the effect has a resolved parent skill.
                    new ConditionalElement(() => !string.IsNullOrEmpty(_tipSource),
                        new TextElement(() => _tipSource, Color: MutedColor)),
                }, Gap: 4f),
                Padding: 8f),
            OnClose: CloseTargetTip));
        _windows.Add(_targetTip);   // Dispose already loops _windows and Remove()s each
    }

    // Opens the tooltip for effect `r` beside its list row (`rowPx` = the row's rect, top-left origin, SCREEN PIXELS —
    // SelectableElement.OnClickWithRect). Caller: a Target Effects row click (OnBuffListRowClick, Plugin.BuffListWindow.cs),
    // which resolves the row in the LIST's order. Clicking the row whose tip is open closes it; another row swaps it.
    internal void OnTargetEffectClick(TargetBuffRow r, WindowRect rowPx)
    {
        bool sameAsShown = ReferenceEquals(r, _tipShownFor) && r.BaseId == _tipShownBase && _targetTip.IsShown;
        // This press already dismissed this row's tip (mouse-down click-away, see _tipDismissedFor) → keep it closed.
        bool justDismissed = ReferenceEquals(r, _tipDismissedFor) && r.BaseId == _tipShownBase
                             && Time.unscaledTime - _tipDismissedAt < TipToggleWindowSec;
        _tipDismissedFor = null;
        if (sameAsShown || justDismissed)
        {
            CloseTargetTip();
            return;
        }
        var info = _services.GameData.Combat.GetBuff(r.BaseId);
        var trName = GameTextName(r.BaseId);
        var trDesc = GameTextDescription(r.BaseId);
        // Name priority: manual override (see EffectOverrides) → English game text (fw GameText) → game data → tracker.
        if (EffectOverrides.TryGetValue(r.BaseId, out var ov) && !string.IsNullOrEmpty(ov.Name)) _tipName = ov.Name;
        else if (!string.IsNullOrEmpty(trName)) _tipName = StripTags(trName);
        else _tipName = StripTags(info?.Name);
        if (string.IsNullOrEmpty(_tipName)) _tipName = StripTags(r.Name);
        // Description priority: English game text → game data.
        _tipDesc = !string.IsNullOrEmpty(trDesc) ? StripTags(trDesc) : StripTags(info?.Description);
        _tipSource = string.IsNullOrEmpty(r.SkillName) ? "" : $"Source: {r.SkillName}";
        _tipTex      = EffectIcon(r, 36f, out _tipUv);   // this row's icon (placeholder when it has none), at the tooltip's size
        _tipShownFor  = r;
        _tipShownBase = r.BaseId;
        ShowTargetTipBesideRow(rowPx);
    }

    // Place the tooltip beside the clicked row and show it: to the RIGHT of the row, top-aligned with it; flipped to the
    // LEFT when the right side would run off the screen (a list docked at the right edge); then clamped on screen.
    // No BringToFront: a DismissOnOutsideClick window sorts as a popup (ZPopup) above every normal window, and a swap
    // to another row re-opens it from hidden (the press dismissed it first) — CLAUDE.md Rule 5.
    private void ShowTargetTipBesideRow(WindowRect rowPx)
    {
        const float TipW = 380f, TipH = 150f, Gap = 8f;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its
        // row whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1f ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        float canvasW = fw.CanvasWidth  > 1f ? fw.CanvasWidth  : fw.ScreenWidth;
        float canvasH = fw.CanvasHeight > 1f ? fw.CanvasHeight : fw.ScreenHeight;
        float rx = rowPx.X * k, ry = rowPx.Y * k, rw = rowPx.Width * k;
        float x = rx + rw + Gap;
        if (x + TipW > canvasW) x = rx - TipW - Gap;
        x = System.Math.Clamp(x, 0f, System.Math.Max(0f, canvasW - TipW));
        float y = System.Math.Clamp(ry, 0f, System.Math.Max(0f, canvasH - TipH));
        _tipRect   = new WindowRect(x, y, TipW, TipH);
        _tipPlaced = false;
        _tipOpen   = true;
        _targetTip.SetRect(_tipRect);   // pre-mount: queued as the popup's PendingRect; TickTargetTipPlace re-asserts too
        _targetTip.SetVisible(true);
    }

    // Re-assert the row-anchored position after the window remounts (destroy-on-hide: the mount can land after the
    // open-time SetRect; IsShown becoming true signals the mount landed). Called each frame from OnTargetHudUpdate —
    // two bool checks while nothing is pending.
    internal void TickTargetTipPlace()
    {
        if (!_tipOpen || _tipPlaced) return;
        if (_targetTip.IsShown) { _targetTip.SetRect(_tipRect); _tipPlaced = true; }
    }

    // Also the window's OnClose, which the framework calls for a click-away (mouse DOWN outside) or Escape dismiss.
    private void CloseTargetTip()
    {
        // A mouse-press dismiss of an open tip: remember it, so the click that completes on the SAME row toggles closed.
        if (_tipShownFor != null && Input.GetMouseButton(0))
        {
            _tipDismissedFor = _tipShownFor;
            _tipDismissedAt  = Time.unscaledTime;
        }
        _tipOpen     = false;
        _tipPlaced   = false;
        _tipShownFor = null;
        _targetTip.SetVisible(false);
    }
}
