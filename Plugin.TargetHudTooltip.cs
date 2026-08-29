using System.Text.RegularExpressions;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

// Click-to-tooltip for the Target HUD's combined buff/debuff tiles. Left-clicking a tile pops a
// context-menu-style panel (PanelElement — themed background + 1px border) showing the effect icon, name,
// description, and source skill. Mirrors the CooldownBar plugin's tile tooltip (Plugin.Tooltip.cs):
// a separate Borderless DismissOnOutsideClick window whose cursor position is re-asserted each frame after
// the destroy-on-hide remount (SetRect no-ops while the window is still unmounted).
public sealed partial class Plugin
{
    private IWindowControl _targetTip = null!;

    private string  _tipName = "";
    private string  _tipDesc = "";
    private string  _tipSource = "";
    private object? _tipTex;
    private UvRect  _tipUv;
    private int     _tipShownFor = -1;   // effect tile index currently shown; -1 when closed
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

    // Called when an effect tile is left-clicked (wired in BuildTargetHudEffectTiles).
    internal void OnTargetEffectClick(int idx)
    {
        _targetBuff.Ensure();
        var list = _targetBuff.All;
        if (idx >= list.Count || (_tipShownFor == idx && _targetTip.IsShown))
        {
            CloseTargetTip();
            return;
        }
        var r = list[idx];
        var info = _services.GameData.Combat.GetBuff(r.BaseId);
        TranslatedBuffText.EnsureLoaded(_services.Log.Info);
        bool hasTr = TranslatedBuffText.TryGet(r.BaseId, out var trName, out var trDesc);
        // Name priority: manual override (see EffectOverrides) → English-translated table → game data → tracker.
        if (EffectOverrides.TryGetValue(r.BaseId, out var ov) && !string.IsNullOrEmpty(ov.Name)) _tipName = ov.Name;
        else if (hasTr && !string.IsNullOrEmpty(trName)) _tipName = StripTags(trName);
        else _tipName = StripTags(info?.Name);
        if (string.IsNullOrEmpty(_tipName)) _tipName = StripTags(r.Name);
        // Description priority: English-translated table → game data.
        _tipDesc = hasTr && !string.IsNullOrEmpty(trDesc) ? StripTags(trDesc) : StripTags(info?.Description);
        _tipSource = string.IsNullOrEmpty(r.SkillName) ? "" : $"Source: {r.SkillName}";
        _tipTex      = GetHudEffectIcon(idx);   // reuse the tile icon (also refreshes _hudEffUv[idx])
        _tipUv       = _hudEffUv[idx];
        _tipShownFor = idx;
        ShowTargetTipAtCursor();
    }

    // Position the tooltip above-right of the current cursor and show it.
    private void ShowTargetTipAtCursor()
    {
        const float TipW = 380f, TipH = 150f;
        float mx = Input.mousePosition.x + 8f;
        float my = Screen.height - Input.mousePosition.y - TipH - 8f;
        mx = System.Math.Clamp(mx, 0f, Screen.width  - TipW);
        my = System.Math.Clamp(my, 0f, Screen.height - TipH);
        _tipRect   = new WindowRect(mx, my, TipW, TipH);
        _tipPlaced = false;
        _tipOpen   = true;
        _targetTip.SetRect(_tipRect);   // may no-op if still unmounted; TickTargetTipPlace re-asserts
        _targetTip.SetVisible(true);
    }

    // Re-assert cursor position after the window remounts (destroy-on-hide: SetRect no-ops on the null token
    // at open time; IsShown becoming true signals the mount landed). Called each frame from OnTargetHudUpdate.
    internal void TickTargetTipPlace()
    {
        if (!_tipOpen || _tipPlaced) return;
        if (_targetTip.IsShown) { _targetTip.SetRect(_tipRect); _tipPlaced = true; }
    }

    private void CloseTargetTip()
    {
        _tipOpen     = false;
        _tipPlaced   = false;
        _tipShownFor = -1;
        _targetTip.SetVisible(false);
    }
}
