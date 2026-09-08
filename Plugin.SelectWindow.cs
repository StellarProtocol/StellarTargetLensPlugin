using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Effect picker — 2 tabs (Debuffs / Buffs) each with a search input + virtual list + toggle per row.
// Tables are loaded lazily on first tab visit (game tables may not be ready at plugin init). Toggling a row
// writes the config immediately; the target HUD reflects it on the next rebuild (via TargetBuffTracker.Selection).
// Ported from CooldownBar's settings picker, minus the Skills tab and the click-to-tooltip SelectableElement
// wrapper (Target Lens has no per-row tooltip in this window — rows are plain icon + name + toggle).
public sealed partial class Plugin
{
    private static readonly string[] ModeOptions = { "Show only selected", "Show all, exclude selected" };

    // Loaded/assigned in the ctor (see Plugin.cs): the dedicated "select" config section + the persisted selection.
    private IConfigSection        _selCfg    = null!;
    private TargetEffectSelection _selection = null!;
    private IWindowControl        _selectWindow = null!;   // registered in RegisterSelectWindow(); drained via _windows

    private int  _activeTab = 0;   // 0 = Debuffs, 1 = Buffs
    private bool _dtScrollReset, _btScrollReset;

    private void RegisterSelectWindow()
    {
        _selectWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.select",
                Title:       "Target Lens — Effects",
                DefaultRect: new WindowRect(900f, 120f, 360f, 520f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true,
              // Gameplay tool: draw only while in-world, and hide during loading screens.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root:    BuildSelectRoot(),
            OnClose: () => _selectWindow.SetVisible(false)));
        _windows.Add(_selectWindow);   // Dispose already loops _windows and Remove()s each
    }

    private HudElement BuildSelectRoot()
    {
        var tabStrip = new RowElement(new HudElement[]
        {
            new CellElement(
                new ButtonElement(() => "Debuffs",
                    OnClick: () => { _activeTab = 0; EnsureBuffTabLoaded(); ApplyDebuffTabFilter(_dtFilter); _dtScrollReset = true; },
                    Active: () => _activeTab == 0),
                Weight: 1f),
            new CellElement(
                new ButtonElement(() => "Buffs",
                    OnClick: () => { _activeTab = 1; EnsureBuffTabLoaded(); ApplyBuffTabFilter(_btFilter); _btScrollReset = true; },
                    Active: () => _activeTab == 1),
                Weight: 1f),
        }, Gap: 4f);

        var modeRow = new RowElement(new HudElement[]
        {
            new TextElement(() => "Filter mode"),
            new SpacerElement(Width: 0f),
            new DropdownElement(
                Selected: () => (int)ActiveTabMode(),
                Options:  () => ModeOptions,
                OnSelect: v => SetActiveTabMode((TrackMode)v),
                Width: 210f),
        }, Gap: 6f);

        return new ColumnElement(new HudElement[]
        {
            new SeparatorElement(),
            new TextElement(() => "Choose which effects appear on the Target HUD", Emphasis: true),
            tabStrip,
            modeRow,
            new SeparatorElement(),
            new ConditionalElement(() => _activeTab == 0, BuildDebuffsTab()),
            new ConditionalElement(() => _activeTab == 1, BuildBuffsTab()),
        }, Gap: 4f);
    }

    private TrackMode ActiveTabMode() => _activeTab == 0 ? _selection.DebuffMode : _selection.BuffMode;

    private void SetActiveTabMode(TrackMode m)
    {
        if (_activeTab == 0) _selection.SetDebuffMode(m);
        else                 _selection.SetBuffMode(m);
        _selection.Save(_selCfg);
    }

    // ── Tab content builders (called once; pool HudElements are reused) ───────

    private HudElement BuildDebuffsTab()
    {
        var pool = new HudElement[SettingsPoolSize];
        for (int i = 0; i < SettingsPoolSize; i++)
        {
            int idx = i;
            pool[i] = new ConditionalElement(
                () => _dtOffset + idx < _dtFiltCount,
                new RowElement(new HudElement[]
                {
                    new CellElement(
                        new GameTextureElement(() => DtIcon(idx), 22, 22, () => _dtUv[idx]),
                        Width: 26f),
                    new TextElement(() => DtLabel(idx)),
                    new SpacerElement(Width: 0f),
                    new ToggleElement(() => "", () => DtTracked(idx), v => SetDtTracked(idx, v)),
                }, Gap: 4f));
        }
        return new ColumnElement(new HudElement[]
        {
            new InputElement(
                Get: () => _dtFilter, Submit: ApplyDebuffTabFilter,
                Width: 340f, OnChange: ApplyDebuffTabFilter),
            new TextElement(() => $"{_dtFiltCount} / {_dtCount} debuffs"),
            new VirtualListElement(
                Count:    () => { EnsureBuffTabLoaded(); return _dtFiltCount; },
                RowHeight: 32f, Pool: pool,
                OnWindow: i => _dtOffset = i, Height: 340f)
            { ResetScroll = () => { if (!_dtScrollReset) return false; _dtScrollReset = false; return true; } },
        }, Gap: 4f);
    }

    private HudElement BuildBuffsTab()
    {
        var pool = new HudElement[SettingsPoolSize];
        for (int i = 0; i < SettingsPoolSize; i++)
        {
            int idx = i;
            pool[i] = new ConditionalElement(
                () => _btOffset + idx < _btFiltCount,
                new RowElement(new HudElement[]
                {
                    new CellElement(
                        new GameTextureElement(() => BtIcon(idx), 22, 22, () => _btUv[idx]),
                        Width: 26f),
                    new TextElement(() => BtLabel(idx)),
                    new SpacerElement(Width: 0f),
                    new ToggleElement(() => "", () => BtTracked(idx), v => SetBtTracked(idx, v)),
                }, Gap: 4f));
        }
        return new ColumnElement(new HudElement[]
        {
            new InputElement(
                Get: () => _btFilter, Submit: ApplyBuffTabFilter,
                Width: 340f, OnChange: ApplyBuffTabFilter),
            new TextElement(() => $"{_btFiltCount} / {_btCount} buffs"),
            new VirtualListElement(
                Count:    () => { EnsureBuffTabLoaded(); return _btFiltCount; },
                RowHeight: 32f, Pool: pool,
                OnWindow: i => _btOffset = i, Height: 340f)
            { ResetScroll = () => { if (!_btScrollReset) return false; _btScrollReset = false; return true; } },
        }, Gap: 4f);
    }

    // ── Debuffs tab row helpers ───────────────────────────────────────────────

    private object? DtIcon(int idx)
    {
        int i = _dtOffset + idx;
        if (i >= _dtFiltCount) { _dtUv[idx] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_dtFiltIds[i], out _dtUv[idx]);
    }

    private string DtLabel(int idx)
    {
        int i = _dtOffset + idx;
        if (i >= _dtFiltCount) return "";
        int n = _dtFiltMembers[i].Length;
        // Same-name variants collapse into one row; show a ×N badge when the group has more than one id.
        return n > 1 ? _dtFiltNames[i] + "  ×" + n : _dtFiltNames[i];
    }

    private bool DtTracked(int idx)
    {
        int i = _dtOffset + idx;
        // ON only when every member id is tracked (partial → OFF, so one tap selects the whole group).
        return i < _dtFiltCount && GroupAllTracked(_dtFiltMembers[i], _selection.IsDebuffTracked);
    }

    private void SetDtTracked(int idx, bool on)
    {
        int i = _dtOffset + idx;
        if (i >= _dtFiltCount) return;
        // Selection stays per-id: write every member id, so the per-id HUD display path is unchanged.
        foreach (int id in _dtFiltMembers[i]) _selection.SetDebuff(id, on);
        _selection.Save(_selCfg);
    }

    // ── Buffs tab row helpers ─────────────────────────────────────────────────

    private object? BtIcon(int idx)
    {
        int i = _btOffset + idx;
        if (i >= _btFiltCount) { _btUv[idx] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_btFiltIds[i], out _btUv[idx]);
    }

    private string BtLabel(int idx)
    {
        int i = _btOffset + idx;
        if (i >= _btFiltCount) return "";
        int n = _btFiltMembers[i].Length;
        // Same-name variants collapse into one row; show a ×N badge when the group has more than one id.
        return n > 1 ? _btFiltNames[i] + "  ×" + n : _btFiltNames[i];
    }

    private bool BtTracked(int idx)
    {
        int i = _btOffset + idx;
        // ON only when every member id is tracked (partial → OFF, so one tap selects the whole group).
        return i < _btFiltCount && GroupAllTracked(_btFiltMembers[i], _selection.IsBuffTracked);
    }

    private void SetBtTracked(int idx, bool on)
    {
        int i = _btOffset + idx;
        if (i >= _btFiltCount) return;
        // Selection stays per-id: write every member id, so the per-id HUD display path is unchanged.
        foreach (int id in _btFiltMembers[i]) _selection.SetBuff(id, on);
        _selection.Save(_selCfg);
    }
}
