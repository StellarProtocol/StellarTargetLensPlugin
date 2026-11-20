using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.TargetLens;

// Placeholder art for an effect with no icon of its own (framework 2.34.0, the tiles CombatMeter 3.0 draws): a white
// category glyph (IGameText.GetEffectCategory) on a green (buff) / red (debuff) tile, from IGameAssets.LoadEffectPlaceholder.
// Used by every EFFECT icon — Effects List rows, the effect tooltip and the Select Effects rows — once the
// real chain (override → imagine / skill → buff icon) comes back null. Boss skill timers and the cast bar are skills
// and keep their own icons.
//
// Game icons load asynchronously (null = "not yet"), so an effect that HAS art to load waits PlaceholderWaitMs from its
// first miss before the placeholder shows — no placeholder flash before the real icon (CombatMeter's 1.5 s rule). An
// effect with nothing to load (no buff icon path, no source skill, no override) shows it at once. Cost per call after an
// id's first miss: one dictionary read; the category is cached per id by the framework, the textures per size.
public sealed partial class Plugin
{
    private const long PlaceholderWaitMs = 1500;
    // Base id → tick of its first icon miss (long.MinValue = nothing to load: no wait). Bounded by the distinct effect ids
    // seen without art; entries are never needed again once art loads, and the set stays small (a few hundred at most).
    private readonly Dictionary<int, long> _effIconMiss = new();

    // A row's effect icon: the real chain (ResolveEffectIcon), else its placeholder. `units` = the icon's size in canvas
    // units (the placeholder art is picked for its on-screen pixel size). Null only while real art may still load.
    private object? EffectIcon(TargetBuffRow r, float units, out UvRect uv)
    {
        var tex = ResolveEffectIcon(r, out uv);
        return tex ?? EffectPlaceholder(r.BaseId, r.BuffType == 0, units, r.SkillId > 0 || EffectOverrides.ContainsKey(r.BaseId), ref uv);
    }

    // Placeholder for buff `baseId` once its wait is over (see the file comment); null while real art may still load.
    // `otherArt` = a source skill / override icon may still arrive besides the buff's own icon.
    private object? EffectPlaceholder(int baseId, bool debuff, float units, bool otherArt, ref UvRect uv)
    {
        long now = System.Environment.TickCount64;
        if (!_effIconMiss.TryGetValue(baseId, out var since))
        {
            bool mayLoad = otherArt || !string.IsNullOrEmpty(_services.GameData.Combat.GetBuff(baseId)?.IconPath);
            since = mayLoad ? now : long.MinValue;
            _effIconMiss[baseId] = since;
        }
        if (since != long.MinValue && now - since < PlaceholderWaitMs) return null;
        var fw = _services.Framework;
        float pxPerUnit = fw.CanvasWidth > 0 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
        int px = (int)System.Math.Ceiling(units * pxPerUnit - 0.01f);
        return _services.GameAssets.LoadEffectPlaceholder(_services.GameText.GetEffectCategory(baseId), debuff, px, out uv);
    }
}
