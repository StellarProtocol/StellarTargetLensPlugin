using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Stellar.TargetLens;

// English-translated buff name/description source for the Target HUD tooltip.
//
// The live game BuffTableBase returns the build's Chinese strings; the bundled BuffTable.json (embedded, from the
// community translation dump) carries the English text. We only need name + description, resolved per these rules:
//   Name = (Name == ""   || Name == Placeholder) ? NameDesign : Name
//   Desc = (Desc == ""   || Desc == Placeholder) ? Note       : Desc
// Name/description only — no icon override; the tile icon still resolves via the imagine/skill/buff chain.
//
// The JSON is a top-level object keyed by buff id; each row has Id/Name/NameDesign/Desc/Note (+ many fields we skip).
// Parsed once, lazily, into an id → (Name, Desc) map. System.Text.Json ignores the unused columns.
internal static class TranslatedBuffText
{
    private const string Placeholder  = "Spirit Blade Thrust count";
    private const string ResourceName = "Stellar.TargetLens.BuffTable.json";

    private static Dictionary<int, (string Name, string Desc)>? _map;

    internal static void EnsureLoaded(Action<string>? log)
    {
        if (_map != null) return;
        var map = new Dictionary<int, (string, string)>();
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null) { log?.Invoke($"[TargetHud] {ResourceName} not embedded"); _map = map; return; }
            using var doc = JsonDocument.Parse(s);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var e = prop.Value;
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty("Id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                int id = idEl.GetInt32();

                string name = Str(e, "Name");
                string desc = Str(e, "Desc");
                if (name.Length == 0 || name == Placeholder) name = Str(e, "NameDesign");
                if (desc.Length == 0 || desc == Placeholder) desc = Str(e, "Note");
                map[id] = (name, desc);
            }
            log?.Invoke($"[TargetHud] translated buff text loaded: {map.Count} entries");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[TargetHud] BuffTable.json parse failed: {ex.Message}");
        }
        _map = map;
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // True + resolved strings when the id is present. Either string may be empty (caller falls back to game data).
    internal static bool TryGet(int id, out string name, out string desc)
    {
        name = ""; desc = "";
        if (_map == null || !_map.TryGetValue(id, out var v)) return false;
        name = v.Name; desc = v.Desc;
        return true;
    }
}
